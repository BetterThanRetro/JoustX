using System.Text;
using System.Text.RegularExpressions;
using Joust.Assembler;
using Joust.Emulation;
using Joust.Port.Translated;

namespace Joust.Tools;

/// <summary>
/// translate [--out dir]: translates the whole original program to C# (src/Joust.Port/Translated/*.g.cs),
/// and the sound board's program too (Sound.g.cs, by <see cref="SoundTranslator"/>).
/// Every instruction the assembler emitted (and any code a branch reaches that the listing does
/// not cover) becomes one <c>case</c> of a routine's switch, with its exact cycle count (measured
/// by running it on the reference CPU) and its source line as a comment. Branches inside a
/// routine are gotos; calls, returns and jumps elsewhere leave through the dispatcher.
/// </summary>
internal static class TranslateCommand
{
    public static int Run(string[] args)
    {
        string? outDir = null;
        for (int i = 0; i < args.Length; i++)
            if (args[i] == "--out") outDir = args[++i];
        string source = JoustRomBuild.FindSourceDirectory(Environment.CurrentDirectory)
            ?? throw new DirectoryNotFoundException(JoustRomBuild.SourceFolderName);
        outDir ??= Path.Combine(Path.GetDirectoryName(source)!, "src", "Joust.Port", "Translated");
        var program = JoustRomBuild.Assemble(source);
        if (!program.Success) throw new InvalidOperationException("assembly failed");

        var translator = new Translator(program);
        translator.Write(outDir);
        Console.WriteLine(translator.Report());

        var sound = JoustRomBuild.AssembleSound(source);
        if (!sound.Success) throw new InvalidOperationException("sound ROM assembly failed");
        var soundTranslator = new SoundTranslator(sound);
        soundTranslator.Write(outDir);                           // after the main program, which clears the folder
        Console.WriteLine(soundTranslator.Report());
        return 0;
    }
}

internal sealed partial class Translator
{
    private sealed class Insn
    {
        public int Pc, Length, Cycles;
        public bool Translatable = true;
        public bool FallsThrough = true;
        public bool ReadsIo;
        public string Why = "";
        public string Text = "";
        public readonly List<string> Body = [];
        public readonly List<int> Targets = [];
    }

    private sealed class Untranslatable(string why) : Exception(why);

    private sealed record UnitPlan(string Name, string Method, List<Insn> Insns);

    private readonly byte[] _mem;
    private readonly AssemblyResult _program;
    private readonly SortedDictionary<int, Insn> _insns = [];
    private readonly Dictionary<int, string> _labels;
    private readonly List<UnitPlan> _units = [];

    private const char GoMark = '\u0001';

    public Translator(AssemblyResult program)
    {
        _program = program;
        _mem = program.Memory;
        var work = new Stack<(int Pc, int From)>();
        foreach (int pc in program.Instructions.Keys.Where(InRom).OrderByDescending(a => a))
            work.Push((pc, -1));
        while (work.Count > 0)
        {
            var (pc, from) = work.Pop();
            if (_insns.ContainsKey(pc)) continue;
            var d = Decode(pc);
            d.Text = program.Instructions.TryGetValue(pc, out var line)
                ? $"{Squash(line.Text)}  ({line.File}:{line.Line})"
                : $"(reached from ${from:X4}; not in the listing)";
            _insns[pc] = d;
            foreach (int target in d.Targets.Append(d.FallsThrough ? (pc + d.Length) & 0xFFFF : -1))
                if (target >= 0 && InRom(target) && !_insns.ContainsKey(target))
                    work.Push((target, pc));
        }

        _labels = program.Symbols
            .Where(kv => !kv.Key.Contains('.') && kv.Value is >= 0 and <= 0xFFFF && _insns.ContainsKey((int)kv.Value))
            .GroupBy(kv => (int)kv.Value)
            .ToDictionary(g => g.Key, g => g.Select(kv => kv.Key).Order(StringComparer.Ordinal).First());
        PlanUnits();
    }

