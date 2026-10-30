using static Joust.Emulation.Alu6809;

namespace Joust.Port.Routines;

/// <summary>
/// The lava troll (JOUSTRV4.ASM, "LAVA TROLL PART 1/2", ADDLAV and CKGND's LNDB7).
/// <list type="number">
/// <item>Spawn (LNDB7): a player or enemy skimming the lava, once the troll's waves have come,
///   starts a troll process just before it (one troll at a time).</item>
/// <item>Reach (LAVAT1): the hand rises out of the lava under the victim frame by frame, then
///   tracks its height until it grips; if the victim gets out of reach the hand sinks again.</item>
/// <item>Grip (LT1GRP / LAVAT2): the victim's gravity becomes the troll's pull (ADDLAV) and a
///   second process holds the hand on the victim until it breaks free (50 points for a living
///   victim) or is dragged into the lava. The pull grows the longer the troll goes on (PATCH2).</item>
/// </list>
/// The processes are state machines over their labels and nap resume points, like the egg.
/// Pulling a victim into the lava (ADGFLR) is not ported and hands back.
/// </summary>
public sealed class LavaTroll
{
    public enum L { Clear, Reach, TrackNap, GripNap, HoldClear, Hold, HoldDie, Drop, Done }

    private readonly Ground _ground;
    private readonly Scheduler _scheduler;
    private readonly Eggs _eggs;
    private readonly Scoring _scoring;

    // Process block
    private readonly int _link, _id, _pc, _nap, _posX, _posY, _velX, _velY, _bumpX, _bumpY, _frame, _joy;
    private readonly int _joyT, _picH, _state, _dist, _gravity, _decision;

    private readonly ushort _lavNbr, _lavTim, _lavKll, _clvGra, _lavGra, _iLavat, _pPrev, _pExec;
    private readonly ushort _vNapTim, _vNapTpc, _vSucide, _vCuProc, _vWr1Cls;
    private readonly ushort _addGra, _addLav, _snTrol, _lavat1, _lavat2, _lav2Lp, _lt2Die, _lt1Drp, _lt1Don;
    private readonly int _lavId, _floor, _eLeft, _eRight, _playerAlive, _enemyAlive;
    private readonly ushort _lavvfyBsrReturn;
    private readonly Dictionary<L, ushort> _address = [];

    public LavaTroll(Symbols s, Ground ground, Scheduler scheduler, Eggs eggs, Scoring scoring)
    {
        _ground = ground;
        _scheduler = scheduler;
        _eggs = eggs;
        _scoring = scoring;
        _link = s["PLINK"]; _id = s["PID"]; _pc = s["PPC"]; _nap = s["PNAP"]; _posX = s["PPOSX"]; _posY = s["PPOSY"];
        _velX = s["PVELX"]; _velY = s["PVELY"]; _bumpX = s["PBUMPX"]; _bumpY = s["PBUMPY"]; _frame = s["PFRAME"];
        _joy = s["PJOY"]; _joyT = s["PJOYT"]; _picH = s["PPICH"]; _state = s["PSTATE"]; _dist = s["PDIST"];
        _gravity = s["PADGRA"]; _decision = s["PDECSN"];
        _lavNbr = s["LAVNBR"]; _lavTim = s["LAVTIM"]; _lavKll = s["LAVKLL"]; _clvGra = s["CLVGRA"]; _lavGra = s["LAVGRA"];
        _iLavat = s["ILAVAT"]; _pPrev = s["PPREV"]; _pExec = s["PEXEC"];
        _vNapTim = s["VNAPTIM"]; _vNapTpc = s["VNAPTPC"]; _vSucide = s["VSUCIDE"]; _vCuProc = s["VCUPROC"]; _vWr1Cls = s["VWR1CLS"];
        _addGra = s["ADDGRA"]; _addLav = s["ADDLAV"]; _snTrol = s["SNTROL"];
        _lavat1 = s["LAVAT1"]; _lavat2 = s["LAVAT2"]; _lav2Lp = s["LAV2LP"]; _lt2Die = s["LT2DIE"];
        _lt1Drp = s["LT1DRP"]; _lt1Don = s["LT1DON"];
        _lavvfyBsrReturn = (ushort)(s["LAVVFY"] + 2);
        _lavId = (int)s.Value("LAVID"); _floor = (int)s.Value("FLOOR");
        _eLeft = (int)s.Value("ELEFT"); _eRight = (int)s.Value("ERIGHT");
        _playerAlive = 0x80 + (int)s.Value("PLYID"); _enemyAlive = 0x80 + (int)s.Value("EMYID");

        void At(L l, string label, int offset = 0) => _address[l] = (ushort)(s[label] + offset);
        At(L.Clear, "LAVATC");
        At(L.Reach, "LAVAT1");
        At(L.TrackNap, "LT1HT.2S", 3 + 5);                       // JSR WLAVAT / PCNAP 1
        At(L.GripNap, "LT1GRP.1S", 5);                           // PCNAP 2
        At(L.HoldClear, "LAV2LP");
        At(L.Hold, "LAVAT2");
        At(L.HoldDie, "LT2DIE");
        At(L.Drop, "LT1DRP");
        At(L.Done, "LT1DON");
    }

