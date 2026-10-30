using System.Text;
using System.Text.RegularExpressions;

namespace Joust.Assembler;

public sealed class AssemblerOptions
{
    /// <summary>Symbols defined before assembly starts (like AS's -D switch).</summary>
    public Dictionary<string, long> Defines { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int MaxPasses { get; set; } = 25;
}

public sealed class AssemblyResult
{
    public required byte[] Memory { get; init; }
    public required bool[] Written { get; init; }
    public required IReadOnlyDictionary<string, long> Symbols { get; init; }
    public required IReadOnlyList<string> Errors { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    public required int Passes { get; init; }

    public bool Success => Errors.Count == 0;

    public byte[] Slice(int start, int length) => Memory.AsSpan(start, length).ToArray();
}

/// <summary>
/// A two-plus-pass 6809 assembler for the Macro Assembler {AS} dialect used by the Joust
/// source rewrite. Passes repeat until every symbol value is stable, picking the shortest
/// encodings (direct page, 5/8-bit index offsets) once operands are known, as AS does.
/// </summary>
public sealed partial class Assembler
{
    private readonly AssemblerOptions _options;
    private readonly Dictionary<string, string[]> _fileCache = new(StringComparer.OrdinalIgnoreCase);

    // Per-pass state
    private Dictionary<string, long> _symbols = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, long> _previous = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _setSymbols = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Macro> _macros = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stack<CondFrame> _cond = new();
    private readonly List<string> _errors = [];
    private readonly List<string> _warnings = [];
    private byte[] _memory = new byte[0x10000];
    private bool[] _written = new bool[0x10000];
    private int _pc;
    private int _linePc;
    private int _dpr = -1;
    private string _scope = "";
    private bool _final;
    private bool _m6800;                                         // CPU 6800: the sound board's instruction set
    private bool _ended;
    private MacroBuilder? _defining;
    private string _file = "";
    private int _line;
    private readonly ExpressionEvaluator _eval;

    public Assembler(AssemblerOptions? options = null)
    {
        _options = options ?? new AssemblerOptions();
        _eval = new ExpressionEvaluator(Lookup, () => _linePc);
    }

    public AssemblyResult AssembleFile(string path)
    {
        path = Path.GetFullPath(path);
        int pass = 0;
        _previous = new(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            pass++;
            RunPass(path, final: false);
            bool stable = pass > 1 && SymbolsEqual(_symbols, _previous);
            _previous = _symbols;
            if (stable || pass >= _options.MaxPasses)
                break;
        }
        RunPass(path, final: true);
        return new AssemblyResult
        {
            Memory = _memory,
            Written = _written,
            Symbols = new Dictionary<string, long>(_symbols, StringComparer.OrdinalIgnoreCase),
            Errors = _errors.ToList(),
            Warnings = _warnings.ToList(),
            Passes = pass + 1,
        };
    }

    private void RunPass(string path, bool final)
    {
        _final = final;
        _symbols = new(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in _options.Defines)
            _symbols[k] = v;
        _setSymbols.Clear();
        _macros.Clear();
        _cond.Clear();
        _errors.Clear();
        _warnings.Clear();
        _memory = new byte[0x10000];
        _written = new bool[0x10000];
        _pc = 0;
        _dpr = -1;
        _m6800 = false;
        _scope = "";
        _ended = false;
        _defining = null;
        ProcessFile(path);
        if (_cond.Count > 0)
            Error("unterminated IF block");
        if (_defining != null)
            Error($"unterminated MACRO {_defining.Name}");
    }

    private static bool SymbolsEqual(Dictionary<string, long> a, Dictionary<string, long> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var (k, v) in a)
            if (!b.TryGetValue(k, out long w) || w != v)
                return false;
        return true;
    }

    private string[] ReadFile(string path)
    {
        if (!_fileCache.TryGetValue(path, out var lines))
        {
            lines = File.ReadAllLines(path, Encoding.Latin1);
            _fileCache[path] = lines;
        }
        return lines;
    }

    private void ProcessFile(string path)
    {
        string[] lines = ReadFile(path);
        string savedFile = _file;
        int savedLine = _line;
        for (int i = 0; i < lines.Length && !_ended; i++)
        {
            _file = path;
            _line = i + 1;
            try
            {
                ProcessLine(lines[i]);
            }
            catch (AsmException ex)
            {
                Error(ex.Message);
            }
        }
        _file = savedFile;
        _line = savedLine;
    }