    private static bool InRom(int a) => a is < 0x9000 or >= 0xD000;

    private static string Squash(string s) => WhitespaceRegex().Replace(s, " ");

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    // ------------------------------------------------------------------ routines

    private void PlanUnits()
    {
        const int maxInsns = 300;
        UnitPlan? current = null;
        int expected = -1;
        string lastLabel = "START";
        foreach (var d in _insns.Values)
        {
            bool labelled = _labels.TryGetValue(d.Pc, out string? label);
            if (labelled) lastLabel = label!;
            if (current == null || labelled || d.Pc != expected || current.Insns.Count >= maxInsns)
            {
                string name = labelled ? label! : $"{lastLabel}_{d.Pc:X4}";
                current = new UnitPlan(name, $"L{d.Pc:X4}_{Identifier(name)}", []);
                _units.Add(current);
            }
            current.Insns.Add(d);
            expected = d.Pc + d.Length;
        }
    }

    private static string Identifier(string name) => IdentifierRegex().Replace(name, "_");

    [GeneratedRegex(@"[^A-Za-z0-9_]")]
    private static partial Regex IdentifierRegex();

    public void Write(string outDir)
    {
        Directory.CreateDirectory(outDir);
        foreach (var old in Directory.GetFiles(outDir, "*.g.cs"))
            File.Delete(old);
        foreach (var chip in _units.GroupBy(u => u.Insns[0].Pc >> 12))
        {
            var sb = new StringBuilder();
            Header(sb);
            sb.AppendLine("internal static partial class Code");
            sb.AppendLine("{");
            foreach (var unit in chip)
                WriteUnit(sb, unit);
            sb.AppendLine("}");
            File.WriteAllText(Path.Combine(outDir, $"Rom{chip.Key:X}.g.cs"), sb.ToString());
        }

        var table = new StringBuilder();
        Header(table);
        table.AppendLine("internal static partial class Code");
        table.AppendLine("{");
        table.AppendLine($"    internal const ulong ImageHash = 0x{Translation.ImageHash(a => _mem[a]):X16}UL;");
        table.AppendLine();
        table.AppendLine("    internal static readonly Unit[] Units =");
        table.AppendLine("    [");
        foreach (var unit in _units)
        {
            var at = unit.Insns.Where(d => d.Translatable).Select(d => $"0x{d.Pc:X4}").ToList();
            if (at.Count == 0) continue;
            table.AppendLine($"        new(\"{unit.Name}\", {unit.Method}, [{string.Join(", ", at)}]),");
        }
        table.AppendLine("    ];");
        table.AppendLine("}");
        File.WriteAllText(Path.Combine(outDir, "Units.g.cs"), table.ToString());
    }

    private static void Header(StringBuilder sb)
    {
        sb.AppendLine("// <auto-generated>");
        sb.AppendLine("// Translated from the original 6809 program (joust-main-asm) by \"joust-tools translate\".");
        sb.AppendLine("// Do not edit: change the translator and run it again.");
        sb.AppendLine("// </auto-generated>");
        sb.AppendLine("#pragma warning disable CS0162, CS0164, CS0168, CS0219");
        sb.AppendLine("using static Joust.Emulation.Alu6809;");
        sb.AppendLine();
        sb.AppendLine("namespace Joust.Port.Translated;");
        sb.AppendLine();
    }

