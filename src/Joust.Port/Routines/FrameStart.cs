using static Joust.Emulation.Alu6809;

namespace Joust.Port.Routines;

/// <summary>
/// EXECST (SYSTEM.ASM): the start of each foreground frame. Pick the other DMA table, wait until
/// the IRQ has finished playing it (EXEINT), then hand the next queued sound to the sound board,
/// reset the eight per-quadrant erase/write lists and walk the process list (EXEC).
///
/// The wait is a two-instruction loop (TST ,X / BNE) that only the IRQ can end. While the table
/// is busy, the native skips as many whole iterations as fit before the next interrupt edge, so
/// the interrupt still arrives at the same instruction boundary as in the original.
/// </summary>
public sealed class FrameStart
{
    private const int LoopCycles = 6 + 3;                        // TST ,X / BNE EXEINT

    private readonly Scheduler _scheduler;
    private readonly ushort _dTbl1, _dTbl2, _dmaFor, _dTbl, _dmaFree, _sTmr, _sndPtr, _piaB, _dmaBeg, _exec;
    private readonly int _firstBlock, _beginToEnd, _quadStep;

    public FrameStart(Symbols s, Scheduler scheduler)
    {
        _scheduler = scheduler;
        _dTbl1 = s["DTBL1"]; _dTbl2 = s["DTBL2"]; _dmaFor = s["DMAFOR"]; _dTbl = s["DTBL"]; _dmaFree = s["DMAFRE"];
        _sTmr = s["STMR"]; _sndPtr = s["SNDPTR"]; _piaB = s["PIAB"]; _dmaBeg = s["DMABEG"]; _exec = s["EXEC"];
        _firstBlock = s["DMA1"] - s["DTBL1"];
        _beginToEnd = s["DMAEND"] - s["DMABEG"];
        _quadStep = (int)(s.Value("QUADB") - s.Value("QUADA"));
        WaitAddress = s["EXEINT"];
    }

    /// <summary>EXEINT, the wait loop's head.</summary>
    public ushort WaitAddress { get; }

    /// <summary>EXECST: choose the table, then wait for it.</summary>
    public ushort? Start(Tx t)
    {
        var a = new Asm(t);
        a.LdxI(_dTbl1);                                          // the new images go in table 1?
        a.Lda(a.M(_dmaFor));
        if (!a.Br(a.Pl)) a.LdxI(_dTbl2);
        a.Stx(a.M(_dTbl));                                       // EXTBL1
        a.Com(a.M(_dmaFor));                                     // the other one next time
        return Wait(t, a, entered: false);
    }

    /// <summary>EXEINT: skip the busy wait up to the next interrupt, or carry on once the table is free.</summary>
    public ushort? Wait(Tx t) => Wait(t, new Asm(t), entered: true);

    private ushort? Wait(Tx t, Asm a, bool entered)
    {
        if (t.Read(t.X) != 0)
        {
            if (!entered) return WaitAddress;                    // stop at the loop head
            int k = 0;                                           // whole iterations before the next edge
            for (int step = 1 << 16; step > 0; step >>= 1)
                if (t.Context.InterruptSafe((k + step) * LoopCycles)) k += step;
            if (k == 0) return null;
            a.Tst(a.X(0));                                       // flags as the loop leaves them
            a.Br(a.Ne);
            t.Cycles = k * LoopCycles;
            return WaitAddress;
        }
        a.Tst(a.X(0));
        a.Br(a.Ne);
        a.Leax(_firstBlock);                                     // the table's first free block
        a.Stx(a.M(_dmaFree));
        SendSound(t, a);
        ResetLists(t, a);
        return _scheduler.Walk(t, Scheduler.Label.Exec) ?? _exec;
    }

    /// <summary>When the current sound's time is up, send the next code of its group to the sound board.</summary>
    private void SendSound(Tx t, Asm a)
    {
        a.Lda(a.M(_sTmr));                                       // any sound left?
        if (a.Br(a.Eq)) return;
        a.Dec(a.M(_sTmr));
        if (a.Br(a.Ne)) return;                                  // its time is not up yet
        a.Ldx(a.M(_sndPtr));
        if (a.Br(a.Eq)) return;                                  // no sound left
        t.D = t.Read16(t.X);                                     // LDD ,X++: code, time
        t.X += 2;
        t.CC = Logic16(t.CC, t.D);
        t.Cycles += 5 + 3;
        if (!a.Br(a.Mi)) a.LdxI(0);                              // no continuation
        a.Stx(a.M(_sndPtr));                                     // .1S
        a.Stb(a.M(_sTmr));
        a.LdbI(0xFF);                                            // send it to the sound board
        a.Stb(a.M(_piaB));
        a.AndaI(0x3F);                                           // leave the 7-segment display alone
        a.Sta(a.M(_piaB));
    }

    /// <summary>EXESND: empty the four quadrants' erase and write lists (each end points at its begin).</summary>
    private void ResetLists(Tx t, Asm a)
    {
        a.Clra();
        a.Clrb();
        a.LdxI(_dmaBeg);
        for (int quad = 0; quad < 4; quad++)
        {
            if (quad > 0) a.Leax(_quadStep - 2);                 // the next quadrant
            a.Stx(a.X(_beginToEnd));                             // erase list: end = begin
            t.Write16(t.X, t.D);                                 // STD ,X++: empty
            t.CC = Logic16(t.CC, t.D);
            t.X += 2;
            t.Cycles += 5 + 3;
            a.Stx(a.X(_beginToEnd));                             // write list
            a.Std(a.X(0));
        }
    }
}

