using System.Security.Cryptography;
using Joust.Assembler;
using Joust.Emulation;

namespace Joust.Tests;

/// <summary>
/// Plays a scripted game on the reference machine and fingerprints the full machine state.
/// The emulated original is the oracle for the native C# port: as routines are rewritten in
/// C#, this replay (and per-frame RAM comparison) must keep producing identical state.
/// </summary>
public class GoldenReplayTests
{
    private static readonly Lazy<AssemblyResult> Program = new(() =>
    {
        string dir = JoustRomBuild.FindSourceDirectory(AppContext.BaseDirectory)
            ?? throw new DirectoryNotFoundException("joust-main-asm not found");
        return JoustRomBuild.Assemble(dir);
    });

    /// <summary>
    /// A machine as an operator would find it: first power-on restores factory settings into
    /// blank CMOS, then the cabinet is power-cycled.
    /// </summary>
    public static JoustMachine NewMachine()
    {
        var m = new JoustMachine(Program.Value.Memory);
        m.Reset();
        for (int f = 0; f < 1300; f++)
            m.RunFrame();
        m.Reset();
        return m;
    }

    /// <summary>Cold boot, coin up, start a 1-player game, flap and steer for ~35 seconds.</summary>
    public static void PlayScript(JoustMachine m, int frames)
    {
        for (int f = 0; f < frames; f++)
        {
            var i = m.Inputs;
            bool Hold(int start, int len = 4) => f >= start && f < start + len;
            i.CoinLeft = Hold(1200);
            i.Start1 = Hold(1300);
            i.P1Flap = f >= 1450 && (f - 1450) % 10 < 4 && f < 2050;
            i.P1Right = f >= 1700 && f < 1720;
            i.P1Left = f >= 1900 && f < 1960;
            m.RunFrame();
        }
    }

    public static string Fingerprint(JoustMachine m)
    {
        using var sha = SHA256.Create();
        sha.TransformBlock(m.Ram, 0, m.Ram.Length, null, 0);
        sha.TransformBlock(m.Palette, 0, m.Palette.Length, null, 0);
        sha.TransformFinalBlock(m.Cmos, 0, m.Cmos.Length);
        return Convert.ToHexString(sha.Hash!)[..16];
    }

    [Fact]
    public void ReplayIsDeterministic()
    {
        var a = NewMachine();
        var b = NewMachine();
        PlayScript(a, 2200);
        PlayScript(b, 2200);
        Assert.Equal(Fingerprint(a), Fingerprint(b));
    }

    [Fact]
    public void ScriptedGameReachesWaveOneWithEnemies()
    {
        var m = NewMachine();
        PlayScript(m, 2200);
        var reader = new GameStateReader(m, JoustLayout.FromSymbols(Program.Value.Symbols));
        var objects = reader.ReadObjects();
        Assert.Contains(objects, o => o.Kind == ObjectKind.Player);
        Assert.Contains(objects, o => o.Kind == ObjectKind.Enemy);
    }

    /// <summary>
    /// Regression pin: the machine state after the scripted game. Any change to the CPU,
    /// board timing or (later) a native routine that alters behaviour changes this value.
    /// Update it only for a deliberate, explained emulation fix.
    /// </summary>
    [Fact]
    public void ScriptedGameStateMatchesPinnedFingerprint()
    {
        var m = NewMachine();
        PlayScript(m, 2200);
        Assert.Equal(PinnedFingerprint, Fingerprint(m));
    }

    private const string PinnedFingerprint = "3EAAB11C9453C78B";
}