    private void WriteUnit(StringBuilder sb, UnitPlan unit)
    {
        var members = unit.Insns.Select(d => d.Pc).ToHashSet();
        var first = unit.Insns[0];
        sb.AppendLine($"    /// <summary>{Escape(unit.Name)}: ${first.Pc:X4}-${(unit.Insns[^1].Pc + unit.Insns[^1].Length - 1) & 0xFFFF:X4}.</summary>");
        sb.AppendLine($"    private static ushort {unit.Method}(Gen g, ushort pc)");
        sb.AppendLine("    {");
        sb.AppendLine("        var t = g.T;");
        sb.AppendLine("        int ea;");
        sb.AppendLine("        switch (pc)");
        sb.AppendLine("        {");
        foreach (var d in unit.Insns)
        {
            if (_labels.TryGetValue(d.Pc, out string? label) && d != first)
                sb.AppendLine($"            // {label}:");
            sb.AppendLine($"            case 0x{d.Pc:X4}: // {d.Text}");
            if (!d.Translatable)
            {
                sb.AppendLine($"                return 0x{d.Pc:X4};    // run by the CPU: {d.Why}");
                continue;
            }
            sb.AppendLine($"                if (!g.{(d.ReadsIo ? "IoI" : "I")}(0x{d.Pc:X4}, {d.Cycles})) return 0x{d.Pc:X4};");
            var body = d.FallsThrough ? d.Body.Append(Go((d.Pc + d.Length) & 0xFFFF)) : d.Body;
            foreach (string line in body)
                sb.AppendLine("                " + Resolve(line, members));
        }
        sb.AppendLine("        }");
        sb.AppendLine("        return pc;");
        sb.AppendLine("    }");
        sb.AppendLine();
    }

    private static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string Go(int target) => $"{GoMark}{target & 0xFFFF:X4}";

    private static string Resolve(string line, HashSet<int> members) =>
        GoRegex().Replace(line, m =>
        {
            int target = Convert.ToInt32(m.Groups[1].Value, 16);
            return members.Contains(target) ? $"goto case 0x{target:X4};" : $"return 0x{target:X4};";
        });

    [GeneratedRegex("\u0001([0-9A-F]{4})")]
    private static partial Regex GoRegex();

