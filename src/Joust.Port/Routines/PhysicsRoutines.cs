using Joust.Emulation;

namespace Joust.Port.Routines;

/// <summary>The ported game logic, bound to one assembled program.</summary>
public sealed class Engine
{
    public Engine(Symbols s)
    {
        Physics = new Physics(s);
        Ground = new Ground(s, Physics.Sound);
        Scoring = new Scoring(s);
        Combat = new Combat(s, Physics, Ground, Scoring);
        Scheduler = new Scheduler(s);
        ScoreDisplay = new ScoreDisplay(s);
        Ai = new EnemyAi(s, Ground);
        Bird = new BirdProcess(s, Physics, Ground, Scheduler, Ai);
        Sweeps = new Sweeps(s, Combat, Scheduler);
        Deaths = new Deaths(s, Scheduler, Ground, Scoring, Combat);
        Combat.Deaths = Deaths;
        Eggs = new Eggs(s, Physics, Ground);
        Text = new TextOut(s);
        EggProcess = new EggProcess(s, Physics, Ground, Scheduler, Eggs, Text);
        Irq = new InterruptHandler(s);
        Troll = new LavaTroll(s, Ground, Scheduler, Eggs, Scoring);
        Physics.SpawnTroll = Troll.Spawn;
        Bird.Troll = Troll;
        Transporter = new Transporter(s, Ground, Scheduler, Eggs, Deaths, Bird);
        Bird.Lava = Transporter;
        FrameStart = new FrameStart(s, Scheduler);
        Waves = new WaveMonitor(s, Scheduler);
        WaveSetup = new WaveSetup(s, Scheduler, Text);
        Checksums = new Checksums(s, Scheduler);
    }

    public Checksums Checksums { get; }

    public WaveSetup WaveSetup { get; }

    public FrameStart FrameStart { get; }
    public WaveMonitor Waves { get; }

    public Transporter Transporter { get; }
    public InterruptHandler Irq { get; }
    public LavaTroll Troll { get; }

    public Eggs Eggs { get; }
    public TextOut Text { get; }
    public EggProcess EggProcess { get; }

    public Deaths Deaths { get; }

    public EnemyAi Ai { get; }
    public BirdProcess Bird { get; }
    public Scoring Scoring { get; }
    public Sweeps Sweeps { get; }

    public Physics Physics { get; }
    public Ground Ground { get; }
    public Combat Combat { get; }
    public Scheduler Scheduler { get; }
    public ScoreDisplay ScoreDisplay { get; }
}

