using Joust.Assembler;
using Joust.Emulation;
using Joust.Port;

namespace Joust.Tools;

/// <summary>
/// census [--frames N] [--out file]: phase 0 of the no-CPU plan (docs/NO-CPU-PLAN.md). Runs a set
/// of coverage scenarios with every native routine installed and records each original 6809
/// instruction that still executes, by the nearest label. A label with a native entry point inside
/// it is a "hand-back" (a native let the original run, or a path it leaves to the original);
/// one without is "not ported". Scenarios may poke RAM to reach rare states: this is coverage, not
/// verification.
/// </summary>
internal static class CensusCommand
{
    private sealed record Scenario(string Name, int Frames, Action<JoustMachine, long> Before);

    public static int Run(string[] args)
    {
        int frames = 30000;
        string? outPath = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--frames") frames = int.Parse(args[++i]);
            else if (args[i] == "--out") outPath = args[++i];
        }

        string dir = JoustRomBuild.FindSourceDirectory(Environment.CurrentDirectory)
            ?? JoustRomBuild.FindSourceDirectory(AppContext.BaseDirectory)
            ?? throw new DirectoryNotFoundException("joust-main-asm not found");
        var program = JoustRomBuild.Assemble(dir);
        var s = program.Symbols;
        byte[] image = program.Memory;
        byte[] cmos = FactoryCmos(image);

        ushort score = (ushort)s["SPLY1"];
        var troll = new TrollScenario(s);
        var eggs = new EggScenario(image, s);
        var cliffs = new CliffScenario(image, s);
        var scenarios = new List<Scenario>
        {
            new("attract", frames, (m, f) => { }),
            Player("one player A", 1, false, 18),
            Player("one player B", 2, false, 18),
            Player("two players", 3, true, 18),
            WithPlayer("troll and lava deaths", new RandomPlayer(4, false, TrollScenario.FlapPercent), (m, f) => troll.Apply(m)),
            WithPlayer("egg waves", new RandomPlayer(5), (m, f) => eggs.Apply(m)),
            WithPlayer("cliffs (wave 81)", new RandomPlayer(6), (m, f) => cliffs.Apply(m)),
            WithPlayer("high-score entry", new RandomPlayer(7), (m, f) =>
            {
                if (f % 2400 == 600) { m.Ram[score] = 0x00; m.Ram[score + 1] = 0x09; m.Ram[score + 2] = 0x87; m.Ram[score + 3] = 0x65; }
            }),
            WithPlayer("tilt and coin slots", new RandomPlayer(8, true), (m, f) =>
            {
                long c = f % 2400;
                m.Inputs.Tilt = c is >= 1500 and < 1510;
                m.Inputs.CoinRight = c is >= 40 and < 44;
                m.Inputs.CoinCenter = c is >= 60 and < 64;
            }),
            new("operator menus and self-test", frames, (m, f) =>
            {
                // Advance every 6 s from 10 s in, stepping backwards (auto-up off) in alternate minutes,
                // and holding the high-score reset now and then.
                m.Inputs.Advance = f >= 600 && f % 360 is >= 0 and < 6;
                m.Inputs.AutoUp = (f / 3600) % 2 == 0;
                m.Inputs.HighScoreReset = f % 5000 is >= 2000 and < 2200;
                m.Inputs.P1Flap = f % 90 < 3;
                m.Inputs.P1Right = f % 400 < 30;
                m.Inputs.Start1 = f % 777 < 4;
            }),
        };

        Scenario Player(string name, int seed, bool two, int flaps) => WithPlayer(name, new RandomPlayer(seed, two, flaps), (m, f) => { });
        Scenario WithPlayer(string name, RandomPlayer p, Action<JoustMachine, long> extra) =>
            new(name, frames, (m, f) => { p.Apply(m.Inputs, f); extra(m, f); });

        var labels = s.Where(kv => !kv.Key.Contains('.') && kv.Value is >= 0x0100 and <= 0xFFFF)
            .GroupBy(kv => (ushort)kv.Value).Select(g => (Address: g.Key, Name: g.Min(kv => kv.Key)!))
            .OrderBy(l => l.Address).ToArray();
        ushort[] starts = labels.Select(l => l.Address).ToArray();
        int LabelOf(int pc)
        {
            int i = Array.BinarySearch(starts, (ushort)pc);
            return i < 0 ? ~i - 1 : i;
        }
        var nativeLabels = new HashSet<int>();
        foreach (var r in NativeRoutines.Create())
            nativeLabels.Add(LabelOf(r.ResolveAddress(s)));

        var cycles = new long[0x10000];
        var seenIn = new int[0x10000];                           // bit per scenario
        long busyTotal = 0;
        for (int k = 0; k < scenarios.Count; k++)
        {
            var sc = scenarios[k];
            var m = new JoustMachine(image);
            cmos.CopyTo(m.Cmos, 0);
            NativeRoutines.InstallAll(m, s);
            m.IdleStart = (ushort)s["EXEINT"];
            m.IdleEnd = (ushort)(m.IdleStart + 4);
            int bit = 1 << k;
            long before = 0;
            m.StepHook = (pc, c) => { cycles[pc] += c; seenIn[pc] |= bit; before += c; };
            m.Reset();
            for (long f = 0; f < sc.Frames; f++)
            {
                sc.Before(m, f);
                m.RunFrame();
            }
            long busy = m.Cpu.Cycles - m.IdleCycles;
            busyTotal += busy;
            Console.WriteLine($"{sc.Name,-30} {sc.Frames} frames, original code {100.0 * before / busy,5:F2}% of non-idle time");
        }

        // Group by label.
        var rows = new Dictionary<int, (long Cycles, int Pcs, int Bytes, int Scenarios)>();
        for (int pc = 0; pc < 0x10000; pc++)
        {
            if (seenIn[pc] == 0) continue;
            int l = LabelOf(pc);
            var r = rows.GetValueOrDefault(l);
            rows[l] = (r.Cycles + cycles[pc], r.Pcs + 1, 0, r.Scenarios | seenIn[pc]);
        }
        var report = new List<string>
        {
            $"census: {scenarios.Count} scenarios, {rows.Count} labels with original code still running, "
            + $"{rows.Values.Sum(r => r.Pcs)} distinct instructions",
            "kind        label        share      instr  scenarios",
        };
        foreach (var (l, r) in rows.OrderBy(kv => nativeLabels.Contains(kv.Key)).ThenByDescending(kv => kv.Value.Cycles))
        {
            string kind = nativeLabels.Contains(l) ? "hand-back" : "not ported";
            string where = string.Join(", ", Enumerable.Range(0, scenarios.Count).Where(k => (r.Scenarios & (1 << k)) != 0).Select(k => scenarios[k].Name));
            report.Add($"{kind,-11} {(l >= 0 ? labels[l].Name : "?"),-12} {100.0 * r.Cycles / busyTotal,7:F4}% {r.Pcs,6}  {where}");
        }
        foreach (var line in report) Console.WriteLine(line);
        if (outPath != null) File.WriteAllLines(outPath, report);
        return 0;
    }

    private static byte[] FactoryCmos(byte[] image)
    {
        var m = new JoustMachine(image);
        m.Reset();
        for (int f = 0; f < 1300; f++) m.RunFrame();
        return (byte[])m.Cmos.Clone();
    }
}