    public ushort Address(L label) => _address[label];

    public IReadOnlyDictionary<L, ushort> Addresses => _address;

    /// <summary>ADDLAV, the troll's pull on its victim (a PADGRA routine).</summary>
    public ushort PullAddress => _addLav;

    private Asm a = null!;

    // ------------------------------------------------------------------ spawn

    /// <summary>
    /// LNDB7 from LDA PID,U, for a player or enemy over the lava with no troll out: start the
    /// troll (LAVAT1) just before this process, then LNDB7C (NE: don't land).
    /// </summary>
    public void Spawn(Tx t)
    {
        a = new Asm(t);
        a.Lda(a.U(_id));
        a.CmpaI(_playerAlive);
        if (!a.Br(a.Eq))
        {
            a.CmpaI(_enemyAlive);
            a.Br(a.Ne);
        }
        a.Inc(a.M(_lavNbr));                                     // .1S: one more troll
        a.LdaI(_lavId);
        a.Ldb(a.U(_id + 1));                                     // PPRI
        a.LdxI(_lavat1);
        a.Ldu(a.M(_pPrev));                                      // after the previous process
        CreateProcess(t);
        a.Ldu(a.M(_pExec));
        a.Stu(a.Y(_joy));                                        // fingerprint the victim
        a.Clr(a.Y(_frame));
        a.LddI(_floor - 9);
        a.Std(a.Y(_posY));
        a.Ldd(a.U(_posX));
        a.AdddI(-2);
        a.Std(a.Y(_posX));
        a.LddI(0);
        a.Std(a.Y(_picH));
        a.LdaI(1);
        a.Sta(a.Y(_joyT));
        a.Ldb(a.U(_posY + 1));                                   // LNDB7C
    }

    private void CreateProcess(Tx t)
    {
        t.Cycles += 7 + Symbols.AddressCost(_vCuProc) + 4;       // JSR VCUPROC / JMP CUPROC
        _scheduler.Create(t, afterRunning: false);
        t.Cycles += 5;
    }

    // ------------------------------------------------------------------ ADDLAV

    /// <summary>
    /// ADDLAV (without its RTS): no bumps or sideways motion; the troll's pull (CLVGRA) plus B is
    /// added to the fall. Rising fast enough breaks free; otherwise the victim is kept on screen
    /// and in reach. False where the victim reaches the lava (hand back).
    /// </summary>
    public bool Pull(Tx t)
    {
        a = new Asm(t);
        a.Clr(a.U(_bumpX));
        a.Clr(a.U(_bumpY));
        a.Clr(a.U(_velX));
        a.Sex();
        a.Addd(a.M(_clvGra));
        a.Addd(a.U(_velY));
        a.Std(a.U(_velY));
        a.CmpdI(-0x180);                                         // break-free velocity?
        if (a.Br(a.Lt)) { BreakFree(t); return true; }
        a.Addd(a.U(_posY + 1));
        a.Std(a.U(_posY + 1));
        a.CmpaI(_floor + 7);                                     // in the lava?
        if (!a.Br(a.Lo)) return false;
        a.Ldd(a.U(_posX));                                       // ADLX: keep on the visible screen
        a.CmpdI(_eLeft + 14 + 4);
        if (!a.Br(a.Gt)) a.LddI(_eLeft + 14 + 4);
        a.CmpdI(_eRight - 14);                                   // .10S
        if (!a.Br(a.Lt)) a.LddI(_eRight - 14);
        a.Std(a.U(_posX));                                       // .11S
        a.TfrUY();
        a.Jsr();
        InReach();                                               // LAVVI3
        a.Rts();
        if (a.Br(a.Ne)) BreakFree(t);
        return true;
    }

