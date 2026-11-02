using Joust.Assembler;
using Joust.Emulation;
using Joust.Port.Translated;

namespace Joust.Tests;

/// <summary>The sound board's program translated to C#, checked against the emulated 6808.</summary>
public class SoundTranslationTests
{
    private static readonly Lazy<AssemblyResult> Sound = new(() =>
        JoustRomBuild.AssembleSound(JoustRomBuild.FindSourceDirectory(AppContext.BaseDirectory)!));

    private static (SoundBoard Emulated, SoundBoard Translated) Pair()
    {
        var emulated = new SoundBoard(Sound.Value.Memory);
        var translated = new SoundBoard(Sound.Value.Memory);
        Assert.True(SoundTranslation.Attach(translated, Sound.Value.Memory), "the sound ROM changed: run joust-tools translate");
        emulated.Reset();
        translated.Reset();
        return (emulated, translated);
    }

    /// <summary>Runs both boards for the same stretch and requires the same samples, registers and RAM.</summary>
    private static void Same(SoundBoard a, SoundBoard b, long cycles, string what)
    {
        var sa = new float[4096];
        var sb = new float[4096];
        int na = a.Run(cycles, 48000, sa), nb = b.Run(cycles, 48000, sb);
        Assert.Equal(na, nb);
        for (int i = 0; i < na; i++)
            Assert.True(sa[i] == sb[i], $"{what}: sample {i} differs ({sa[i]} vs {sb[i]})");
        var (ca, cb) = (a.Cpu, b.Cpu);
        Assert.True((ca.A, ca.B, ca.CC, ca.X, ca.SP, ca.PC, ca.Cycles, ca.Waiting) == (cb.A, cb.B, cb.CC, cb.X, cb.SP, cb.PC, cb.Cycles, cb.Waiting),
            $"{what}: registers differ");
        for (ushort r = 0; r < 0x80; r++)
            Assert.True(a.Read(r) == b.Read(r), $"{what}: RAM ${r:X2} differs");
    }

    [Fact]
    public void EverySoundIsIdenticalSampleForSample()
    {
        var (emulated, translated) = Pair();
        Same(emulated, translated, 20000, "boot");
        for (byte cmd = 0; cmd < 0x3F; cmd++)
        {
            foreach (var b in new[] { emulated, translated }) b.Command(cmd);
            Same(emulated, translated, 300, $"command ${cmd:X2} taken");
            foreach (var b in new[] { emulated, translated }) b.Command(0x3F);
            // About 2.5 s of the sound, in frame-sized slices as the game runs it.
            for (int slice = 0; slice < 150; slice++)
                Same(emulated, translated, 14915, $"command ${cmd:X2}, slice {slice}");
        }
    }

    [Fact]
    public void SoundsInterruptingEachOtherStayIdentical()
    {
        // Commands arriving while another sound plays, at uneven moments.
        var (emulated, translated) = Pair();
        Same(emulated, translated, 20000, "boot");
        var rng = new Random(11);
        for (int i = 0; i < 400; i++)
        {
            byte cmd = (byte)rng.Next(0x3F);
            foreach (var b in new[] { emulated, translated }) b.Command(cmd);
            Same(emulated, translated, rng.Next(50, 400), $"command {i} (${cmd:X2})");
            foreach (var b in new[] { emulated, translated }) b.Command(0x3F);
            Same(emulated, translated, rng.Next(100, 30000), $"after command {i}");
        }
    }
}
