using Joust.Emulation;

namespace Joust.Tools;

/// <summary>
/// Boots the reference machine without a window and captures screenshots.
/// Script entries are "frame:action[+action]" e.g. "900:coin 960:start1 1200:flap".
/// Each action holds its switch for 4 frames.
/// </summary>
internal static class HeadlessRunner
{
    public static int Run(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("run <joust_main_64k.bin> <frames> <outdir> [--shots f1,f2,...] [--script \"f:action ...\"] [--cmos file]");
            return 1;
        }
        var image = File.ReadAllBytes(args[0]);
        int frames = int.Parse(args[1]);
        string outDir = args[2];
        Directory.CreateDirectory(outDir);

        var shots = new HashSet<int>();
        var script = new List<(int Frame, string Action)>();
        string? cmosPath = null;
        int watchLo = -1, watchHi = -1;
        for (int i = 3; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--shots":
                    foreach (var s in args[++i].Split(',')) shots.Add(int.Parse(s));
                    break;
                case "--script":
                    foreach (var entry in args[++i].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var kv = entry.Split(':');
                        foreach (var a in kv[1].Split('+'))
                            script.Add((int.Parse(kv[0]), a));
                    }
                    break;
                case "--cmos":
                    cmosPath = args[++i];
                    break;
                case "--watch":
                {
                    var r = args[++i].Split('-');
                    watchLo = Convert.ToInt32(r[0], 16);
                    watchHi = Convert.ToInt32(r[^1], 16);
                    break;
                }
            }
        }

        var m = new JoustMachine(image);
        if (watchLo >= 0)
        {
            int logged = 0;
            m.WriteHook = (a, v) =>
            {
                if (a >= watchLo && a <= watchHi && logged++ < 400)
                    Console.WriteLine($"  f{m.Frame} PC~{m.Cpu.PC:X4} [{a:X4}]={v:X2}");
            };
        }
        if (cmosPath != null && File.Exists(cmosPath))
            File.ReadAllBytes(cmosPath).CopyTo(m.Cmos, 0);
        var sounds = new List<(long Frame, byte Cmd)>();
        m.SoundCommand += cmd => sounds.Add((m.Frame, cmd));
        m.Reset();

        GameStateReader? reader = null;
        string symPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[0]))!, "joust.sym");
        if (File.Exists(symPath))
        {
            var syms = File.ReadAllLines(symPath)
                .Select(l => l.Split(' ', 2))
                .ToDictionary(p => p[1], p => Convert.ToInt64(p[0], 16), StringComparer.OrdinalIgnoreCase);
            reader = new GameStateReader(m, JoustLayout.FromSymbols(syms));
        }

        var pixels = new uint[JoustMachine.ScreenWidth * JoustMachine.ScreenHeight];
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int f = 0; f < frames; f++)
        {
            ApplyScript(m.Inputs, script, f);
            try
            {
                m.RunFrame();
            }
            catch (IllegalInstructionException ex)
            {
                Console.WriteLine($"frame {f}: {ex.Message}");
                DumpCpu(m.Cpu);
                return 2;
            }
            if (shots.Contains(f))
            {
                m.RenderArgb(pixels);
                string path = Path.Combine(outDir, $"frame_{f:D5}.png");
                PngWriter.Write(path, pixels, JoustMachine.ScreenWidth, JoustMachine.ScreenHeight, 2);
                Console.WriteLine($"saved {path}");
                if (reader != null)
                    foreach (var o in reader.ReadObjects())
                        Console.WriteLine($"    {o.Kind,-12} id={o.Id:X2} x={o.X,3} y={o.Y,3} {(o.FacingLeft ? "L" : "R")} horse={o.HorseImage:X4} rider={o.RiderImage:X4}");
            }
        }
        sw.Stop();
        Console.WriteLine($"{frames} frames in {sw.ElapsedMilliseconds} ms ({frames * 1000.0 / Math.Max(1, sw.ElapsedMilliseconds):F0} fps), cpu cycles {m.Cpu.Cycles}");
        Console.WriteLine($"{sounds.Count} sound commands; first: {string.Join(" ", sounds.Take(16).Select(s => $"{s.Frame}:{s.Cmd:X2}"))}");
        DumpCpu(m.Cpu);
        Console.WriteLine("palette: " + string.Join(" ", m.Palette.Select(p => p.ToString("X2"))));
        foreach (int y in new[] { 200, 215, 230, 240 })
        {
            var counts = new int[16];
            for (int col = 0; col < 152; col++)
            {
                byte b = m.Ram[(col << 8) | y];
                counts[b >> 4]++;
                counts[b & 15]++;
            }
            Console.WriteLine($"row {y}: " + string.Join(" ", counts.Select((c, i) => (c, i)).Where(t => t.c > 0).Select(t => $"{t.i:X}x{t.c}")));
        }
        if (cmosPath != null)
            File.WriteAllBytes(cmosPath, m.Cmos);
        return 0;
    }

    private static void ApplyScript(JoustInputs inp, List<(int Frame, string Action)> script, int frame)
    {
        bool Held(string a) => script.Any(s => s.Action == a && frame >= s.Frame && frame < s.Frame + 4);
        inp.CoinLeft = Held("coin");
        inp.Start1 = Held("start1");
        inp.Start2 = Held("start2");
        inp.P1Flap = Held("flap");
        inp.P1Left = Held("left");
        inp.P1Right = Held("right");
        inp.Advance = Held("advance");
        inp.AutoUp = Held("autoup");
    }

    private static void DumpCpu(Cpu6809 c) =>
        Console.WriteLine($"PC={c.PC:X4} A={c.A:X2} B={c.B:X2} X={c.X:X4} Y={c.Y:X4} U={c.U:X4} S={c.S:X4} DP={c.DP:X2} CC={c.CC:X2}");
}