    /// <summary>ADLFRE: 50 points if alive, stop the grip and drop the hand, normal gravity again.</summary>
    private void BreakFree(Tx t)
    {
        a.Lda(a.U(_id));
        if (!a.Br(a.Pl))
        {
            a.LdaI(0x50);
            a.Ldx(a.U(_decision));
            t.Cycles += 8;                                       // JSR SCRTEN
            if (!_scoring.Add(t, tens: true)) throw new HandBackException();
            t.Cycles += 5;
        }
        a.Ldx(a.U(_link));                                       // .33S: the grip process
        a.Lda(a.X(_id));
        a.CmpaI(_lavId + 1);
        if (!a.Br(a.Ne))
        {
            a.LddI(_lt2Die);
            a.Std(a.X(_pc));
        }
        a.Ldx(a.M(_pPrev));                                      // .11S: drop the hand
        a.LddI(_lt1Drp);
        a.Std(a.X(_pc));
        a.LdaI(1);
        a.Sta(a.X(_nap));
        a.LddI(_addGra);                                         // .22S
        a.Std(a.U(_gravity));
        a.Ldd(a.U(_velY));                                       // show the new higher position
        a.Addd(a.U(_posY + 1));
        a.Std(a.U(_posY + 1));
    }

    // ------------------------------------------------------------------ victim checks

    /// <summary>
    /// LAVVIC / LAVVI2 / LAVVI3 (body, without its RTS): EQ if the victim Y is still this
    /// troll's, alive, in the air, low enough and over the lava's reach; D = its X - 2.
    /// </summary>
    private void InReach(bool fromLink = false, bool checkJoy = false)
    {
        if (fromLink) a.Ldy(a.U(_link));                         // LAVVIC
        if (checkJoy)
        {
            a.Cmpy(a.U(_joy));                                   // LAVVI2: still the victim?
            if (a.Br(a.Ne)) { Out(); return; }
        }
        a.Lda(a.Y(_id));                                         // LAVVI3: alive?
        if (a.Br(a.Pl)) { Out(); return; }
        a.Lda(a.Y(_state));                                      // in the air?
        if (a.Br(a.Ne)) { Out(); return; }
        a.Lda(a.Y(_posY + 1));                                   // too high?
        a.CmpaI(_floor + 7 - 32);
        if (a.Br(a.Lo)) { Out(); return; }
        a.Ldd(a.Y(_posX));
        a.AdddI(-2);
        a.CmpdI(54 - 14);                                        // left side of the lava
        if (!a.Br(a.Le))
        {
            a.CmpdI(240);                                        // right side
            if (a.Br(a.Lt)) { Out(); return; }
        }
        a.Orcc(Z);                                               // LVVIN

        void Out() => a.Andcc(~Z);                               // LVVOUT
    }

    /// <summary>
    /// JSR LAVVFY: true if the victim is in reach (D = its X - 2). False leaves for LAVATF with
    /// LAVVFY's return address (<paramref name="returnTo"/>) still on the stack.
    /// </summary>
    private bool Verify(ushort returnTo)
    {
        a.T.Push16(returnTo);                                    // JSR LAVVFY
        a.T.Cycles += 8;
        a.T.Push16(_lavvfyBsrReturn);                            // BSR LAVVIC
        a.T.Cycles += 7;
        InReach(fromLink: true, checkJoy: true);
        a.Rts();
        if (a.Br(a.Ne)) return false;                            // BNE LAVATF (its return address stays)
        a.Rts();
        return true;
    }

    // ------------------------------------------------------------------ process

    public ushort? Run(Tx t, L start)
    {
        var label = start;
        int startCycles = t.Cycles;
        while (true)
        {
            var mark = t.Mark();
            (L Next, ushort? Exit) step;
            try
            {
                a = new Asm(t);
                step = Step(t, label);
            }
            catch (HandBackException)
            {
                t.Rollback(mark);
                if (t.Cycles == startCycles) throw;
                return _address[label];
            }
            if (!t.CanRun(0))
            {
                t.Rollback(mark);
                return t.Cycles == startCycles ? null : _address[label];
            }
            if (step.Exit != null) return step.Exit;
            label = step.Next;
        }
    }

