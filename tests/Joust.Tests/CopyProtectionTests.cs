using Joust.Assembler;
using Joust.Emulation;
using Joust.Port;
using Joust.Port.Routines;

namespace Joust.Tests;

/// <summary>
/// The original's copy-protection checks (JZAP, LZAP, KZAP) quietly corrupt the game when the
/// program has been changed. The port keeps their sums and timing but stops with a clear error.
/// </summary>
public class CopyProtectionTests
{
    private static readonly Lazy<AssemblyResult> Program = new(() =>
        JoustRomBuild.Assemble(JoustRomBuild.FindSourceDirectory(AppContext.BaseDirectory)
            ?? throw new DirectoryNotFoundException("joust-main-asm not found")));

    [Fact]
    public void TrapsSitOnTheSabotageInstructions()
    {
        var s = Program.Value.Symbols;
        var mem = Program.Value.Memory;
        var kzapNap = s["KZAP.1S"] + 5;
        Assert.Equal(0x0C, mem[s["JZAP.2S"] - 2]);                 // INC <LXPOS2+1 (direct page)
        Assert.Equal(s["LXPOS2"] + 1 & 0xFF, mem[s["JZAP.2S"] - 1]);
        Assert.Equal(0xAE, mem[s["LZAP.2S"] - 6]);                 // LDX [PFREE]
        Assert.Equal(0x9F, mem[s["LZAP.2S"] - 5]);
        var natives = NativeRoutines.Create().Where(r => r.Symbol.EndsWith("@sabotage")).ToList();
        Assert.Equal(3, natives.Count);
        var kzap = natives.Single(r => r.Symbol == "KZAP@sabotage").ResolveAddress(s);
        Assert.Equal(0xBD, mem[kzap]);                              // JSR RADDR (extended)
        Assert.Equal(s["RADDR"], (mem[kzap + 1] << 8) | mem[kzap + 2]);
        Assert.InRange(kzap, kzapNap, kzapNap + 24);
    }

    [Fact]
    public void AChangedProgramStopsTheGameInsteadOfCorruptingIt()
    {
        var factory = new JoustMachine(Program.Value.Memory);
        factory.Reset();
        for (int f = 0; f < 1300; f++) factory.RunFrame();
        var m = new JoustMachine(Program.Value.Memory);
        factory.Cmos.CopyTo(m.Cmos, 0);
        NativeRoutines.InstallAll(m, Program.Value.Symbols);
        m.Reset();
        for (int f = 0; f < 800; f++) m.RunFrame();                // past the power-on ROM test

        const ushort doNothing = 0x663C;                           // "A DO NOTHING" byte in ROM 7
        Assert.Equal(0x20, m.ReadRom(doNothing));
        m.PatchRom(doNothing, 0x21);

        var e = Assert.Throws<RomTamperedException>(() =>
        {
            for (int f = 800; f < 1400; f++)
            {
                m.Inputs.CoinLeft = f is >= 900 and < 904;
                m.Inputs.Start1 = f is >= 960 and < 964;           // JZAP runs as the players are created
                m.RunFrame();
            }
        });
        Assert.Equal("JZAP", e.Check);
    }
}
