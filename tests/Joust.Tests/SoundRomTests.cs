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