    public string Report()
    {
        int total = _insns.Count, listed = _insns.Keys.Count(_program.Instructions.ContainsKey);
        var cpu = _insns.Values.Where(d => !d.Translatable).GroupBy(d => d.Why).OrderByDescending(g => g.Count());
        var sb = new StringBuilder();
        sb.AppendLine($"{total} instructions ({listed} from the listing, {total - listed} reached by branches) in {_units.Count} routines");
        sb.AppendLine($"translated: {_insns.Values.Count(d => d.Translatable)} ({_insns.Values.Count(d => d.ReadsIo)} read I/O registers and run only single-stepped)");
        foreach (var g in cpu)
            sb.AppendLine($"left to the CPU: {g.Count(),5}  {g.Key}");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ decoding

    private Insn Decode(int pc)
    {
        var d = new Insn { Pc = pc };
        try
        {
            new Emitter(this, d).Translate();
            if (d.Translatable) d.Cycles = d.Cycles > 0 ? d.Cycles : MeasureCycles(pc);
        }
        catch (Untranslatable u)
        {
            d.Translatable = false;
            d.Why = u.Message;
            d.Body.Clear();
            d.FallsThrough = false;
            if (d.Length == 0) d.Length = 1;
        }
        catch (IllegalInstructionException)
        {
            d.Translatable = false;
            d.Why = "illegal opcode";
            d.Body.Clear();
            d.FallsThrough = false;
            if (d.Length == 0) d.Length = 1;
        }
        return d;
    }

    /// <summary>Cycles of the instruction at <paramref name="pc"/> as the reference CPU charges them.</summary>
    private int MeasureCycles(int pc)
    {
        var cpu = new Cpu6809(new ImageBus(_mem)) { PC = (ushort)pc };
        return cpu.Step();
    }

    private sealed class ImageBus(byte[] mem) : IBus
    {
        public byte Read(ushort a) => mem[a];
        public void Write(ushort a, byte v) { }
    }

    /// <summary>Turns one instruction into C# statements over the transaction <c>t</c> and the bus <c>g</c>.</summary>
    private sealed class Emitter(Translator tr, Insn d)
    {
        private int _pos;

        private byte B(int i) => tr._mem[(d.Pc + i) & 0xFFFF];
        private int W(int i) => (B(i) << 8) | B(i + 1);
        private byte Next8() => B(_pos++);
        private int Next16() { int w = W(_pos); _pos += 2; return w; }
        private ushort NextPc => (ushort)(d.Pc + d.Length);

        private void E(string s) => d.Body.Add(s);

        private string Go(int target)
        {
            d.Targets.Add(target & 0xFFFF);
            return Translator.Go(target);
        }

        private static readonly string[] IndexRegs = ["t.X", "t.Y", "t.U", "t.S"];

        private static string Signed(int v) => v >= 0 ? $"+ {v}" : $"- {-v}";

        public void Translate()
        {
            byte op = Next8();
            switch (op)
            {
                case 0x10: Page1(Next8()); return;
                case 0x11: Page2(Next8()); return;
            }
            int hi = op >> 4, lo = op & 0x0F;
            switch (hi)
            {
                case 0x0: MemoryUnary(lo, 1); return;
                case 0x6: MemoryUnary(lo, 2); return;
                case 0x7: MemoryUnary(lo, 3); return;
                case 0x4: InherentUnary(lo, "t.A"); return;
                case 0x5: InherentUnary(lo, "t.B"); return;
                case 0x2: Branch(lo); return;
                case 0x1: Misc(op); return;
                case 0x3: StackGroup(op); return;
                default: Accumulator(op); return;
            }
        }

        private void Done() => d.Length = _pos;

        // ---------------------------------------------------------------- operands

        /// <summary>
        /// Computes an operand's address for <paramref name="mode"/> (1 direct, 2 indexed, 3 extended):
        /// a constant when it is known now, or <c>ea</c> after the statements this emits.
        /// </summary>
        private string Ea(int mode, out int? known)
        {
            known = null;
            switch (mode)
            {
                case 1:
                    E($"ea = (t.DP << 8) | 0x{Next8():X2};");
                    return "ea";
                case 3:
                    known = Next16();
                    return $"0x{known:X4}";
            }
            byte pb = Next8();
            string r = IndexRegs[(pb >> 5) & 3];
            if ((pb & 0x80) == 0)
            {
                int off = (pb & 0x0F) - ((pb & 0x10) != 0 ? 16 : 0);
                E($"ea = (ushort)({r} {Signed(off)});");
                return "ea";
            }
            switch (pb & 0x0F)
            {
                case 0x0: E($"ea = {r}; {r} += 1;"); break;
                case 0x1: E($"ea = {r}; {r} += 2;"); break;
                case 0x2: E($"{r} -= 1; ea = {r};"); break;
                case 0x3: E($"{r} -= 2; ea = {r};"); break;
                case 0x4: E($"ea = {r};"); break;
                case 0x5: E($"ea = (ushort)({r} + (sbyte)t.B);"); break;
                case 0x6: E($"ea = (ushort)({r} + (sbyte)t.A);"); break;
                case 0x8: E($"ea = (ushort)({r} {Signed((sbyte)Next8())});"); break;
                case 0x9: E($"ea = (ushort)({r} + 0x{Next16():X4});"); break;
                case 0xB: E($"ea = (ushort)({r} + t.D);"); break;
                case 0xC: { int o = (sbyte)Next8(); known = (d.Pc + _pos + o) & 0xFFFF; break; }
                case 0xD: { int o = Next16(); known = (d.Pc + _pos + o) & 0xFFFF; break; }
                case 0xF: known = Next16(); break;
                default: throw new Untranslatable("illegal indexed postbyte");
            }
            if ((pb & 0x10) != 0)
            {
                E(known != null ? $"ea = g.Rd16(0x{known:X4});" : "ea = g.Rd16(ea);");
                known = null;
                return "ea";
            }
            return known != null ? $"0x{known:X4}" : "ea";
        }

        private static bool IsIo(int? a) => a is >= 0xC400 and < 0xCC00;

        /// <summary>An address the instruction reads from (an I/O register is read only when single-stepping).</summary>
        private string ReadEa(int mode)
        {
            string ea = Ea(mode, out int? known);
            if (IsIo(known) || IsIo(known + 1)) d.ReadsIo = true;
            return ea;
        }

        private string Operand8(int mode) => mode == 0 ? $"0x{Next8():X2}" : $"g.Rd({ReadEa(mode)})";

        private string Operand16(int mode) => mode == 0 ? $"0x{Next16():X4}" : $"g.Rd16({ReadEa(mode)})";

        /// <summary>Jumps to an address: a known one by name, a computed one through the dispatcher.</summary>
        private void JumpTo(string ea, int? known)
        {
            d.FallsThrough = false;
            E(known != null ? Go(known.Value) : $"return (ushort){ea};");
        }

        // ---------------------------------------------------------------- page 0

        private static readonly Dictionary<int, string> UnaryFn = new()
        {
            [0x0] = "Neg8", [0x3] = "Com8", [0x4] = "Lsr8", [0x6] = "Ror8", [0x7] = "Asr8",
            [0x8] = "Asl8", [0x9] = "Rol8", [0xA] = "Dec", [0xC] = "Inc",
        };

        private void MemoryUnary(int lo, int mode)
        {
            if (lo == 0xE)
            {
                string target = Ea(mode, out int? known);
                Done();
                JumpTo(target, known);
                return;
            }
            if (lo != 0xD && lo != 0xF && !UnaryFn.ContainsKey(lo)) throw new Untranslatable("illegal opcode");
            string ea = ReadEa(mode);
            Done();
            if (lo == 0xD) E($"t.CC = Logic8(t.CC, g.Rd({ea}));");
            else if (lo == 0xF) E($"g.Rd({ea}); g.Wr({ea}, 0); t.CC = Clr(t.CC);");
            else E($"g.Wr({ea}, {UnaryFn[lo]}(ref t.CC, g.Rd({ea})));");
        }

        private void InherentUnary(int lo, string r)
        {
            Done();
            if (lo == 0xD) E($"t.CC = Logic8(t.CC, {r});");
            else if (lo == 0xF) E($"{r} = 0; t.CC = Clr(t.CC);");
            else if (UnaryFn.TryGetValue(lo, out string? fn)) E($"{r} = {fn}(ref t.CC, {r});");
            else throw new Untranslatable("illegal opcode");
        }

        private static string Condition(int cond) => cond switch
        {
            0x2 => "Hi(t.CC)", 0x3 => "!Hi(t.CC)", 0x4 => "!Cs(t.CC)", 0x5 => "Cs(t.CC)",
            0x6 => "!Eq(t.CC)", 0x7 => "Eq(t.CC)", 0x8 => "!Vs(t.CC)", 0x9 => "Vs(t.CC)",
            0xA => "!Mi(t.CC)", 0xB => "Mi(t.CC)", 0xC => "!Lt(t.CC)", 0xD => "Lt(t.CC)",
            0xE => "!Le(t.CC)", _ => "Le(t.CC)",
        };

        private void Branch(int cond)
        {
            int rel = (sbyte)Next8();
            Done();
            int target = d.Pc + d.Length + rel;
            if (cond == 0x0) { d.FallsThrough = false; E(Go(target)); }
            else if (cond != 0x1) E($"if ({Condition(cond)}) {Go(target)}");
        }

        private void Misc(byte op)
        {
            switch (op)
            {
                case 0x12: Done(); return;                                       // NOP
                case 0x13: throw new Untranslatable("SYNC waits for an interrupt");
                case 0x16:                                                       // LBRA
                {
                    int rel = Next16();
                    Done();
                    d.FallsThrough = false;
                    E(Go(d.Pc + d.Length + rel));
                    return;
                }
                case 0x17:                                                       // LBSR
                {
                    int rel = Next16();
                    Done();
                    E($"g.PushS16(0x{NextPc:X4});");
                    d.FallsThrough = false;
                    E(Go(d.Pc + d.Length + rel));
                    return;
                }
                case 0x19: Done(); E("t.A = Daa(ref t.CC, t.A);"); return;
                case 0x1A: E($"t.CC |= 0x{Next8():X2};"); Done(); return;     // ORCC
                case 0x1C: E($"t.CC &= 0x{Next8():X2}; g.StopAfter();"); Done(); return;   // ANDCC: may unmask
                case 0x1D: Done(); E("t.A = (t.B & 0x80) != 0 ? (byte)0xFF : (byte)0; t.CC = Logic16(t.CC, t.D);"); return;
                case 0x1E: Exchange(Next8()); return;
                case 0x1F: Transfer(Next8()); return;
            }
            throw new Untranslatable("illegal opcode");
        }

        private static string? Reg16(int code) => code switch { 0 => "t.D", 1 => "t.X", 2 => "t.Y", 3 => "t.U", 4 => "t.S", _ => null };
        private static string? Reg8(int code) => code switch { 8 => "t.A", 9 => "t.B", 0xA => "t.CC", 0xB => "t.DP", _ => null };

        private void Transfer(byte pb)
        {
            Done();
            int src = pb >> 4, dst = pb & 0x0F;
            string from16 = Reg16(src) ?? (src == 5 ? $"0x{NextPc:X4}" : Reg8(src) is { } r8 ? $"(ushort)(0xFF00 | {r8})" : "0xFFFF");
            if (dst == 5)
            {
                d.FallsThrough = false;
                if (src == 5) E(Go(NextPc));
                else E($"return {(Reg16(src) != null ? from16 : $"(ushort){from16}")};");
                return;
            }
            if (Reg8(dst) is { } to8)
                E($"{to8} = {Reg8(src) ?? (Reg16(src) is { } r16 ? $"(byte){r16}" : src == 5 ? $"0x{NextPc & 0xFF:X2}" : "0xFF")};");
            else if (Reg16(dst) is { } to16)
                E($"{to16} = {from16};");
            if (dst == 0xA) E("g.StopAfter();");
        }

        private void Exchange(byte pb)
        {
            Done();
            int a = pb >> 4, b = pb & 0x0F;
            E($"{{ ushort a = g.Reg(0x{a:X}, 0x{NextPc:X4}), b = g.Reg(0x{b:X}, 0x{NextPc:X4}); g.SetReg(0x{a:X}, b); g.SetReg(0x{b:X}, a);");
            if (a == 5 || b == 5)
            {
                d.FallsThrough = false;
                E(a == 5 ? "return b; }" : "return a; }");
                return;
            }
            E(a == 0xA || b == 0xA ? "g.StopAfter(); }" : "}");
        }

        private void StackGroup(byte op)
        {
            switch (op)
            {
                case 0x30: case 0x31: case 0x32: case 0x33:                       // LEAX/Y/S/U
                {
                    string ea = Ea(2, out _);
                    Done();
                    string r = op switch { 0x30 => "t.X", 0x31 => "t.Y", 0x32 => "t.S", _ => "t.U" };
                    E($"{r} = (ushort)({ea});");
                    if (op <= 0x31) E($"t.CC = Set(t.CC, Z, {r} == 0);");
                    return;
                }
                case 0x34: Push(Next8(), system: true); return;
                case 0x35: Pull(Next8(), system: true); return;
                case 0x36: Push(Next8(), system: false); return;
                case 0x37: Pull(Next8(), system: false); return;
                case 0x39: Done(); d.FallsThrough = false; E("return g.PullS16();"); return;   // RTS
                case 0x3A: Done(); E("t.X += t.B;"); return;                     // ABX
                case 0x3B:                                                       // RTI
                    Done();
                    d.Cycles = 6;
                    d.FallsThrough = false;
                    E("t.CC = g.PullS();");
                    E("if ((t.CC & 0x80) != 0) { t.A = g.PullS(); t.B = g.PullS(); t.DP = g.PullS(); t.X = g.PullS16(); t.Y = g.PullS16(); t.U = g.PullS16(); t.Cycles += 9; }");
                    E("return g.PullS16();");
                    return;
                case 0x3C: throw new Untranslatable("CWAI waits for an interrupt");
                case 0x3D: Done(); E("t.D = (ushort)(t.A * t.B); t.CC = Set(Set(t.CC, Z, t.D == 0), C, (t.B & 0x80) != 0);"); return;
                case 0x3F: SoftwareInterrupt(0xFFFA, mask: true); return;
            }
            throw new Untranslatable("illegal opcode");
        }

        /// <summary>SWI/SWI2/SWI3: the whole register set stacked (E set), then the vector.</summary>
        private void SoftwareInterrupt(int vector, bool mask)
        {
            Done();
            d.FallsThrough = false;
            E("t.CC |= 0x80;");
            E($"g.PushS16(0x{NextPc:X4}); g.PushS16(t.U); g.PushS16(t.Y); g.PushS16(t.X); g.PushS(t.DP); g.PushS(t.B); g.PushS(t.A); g.PushS(t.CC);");
            if (mask) E("t.CC |= 0x50;");
            E($"return g.Rd16(0x{vector:X4});");
        }

        private void Push(byte mask, bool system)
        {
            Done();
            string p = system ? "S" : "U", other = system ? "t.U" : "t.S";
            if ((mask & 0x80) != 0) E($"g.Push{p}16(0x{NextPc:X4});");
            if ((mask & 0x40) != 0) E($"g.Push{p}16({other});");
            if ((mask & 0x20) != 0) E($"g.Push{p}16(t.Y);");
            if ((mask & 0x10) != 0) E($"g.Push{p}16(t.X);");
            if ((mask & 0x08) != 0) E($"g.Push{p}(t.DP);");
            if ((mask & 0x04) != 0) E($"g.Push{p}(t.B);");
            if ((mask & 0x02) != 0) E($"g.Push{p}(t.A);");
            if ((mask & 0x01) != 0) E($"g.Push{p}(t.CC);");
        }

        private void Pull(byte mask, bool system)
        {
            Done();
            string p = system ? "S" : "U", other = system ? "t.U" : "t.S";
            if ((mask & 0x01) != 0) E($"t.CC = g.Pull{p}(); g.StopAfter();");
            if ((mask & 0x02) != 0) E($"t.A = g.Pull{p}();");
            if ((mask & 0x04) != 0) E($"t.B = g.Pull{p}();");
            if ((mask & 0x08) != 0) E($"t.DP = g.Pull{p}();");
            if ((mask & 0x10) != 0) E($"t.X = g.Pull{p}16();");
            if ((mask & 0x20) != 0) E($"t.Y = g.Pull{p}16();");
            if ((mask & 0x40) != 0) E($"{other} = g.Pull{p}16();");
            if ((mask & 0x80) != 0)
            {
                d.FallsThrough = false;
                E($"return g.Pull{p}16();");
            }
        }

        // ---------------------------------------------------------------- pages 1 and 2

        private void Page1(byte op)
        {
            if (op is >= 0x21 and <= 0x2F)                                       // LBcc
            {
                int rel = Next16();
                Done();
                d.Cycles = 5;
                E($"if ({Condition(op & 0x0F)}) {{ t.Cycles += 1; {Go(d.Pc + d.Length + rel)} }}");
                return;
            }
            if (op == 0x3F) { SoftwareInterrupt(0xFFF4, mask: false); return; }   // SWI2
            int mode = (op >> 4) & 3;
            switch (op & 0xCF)
            {
                case 0x83: Compare16("t.D", mode); return;                       // CMPD
                case 0x8C: Compare16("t.Y", mode); return;                       // CMPY
                case 0x8E: Load16("t.Y", mode); return;                          // LDY
                case 0x8F when mode != 0: Store16("t.Y", mode); return;          // STY
                case 0xCE when op >= 0xC0: Load16("t.S", mode); return;          // LDS
                case 0xCF when op >= 0xC0 && mode != 0: Store16("t.S", mode); return;   // STS
            }
            throw new Untranslatable("illegal opcode");
        }

        private void Page2(byte op)
        {
            if (op == 0x3F) { SoftwareInterrupt(0xFFF2, mask: false); return; }   // SWI3
            int mode = (op >> 4) & 3;
            if (op is >= 0x80 and < 0xC0)
            {
                switch (op & 0x0F)
                {
                    case 0x3: Compare16("t.U", mode); return;                    // CMPU
                    case 0xC: Compare16("t.S", mode); return;                    // CMPS
                }
            }
            throw new Untranslatable("illegal opcode");
        }

        private void Compare16(string r, int mode)
        {
            string w = Operand16(mode);
            Done();
            E($"Sub16(ref t.CC, {r}, {w});");
        }

        private void Load16(string r, int mode)
        {
            string w = Operand16(mode);
            Done();
            E($"{r} = {w}; t.CC = Logic16(t.CC, {r});");
        }

        private void Store16(string r, int mode)
        {
            string ea = Ea(mode, out _);
            Done();
            E($"g.Wr16({ea}, {r}); t.CC = Logic16(t.CC, {r});");
        }

        // ---------------------------------------------------------------- accumulator ops

        private void Accumulator(byte op)
        {
            int mode = (op >> 4) & 3;
            bool isB = op >= 0xC0;
            int lo = op & 0x0F;
            string acc = isB ? "t.B" : "t.A";
            switch (lo)
            {
                case 0x3:                                                        // SUBD / ADDD
                {
                    string w = Operand16(mode);
                    Done();
                    E($"t.D = {(isB ? "Add16" : "Sub16")}(ref t.CC, t.D, {w});");
                    return;
                }
                case 0xC:                                                        // CMPX / LDD
                    if (isB) Load16("t.D", mode);
                    else Compare16("t.X", mode);
                    return;
                case 0xD:
                    if (!isB)
                    {
                        if (mode == 0)                                           // BSR
                        {
                            int rel = (sbyte)Next8();
                            Done();
                            E($"g.PushS16(0x{NextPc:X4});");
                            d.FallsThrough = false;
                            E(Go(d.Pc + d.Length + rel));
                            return;
                        }
                        string target = Ea(mode, out int? known);                // JSR
                        Done();
                        E($"g.PushS16(0x{NextPc:X4});");
                        JumpTo(target, known);
                        return;
                    }
                    if (mode == 0) break;
                    Store16("t.D", mode);                                        // STD
                    return;
                case 0xE: Load16(isB ? "t.U" : "t.X", mode); return;            // LDX / LDU
                case 0xF:                                                        // STX / STU
                    if (mode == 0) break;
                    Store16(isB ? "t.U" : "t.X", mode);
                    return;
                case 0x7:                                                        // STA / STB
                {
                    if (mode == 0) break;
                    string ea = Ea(mode, out _);
                    Done();
                    E($"g.Wr({ea}, {acc}); t.CC = Logic8(t.CC, {acc});");
                    return;
                }
                default:
                {
                    string m = Operand8(mode);
                    Done();
                    switch (lo)
                    {
                        case 0x0: E($"{acc} = Sub8(ref t.CC, {acc}, {m});"); return;
                        case 0x1: E($"Sub8(ref t.CC, {acc}, {m});"); return;
                        case 0x2: E($"{acc} = Sbc8(ref t.CC, {acc}, {m});"); return;
                        case 0x4: E($"{acc} &= {m}; t.CC = Logic8(t.CC, {acc});"); return;
                        case 0x5: E($"t.CC = Logic8(t.CC, (byte)({acc} & {m}));"); return;
                        case 0x6: E($"{acc} = {m}; t.CC = Logic8(t.CC, {acc});"); return;
                        case 0x8: E($"{acc} ^= {m}; t.CC = Logic8(t.CC, {acc});"); return;
                        case 0x9: E($"{acc} = Add8(ref t.CC, {acc}, {m}, true);"); return;
                        case 0xA: E($"{acc} |= {m}; t.CC = Logic8(t.CC, {acc});"); return;
                        case 0xB: E($"{acc} = Add8(ref t.CC, {acc}, {m}, false);"); return;
                    }
                    break;
                }
            }
            throw new Untranslatable("illegal opcode");
        }
    }
}