    // ---------------------------------------------------------------- line parsing

    private readonly record struct Parsed(string? Label, string Op, string Operand);

    internal static string StripComment(string raw)
    {
        char quote = '\0';
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
            }
            else if (c == '\'' || c == '"')
            {
                // Only treat as a string/char opener when a closing quote follows.
                if (raw.IndexOf(c, i + 1) > 0) quote = c;
            }
            else if (c == ';')
            {
                return raw[..i];
            }
        }
        return raw;
    }

    private static Parsed Parse(string raw)
    {
        string code = StripComment(raw).TrimEnd();
        if (code.Length == 0 || code[0] == '*')
            return new Parsed(null, "", "");
        string? label = null;
        int p = 0;
        if (!char.IsWhiteSpace(code[0]))
        {
            while (p < code.Length && !char.IsWhiteSpace(code[p])) p++;
            label = code[..p].TrimEnd(':');
        }
        while (p < code.Length && char.IsWhiteSpace(code[p])) p++;
        int opStart = p;
        while (p < code.Length && !char.IsWhiteSpace(code[p])) p++;
        string op = code[opStart..p];
        string operand = code[p..].Trim();
        return new Parsed(label, op, operand);
    }

    // ---------------------------------------------------------------- line processing

    private bool Active => _cond.Count == 0 || _cond.Peek().Active;

    private void ProcessLine(string raw)
    {
        if (_defining != null)
        {
            CollectMacroLine(raw);
            return;
        }

        var line = Parse(raw);
        string op = line.Op.ToUpperInvariant();
        _linePc = _pc;

        if (HandleConditional(op, line))
            return;
        if (!Active)
            return;

        if (op == "MACRO")
        {
            if (line.Label == null) throw new AsmException("MACRO needs a name");
            var parms = line.Operand.Length == 0
                ? []
                : line.Operand.Split(',').Select(s => s.Trim()).ToArray();
            _defining = new MacroBuilder(line.Label, parms);
            return;
        }

        if (op == "IRP")
        {
            // IRP P,v1,v2,...: repeat the block up to its ENDM once per value, with P replaced.
            var items = SplitOperands(line.Operand);
            if (items.Count == 0) throw new AsmException("IRP needs a parameter");
            _defining = new MacroBuilder("", [items[0]]) { IrpValues = items.Skip(1).ToList() };
            return;
        }

        if (op is "EQU" or "SET" or "=")
        {
            if (line.Label == null) throw new AsmException($"{op} needs a label");
            long v = Eval(line.Operand, out _);
            DefineSymbol(line.Label, v, isSet: op == "SET");
            return;
        }

        if (line.Label != null)
            DefineSymbol(line.Label, _pc, isSet: false);

        if (op.Length == 0)
            return;

        if (_macros.TryGetValue(op, out var macro))
        {
            ExpandMacro(macro, line.Operand);
            return;
        }

        switch (op)
        {
            case "ORG":
                _pc = (int)(Eval(line.Operand, out _) & 0xFFFF);
                return;
            case "RMB":
            case "DS":
                _pc += (int)Eval(line.Operand, out _);
                return;
            case "FCB":
            case "DB":
                EmitData(line.Operand, 1);
                return;
            case "FDB":
            case "DW":
                EmitData(line.Operand, 2);
                return;
            case "FCC":
                EmitData(line.Operand, 1, stringsOnly: true);
                return;
            case "END":
                _ended = true;
                return;
            case "INCLUDE":
                ProcessFile(ResolveInclude(line.Operand));
                return;
            case "ASSUME":
                Assume(line.Operand);
                return;
            case "CPU":
                _m6800 = line.Operand.Trim() is "6800" or "6802" or "6808";
                return;
            case "PLAINBASE":
            case "PAGE":
            case "LISTING":
                return;
            case "ENDM":
                throw new AsmException("ENDM without MACRO");
        }

        if (_m6800)
        {
            if (Instructions6800.Table.TryGetValue(op, out var op6800))
            {
                Assemble6800(op, op6800, line.Operand);
                return;
            }
        }
        else if (Instructions6809.Table.TryGetValue(op, out var info))
        {
            AssembleInstruction(op, info, line.Operand);
            return;
        }

        throw new AsmException($"unknown instruction or directive '{line.Op}'");
    }

    private string ResolveInclude(string operand)
    {
        string name = operand.Trim().Trim('"', '\'');
        string dir = Path.GetDirectoryName(_file) ?? ".";
        string full = Path.Combine(dir, name);
        if (!File.Exists(full))
        {
            // The source was written on case-insensitive file systems (e.g. joust_mods.asm vs
            // joust_mods.ASM); match by name ignoring case so it also builds on Linux.
            full = Directory.EnumerateFiles(dir)
                .FirstOrDefault(f => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase))
                ?? throw new AsmException($"include file not found: {name}");
        }
        return Path.GetFullPath(full);
    }

    private void Assume(string operand)
    {
        foreach (var part in operand.Split(','))
        {
            var kv = part.Split(':', 2);
            if (kv.Length != 2) throw new AsmException($"bad ASSUME '{part}'");
            if (!kv[0].Trim().Equals("DPR", StringComparison.OrdinalIgnoreCase))
                continue;
            string v = kv[1].Trim();
            _dpr = v.Equals("NOTHING", StringComparison.OrdinalIgnoreCase) ? -1 : (int)(Eval(v, out _) & 0xFF);
        }
    }

    // ---------------------------------------------------------------- conditionals

    private sealed class CondFrame(bool parentActive, bool active)
    {
        public bool ParentActive { get; } = parentActive;
        public bool Active { get; set; } = active;
        public bool TakenAny { get; set; } = active;
        public bool ElseSeen { get; set; }
    }

    private bool HandleConditional(string op, Parsed line)
    {
        switch (op)
        {
            case "IF":
            case "IFDEF":
            case "IFNDEF":
            case "IFB":
            case "IFNB":
            {
                bool parent = Active;
                bool cond = false;
                if (parent)
                {
                    cond = op switch
                    {
                        "IF" => Eval(line.Operand, out _) != 0,
                        "IFDEF" => IsDefined(line.Operand.Trim()),
                        "IFNDEF" => !IsDefined(line.Operand.Trim()),
                        "IFB" => line.Operand.Trim().Length == 0,
                        _ => line.Operand.Trim().Length != 0,
                    };
                }
                _cond.Push(new CondFrame(parent, parent && cond));
                return true;
            }
            case "ELSE":
            {
                if (_cond.Count == 0) throw new AsmException("ELSE without IF");
                var f = _cond.Peek();
                if (f.ElseSeen) throw new AsmException("duplicate ELSE");
                f.ElseSeen = true;
                f.Active = f.ParentActive && !f.TakenAny;
                f.TakenAny |= f.Active;
                return true;
            }
            case "ENDIF":
            case "ENDC":
                if (_cond.Count == 0) throw new AsmException("ENDIF without IF");
                _cond.Pop();
                return true;
        }
        return false;
    }

    private bool IsDefined(string name) => _symbols.ContainsKey(ResolveName(name));

    // ---------------------------------------------------------------- macros

    private sealed record Macro(string Name, string[] Params, List<string> Body);

    private sealed class MacroBuilder(string name, string[] parms)
    {
        public string Name { get; } = name;
        public string[] Params { get; } = parms;
        public List<string> Body { get; } = [];
        public int Depth { get; set; }
        public List<string>? IrpValues { get; init; }
    }

    private void CollectMacroLine(string raw)
    {
        var parsed = Parse(raw);
        string op = parsed.Op.ToUpperInvariant();
        if (op is "MACRO" or "IRP")
            _defining!.Depth++;
        else if (op == "ENDM")
        {
            if (_defining!.Depth == 0)
            {
                var done = _defining;
                _defining = null;
                if (done.IrpValues is { } values)
                {
                    var block = new Macro("", done.Params, done.Body);
                    foreach (var v in values)
                    {
                        ExpandMacro(block, v);
                        if (_ended) return;
                    }
                }
                else
                {
                    _macros[done.Name] = new Macro(done.Name, done.Params, done.Body);
                }
                return;
            }
            _defining.Depth--;
        }
        _defining!.Body.Add(StripComment(raw));
    }

    private void ExpandMacro(Macro macro, string operand)
    {
        var args = SplitOperands(operand);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < macro.Params.Length; i++)
            map[macro.Params[i]] = i < args.Count ? args[i] : "";
        foreach (var body in macro.Body)
        {
            string text = IdentifierRegex().Replace(body, m => map.TryGetValue(m.Value, out var a) ? a : m.Value);
            ProcessLine(text);
            if (_ended) return;
        }
    }

    // Like AS, a parameter is also replaced in a name's '_'-separated parts (LT_C -> LT_7).
    [GeneratedRegex(@"[A-Za-z][A-Za-z0-9]*")]
    private static partial Regex IdentifierRegex();

    /// <summary>Splits on top-level commas, respecting parentheses, brackets and quotes.</summary>
    internal static List<string> SplitOperands(string s)
    {
        var result = new List<string>();
        int depth = 0, start = 0;
        char quote = '\0';
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                continue;
            }
            switch (c)
            {
                case '\'' or '"' when s.IndexOf(c, i + 1) > 0:
                    quote = c;
                    break;
                case '(' or '[':
                    depth++;
                    break;
                case ')' or ']':
                    depth--;
                    break;
                case ',' when depth == 0:
                    result.Add(s[start..i].Trim());
                    start = i + 1;
                    break;
            }
        }
        if (s.Length > 0)
            result.Add(s[start..].Trim());
        return result;
    }

    // ---------------------------------------------------------------- symbols

    private string ResolveName(string name)
    {
        if (name.StartsWith('.'))
            return _scope + name;
        return name;
    }

    private long? Lookup(string name)
    {
        string full = ResolveName(name);
        if (_symbols.TryGetValue(full, out long v))
            return v;
        if (_previous.TryGetValue(full, out v))
            return v;
        return null;
    }

    private void DefineSymbol(string name, long value, bool isSet)
    {
        if (!name.StartsWith('.'))
            _scope = name;
        string full = ResolveName(name);
        if (isSet)
        {
            _setSymbols.Add(full);
            _symbols[full] = value;
            return;
        }
        if (_symbols.TryGetValue(full, out long old) && !_setSymbols.Contains(full))
        {
            if (old != value)
                Error($"symbol '{name}' redefined ({old:X4} -> {value:X4})");
            return;
        }
        _symbols[full] = value;
    }

    private long Eval(string expr, out bool unknown)
    {
        if (expr.Trim().Length == 0)
            throw new AsmException("missing expression");
        long v = _eval.Evaluate(expr);
        unknown = _eval.Unknown;
        if (unknown && _final)
            Error($"undefined symbol(s): {string.Join(", ", _eval.UnknownSymbols)}");
        return v;
    }

    // ---------------------------------------------------------------- output

    private void Emit(int b)
    {
        int addr = _pc & 0xFFFF;
        if (_final && _written[addr])
            _warnings.Add($"{Location}: overwriting ${addr:X4}");
        _memory[addr] = (byte)b;
        _written[addr] = true;
        _pc++;
    }

    private void Emit16(int w)
    {
        Emit(w >> 8);
        Emit(w);
    }

    private void EmitData(string operand, int width, bool stringsOnly = false)
    {
        foreach (var item in SplitOperands(operand))
        {
            if (item.Length >= 2 && (item[0] == '"' || (item[0] == '\'' && (stringsOnly || item.Length != 3))) && item[^1] == item[0])
            {
                foreach (char c in item[1..^1])
                {
                    if (width == 2) Emit(0);
                    Emit((byte)c);
                }
                continue;
            }
            if (stringsOnly)
                throw new AsmException($"FCC expects a quoted string, got '{item}'");
            // '*' stays the address of the start of the statement for every item.
            long v = Eval(item, out _);
            if (width == 1)
            {
                CheckRange(v, -128, 255, "byte");
                Emit((int)v);
            }
            else
            {
                CheckRange(v, -32768, 65535, "word");
                Emit16((int)v);
            }
        }
    }

    private void CheckRange(long v, long min, long max, string what)
    {
        if (_final && (v < min || v > max))
            Error($"{what} value ${v:X} out of range");
    }

    private string Location => $"{Path.GetFileName(_file)}:{_line}";

    private void Error(string message)
    {
        if (_final)
            _errors.Add($"{Location}: {message}");
    }
}
