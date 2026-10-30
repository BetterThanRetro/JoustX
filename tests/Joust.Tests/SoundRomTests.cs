using Joust.Assembler;

namespace Joust.Tests;

public class SoundRomTests
{
    private static readonly Lazy<AssemblyResult> Sound = new(() =>
        JoustRomBuild.AssembleSound(JoustRomBuild.FindSourceDirectory(AppContext.BaseDirectory)!));

    [Fact]
    public void SoundRomAssemblesAndMatchesItsChecksum()
    {
        var r = Sound.Value;
        Assert.True(r.Success, string.Join("\n", r.Errors.Take(20)));
        var (sum, stored) = JoustRomBuild.SoundChecksum(r);
        Assert.Equal(0x74, stored);
        Assert.Equal(stored, sum);
    }

    [Fact]
    public void BoardBootsToItsIdleLoopSilently()
    {
        var board = new Joust.Emulation.SoundBoard(Sound.Value.Memory);
        board.Reset();
        var samples = new float[48000];
        board.Run((long)Joust.Emulation.SoundBoard.ClockHz / 10, 48000, samples);
        ushort idle = (ushort)(Sound.Value.Symbols["SETUP"] + 0x1C);       // BRA * after CLI
        Assert.Equal(0x20, board.Read(idle));
        Assert.Equal(idle, board.Cpu.PC);
        Assert.Equal(0x37, board.Pia.Read(3) & 0x3F);                       // CB1 rising edge, IRQ on
    }

    [Theory]
    [InlineData(0x1F)]                                                       // wing flap
    [InlineData(0x35)]                                                       // coin
    [InlineData(0x24)]                                                       // start
    public void CommandsMakeSound(byte command)
    {
        var board = new Joust.Emulation.SoundBoard(Sound.Value.Memory);
        board.Reset();
        var samples = new float[48000];
        board.Run(20000, 48000, samples);
        board.Command(command);
        int n = board.Run((long)Joust.Emulation.SoundBoard.ClockHz / 2, 48000, samples);
        board.Command(0x3F);
        double energy = 0;
        for (int i = 0; i < n; i++) energy += samples[i] * samples[i];
        Assert.True(energy / n > 1e-4, $"command ${command:X2} stayed silent (mean square {energy / n})");
    }

    [Fact]
    public void EveryCommandRunsAndTheBoardGoesQuietAgain()
    {
        var board = new Joust.Emulation.SoundBoard(Sound.Value.Memory);
        board.Reset();
        var samples = new float[48000 * 4];
        board.Run(20000, 48000, samples);
        int sounding = 0;
        for (byte cmd = 0; cmd < 0x3F; cmd++)
        {
            board.Command(cmd);
            board.Run(200, 48000, samples);
            board.Command(0x3F);
            int n = board.Run((long)Joust.Emulation.SoundBoard.ClockHz, 48000, samples);   // 1 s
            if (samples.Take(n).Any(s => Math.Abs(s) > 0.02f)) sounding++;
        }
        Assert.True(sounding > 30, $"only {sounding} of 63 commands made a sound");
    }

    [Fact]
    public void SoundsCanBeTracedToTheObjectThatMadeThem()
    {
        var program = JoustRomBuild.Assemble(JoustRomBuild.FindSourceDirectory(AppContext.BaseDirectory)!);
        var factory = new Joust.Emulation.JoustMachine(program.Memory);
        factory.Reset();
        for (int f = 0; f < 1300; f++) factory.RunFrame();
        var m = new Joust.Emulation.JoustMachine(program.Memory);
        factory.Cmos.CopyTo(m.Cmos, 0);
        m.Reset();
        var reader = new Joust.Emulation.GameStateReader(m, Joust.Emulation.JoustLayout.FromSymbols(program.Symbols));
        int fromPlayer = 0, total = 0;
        m.WatchAddress = (int)program.Symbols["SNDPTR"];
        m.Watched = _ =>
        {
            total++;
            if (reader.Executing() is { Kind: Joust.Emulation.ObjectKind.Player }) fromPlayer++;
        };
        for (int f = 0; f < 2400; f++)
        {
            m.Inputs.CoinLeft = f is >= 900 and < 904;
            m.Inputs.Start1 = f is >= 960 and < 964;
            m.Inputs.P1Flap = f > 1100 && f % 12 < 3;
            m.RunFrame();
        }
        Assert.True(fromPlayer > 5, $"{fromPlayer} of {total} sounds traced to the player");
    }

    [Fact]
    public void SoundRomVectorsPointIntoTheRom()
    {
        var r = Sound.Value;
        foreach (int v in new[] { 0xFFF8, 0xFFFA, 0xFFFC, 0xFFFE })   // IRQ, SWI, NMI, RESET
        {
            int target = (r.Memory[v] << 8) | r.Memory[v + 1];
            Assert.InRange(target, JoustRomBuild.SoundRomBase + 1, 0xFFF7);
        }
    }
}
