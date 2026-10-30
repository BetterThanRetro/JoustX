using Joust.Assembler;
using Joust.Emulation;
using Joust.Port;

namespace Joust.Tools;

/// <summary>
/// verify-native [--frames N] [--seeds 1,2,3]
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
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--frames") frames = int.Parse(args[++i]);
            else if (args[i] == "--seeds") seeds = args[++i].Split(',').Select(int.Parse).ToArray();
            else if (args[i] == "--only") only = new HashSet<string>(args[++i].Split(','), StringComparer.OrdinalIgnoreCase);
            else if (args[i] == "--trace") trace = true;
        }

        void Install(JoustMachine m, IReadOnlyDictionary<string, long> symbols)
        {
            foreach (var r in NativeRoutines.Create())
            {
                if (only != null && !only.Contains(r.Symbol)) continue;
                r.Bind(symbols);
                m.InstallNative(r, r.ResolveAddress(symbols));
            }
        }

        string dir = JoustRomBuild.FindSourceDirectory(Environment.CurrentDirectory)
            ?? JoustRomBuild.FindSourceDirectory(AppContext.BaseDirectory)
            ?? throw new DirectoryNotFoundException("joust-main-asm not found");
        var program = JoustRomBuild.Assemble(dir);
        if (!program.Success) throw new InvalidOperationException("assembly failed");
        byte[] cmos = FactoryCmos(program.Memory);

        bool ok = true;
        foreach (int seed in seeds)
        {
            Console.WriteLine($"seed {seed}, {frames} frames");

            // Per-call differential verification.
            var m = Boot(program.Memory, cmos);
            Install(m, program.Symbols);
            var verifier = new NativeVerifier(m) { TraceOriginal = trace };
            var player = new RandomPlayer(seed);
            for (int f = 0; f < frames; f++)
            {
                player.Apply(m.Inputs, f);
                m.RunFrame();
            }
            Console.Write(verifier.Summary());
            foreach (var r in verifier.Reports)
                Console.WriteLine("  MISMATCH " + r);
            ok &= verifier.TotalMismatches == 0;

            // Frame lockstep: pure original vs native.
            var original = Boot(program.Memory, cmos);
            var native = Boot(program.Memory, cmos);
            Install(native, program.Symbols);
            native.IdleStart = (ushort)program.Symbols["EXEINT"];
            native.IdleEnd = (ushort)(native.IdleStart + 4);          // TST ,X / BNE EXEINT
            var p1 = new RandomPlayer(seed);
            var p2 = new RandomPlayer(seed);
            string? diff = null;
            int frame = 0;
            for (; frame < frames && diff == null; frame++)
            {
                p1.Apply(original.Inputs, frame);
                p2.Apply(native.Inputs, frame);
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
            ok &= diff == null;
        }
        Console.WriteLine(ok ? "All native routines match the original." : "Native routines do NOT match.");
        return ok ? 0 : 4;
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
