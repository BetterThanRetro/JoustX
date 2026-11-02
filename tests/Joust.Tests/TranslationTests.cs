using Joust.Assembler;
using Joust.Emulation;
using Joust.Port;
using Joust.Port.Translated;

namespace Joust.Tests;

/// <summary>The whole program translated to C# (joust-tools translate), checked against the original.</summary>
public class TranslationTests
{
    private static readonly Lazy<AssemblyResult> Program = new(() =>
        JoustRomBuild.Assemble(JoustRomBuild.FindSourceDirectory(AppContext.BaseDirectory)
            ?? throw new DirectoryNotFoundException("joust-main-asm not found")));

    private static readonly Lazy<byte[]> FactoryCmos = new(() =>
    {
        var m = new JoustMachine(Program.Value.Memory);
        m.Reset();
        for (int f = 0; f < 1300; f++) m.RunFrame();
        return (byte[])m.Cmos.Clone();
    });

    private static JoustMachine Boot(bool blankCmos = false)
    {
        var m = new JoustMachine(Program.Value.Memory);
        if (!blankCmos) FactoryCmos.Value.CopyTo(m.Cmos, 0);
        m.Reset();
        return m;
    }

    [Fact]
    public void TheTranslationIsOfThisProgram()
    {
        var m = Boot();
        Assert.True(Translation.Install(m), "the ROM changed since the translation was made: run joust-tools translate");
        // Every instruction of the listing that is in ROM is translated (or knowingly left to the CPU).
        int inRom = Program.Value.Instructions.Keys.Count(a => a is < 0x9000 or >= 0xD000);
        Assert.True(Translation.Instructions > inRom * 99 / 100, $"{Translation.Instructions} of {inRom} instructions translated");
    }

    [Fact]
    public void TheTranslationAloneMatchesTheOriginalCallByCall()
    {
        var m = Boot();
        Assert.True(Translation.Install(m));
        var v = new NativeVerifier(m);
        var p = new RandomPlayer(4);
        for (int f = 0; f < 4000; f++)
        {
            p.Apply(m.Inputs, f);
            m.RunFrame();
        }
        Assert.True(v.TotalMismatches == 0, string.Join("\n", v.Reports));
        Assert.True(v.Stats.Count > 300, $"only {v.Stats.Count} translated routines ran");
    }

    /// <summary>
    /// From blank CMOS, as a new board: the power-on self-test and the factory restore (which
    /// then waits for a power cycle, with interrupts off), the power cycle, then attract mode,
    /// coins and a game. Both machines get the same inputs; compared every frame.
    /// </summary>
    private static void PowerOnInLockstep(JoustMachine original, JoustMachine native, int seed, int frames)
    {
        var p1 = new RandomPlayer(seed);
        var p2 = new RandomPlayer(seed);
        for (int f = 0; f < frames; f++)
        {
            if (f == 1300)
            {
                original.Reset();
                native.Reset();
            }
            p1.Apply(original.Inputs, f);
            p2.Apply(native.Inputs, f);
            original.RunFrame();
            native.RunFrame();
            var diff = Lockstep.Compare(original, native);
            Assert.True(diff == null, $"frame {f}: {diff}");
        }
    }

    [Fact]
    public void TheTranslationAloneStaysInLockstepFromPowerOn()
    {
        var original = Boot(blankCmos: true);
        var native = Boot(blankCmos: true);
        Assert.True(Translation.Install(native));
        PowerOnInLockstep(original, native, seed: 6, frames: 6000);
        Assert.True(native.NativeCycles > native.Cpu.Cycles * 95 / 100,
            $"translated code ran {100.0 * native.NativeCycles / native.Cpu.Cycles:F1}% of the time");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WithNoCpuTheGameIsIdenticalFromPowerOn(bool handWritten)
    {
        var original = Boot(blankCmos: true);
        var native = Boot(blankCmos: true);
        if (handWritten) NativeRoutines.InstallAll(native, Program.Value.Symbols);
        else Assert.True(Translation.Install(native));
        var runtime = NoCpuRuntime.Attach(native);
        PowerOnInLockstep(original, native, seed: 7, frames: 6000);
        Assert.Equal(0, native.CpuSteps);
        Assert.True(runtime.Interrupts > 4 * 4000, $"only {runtime.Interrupts} interrupts");
    }

    [Fact]
    public void ChasingEggsMatchesTheOriginalCallByCallAndWithNoCpu()
    {
        // The egg chaser collects eggs, which the random player rarely does.
        var layout = JoustLayout.FromSymbols(Program.Value.Symbols);
        var verified = Boot();
        NativeRoutines.InstallAll(verified, Program.Value.Symbols);
        var v = new NativeVerifier(verified);
        var chaser = new EggChaser(new GameStateReader(verified, layout), seed: 3);
        for (int f = 0; f < 8000; f++)
        {
            chaser.Apply(verified.Inputs, f);
            verified.RunFrame();
        }
        Assert.True(v.TotalMismatches == 0, string.Join("\n", v.Reports));
        Assert.True(v.Stats.TryGetValue("EGGSC2", out var collected) && collected.Calls > 10,
            $"eggs collected: {(collected?.Calls ?? 0)} (went for eggs in {chaser.EggFrames} frames)");

        var original = Boot();
        var native = Boot();
        NativeRoutines.InstallAll(native, Program.Value.Symbols);
        NoCpuRuntime.Attach(native);
        var p1 = new EggChaser(new GameStateReader(original, layout), seed: 3);
        var p2 = new EggChaser(new GameStateReader(native, layout), seed: 3);
        for (int f = 0; f < 8000; f++)
        {
            p1.Apply(original.Inputs, f);
            p2.Apply(native.Inputs, f);
            original.RunFrame();
            native.RunFrame();
            var diff = Lockstep.Compare(original, native);
            Assert.True(diff == null, $"frame {f}: {diff}");
        }
        Assert.Equal(0, native.CpuSteps);
    }

    [Fact]
    public void WithNoCpuTheOperatorMenusAreIdentical()
    {
        // Advance on the coin door every 6 s: the hardware tests, bookkeeping and adjustments, stepped
        // through, backwards in alternate minutes, with switches pressed along the way (the census's script).
        var original = Boot();
        var native = Boot();
        NativeRoutines.InstallAll(native, Program.Value.Symbols);
        NoCpuRuntime.Attach(native);
        for (int f = 0; f < 9000; f++)
        {
            foreach (var m in new[] { original, native })
            {
                m.Inputs.Advance = f >= 600 && f % 360 is >= 0 and < 6;
                m.Inputs.AutoUp = (f / 3600) % 2 == 0;
                m.Inputs.HighScoreReset = f % 5000 is >= 2000 and < 2200;
                m.Inputs.P1Flap = f % 90 < 3;
                m.Inputs.P1Right = f % 400 < 30;
                m.Inputs.Start1 = f % 777 < 4;
                m.RunFrame();
            }
            var diff = Lockstep.Compare(original, native);
            Assert.True(diff == null, $"frame {f}: {diff}");
        }
        Assert.Equal(0, native.CpuSteps);
    }
}
