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

    private static NativeVerifier VerifiedRun(IEnumerable<NativeRoutine> routines, int seed, int frames, bool twoPlayers = false)
    {
        var m = Boot();
        foreach (var r in routines)
        {
            r.Bind(Program.Value.Symbols);
            m.InstallNative(r, r.ResolveAddress(Program.Value.Symbols));
        }
        var v = new NativeVerifier(m);
        Play(m, new RandomPlayer(seed, twoPlayers), frames);
        return v;
    }

    [Fact]
    public void EveryNativeCallMatchesTheOriginal()
    {
        var v = VerifiedRun(NativeRoutines.Create(), seed: 1, frames: 8000);
        Assert.True(v.TotalMismatches == 0, string.Join("\n", v.Reports));
        foreach (var name in new[]
                 {
                     "IRQ", "EXECST", "EXEINT", "EMYOK@nap", "JZAP.1S", "KZAP.1S@nap", "RANDSYS", "SND", "DCOLOR",
                     "CHROUT", "CHR35", "PHROUT",
                     "WRAPX", "ADDGRA", "ADDGRX", "ADDFLP", "CKGND", "BCKCOL",
                     "RUNR", "SKIDR", "STANDR", "SRCADA", "SRCADH",
                     "WPLYR", "CPLYR", "CL1CLS", "WR1CLS", "HITEM",
                     "NAPTPC", "EXEC", "DTBOVR", "CRPROC", "CUPROC", "SUCIDE",
                     "SCODSP@idle", "SCODSP@digit", "BCDMSN", "BCDLSN",
                     "PLYRLP@nap", "PLYRS2", "UPDNO2@state", "FLAPLP@nap", "FLAPLP@air",
                     "FLIPLP@nap", "FLIPLP@air", "FLIPST@grav", "GOTFIT@ground", "AIROVR@joy",
                     "PLYCOL@nap", "OPPCOL.25S",
                     "ADDEGG", "WEGG", "CEGG", "EGGBKG", "CLIPER", "WEGPAR",
                     "EGGLPA@nap", "EGGLN2@nap",
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
    public void DeathRoutinesMatchWhenCalledFromOriginalCode()
    {
        // OSTWIN calls them inline now; hook just them so the original OSTWIN calls them.
        var v = VerifiedRun(NativeRoutines.Create().Where(r => r.Symbol.StartsWith("DEATH")), seed: 1, frames: 40000, twoPlayers: true);
        Assert.True(v.TotalMismatches == 0, string.Join("\n", v.Reports));
        foreach (var name in new[] { "DEATH1", "DEATH2", "DEATH3" })
            Assert.True(v.Stats.TryGetValue(name, out var s) && s.Calls > 0, $"{name} was never exercised");
    }

    [Fact]
    public void TwoPlayerGameStaysInLockstep()
    {
        var original = Boot();
        var native = Boot();
        NativeRoutines.InstallAll(native, Program.Value.Symbols);
        var p1 = new RandomPlayer(5, twoPlayers: true);
        var p2 = new RandomPlayer(5, twoPlayers: true);
        for (int f = 0; f < 8000; f++)
        {
            p1.Apply(original.Inputs, f);
            p2.Apply(native.Inputs, f);
            original.RunFrame();
            native.RunFrame();
            var diff = Lockstep.Compare(original, native);
            Assert.True(diff == null, $"frame {f}: {diff}");
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
    public void EggResumePointsFollowNaps()
    {
        var s = Program.Value.Symbols;
        var rom = Program.Value.Memory;
        var eggs = new Engine(new Symbols(s)).EggProcess.Addresses;
        ushort nap = (ushort)s["VNAPTPC"];
        foreach (var l in new[]
                 {
                     EggProcess.L.FallNap, EggProcess.L.WaitNap, EggProcess.L.NextFrame, EggProcess.L.Hatched,
                     EggProcess.L.IdleNap, EggProcess.L.HitNap, EggProcess.L.DelayNap, EggProcess.L.Show,
                     EggProcess.L.ScoreNap,
                 })
        {
            ushort ret = eggs[l];
            Assert.True(rom[ret - 3] == 0xBD && ((rom[ret - 2] << 8) | rom[ret - 1]) == nap, $"{l} at {ret:X4} does not follow JSR VNAPTPC");
        }
    }

    [Fact]
    public void EggPickupAndKillsMatchWhenCalledFromOriginalCode()
    {
        // With the sweeps native these run inline; hook just them so the original HITEM calls them.
        var v = VerifiedRun(NativeRoutines.Create().Where(r => r.Symbol is "EGGSCR" or "OSTWIN"), seed: 1, frames: 40000);
        Assert.True(v.TotalMismatches == 0, string.Join("\n", v.Reports));
        Assert.True(v.Stats["EGGSCR"].Calls > 0, "no egg was collected");
        Assert.True(v.Stats["OSTWIN"].Calls > 10, "too few kills");

        // The joust's resume points, entered after the original OSTWIN and death routines.
        string[] resume = ["OSTWIN@dead", "OSTWIN@score", "OSTXT3@win", "OSTPYP@win"];
        var r = VerifiedRun(NativeRoutines.Create().Where(n => resume.Contains(n.Symbol)), seed: 1, frames: 40000);
        Assert.True(r.TotalMismatches == 0, string.Join("\n", r.Reports));
        foreach (var name in new[] { "OSTWIN@dead", "OSTXT3@win", "OSTPYP@win" })
            Assert.True(r.Stats.TryGetValue(name, out var s) && s.Calls > 0, $"{name} was never exercised");
    }

    [Fact]
    public void EnemyAiMatchesWhenCalledFromOriginalCode()
    {
        // The bird process runs the AI inline; hook just the AI so the original JSR [PJOY,U]
        // calls every routine (seed 3 reaches the shadow lords).
        var v = VerifiedRun(NativeRoutines.Create().Where(r => EnemyAi.Labels.Contains(r.Symbol)), seed: 3, frames: 40000);
        Assert.True(v.TotalMismatches == 0, string.Join("\n", v.Reports));
        foreach (var name in new[]
                 {
                     "LINET", "LNTOFP", "BODN1", "BOUP1", "BOUP2", "BOLEV1", "BOLEV2",
                     "B2DN1", "B2UP1", "B2UP2", "B2UP3A", "B2LEV1", "B2AV",
                     "SHADOW", "SHDN2", "SHLEP1", "SHLEV1", "SHUP3A",
                     "PTERO", "PTEATK", "PTEUP2", "PTEOFF", "SEEKE", "SEEKF2", "MOUNLP", "AUTOFF",
                 })
            Assert.True(v.Stats.TryGetValue(name, out var s) && s.Calls > 0, $"{name} was never exercised");
    }

    /// <summary>
    /// A verified run of the <see cref="TrollScenario"/>: the troll and a burnt bridge from the
    /// first wave, and players that end up in the lava (normally waves 3-4 and rare).
    /// </summary>
    private static NativeVerifier TrollRun(IEnumerable<NativeRoutine> routines, int seed, int frames)
    {
        var m = Boot();
        foreach (var r in routines)
        {
            r.Bind(Program.Value.Symbols);
            m.InstallNative(r, r.ResolveAddress(Program.Value.Symbols));
        }
        var v = new NativeVerifier(m);
        var p = new RandomPlayer(seed, flapPercent: TrollScenario.FlapPercent);
        var scenario = new TrollScenario(Program.Value.Symbols);
        for (int f = 0; f < frames; f++)
        {
            p.Apply(m.Inputs, f);
            scenario.Apply(m);
            m.RunFrame();
        }
        return v;
    }

    private static readonly string[] TransporterLabels = Enum.GetValues<Transporter.L>().Select(TxJumpRoutine.TransporterName).ToArray();

    [Fact]
    public void TransportersAndTheLavaDeathMatchTheOriginal()
    {
        // Hook just these so the original scheduler and bird process enter every label.
        var v = TrollRun(NativeRoutines.Create().Where(r => TransporterLabels.Contains(r.Symbol)), seed: 1, frames: 30000);
        Assert.True(v.TotalMismatches == 0, string.Join("\n", v.Reports));
        foreach (var name in new[]
                 {
                     "CREP1", "CREP1@lives", "CREEM", "CRELP@nap", "TREFF2@nap", "TREFF2.40S", "PLAYR", "PLAYR@nap",
                     "PLAYR@lives", "PLYLI2", "ADGFLR", "ADGLAV@nap", "ADGDED@nap",
                 })
            Assert.True(v.Stats.TryGetValue(name, out var s) && s.Calls > s.Declined, $"{name} never ran natively");
    }

    [Fact]
    public void EggWaveMatchesTheOriginal()
    {
        // Every wave an egg wave (normally wave 5 and later); only the wave labels hooked.
        string[] labels = Enum.GetValues<WaveSetup.L>().Select(TxJumpRoutine.WaveName).ToArray();
        var m = Boot();
        foreach (var r in NativeRoutines.Create().Where(r => labels.Contains(r.Symbol)))
        {
            r.Bind(Program.Value.Symbols);
            m.InstallNative(r, r.ResolveAddress(Program.Value.Symbols));
        }
        var v = new NativeVerifier(m);
        var p = new RandomPlayer(1);
        var eggs = new EggScenario(Program.Value.Memory, Program.Value.Symbols);
        for (int f = 0; f < 12000; f++)
        {
            p.Apply(m.Inputs, f);
            eggs.Apply(m);
            m.RunFrame();
        }
        Assert.True(v.TotalMismatches == 0, string.Join("\n", v.Reports));
        Assert.True(v.Stats.TryGetValue("CREGG@nap", out var s) && s.Calls > s.Declined, "no egg was created natively");
        Assert.True(v.Stats.ContainsKey("WAVEGG"), "no egg wave started");
    }

    [Theory]
    [InlineData(true, "WCLFEW.22S")]
    [InlineData(true, "WCLFEW.23S")]
    [InlineData(false, "CLRBIT")]
    public void CliffTableLoopsMatchTheOriginal(bool creating, string loop)
    {
        // Games start at wave 81 on, where cliffs vanish and come back; only the loops hooked.
        var m = Boot();
        foreach (var r in NativeRoutines.Create().Where(r => r.Symbol is "WCLFEW.22S" or "WCLFEW.23S" or "CLRBIT"))
        {
            r.Bind(Program.Value.Symbols);
            m.InstallNative(r, r.ResolveAddress(Program.Value.Symbols));
        }
        var v = new NativeVerifier(m);
        var p = new RandomPlayer(1);
        var cliffs = new CliffScenario(Program.Value.Memory, Program.Value.Symbols, creating);
        for (int f = 0; f < 12000; f++)
        {
            p.Apply(m.Inputs, f);
            cliffs.Apply(m);
            m.RunFrame();
        }
        Assert.True(v.TotalMismatches == 0, string.Join("\n", v.Reports));
        Assert.True(v.Stats.TryGetValue(loop, out var s) && s.Calls > s.Declined, $"{loop} never ran natively");
    }

    [Fact]
    public void GameOverMatchesTheOriginal()
    {
        string[] labels = ["GAMOVR", "GAMOVR@nap", "GAMOVR@credits", "GOVWAT@nap", "GAMOV1", "GAMOV1@nap"];
        var v = VerifiedRun(NativeRoutines.Create().Where(r => labels.Contains(r.Symbol)), seed: 5, frames: 20000, twoPlayers: true);
        Assert.True(v.TotalMismatches == 0, string.Join("\n", v.Reports));
        foreach (var name in new[] { "GAMOVR", "GAMOVR@nap", "GOVWAT@nap", "GAMOV1@nap" })
            Assert.True(v.Stats.TryGetValue(name, out var s) && s.Calls > s.Declined, $"{name} never ran natively");
    }

    [Fact]
    public void AttractDrawingMatchesTheOriginal()
    {
        // The marquee page (logo lines, fill, stripes) and the compacted images, only these hooked.
        string[] labels = ["COUNTS", "FILLDN.1S", "COUNTS.6S", "WRPIXH", "WRPIXV", "UNCLP", "WRPIX.33S"];
        var v = VerifiedRun(NativeRoutines.Create().Where(r => labels.Contains(r.Symbol)), seed: 1, frames: 20000);
        Assert.True(v.TotalMismatches == 0, string.Join("\n", v.Reports));
        foreach (var name in labels)
            Assert.True(v.Stats.TryGetValue(name, out var s) && s.Calls > s.Declined, $"{name} never ran natively");

        // The stripe nap is PCNAP 1, and FILLDN returns to COUNTS.6S.
        var sym = Program.Value.Symbols;
        var rom = Program.Value.Memory;
        int nap = (int)sym["WRPRTS.10S"] - 5;
        Assert.True(rom[nap] == 0x86 && rom[nap + 2] == 0xBD && ((rom[nap + 3] << 8) | rom[nap + 4]) == (ushort)sym["VNAPTPC"]);
        int after = (int)sym["COUNTS.6S"];
        Assert.True(rom[after - 3] == 0xBD && ((rom[after - 2] << 8) | rom[after - 1]) == (ushort)sym["FILLDN"]);
    }

    [Fact]
    public void GlyphsPhrasesAndImageBlocksMatchTheOriginal()
    {
        // Only these hooked, so the phrase routines run original and reach OUTPUT and PHROT1.1S.
        string[] labels = ["OUTPUT", "PHROT1.1S", "WRRIDR", "WRHOR2"];
        var v = VerifiedRun(NativeRoutines.Create().Where(r => labels.Contains(r.Symbol)), seed: 1, frames: 12000);
        Assert.True(v.TotalMismatches == 0, string.Join("\n", v.Reports));
        foreach (var name in labels)
            Assert.True(v.Stats.TryGetValue(name, out var s) && s.Calls > s.Declined, $"{name} never ran natively");
        Assert.Equal(0x39, Program.Value.Memory[(int)Program.Value.Symbols["PHROT1.1S"] + 12]);   // the phrase loop's RTS
    }

    [Fact]
    public void LedgeRefreshAndPterodactylMatchTheOriginal()
    {
        string[] labels = ["NOBCKR@nap", "NOBCKR@left", "NOBCKR@right", "SRCADP", "WNORIA"];
        var v = VerifiedRun(NativeRoutines.Create().Where(r => labels.Contains(r.Symbol)), seed: 1, frames: 20000);
        Assert.True(v.TotalMismatches == 0, string.Join("\n", v.Reports));
        foreach (var name in labels)
            Assert.True(v.Stats.TryGetValue(name, out var s) && s.Calls > s.Declined, $"{name} never ran natively");
        var ledges = new LedgeRefresh(new Symbols(Program.Value.Symbols), new Scheduler(new Symbols(Program.Value.Symbols)));
        foreach (var side in new[] { LedgeRefresh.L.Left, LedgeRefresh.L.Right })
            Assert.Equal(0x10, Program.Value.Memory[ledges.Address(side)]);                     // LDY CLIF5
    }

    [Fact]
    public void SelfTestAndClearsMatchTheOriginal()
    {
        // Power-on: RAM fill and read-back, ROM sums, the start-up delay, and the memory clears.
        string[] labels = ["RAM3", "RAM7", "ROM1", "DELA2", "INIT2.1S", "SCCLER.10S"];
        var v = VerifiedRun(NativeRoutines.Create().Where(r => labels.Contains(r.Symbol)), seed: 1, frames: 3000);
        Assert.True(v.TotalMismatches == 0, string.Join("\n", v.Reports));
        foreach (var name in labels)
            Assert.True(v.Stats.TryGetValue(name, out var s) && s.Calls > s.Declined, $"{name} never ran natively");
    }

    [Fact]
    public void DeathPuffMatchesTheOriginal()
    {
        string[] labels = ["DEATH", "DEATH@frame2", "DEATH@frame3", "DEATH@end"];
        var v = VerifiedRun(NativeRoutines.Create().Where(r => labels.Contains(r.Symbol)), seed: 1, frames: 20000);
        Assert.True(v.TotalMismatches == 0, string.Join("\n", v.Reports));
        foreach (var name in labels)
            Assert.True(v.Stats.TryGetValue(name, out var s) && s.Calls > s.Declined, $"{name} never ran natively");

        // Each resume point follows the PCNAP of the frame before.
        var s2 = Program.Value.Symbols;
        var rom = Program.Value.Memory;
        var puff = new Engine(new Symbols(s2)).Puff;
        foreach (var l in new[] { DeathPuff.L.Frame2, DeathPuff.L.Frame3, DeathPuff.L.End })
        {
            ushort ret = puff.Address(l);
            Assert.True(rom[ret - 3] == 0xBD && ((rom[ret - 2] << 8) | rom[ret - 1]) == (ushort)s2["VNAPTPC"], $"{l} does not follow JSR VNAPTPC");
        }
    }

    [Fact]
    public void WaveSetupResumePointsFollowTheirCalls()
    {
        var s = Program.Value.Symbols;
        var rom = Program.Value.Memory;
        var w = new Engine(new Symbols(s)).WaveSetup;
        ushort Word(int a) => (ushort)((rom[a] << 8) | rom[a + 1]);
        void Follows(WaveSetup.L l, string routine)
        {
            ushort ret = w.Address(l);
            Assert.True(rom[ret - 3] == 0xBD && Word(ret - 2) == (ushort)s[routine], $"{l} at {ret:X4} does not follow JSR {routine}");
        }
        foreach (var l in new[] { WaveSetup.L.InitNap, WaveSetup.L.NumberNap, WaveSetup.L.MessageNap, WaveSetup.L.CreateNap })
            Follows(l, "VNAPTPC");
        Follows(WaveSetup.L.Settings, "RCMSA");
        foreach (var l in new[] { WaveSetup.L.Cliff1, WaveSetup.L.Cliff2, WaveSetup.L.Cliff3, WaveSetup.L.Cliff4 })
            Follows(l, "WCLFEW");
        foreach (var l in new[] { WaveSetup.L.Delayed, WaveSetup.L.IntroDelay, WaveSetup.L.GladDelay, WaveSetup.L.PteroDelay })
            Follows(l, "WAVDEL");
        Follows(WaveSetup.L.PteroWait, "WATWAV");
        Follows(WaveSetup.L.EggDelay, "WAVDEL");
        Follows(WaveSetup.L.EggWait, "WATWAV");
        Follows(WaveSetup.L.EggNap, "VNAPTPC");
        foreach (var l in new[] { WaveSetup.L.LedgeEggBack, WaveSetup.L.SpareEggBack })
            Follows(l, "CREGG");
        Follows(WaveSetup.L.Waited, "WATWAV");
        foreach (var l in new[] { WaveSetup.L.Bounders, WaveSetup.L.Hunters, WaveSetup.L.Lords })
            Follows(l, "WCREATE");
        Assert.Equal(0xAD, rom[w.Address(WaveSetup.L.Previous) - 3]);  // JSR [PWPREV,U]
        Assert.Equal(0xAD, rom[w.Address(WaveSetup.L.Started) - 2]);   // JSR [B,X]
        foreach (var l in new[] { WaveSetup.L.DelayBack, WaveSetup.L.WaitBack })
            Assert.Equal(0x8D, rom[w.Address(l) - 2]);                 // BSR REWMSG
    }

    [Fact]
    public void WaveSetupMatchesWhenCalledFromOriginalCode()
    {
        string[] labels = Enum.GetValues<WaveSetup.L>().Select(TxJumpRoutine.WaveName).ToArray();
        var v = VerifiedRun(NativeRoutines.Create().Where(r => labels.Contains(r.Symbol)), seed: 1, frames: 40000);
        Assert.True(v.TotalMismatches == 0, string.Join("\n", v.Reports));
        foreach (var name in new[] { "CIA", "CIA@nap", "IWAVE", "WNRM@nap", "REWMSG@nap", "WCREATE@nap" })
            Assert.True(v.Stats.TryGetValue(name, out var s) && s.Calls > s.Declined, $"{name} never ran natively");
    }

    [Fact]
    public void JoysticksMatchWhenCalledFromOriginalCode()
    {
        // Every caller reads the joystick inline now; hook just the reads so original code calls them.
        var v = VerifiedRun(NativeRoutines.Create().Where(r => r.Symbol is "P1JOY" or "P2JOY"), seed: 5, frames: 8000, twoPlayers: true);
        Assert.True(v.TotalMismatches == 0, string.Join("\n", v.Reports));
        foreach (var name in new[] { "P1JOY", "P2JOY" })
            Assert.True(v.Stats.TryGetValue(name, out var s) && s.Calls > 0, $"{name} was never exercised");
    }

    [Fact]
    public void TransporterResumePointsFollowTheirCalls()
    {
        var s = Program.Value.Symbols;
        var rom = Program.Value.Memory;
        var tr = new Engine(new Symbols(s)).Transporter;
        ushort Word(int a) => (ushort)((rom[a] << 8) | rom[a + 1]);
        foreach (var l in new[] { Transporter.L.QueueNap, Transporter.L.FadeNap, Transporter.L.WaitNap, Transporter.L.StartNap,
                                  Transporter.L.SinkNap, Transporter.L.SunkNap })
        {
            ushort ret = tr.Address(l);
            Assert.True(rom[ret - 3] == 0xBD && Word(ret - 2) == (ushort)s["VNAPTPC"], $"{l} at {ret:X4} does not follow JSR VNAPTPC");
        }
        foreach (var l in new[] { Transporter.L.CreateLives, Transporter.L.StartLives })
        {
            ushort ret = tr.Address(l);
            Assert.True(rom[ret - 3] == 0xBD && Word(ret - 2) == (ushort)s["DECLIV"], $"{l} at {ret:X4} does not follow JSR DECLIV");
        }
        foreach (var l in new[] { Transporter.L.WaitJoy, Transporter.L.Sunk })
        {
            ushort ret = tr.Address(l);
            Assert.True(rom[ret - 3] == 0xAD, $"{l} at {ret:X4} does not follow JSR [n,R]");     // JSR [PJOY,U] / JSR [DDEAD,Y]
        }
    }

    [Fact]
    public void LavaTrollMatchesTheOriginal()
    {
        var v = TrollRun(NativeRoutines.Create(), seed: 1, frames: 20000);
        Assert.True(v.TotalMismatches == 0, string.Join("\n", v.Reports));
        foreach (var name in new[] { "LAVATC", "LAVAT1", "LT1HT.2S@nap", "LT1GRP.1S@nap", "LAV2LP", "LAVAT2", "LT2DIE", "LT1DRP" })
            Assert.True(v.Stats.TryGetValue(name, out var s) && s.Calls > 0, $"{name} was never exercised");

        // The pull, called from the original bird process.
        var pull = TrollRun(NativeRoutines.Create().Where(r => r.Symbol == "ADDLAV"), seed: 1, frames: 20000);
        Assert.True(pull.TotalMismatches == 0, string.Join("\n", pull.Reports));
        Assert.True(pull.Stats.TryGetValue("ADDLAV", out var a) && a.Calls > a.Declined, "the troll's pull never ran natively");
    }

    [Fact]
    public void LavaTrollResumePointsFollowNaps()
    {
        var s = Program.Value.Symbols;
        var rom = Program.Value.Memory;
        var troll = new Engine(new Symbols(s)).Troll;
        ushort nap = (ushort)s["VNAPTPC"];
        foreach (var l in new[] { LavaTroll.L.TrackNap, LavaTroll.L.GripNap })
        {
            ushort ret = troll.Address(l);
            Assert.True(rom[ret - 3] == 0xBD && ((rom[ret - 2] << 8) | rom[ret - 1]) == nap, $"{l} at {ret:X4} does not follow JSR VNAPTPC");
        }
    }

    [Fact]
    public void EnemyAiLabelsAreDistinctCode()
    {
        var s = Program.Value.Symbols;
        var addresses = EnemyAi.Labels.Select(l => (ushort)s[l]).ToList();
        Assert.Equal(addresses.Count, addresses.Distinct().Count());
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