/// <summary>
/// A transaction-based native for code that leaves by jumping (scheduler, process slices).
/// The body returns where execution continues, or null to hand the call back.
/// </summary>
public sealed class TxJumpRoutine(
    string name,
    Func<Engine, Tx, ushort?> body,
    Func<Engine, ushort>? address = null) : NativeRoutine
{
    private Engine _engine = null!;

    public override string Symbol => name;
    public override int MaxCycles => 0;
    public override bool EndsWithRts => false;

    public override void Bind(IReadOnlyDictionary<string, long> symbols) => _engine = new Engine(new Symbols(symbols));

    public override ushort ResolveAddress(IReadOnlyDictionary<string, long> symbols)
    {
        Bind(symbols);
        return address?.Invoke(_engine) ?? base.ResolveAddress(symbols);
    }

    public override bool Execute(NativeContext c)
    {
        var t = new Tx(c);
        ushort? next;
        try
        {
            next = body(_engine, t);
        }
        catch (HandBackException)
        {
            return false;                                        // nothing was applied
        }
        return next != null && t.CommitJump(next.Value);
    }

    public static IEnumerable<NativeRoutine> All() =>
    [
        // The IRQ handler: palette, switches, and playing the DMA lists to the blitter
        new TxJumpRoutine("IRQ", (e, t) => e.Irq.Run(t)),

        // Frame start: the wait for a free DMA table, the sound board, the list reset
        new TxJumpRoutine("EXECST", (e, t) => e.FrameStart.Start(t)),
        new TxJumpRoutine("EXEINT", (e, t) => e.FrameStart.Wait(t)),

        // The wave controller between waves, at any label or resume point
        .. Enum.GetValues<WaveSetup.L>().Select(l => new TxJumpRoutine(
            WaveName(l),
            (e, t) => e.WaveSetup.Run(t, l),
            e => e.WaveSetup.Address(l))),

        // The anti-tamper checksums: JZAP's summing loop in chunks, the KZAP process
        new TxJumpRoutine("JZAP.1S", (e, t) => e.Checksums.Sum(t)),
        new TxJumpRoutine("KZAP", (e, t) => e.Checksums.Run(t, Checksums.L.KzapStart)),
        new TxJumpRoutine("KZAP.1S@nap", (e, t) => e.Checksums.Run(t, Checksums.L.KzapNap), e => e.Checksums.Address(Checksums.L.KzapNap)),
        new TxJumpRoutine("KZAP@raddr", (e, t) => e.Checksums.Run(t, Checksums.L.KzapFailed), e => e.Checksums.Address(Checksums.L.KzapFailed)),

        // The wave controller's in-play loop
        new TxJumpRoutine("EMY2", (e, t) => e.Waves.Run(t, WaveMonitor.L.Reset)),
        new TxJumpRoutine("EMYOK@nap", (e, t) => e.Waves.Run(t, WaveMonitor.L.Check), e => e.Waves.Address(WaveMonitor.L.Check)),

        // Scheduler: sleeping, dying, walking the process list, end of frame
        new TxJumpRoutine("NAPTPC", (e, t) => e.Scheduler.Nap(t, fromJsr: true)),
        new TxJumpRoutine("NAPTIM", (e, t) => e.Scheduler.Nap(t, fromJsr: false)),
        new TxJumpRoutine("SUCIDE", (e, t) => e.Scheduler.Suicide(t)),
        new TxJumpRoutine("EXEC", (e, t) => e.Scheduler.Walk(t, Scheduler.Label.Exec)),
        new TxJumpRoutine("EXEPS", (e, t) => e.Scheduler.Walk(t, Scheduler.Label.Ps)),
        new TxJumpRoutine("EXEPRI", (e, t) => e.Scheduler.Walk(t, Scheduler.Label.Pri)),
        new TxJumpRoutine("EXEPEN", (e, t) => e.Scheduler.Walk(t, Scheduler.Label.Pen)),
        new TxJumpRoutine("EXESEC", (e, t) => e.Scheduler.Walk(t, Scheduler.Label.Sec)),
        new TxJumpRoutine("DTBOVR", (e, t) => e.Scheduler.LinkDmaTables(t)),

        // The score display process, as coroutine slices
        new TxJumpRoutine("SCODSP", (e, t) => e.ScoreDisplay.Start(t)),
        new TxJumpRoutine("SCODSP@digit", (e, t) => e.ScoreDisplay.NextDigit(t), e => e.ScoreDisplay.AfterDigit),
        new TxJumpRoutine("SCODSP@idle", (e, t) => e.ScoreDisplay.CheckChanged(t), e => e.ScoreDisplay.AfterIdle),
        new DrawDigitRoutine(highNibble: true),
        new DrawDigitRoutine(highNibble: false),

        // The bird process (players and enemies), entered at any of its labels or resume points
        .. Enum.GetValues<BirdProcess.L>().Select(l => new TxJumpRoutine(
            BirdName(l),
            (e, t) => e.Bird.Run(t, l),
            e => e.Bird.Address(l))),
        // Collision: the pair test and the joust, with its resume points around the death routine
        new TxJumpRoutine("HITEM", (e, t) => e.Combat.Hit(t) ?? BirdProcess.Return(t)),
        new TxJumpRoutine("OSTWIN", (e, t) => e.Combat.Kill(t)),
        new TxJumpRoutine("OSTWIN@dead", (e, t) => e.Combat.KillAfterDeath(t), e => e.Combat.OstwinAfterDeath),
        new TxJumpRoutine("OSTWIN@score", (e, t) => e.Combat.KillAfterScore(t), e => e.Combat.OstwinAfterScore),
        new TxJumpRoutine("OSTXT3@win", (e, t) => e.Combat.ULosesAfterKill(t) ?? BirdProcess.Return(t), e => e.Combat.ULosesAfterWin),
        new TxJumpRoutine("OSTPYP@win", (e, t) => e.Combat.XLosesAfterKill(t) ?? BirdProcess.Return(t), e => e.Combat.XLosesAfterWin),
        new TxJumpRoutine("EGGSCR", (e, t) => e.Combat.CollectEgg(t)),

        // The collision sweeps, entered at any of their labels or resume points
        .. Enum.GetValues<Sweeps.L>().Select(l => new TxJumpRoutine(
            SweepName(l),
            (e, t) => e.Sweeps.Run(t, l),
            e => e.Sweeps.Address(l))),

        // The egg process, entered at any of its labels or resume points
        .. Enum.GetValues<EggProcess.L>().Select(l => new TxJumpRoutine(
            EggName(l),
            (e, t) => e.EggProcess.Run(t, l),
            e => e.EggProcess.Address(l))),

        // Entering through a transporter, and the lava death, at any label or resume point
        .. Enum.GetValues<Transporter.L>().Select(l => new TxJumpRoutine(
            TransporterName(l),
            (e, t) => e.Transporter.Run(t, l),
            e => e.Transporter.Address(l))),

        // The lava troll's processes, entered at any of their labels or resume points
        .. Enum.GetValues<LavaTroll.L>().Select(l => new TxJumpRoutine(
            TrollName(l),
            (e, t) => e.Troll.Run(t, l),
            e => e.Troll.Address(l))),

        new TxJumpRoutine("AIROVR",(e, t) => e.Bird.AirOverhead(t) ?? BirdProcess.Return(t)),
        new TxJumpRoutine("AIROVR@joy", (e, t) =>
        {
            e.Bird.AirAfterControls(t);
            return BirdProcess.Return(t);
        }, e => e.Bird.AirJoyReturn),
    ];

    public static string WaveName(WaveSetup.L l) => l switch
    {
        WaveSetup.L.Init => "CIA",
        WaveSetup.L.InitNap => "CIA@nap",
        WaveSetup.L.Next => "IWAVE",
        WaveSetup.L.Settings => "IWAVE@settings",
        WaveSetup.L.Previous => "WNRM@cleanup",
        WaveSetup.L.NumberNap => "WNRM@nap",
        WaveSetup.L.Cliff1 => "WNRM@cliff1",
        WaveSetup.L.Cliff2 => "WNRM@cliff2",
        WaveSetup.L.Cliff3 => "WNRM@cliff3",
        WaveSetup.L.Cliff4 => "WNRM.4S",
        WaveSetup.L.Started => "WNRM@started",
        WaveSetup.L.MessageNap => "REWMSG@nap",
        WaveSetup.L.DelayBack => "WAVDEL@message",
        WaveSetup.L.WaitBack => "WATWAV@message",
        WaveSetup.L.Delayed => "WPAUS2@delay",
        WaveSetup.L.Waited => "WPAUS3",
        WaveSetup.L.Bounders => "WPAUS3.2S",
        WaveSetup.L.Hunters => "WPAUS3.3S",
        WaveSetup.L.Lords => "WBEGIN",
        WaveSetup.L.CreateNap => "WCREATE@nap",
        _ => throw new ArgumentOutOfRangeException(nameof(l)),
    };

    public static string TransporterName(Transporter.L l) => l switch
    {
        Transporter.L.Create => "CREP1",
        Transporter.L.CreateLives => "CREP1@lives",
        Transporter.L.CreateEnemy => "CREEM",
        Transporter.L.QueueNap => "CRELP@nap",
        Transporter.L.FadeNap => "TREFF2@nap",
        Transporter.L.WaitNap => "TREFF2.40S",
        Transporter.L.WaitJoy => "TREFF2.40S@joy",
        Transporter.L.Start => "PLAYR",
        Transporter.L.StartNap => "PLAYR@nap",
        Transporter.L.StartLives => "PLAYR@lives",
        Transporter.L.Reborn => "PLYLI2",
        Transporter.L.Lava => "ADGFLR",
        Transporter.L.SinkNap => "ADGLAV@nap",
        Transporter.L.Sunk => "ADGDED",
        Transporter.L.SunkNap => "ADGDED@nap",
        _ => throw new ArgumentOutOfRangeException(nameof(l)),
    };

    public static string TrollName(LavaTroll.L l) => l switch
    {
        LavaTroll.L.Clear => "LAVATC",
        LavaTroll.L.Reach => "LAVAT1",
        LavaTroll.L.TrackNap => "LT1HT.2S@nap",
        LavaTroll.L.GripNap => "LT1GRP.1S@nap",
        LavaTroll.L.HoldClear => "LAV2LP",
        LavaTroll.L.Hold => "LAVAT2",
        LavaTroll.L.HoldDie => "LT2DIE",
        LavaTroll.L.Drop => "LT1DRP",
        LavaTroll.L.Done => "LT1DON",
        _ => throw new ArgumentOutOfRangeException(nameof(l)),
    };

    public static string EggName(EggProcess.L l) => l switch
    {
        EggProcess.L.Fall => "EGGLPA",
        EggProcess.L.FallNap => "EGGLPA@nap",
        EggProcess.L.Back => "EGGBCK",
        EggProcess.L.Settle => "EGGLND",
        EggProcess.L.Wait => "EGGLN2",
        EggProcess.L.WaitNap => "EGGLN2@nap",
        EggProcess.L.NextFrame => "EGGNXF",
        EggProcess.L.Hatched => "EGGNXF@hatched",
        EggProcess.L.Idle => "EGGLLP",
        EggProcess.L.IdleNap => "EGGLLP@nap",
        EggProcess.L.Hit => "EGGHIT",
        EggProcess.L.HitNap => "EGGHIT@nap",
        EggProcess.L.Delay => "EGGHIT.101S",
        EggProcess.L.DelayNap => "EGGHIT.101S@nap",
        EggProcess.L.Show => "EGGHIT.112S",
        EggProcess.L.Score => "EGGSC2",
        EggProcess.L.ScoreNap => "EGGSC2@nap",
        EggProcess.L.ScoreInAir => "SCRAIR",
        EggProcess.L.Done => "EGGDON",
        _ => throw new ArgumentOutOfRangeException(nameof(l)),
    };

    public static string SweepName(Sweeps.L l) => l switch
    {
        Sweeps.L.PlyTop => "PLYCOL",
        Sweeps.L.PlyAfterNap => "PLYCOL@nap",
        Sweeps.L.PlyOuter => "PLYCOL.25S",
        Sweeps.L.Ply35 => "PLYCOL.35S",
        Sweeps.L.Ply35After => "PLYCOL.35S@hit",
        Sweeps.L.Ply45 => "PLYCOL.45S",
        Sweeps.L.Ply45After => "PLYCOL.45S@hit",
        Sweeps.L.OppTop => "OPPCOL",
        Sweeps.L.OppOuter => "OPPCOL.25S",
        Sweeps.L.OppIn => "OPPCOL.45S",
        Sweeps.L.OppAfter => "OPPCOL.45S@hit",
        _ => throw new ArgumentOutOfRangeException(nameof(l)),
    };

    /// <summary>Hook name for a bird label: the source label, or label@resume for return points.</summary>
    public static string BirdName(BirdProcess.L l) => l switch
    {
        BirdProcess.L.PlyrLp => "PLYRLP",
        BirdProcess.L.PlyrNap => "PLYRLP@nap",
        BirdProcess.L.PlyrS2 => "PLYRS2",
        BirdProcess.L.Updno2After => "UPDNO2@state",
        BirdProcess.L.Pl2Rit => "PL2RIT",
        BirdProcess.L.Pl2Edg => "PL2EDG",
        BirdProcess.L.StFly => "STFLY",
        BirdProcess.L.StFall => "STFALL",
        BirdProcess.L.StLan => "STLAN",
        BirdProcess.L.FlapLp => "FLAPLP",
        BirdProcess.L.FlapNap => "FLAPLP@nap",
        BirdProcess.L.FlapAir => "FLAPLP@air",
        BirdProcess.L.FlapS2 => "FLAPS2",
        BirdProcess.L.FlapGrav => "FLAPST@grav",
        BirdProcess.L.WingFk => "WINGFK",
        BirdProcess.L.WingFkGround => "WINGFK@ground",
        BirdProcess.L.GoFlip => "GOFLIP",
        BirdProcess.L.FlipLp => "FLIPLP",
        BirdProcess.L.FlipNap => "FLIPLP@nap",
        BirdProcess.L.FlipAir => "FLIPLP@air",
        BirdProcess.L.FlipS2 => "FLIPS2",
        BirdProcess.L.FlipGrav => "FLIPST@grav",
        BirdProcess.L.GotFit => "GOTFIT",
        BirdProcess.L.GotFitGround => "GOTFIT@ground",
        BirdProcess.L.GoFlap => "GOFLAP",
        BirdProcess.L.Flast2 => "FLAST2",
        _ => throw new ArgumentOutOfRangeException(nameof(l)),
    };
}