/// <summary>
/// The wave controller's in-play loop (JOUSTRV4.ASM, EMY2/EMYOK), every 8 ticks: end the wave
/// when no riders are left, send off baiter pterodactyls on their timetable (at most three on
/// screen), and every 15 seconds that enemies are alive let one more enemy get smart.
/// The wave set-up and its messages (IWAVE) are still original code.
/// </summary>
public sealed class WaveMonitor
{
    public enum L { Reset, Check }

    private readonly Scheduler _scheduler;
    private readonly int _delay, _baitN, _chase;
    private readonly ushort _nRider, _dBait, _cBait, _nBait, _nEnemy, _gOver, _wSmart, _baitTbl, _baitSt;
    private readonly ushort _vNapTpc, _vCrProc, _iWave;
    private readonly byte _pteId;
    private readonly Dictionary<L, ushort> _address = [];

    public WaveMonitor(Symbols s, Scheduler scheduler)
    {
        _scheduler = scheduler;
        _delay = s["PDELAY"]; _baitN = s["PBAITN"]; _chase = s["PCHASE"];
        _nRider = s["NRIDER"]; _dBait = s["DBAIT"]; _cBait = s["CBAIT"]; _nBait = s["NBAIT"]; _nEnemy = s["NENEMY"];
        _gOver = s["GOVER"]; _wSmart = s["WSMART"]; _baitTbl = s["BAITBL"]; _baitSt = s["BAITST"];
        _vNapTpc = s["VNAPTPC"]; _vCrProc = s["VCRPROC"]; _iWave = s["IWAVE"];
        _pteId = (byte)s.Value("PTEID");
        _address[L.Reset] = s["EMY2"];
        _address[L.Check] = (ushort)(s["EMYOK"] + 5);            // after PCNAP 8
    }

    public ushort Address(L label) => _address[label];

    public ushort? Run(Tx t, L start)
    {
        var a = new Asm(t);
        var mark = t.Mark();
        ushort exit = start == L.Reset ? Reset(t, a) : Check(t, a);
        if (!t.CanRun(0))
        {
            t.Rollback(mark);
            return null;
        }
        return exit;
    }

    /// <summary>EMY2: 112 checks (15 s) until the next intelligence bump, then nap.</summary>
    private ushort Reset(Tx t, Asm a)
    {
        a.LdaI(112);
        a.Sta(a.U(_delay));
        return Nap(t, a);
    }

    /// <summary>EMYOK after its nap.</summary>
    private ushort Check(Tx t, Asm a)
    {
        t.X = t.U;                                               // LEAX ,U
        t.CC = Set(t.CC, Z, t.X == 0);
        t.Cycles += 4;
        a.Lda(a.M(_nRider));                                     // all enemies dead?
        if (a.Lbr(a.Eq)) return _iWave;                          // end of the wave
        a.Lda(a.M(_dBait));                                      // baiters delayed?
        bool countDown = a.Br(a.Eq);
        if (!countDown)
        {
            a.Dec(a.M(_dBait));
            countDown = a.Br(a.Eq);
        }
        if (countDown) SendOffBaiter(t, a);
        a.Lda(a.M(_nEnemy));                                     // .11S: any enemies active?
        if (a.Br(a.Eq)) return Nap(t, a);
        a.Dec(a.U(_delay));
        if (a.Br(a.Ne)) return Nap(t, a);
        a.Lda(a.M(_gOver));                                      // 15 s up: one more may get smart
        if (!a.Br(a.Eq))
        {
            a.Inc(a.M(_wSmart));
            if (!a.Br(a.Ne)) a.Dec(a.M(_wSmart));                // (saturating)
            else return Reset(t, a);
            a.Bra();
        }
        return Reset(t, a);
    }

    /// <summary>.9S: count down CBAIT; at zero send a baiter (up to three on screen) and set the next time.</summary>
    private void SendOffBaiter(Tx t, Asm a)
    {
        a.Ldd(a.M(_cBait));
        a.SubdI(1);
        if (a.Br(a.Gt))
        {
            a.Std(a.M(_cBait));                                  // .10S
            return;
        }
        a.Lda(a.M(_nBait));
        a.CmpaI(3 - 1);                                          // only three baiters at a time
        if (a.Br(a.Hi)) return;
        a.LdxI(_baitSt);                                         // SECCR BAITST,PTEID
        a.LddI((_pteId << 8) | 0xFF);
        t.Cycles += 7 + Symbols.AddressCost(_vCrProc) + 4;       // JSR VCRPROC / JMP CRPROC
        _scheduler.Create(t, afterRunning: true);
        t.Cycles += 5;
        a.Inc(a.M(_nBait));
        a.LdaI(-1);
        a.Sta(a.Y(_chase));                                      // a baiter
        a.Lda(a.U(_baitN));
        if (!a.Br(a.Eq)) a.Dec(a.U(_baitN));
        t.A = Asl8(ref t.CC, t.A);                               // .100S: ASLA
        t.Cycles += 2;
        a.LdxI(_baitTbl);
        t.D = t.Read16(t.X + (sbyte)t.A);                        // LDD A,X: its sleep time
        t.CC = Logic16(t.CC, t.D);
        t.Cycles += 6;
        a.Std(a.M(_cBait));                                      // .10S
    }

    /// <summary>EMYOK: PCNAP 8.</summary>
    private ushort Nap(Tx t, Asm a)
    {
        a.LdaI(8);
        t.Cycles += 7 + Symbols.AddressCost(_vNapTpc) + 4;
        t.Push16(_address[L.Check]);
        return _scheduler.Nap(t, fromJsr: true) ?? throw new InvalidOperationException("nap without a continuation");
    }
}
