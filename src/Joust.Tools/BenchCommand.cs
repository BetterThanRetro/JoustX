using System.Diagnostics;
using Joust.Assembler;
using Joust.Emulation;
using Joust.Port;

namespace Joust.Tools;

/// <summary>
/// bench [--frames N]: emulated frames per second of the original on the emulated 6809, the C#
/// routines with the 6809 behind them, and the C# routines with no CPU, playing the same game.
/// </summary>
internal static class BenchCommand
{
    public static int Run(string[] args)
    {
        int frames = 20000;
        for (int i = 0; i < args.Length; i++)
            if (args[i] == "--frames") frames = int.Parse(args[++i]);
        string dir = JoustRomBuild.FindSourceDirectory(Environment.CurrentDirectory)
            ?? throw new DirectoryNotFoundException(JoustRomBuild.SourceFolderName);
        var program = JoustRomBuild.Assemble(dir);
        var factory = new JoustMachine(program.Memory);
        factory.Reset();
        for (int f = 0; f < 1300; f++) factory.RunFrame();

        foreach (var (name, setup) in new (string, Action<JoustMachine>)[]
                 {
                     ("original (6809)", _ => { }),
                     ("C# with the 6809 behind it", m => NativeRoutines.InstallAll(m, program.Symbols)),
                     ("C#, no CPU", m => { NativeRoutines.InstallAll(m, program.Symbols); NoCpuRuntime.Attach(m); }),
                 })
        {
            var m = new JoustMachine(program.Memory);
            factory.Cmos.CopyTo(m.Cmos, 0);
            m.Reset();
            setup(m);
            var player = new RandomPlayer(1);
            var sw = Stopwatch.StartNew();
            for (int f = 0; f < frames; f++)
            {
                player.Apply(m.Inputs, f);
                m.RunFrame();
            }
            sw.Stop();
            Console.WriteLine($"{name,-28} {frames / sw.Elapsed.TotalSeconds,7:F0} frames/s  ({sw.Elapsed.TotalMilliseconds / frames:F3} ms a frame)");
        }
        return 0;
    }
}