    private (L, ushort?) Step(Tx t, L label)
    {
        switch (label)
        {
            case L.Clear:
                Call(t, () => _eggs.Erase(t));                   // JSR CLAVAT
                return (L.Reach, null);

            case L.Reach:
                if (!Verify((ushort)(_address[L.Reach] + 3))) return Release(t);
                a.Std(a.U(_posX));                               // under the victim
                a.Lda(a.U(_frame));
                a.Dec(a.U(_joyT));                               // time for the next frame?
                if (!a.Br(a.Gt))
                {
                    a.Lda(a.M(_lavTim));
                    a.Sta(a.U(_joyT));
                    a.Lda(a.U(_frame));
                    a.CmpaI(5 * 6);                              // hand fully out?
                    if (a.Br(a.Eq)) return Track(t);
                    a.AddaI(6);
                    a.Sta(a.U(_frame));
                }
                Call(t, () => Draw(t));                          // LT1TRK
                return (default, NapGo(t, 1, _address[L.Clear]));

            case L.TrackNap:
                Call(t, () => _eggs.Erase(t));
                if (!Verify((ushort)(_address[L.TrackNap] + 3 + 3))) return Release(t);
                a.Std(a.U(_posX));
                a.Bra();
                return Track(t);

            case L.GripNap:
                if (!Verify((ushort)(_address[L.GripNap] + 3))) return Release(t);
                a.Bra();
                return (default, PcNap(t, 2, _address[L.GripNap]));

            case L.HoldClear:
                Call(t, () => _eggs.Erase(t));
                return (L.Hold, null);

            case L.Hold:
            {
                a.Ldy(a.M(_pPrev));                              // the victim is just before
                a.Jsr();
                InReach(checkJoy: true);                         // LAVVI2
                a.Rts();
                if (a.Br(a.Ne)) return (default, Suicide(t));    // LT2SUC: out of its hands
                a.Ldx(a.U(_dist));                               // keep LAVAT1 in place too
                a.Std(a.U(_posX));
                a.Std(a.X(_posX));
                a.Ldd(a.Y(_posY));
                a.AddbI(-7 + 17);                                // hand hold
                a.Std(a.U(_posY));
                a.Std(a.X(_posY));
                a.LdaI(6 * 6);                                   // the holding frame
                a.Sta(a.U(_frame));
                Call(t, () => Draw(t));
                a.LdxI(_lav2Lp);
                a.LdaI(1);
                a.Jmp();                                         // JMP PATCH2
                return (default, StrongerPull(t));
            }

            case L.HoldDie:
                Call(t, () => _eggs.Erase(t));
                return (default, Suicide(t));

            case L.Drop:
                Call(t, () => Draw(t));
                a.LdaI(5);
                a.LdxI(_address[L.Clear]);
                return (default, JmpNap(t));

            case L.Done:
                a.Dec(a.M(_lavNbr));                             // free up the troll
                return (default, Suicide(t));
        }
        throw new InvalidOperationException($"unknown label {label}");
    }

    /// <summary>LT1HT: move the hand up or down to the victim's grip height; grip when there.</summary>
    private (L, ushort?) Track(Tx t)
    {
        a.Ldb(a.Y(_posY + 1));
        a.AddbI(10 - 7);
        a.Cmpb(a.U(_posY + 1));
        if (a.Br(a.Eq)) return Grip(t);
        if (a.Br(a.Hi))
        {
            a.Inc(a.U(_posY + 1));                               // .1S: too high, go down
        }
        else
        {
            a.Dec(a.U(_posY + 1));                               // raise the hand
            a.Bra();
        }
        Call(t, () => Draw(t));                                  // .2S
        return (default, PcNap(t, 1, _address[L.TrackNap]));
    }

    /// <summary>LT1GRP: grip the victim: its gravity becomes the troll's pull; start the hold process.</summary>
    private (L, ushort?) Grip(Tx t)
    {
        a.LdxI(_snTrol);
        _ground.CallSound(t);
        a.Ldd(a.U(_id));                                         // (and PPRI)
        a.Inca();                                                // grip id = troll + 1
        a.Ldu(a.U(_link));                                       // the victim
        a.LdxI(_addLav);
        a.Stx(a.U(_gravity));
        a.LdxI(_lavat2);
        CreateProcess(t);                                        // just after the victim
        a.Ldu(a.M(_pExec));
        a.Ldd(a.U(_posX));
        a.Std(a.Y(_posX));
        a.Ldd(a.U(_posY));
        a.Std(a.Y(_posY));
        a.Ldd(a.U(_joy));
        a.Std(a.Y(_joy));
        a.Stu(a.Y(_dist));                                       // the hold knows this process
        a.LddI(0);
        a.Jsr();                                                 // JSR PATCH1
        a.Std(a.Y(_picH));
        a.LddI(30 * 60);                                         // 30 s until a tougher troll
        a.Std(a.M(_lavKll));
        a.Ldd(a.M(_lavGra));
        a.Std(a.M(_clvGra));
        a.Rts();
        return (default, PcNap(t, 2, _address[L.GripNap]));
    }

