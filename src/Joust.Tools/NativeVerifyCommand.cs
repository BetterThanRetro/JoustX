using Joust.Assembler;
using Joust.Emulation;
using Joust.Port;

namespace Joust.Tools;

/// <summary>
/// verify-native [--frames N] [--seeds 1,2,3] [--only SYM,...] [--trace] [--two-players] [--troll] [--probe LABEL,...] [--profile] [--eggs] [--cliffs]
/// --eggs makes every wave an egg wave (the <see cref="EggScenario"/>); --cliffs starts each game at wave 81,
/// where the cliffs change (the <see cref="CliffScenario"/>).
/// --troll runs the <see cref="TrollScenario"/>: the troll and a burnt bridge from the first wave,
/// a player that rarely flaps, and a player dropped into the lava now and then.
/// --probe counts how often the original reaches the given labels.
/// Checks every native routine against the original 6809 code, two ways:
///   per-call: each real call runs both versions and compares registers, flags, cycles, memory;
///   lockstep: an all-original machine and a native machine run side by side, compared every frame.
/// </summary>
internal static class NativeVerifyCommand
{
    public static int Run(string[] args)
    {
        int frames = 20000;
        int[] seeds = [1, 2, 3];
        HashSet<string>? only = null;
        bool trace = false;
        bool twoPlayers = false;
        bool troll = false;
        string[] probes = [];
        bool profile = false;
        bool eggs = false;
        bool cliffs = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--frames") frames = int.Parse(args[++i]);
            else if (args[i] == "--seeds") seeds = args[++i].Split(',').Select(int.Parse).ToArray();
            else if (args[i] == "--only") only = new HashSet<string>(args[++i].Split(','), StringComparer.OrdinalIgnoreCase);
            else if (args[i] == "--trace") trace = true;
            else if (args[i] == "--two-players") twoPlayers = true;
            else if (args[i] == "--troll") troll = true;
            else if (args[i] == "--probe") probes = args[++i].Split(',');
            else if (args[i] == "--profile") profile = true;
            else if (args[i] == "--eggs") eggs = true;
            else if (args[i] == "--cliffs") cliffs = true;
        }

        void Install(JoustMachine m, IReadOnlyDictionary<string, long> symbols)
        {
            foreach (var r in NativeRoutines.Create())
            {
                if (only != null && !only.Contains(r.Symbol)) continue;
                r.Bind(symbols);
                m.InstallNative(r, r.ResolveAddress(symbols));
            }
            foreach (string label in probes)
                m.InstallNative(new Probe(label), (ushort)symbols[label]);
        }

        string dir = JoustRomBuild.FindSourceDirectory(Environment.CurrentDirectory)
            ?? JoustRomBuild.FindSourceDirectory(AppContext.BaseDirectory)
            ?? throw new DirectoryNotFoundException("joust-main-asm not found");
        var program = JoustRomBuild.Assemble(dir);
        if (!program.Success) throw new InvalidOperationException("assembly failed");
        byte[] image = program.Memory;
        byte[] cmos = FactoryCmos(image);
        var scenario = troll ? new TrollScenario(program.Symbols) : null;
        var eggScenario = eggs ? new EggScenario(program.Memory, program.Symbols) : null;
        var cliffScenario = cliffs ? new CliffScenario(program.Memory, program.Symbols) : null;
        void Prepare(JoustMachine m)
        {
            scenario?.Apply(m);
            eggScenario?.Apply(m);
            cliffScenario?.Apply(m);
        }
        int flaps = troll ? TrollScenario.FlapPercent : 18;

