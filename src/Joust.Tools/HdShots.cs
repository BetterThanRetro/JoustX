using Joust.Assembler;
using Joust.Emulation;
using Joust.Render;

namespace Joust.Tools;

/// <summary>
/// hd-shots &lt;outdir&gt; [--frames f1,f2,...] [--crt]: boot the game from source, drop a coin and
/// start a one-player game (flapping now and then), and save HD renders of the chosen frames.
/// </summary>
internal static class HdShots
{
    public static int Run(string[] args)
    {
        string outDir = args.Length > 0 ? args[0] : "build/hd";
        var frames = new SortedSet<int> { 1500, 2200, 3000 };
        bool crt = false;
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--frames") frames = new SortedSet<int>(args[++i].Split(',').Select(int.Parse));
            else if (args[i] == "--crt") crt = true;
        }
        Directory.CreateDirectory(outDir);

        string source = JoustRomBuild.FindSourceDirectory(Environment.CurrentDirectory)
            ?? throw new DirectoryNotFoundException(JoustRomBuild.SourceFolderName);
        var program = JoustRomBuild.Assemble(source);
        // First power-on restores factory settings into blank CMOS; then power-cycle, as an operator would.
        var factory = new JoustMachine(program.Memory);
        factory.Reset();
        for (int f = 0; f < 1300; f++) factory.RunFrame();
        var m = new JoustMachine(program.Memory);
        factory.Cmos.CopyTo(m.Cmos, 0);
        m.Reset();

        var renderer = new HdRenderer();
        var events = new EventEffects(new GameStateReader(m, JoustLayout.FromSymbols(program.Symbols)), renderer.Effects);
        var options = new HdOptions { Crt = crt };
        var indices = new byte[JoustMachine.ScreenWidth * JoustMachine.ScreenHeight];
        var palette = new uint[16];
        var rng = new Random(7);
        int last = frames.Max;
        for (int f = 0; f <= last; f++)
        {
            var i = m.Inputs;
            i.CoinLeft = f is >= 900 and < 904;
            i.Start1 = f is >= 960 and < 964;
            i.P1Flap = f > 1000 && rng.Next(6) == 0;
            i.P1Right = f > 1000 && (f / 90) % 3 == 0;
            i.P1Left = f > 1000 && (f / 90) % 3 == 1;
            m.RunFrame();
            events.Update();
            if (!frames.Any(s => f <= s && f > s - 120)) continue;   // render the run-up so particles move

            m.RenderIndices(indices);
            for (int c = 0; c < 16; c++) palette[c] = JoustMachine.PaletteToArgb(m.Palette[c]);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            renderer.Render(indices, palette, f, options);
            sw.Stop();
            if (!frames.Contains(f)) continue;
            string path = Path.Combine(outDir, $"hd_{f:D5}{(crt ? "_crt" : "")}.png");
            PngWriter.Write(path, renderer.Output, HdRenderer.Width, HdRenderer.Height);
            Console.WriteLine($"{path}  ({sw.Elapsed.TotalMilliseconds:F1} ms)");
        }
        return 0;
    }
}