    /// <summary>LAVATF: the victim got away: sink the hand a frame at a time, then end.</summary>
    private (L, ushort?) Release(Tx t)
    {
        a.Lda(a.U(_frame));
        a.AddaI(-6);
        a.Sta(a.U(_frame));
        if (a.Br(a.Le))
        {
            a.Dec(a.M(_lavNbr));                                 // LT1DON
            return (default, Suicide(t));
        }
        Call(t, () => Draw(t));                                  // LT1DRP
        a.LdaI(5);
        a.LdxI(_address[L.Clear]);
        return (default, JmpNap(t));
    }

    /// <summary>PATCH2: every tick of holding counts down LAVKLL; at zero the pull grows (up to $500). Then nap 1.</summary>
    private ushort StrongerPull(Tx t)
    {
        a.Ldd(a.M(_lavKll));
        a.AdddI(-1);
        if (!a.Br(a.Gt))
        {
            a.Ldd(a.M(_clvGra));
            a.CmpdI(0x500);
            if (!a.Br(a.Hi))
            {
                a.AdddI(1);
                a.Std(a.M(_clvGra));
            }
            a.LddI(1);                                           // .5S
        }
        a.Std(a.M(_lavKll));                                     // .10S
        a.LdaI(1);
        return JmpNap(t);
    }

    /// <summary>WLAVAT (without its RTS): queue troll frame PFRAME (none if 0) and clip it at the lava.</summary>
    private void Draw(Tx t)
    {
        a.Lda(a.U(_frame));
        a.SubaI(6);
        if (a.Br(a.Mi)) return;                                  // WLARTS
        a.Ldy(a.M(_iLavat));
        a.LeayAY();
        a.Sty(a.U(_picH));
        t.Cycles += 7 + Symbols.AddressCost(_vWr1Cls) + 4;       // JSR VWR1CLS / JMP
        _ground.Allocate(t, write: true, blocks: 1);
        t.Cycles += 5 + 8;                                       // RTS / JSR WRHOR2
        _ground.QueueImage(t);
        t.Cycles += 5 + 4;                                       // RTS / JMP CLIPER
        _eggs.Clip(t);
    }

    // ------------------------------------------------------------------ scheduling

    private static void Call(Tx t, Action body)
    {
        t.Cycles += 8;
        body();
        t.Cycles += 5;
    }

    /// <summary>NAPGO n,addr: LDA #n / LDX #addr / JMP VNAPTIM.</summary>
    private ushort NapGo(Tx t, byte ticks, ushort resume)
    {
        a.LdaI(ticks);
        a.LdxI(resume);
        return JmpNap(t);
    }

    /// <summary>JMP VNAPTIM with A = ticks and X = where to resume.</summary>
    private ushort JmpNap(Tx t)
    {
        t.Cycles += 3 + Symbols.AddressCost(_vNapTim) + 4;       // JMP VNAPTIM / JMP NAPTIM
        return _scheduler.Nap(t, fromJsr: false) ?? throw new InvalidOperationException("nap without a continuation");
    }

    /// <summary>PCNAP n: LDA #n / JSR VNAPTPC, resuming after it.</summary>
    private ushort PcNap(Tx t, byte ticks, ushort resume)
    {
        a.LdaI(ticks);
        t.Cycles += 7 + Symbols.AddressCost(_vNapTpc) + 4;       // JSR VNAPTPC / JMP NAPTPC
        t.Push16(resume);
        return _scheduler.Nap(t, fromJsr: true) ?? throw new InvalidOperationException("nap without a continuation");
    }

    /// <summary>JMP VSUCIDE.</summary>
    private ushort Suicide(Tx t)
    {
        t.Cycles += 3 + Symbols.AddressCost(_vSucide) + 4;
        return _scheduler.Suicide(t) ?? throw new InvalidOperationException("suicide without a continuation");
    }
}