/// <summary>
/// Installs one transaction-based routine at an original entry point. The body returns false
/// to hand the call back to the original; the transaction then is simply not committed.
/// </summary>
public sealed class TxRoutine(string symbol, Func<Engine, Tx, bool> body) : NativeRoutine
{
    private Engine _engine = null!;

    public override string Symbol => symbol;
    public override int MaxCycles => 0; // exact cycles known at commit

    public override void Bind(IReadOnlyDictionary<string, long> symbols) => _engine = new Engine(new Symbols(symbols));

    public override bool Execute(NativeContext c)
    {
        var t = new Tx(c);
        try
        {
            return body(_engine, t) && t.Commit();
        }
        catch (HandBackException)
        {
            return false;                                        // nothing was applied
        }
    }

    private static TxRoutine Always(string symbol, Action<Engine, Tx> body) =>
        new(symbol, (e, t) => { body(e, t); return true; });

    public static IEnumerable<NativeRoutine> All() =>
    [
        // Flight physics and background collision
        Always("WRAPX", (e, t) => e.Physics.WrapX(t)),
        new TxRoutine("ADDGRA", (e, t) => e.Physics.AddGravity(t)),
        new TxRoutine("ADDGRX", (e, t) => e.Physics.Integrate(t)),
        Always("ADDFLP", (e, t) => e.Physics.Flap(t)),
        new TxRoutine("CKGND", (e, t) => e.Physics.CheckGround(t)),
        Always("BCKCOL", (e, t) => e.Physics.BackgroundCollision(t)),
        Always("BCKPIX", (e, t) => e.Physics.PixelCollision(t)),
        Always("BPCOL", (e, t) => e.Physics.ScanLines(t)),

        // Ground movement state callbacks
        Always("REVR", (e, t) => e.Ground.Reverse(t)),
        Always("STANDR", (e, t) => e.Ground.Stand(t)),
        Always("RUNR", (e, t) => e.Ground.Run(t)),
        Always("RUNSR", (e, t) => e.Ground.StopRunning(t)),
        Always("SKIDR", (e, t) => e.Ground.Skid(t)),

        // Image selection and collision box
        Always("SRCADG", (e, t) => e.Ground.SelectImage(t, storeImage: true, onGround: true)),
        Always("SRCADH", (e, t) => e.Ground.SelectImage(t, storeImage: false, onGround: true)),
        Always("SRCADA", (e, t) => e.Ground.SelectImage(t, storeImage: true, onGround: false)),
        Always("SRCADR", (e, t) => e.Ground.SelectImage(t, storeImage: false, onGround: false)),

        // Draw / erase queue
        Always("WPLYR", (e, t) => e.Ground.DrawObject(t, write: true)),
        Always("CPLYR", (e, t) => e.Ground.DrawObject(t, write: false)),
        Always("CL1CLS", (e, t) => e.Ground.Allocate(t, write: false, blocks: 1)),
        Always("WR1CLS", (e, t) => e.Ground.Allocate(t, write: true, blocks: 1)),
        Always("CL2CLS", (e, t) => e.Ground.Allocate(t, write: false, blocks: 2)),
        Always("WR2CLS", (e, t) => e.Ground.Allocate(t, write: true, blocks: 2)),

        // Bird against bird
        Always("OSTXUP", (e, t) => e.Combat.BumpUp(t)),
        Always("OSTXDN", (e, t) => e.Combat.BumpDown(t)),
        Always("OSTLR", (e, t) => e.Combat.BounceApart(t)),
        Always("OSTUTP", (e, t) => e.Combat.UOnTop(t)),
        Always("OSTXTP", (e, t) => e.Combat.XOnTop(t)),
        Always("OSTBMP", (e, t) => e.Combat.Bump(t)),
        Always("PTEBRD", (e, t) => e.Combat.PterodactylHit(t)),

        // Process list edits
        Always("CRPROC", (e, t) => e.Scheduler.Create(t, afterRunning: true)),
        Always("CUPROC", (e, t) => e.Scheduler.Create(t, afterRunning: false)),
        Always("KLPROC", (e, t) => e.Scheduler.Kill(t)),

        // Death routines (also reached from the lava)
        Always("DEATH1", (e, t) => e.Deaths.PlayerDies(t, player2: false)),
        Always("DEATH2", (e, t) => e.Deaths.PlayerDies(t, player2: true)),
        Always("DEATH3", (e, t) => e.Deaths.EnemyDies(t)),
        Always("DEATH4", (e, t) => e.Deaths.PterodactylDies(t)),

        // Egg helpers
        Always("ADDEGG", (e, t) => e.Eggs.Fall(t)),
        Always("WEGG", (e, t) => e.Eggs.Draw(t)),
        Always("WEGPAR", (e, t) => e.Eggs.DrawFrame(t)),
        Always("CLIPER", (e, t) => e.Eggs.Clip(t)),
        Always("CEGG", (e, t) => e.Eggs.Erase(t)),
        Always("EGGBKG", (e, t) => e.Eggs.RefreshBackground(t)),
        Always("EGAREA", (e, t) => e.Eggs.ScoreArea(t)),

        // Joystick reads (widget PIA)
        Always("P1JOY", (e, t) => e.Bird.Joystick(t, player1: true)),
        Always("P2JOY", (e, t) => e.Bird.Joystick(t, player1: false)),

        // The lava troll's pull on a gripped bird (its PADGRA routine)
        new TxRoutine("ADDLAV", (e, t) => e.Troll.Pull(t)),

        // Enemy AI (PJOY routines), at every label that can be stored in PJOY
        .. EnemyAi.Labels.Select(label => Always(label, (e, t) => e.Ai.Run(t, e.Ai.Entry(label)))),
    ];
}