        bool ok = true;
        foreach (int seed in seeds)
        {
            Console.WriteLine($"seed {seed}, {frames} frames");

            // Per-call differential verification.
            var m = Boot(image, cmos);
            Install(m, program.Symbols);
            var verifier = new NativeVerifier(m) { TraceOriginal = trace };
            var player = new RandomPlayer(seed, twoPlayers, flaps);
            for (int f = 0; f < frames; f++)
            {
                player.Apply(m.Inputs, f);
                Prepare(m);
                m.RunFrame();
            }
            Console.Write(verifier.Summary());
            foreach (var r in verifier.Reports)
                Console.WriteLine("  MISMATCH " + r);
            ok &= verifier.TotalMismatches == 0;

            // Frame lockstep: pure original vs native.
            var original = Boot(image, cmos);
            var native = Boot(image, cmos);
            Install(native, program.Symbols);
            native.IdleStart = (ushort)program.Symbols["EXEINT"];
            native.IdleEnd = (ushort)(native.IdleStart + 4);          // TST ,X / BNE EXEINT
            var p1 = new RandomPlayer(seed, twoPlayers, flaps);
            var p2 = new RandomPlayer(seed, twoPlayers, flaps);
            var profileCycles = new long[0x10000];
            if (profile) native.StepHook = (pc, cycles) => profileCycles[pc] += cycles;
            string? diff = null;
            int frame = 0;
            for (; frame < frames && diff == null; frame++)
            {
                p1.Apply(original.Inputs, frame);
                p2.Apply(native.Inputs, frame);
                Prepare(original);
                Prepare(native);
                original.RunFrame();
                native.RunFrame();
                diff = Lockstep.Compare(original, native);
            }
            Console.WriteLine(diff == null
                ? $"  lockstep: identical for {frames} frames ({native.NativeCalls} native calls)"
                : $"  lockstep: DIVERGED at frame {frame - 1}: {diff}");
            long busy = native.Cpu.Cycles - native.IdleCycles;
            Console.WriteLine($"  native share of non-idle CPU time: {100.0 * native.NativeCycles / busy:F1}% "
                              + $"(idle wait for the blitter IRQ: {100.0 * native.IdleCycles / native.Cpu.Cycles:F1}% of all cycles)");
            if (profile) PrintProfile(profileCycles, busy, program.Symbols);
            ok &= diff == null;
        }
        Console.WriteLine(ok ? "All native routines match the original." : "Native routines do NOT match.");
        return ok ? 0 : 4;
    }

    /// <summary>
    /// --profile: the original code's non-idle cycles in the native run, by the nearest label at or
    /// below each PC (labels are approximated by the symbol table, which also holds equates).
    /// </summary>
    private static void PrintProfile(long[] cycles, long busy, IReadOnlyDictionary<string, long> symbols)
    {
        var labels = symbols.Where(kv => !kv.Key.Contains('.') && kv.Value is >= 0x0100 and <= 0xFFFF)
            .GroupBy(kv => (ushort)kv.Value).Select(g => (Address: g.Key, Name: g.Min(kv => kv.Key)!))
            .OrderBy(l => l.Address).ToArray();
        ushort[] addresses = labels.Select(l => l.Address).ToArray();
        var byLabel = new Dictionary<string, long>();
        long total = 0;
        for (int pc = 0; pc < cycles.Length; pc++)
        {
            if (cycles[pc] == 0) continue;
            total += cycles[pc];
            int i = Array.BinarySearch(addresses, (ushort)pc);
            if (i < 0) i = ~i - 1;
            string name = i >= 0 ? labels[i].Name : "?";
            byLabel[name] = byLabel.GetValueOrDefault(name) + cycles[pc];
        }
        Console.WriteLine($"  original code: {100.0 * total / busy:F1}% of non-idle time; top labels:");
        foreach (var (name, c) in byLabel.OrderByDescending(kv => kv.Value).Take(25))
            Console.WriteLine($"    {name,-10} {100.0 * c / busy,5:F2}%");
    }

    /// <summary>
    /// --probe LABEL,...: counts how often the original code reaches a label (it shows in the
    /// summary as calls, all declined), without changing anything.
    /// </summary>
    private sealed class Probe(string label) : NativeRoutine
    {
        public override string Symbol => "probe:" + label;
        public override int MaxCycles => 0;
        public override bool Execute(NativeContext c) => false;
    }

    private static JoustMachine Boot(byte[] image, byte[] cmos)
    {
        var m = new JoustMachine(image);
        cmos.CopyTo(m.Cmos, 0);
        m.Reset();
        return m;
    }

    /// <summary>CMOS after a first power-on has restored factory settings.</summary>
    private static byte[] FactoryCmos(byte[] image)
    {
        var m = new JoustMachine(image);
        m.Reset();
        for (int f = 0; f < 1300; f++) m.RunFrame();
        return (byte[])m.Cmos.Clone();
    }
}
