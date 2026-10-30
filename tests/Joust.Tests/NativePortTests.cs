using Joust.Assembler;
using Joust.Emulation;
using Joust.Port;
using Joust.Port.Routines;

namespace Joust.Tests;

public class NativePortTests
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

    private static JoustMachine Boot()
    {
        var m = new JoustMachine(Program.Value.Memory);
        FactoryCmos.Value.CopyTo(m.Cmos, 0);
        m.Reset();
        return m;
    }

    private static void Play(JoustMachine m, RandomPlayer p, int frames)
    {
        for (int f = 0; f < frames; f++)
        {
            p.Apply(m.Inputs, f);
            m.RunFrame();
        }
    }

    private static NativeVerifier VerifiedRun(IEnumerable<NativeRoutine> routines, int seed, int frames)
    {
        var m = Boot();
        foreach (var r in routines)
        {
            r.Bind(Program.Value.Symbols);
            m.InstallNative(r, r.ResolveAddress(Program.Value.Symbols));
        }
        var v = new NativeVerifier(m);
        Play(m, new RandomPlayer(seed), frames);
        return v;
    }

    [Fact]
    public void EveryNativeCallMatchesTheOriginal()
    {
        var v = VerifiedRun(NativeRoutines.Create(), seed: 1, frames: 8000);
        Assert.True(v.TotalMismatches == 0, string.Join("\n", v.Reports));
        foreach (var name in new[]
                 {
                     "RANDSYS", "SND", "DCOLOR",
                     "CHROUT", "CHR35", "PHROUT",
                     "WRAPX", "ADDGRA", "ADDGRX", "ADDFLP", "CKGND", "BCKCOL",
                     "RUNR", "SKIDR", "STANDR", "SRCADA", "SRCADH",
                     "WPLYR", "CPLYR", "CL1CLS", "WR1CLS", "HITEM",
                     "NAPTPC", "EXEC", "DTBOVR", "CRPROC", "CUPROC", "SUCIDE",
                     "SCODSP@idle", "SCODSP@digit", "BCDMSN", "BCDLSN",
                     "PLYRLP@nap", "PLYRS2", "UPDNO2@state", "FLAPLP@nap", "FLAPLP@air",
                     "FLIPLP@nap", "FLIPLP@air", "FLIPST@grav", "GOTFIT@ground", "AIROVR@joy", "P1JOY",
                     "PLYCOL@nap", "OPPCOL.25S", "OSTWIN@dead", "OSTXT3@win",
                 })
            Assert.True(v.Stats.TryGetValue(name, out var s) && s.Calls > 0, $"{name} was never exercised");
    }

    [Fact]
    public void NativeMachineStaysInLockstepWithOriginal()
    {
        var original = Boot();
        var native = Boot();
        NativeRoutines.InstallAll(native, Program.Value.Symbols);
        var p1 = new RandomPlayer(2);
        var p2 = new RandomPlayer(2);
        for (int f = 0; f < 8000; f++)
        {
            p1.Apply(original.Inputs, f);
            p2.Apply(native.Inputs, f);
            original.RunFrame();
            native.RunFrame();
            var diff = Lockstep.Compare(original, native);
            Assert.True(diff == null, $"frame {f}: {diff}");
        }
        Assert.True(native.NativeCalls > 1000);
    }

    /// <summary>Wraps a correct routine and corrupts its result, to prove the checks catch it.</summary>
    private sealed class Mutant(NativeRoutine inner, Action<NativeContext> corrupt) : NativeRoutine
    {
        public override string Symbol => inner.Symbol;
        public override int MaxCycles => inner.MaxCycles;
        public override bool EndsWithRts => inner.EndsWithRts;
        public override void Bind(IReadOnlyDictionary<string, long> s) => inner.Bind(s);
        public override ushort ResolveAddress(IReadOnlyDictionary<string, long> s) => inner.ResolveAddress(s);

        public override bool Execute(NativeContext c)
        {
            bool handled = inner.Execute(c);
            if (handled) corrupt(c);
            return handled;
        }
    }

    [Fact]
    public void VerifierCatchesOneCycleError()
    {
        var v = VerifiedRun([new Mutant(new RandomRoutine(), c => c.Cpu.Cycles++)], seed: 1, frames: 3000);
        Assert.True(v.TotalMismatches > 0);
        Assert.Contains("cycles", v.Reports[0]);
    }

    [Fact]
    public void VerifierCatchesErrorsInJumpStyleRoutines()
    {
        // A scheduler slice one cycle long makes the original stop somewhere else.
        var nap = NativeRoutines.Create().Single(r => r.Symbol == "NAPTPC");
        var v = VerifiedRun([new Mutant(nap, c => c.Cpu.Cycles++)], seed: 1, frames: 1500);
        Assert.True(v.TotalMismatches > 0);
    }

    [Fact]
    public void VerifierCatchesWrongFlagAndWrongMemory()
    {
        var flag = VerifiedRun([new Mutant(new RandomRoutine(), c => c.Cpu.CC ^= Cpu6809.FlagV)], seed: 1, frames: 3000);
        Assert.Contains("CC", flag.Reports[0]);

        ushort random = (ushort)Program.Value.Symbols["RANDOM"];
        var mem = VerifiedRun([new Mutant(new RandomRoutine(), c => c.Write(random + 1, (byte)(c.Read(random + 1) ^ 0x10)))], seed: 1, frames: 3000);
        Assert.Contains($"[{random + 1:X4}]", mem.Reports[0]);
    }

    [Fact]
    public void LockstepCatchesDivergence()
    {
        var original = Boot();
        var native = Boot();
        var broken = new Mutant(new RandomRoutine(), c => c.Cpu.A ^= 1);
        broken.Bind(Program.Value.Symbols);
        native.InstallNative(broken, (ushort)Program.Value.Symbols["RANDSYS"]);
        var p1 = new RandomPlayer(3);
        var p2 = new RandomPlayer(3);
        string? diff = null;
        for (int f = 0; f < 6000 && diff == null; f++)
        {
            p1.Apply(original.Inputs, f);
            p2.Apply(native.Inputs, f);
            original.RunFrame();
            native.RunFrame();
            diff = Lockstep.Compare(original, native);
        }
        Assert.NotNull(diff);
    }

    [Fact]
    public void PixelCollisionMatchesWhenCalledFromOriginalCode()
    {
        // With BCKCOL native, BCKPIX is mostly reached internally; check it on its own too.
        var v = VerifiedRun(NativeRoutines.Create().Where(r => r.Symbol == "BCKPIX"), seed: 2, frames: 8000);
        Assert.True(v.TotalMismatches == 0, string.Join("\n", v.Reports));
        Assert.True(v.Stats["BCKPIX"].Calls > 100);
    }

    [Fact]
    public void BirdResumePointsFollowTheExpectedCalls()
    {
        var s = Program.Value.Symbols;
        var rom = Program.Value.Memory;
        var bird = new Engine(new Symbols(s)).Bird;
        ushort Word(int a) => (ushort)((rom[a] << 8) | rom[a + 1]);

        void AfterJsrExtended(ushort ret, string target)
        {
            Assert.Equal(0xBD, rom[ret - 3]);
            Assert.Equal((ushort)s[target], Word(ret - 2));
        }

        void AfterJsrIndirectU(ushort ret, string offset)
        {
            Assert.Equal(0xAD, rom[ret - 3]);                    // JSR indexed
            Assert.Equal(0xD8, rom[ret - 2]);                    // [n8,U]
            Assert.Equal((byte)s[offset], rom[ret - 1]);
        }

        var a = bird.Addresses;
        AfterJsrExtended(a[BirdProcess.L.PlyrNap], "VNAPTPC");
        AfterJsrExtended(a[BirdProcess.L.FlapNap], "VNAPTPC");
        AfterJsrExtended(a[BirdProcess.L.FlipNap], "VNAPTPC");
        AfterJsrExtended(a[BirdProcess.L.FlapAir], "AIROVR");
        AfterJsrExtended(a[BirdProcess.L.FlipAir], "AIROVR");
        AfterJsrExtended(a[BirdProcess.L.WingFkGround], "CKGND");
        AfterJsrExtended(a[BirdProcess.L.GotFitGround], "CKGND");
        AfterJsrIndirectU(a[BirdProcess.L.FlapGrav], "PADGRA");
        AfterJsrIndirectU(a[BirdProcess.L.FlipGrav], "PADGRA");
        AfterJsrIndirectU(bird.AirJoyReturn, "PJOY");
        AfterJsrIndirectU(a[BirdProcess.L.PlyrS2], "PJOY");
        ushort state = a[BirdProcess.L.Updno2After];
        Assert.Equal(0xAD, rom[state - 2]);                      // JSR [,X]
        Assert.Equal(0x94, rom[state - 1]);
        Assert.Equal((ushort)s["FLAST2"], (ushort)(s["GOFLAP"] + 10)); // GOFLAP's gravity call returns to FLAST2
    }

    [Fact]
    public void JoustAndSweepResumePointsFollowTheExpectedCalls()
    {
        var s = Program.Value.Symbols;
        var rom = Program.Value.Memory;
        var engine = new Engine(new Symbols(s));
        ushort Word(int a) => (ushort)((rom[a] << 8) | rom[a + 1]);

        void AfterJsrIndirectY(ushort ret, string offset)
        {
            Assert.Equal(0xAD, rom[ret - 3]);                    // JSR indexed
            Assert.Equal(0xB8, rom[ret - 2]);                    // [n8,Y]
            Assert.Equal((byte)s[offset], rom[ret - 1]);
        }

        void AfterBsrHitem(ushort ret)
        {
            Assert.Equal(0x8D, rom[ret - 2]);                    // BSR
            Assert.Equal((ushort)s["HITEM"], (ushort)(ret + (sbyte)rom[ret - 1]));
        }

        AfterJsrIndirectY(engine.Combat.OstwinAfterDeath, "DDEAD");
        AfterJsrIndirectY(engine.Combat.OstwinAfterScore, "DVALUR");
        foreach (var ret in new[] { engine.Combat.ULosesAfterWin, engine.Combat.XLosesAfterWin })
        {
            Assert.Equal(0xBD, rom[ret - 3]);
            Assert.Equal((ushort)s["OSTWIN"], Word(ret - 2));
        }

        var a = engine.Sweeps.Addresses;
        AfterBsrHitem(a[Sweeps.L.Ply35After]);
        AfterBsrHitem(a[Sweeps.L.Ply45After]);
        AfterBsrHitem(a[Sweeps.L.OppAfter]);
        foreach (var afterNap in new[] { a[Sweeps.L.PlyAfterNap], a[Sweeps.L.OppOuter] })
        {
            Assert.Equal(0xBD, rom[afterNap - 3]);               // PCNAP 1: JSR VNAPTPC
            Assert.Equal((ushort)s["VNAPTPC"], Word(afterNap - 2));
        }
    }

    [Fact]
    public void ScoreRoutinesMatchWhenCalledFromOriginalCode()
    {
        // The joust calls these inline now; hook just them so original code calls them.
        var v = VerifiedRun(NativeRoutines.Create().Where(r => r.Symbol is "SCRHUN" or "SCRTEN"), seed: 1, frames: 8000);
        Assert.True(v.TotalMismatches == 0, string.Join("\n", v.Reports));
        Assert.True(v.Stats["SCRHUN"].Calls > 0 && v.Stats["SCRTEN"].Calls > 0, "score routines not exercised");
    }

    [Fact]
    public void EggPickupAndKillsMatchWhenCalledFromOriginalCode()
    {
        // With the sweeps native these run inline; hook just them so the original HITEM calls them.
        var v = VerifiedRun(NativeRoutines.Create().Where(r => r.Symbol is "EGGSCR" or "OSTWIN"), seed: 1, frames: 40000);
        Assert.True(v.TotalMismatches == 0, string.Join("\n", v.Reports));
        Assert.True(v.Stats["EGGSCR"].Calls > 0, "no egg was collected");
        Assert.True(v.Stats["OSTWIN"].Calls > 10, "too few kills");
    }

    [Fact]
    public void JoustBounceRoutinesMatch()
    {
        // Contacts are rarer than flight, so run longer with just the bounce routines hooked
        // (called from the original joust resolution, OSTHIT).
        string[] bounces = ["OSTUTP", "OSTXTP", "OSTBMP", "PTEBRD", "OSTLR", "OSTXUP", "OSTXDN"];
        var v = VerifiedRun(NativeRoutines.Create().Where(r => bounces.Contains(r.Symbol)), seed: 4, frames: 40000);
        Assert.True(v.TotalMismatches == 0, string.Join("\n", v.Reports));
        foreach (var name in new[] { "OSTUTP", "OSTXTP", "OSTBMP", "PTEBRD" })
            Assert.True(v.Stats.TryGetValue(name, out var s) && s.Calls > 0, $"{name} was never exercised");
    }

    [Fact]
    public void ScoreHandsExtraMountBackToOriginalWithoutSideEffects()
    {
        var s = Program.Value.Symbols;
        var m = new JoustMachine(Program.Value.Memory);
        var routine = new AddScoreRoutine(tens: false);
        routine.Bind(s);

        const ushort decision = 0x9C00, area = 0x9D00;
        int dscore = (int)s["DSCORE"];
        ushort levpas = (ushort)s["LEVPAS"];
        m.Ram[decision + dscore] = area >> 8;
        m.Ram[decision + dscore + 1] = area & 0xFF;
        m.Ram[levpas] = 0x00; m.Ram[levpas + 1] = 0x20;       // extra man every 20,000
        byte[] score = [0x00, 0x01, 0x99, 0x00, 0x00, 0, 0, 0x00, 0x02, 0x00];
        score.CopyTo(m.Ram, area);                             // 19,900; next level at 20,000
        m.Cpu.DP = 0xA0;
        m.Cpu.X = decision;
        m.Cpu.A = 0x01;                                        // +100 points
        m.Cpu.S = 0xBF00;
        var before = m.Cpu.Save();

        var journal = new List<(ushort, byte)>();
        m.WriteJournal = journal;
        bool handled = routine.Execute(m.NativeContext);
        m.WriteJournal = null;

        Assert.False(handled);
        Assert.Empty(journal);
        Assert.Equal(before, m.Cpu.Save());
    }
}
