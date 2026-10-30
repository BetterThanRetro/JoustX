using static Joust.Emulation.Alu6809;

namespace Joust.Port.Routines;

/// <summary>
/// The wave controller between waves (JOUSTRV4.ASM, "CENTRAL INTELLIGENCE AGENCY": CIA, IWAVE,
/// WNRM .. WBEGIN), run by the CIA process:
/// <list type="number">
/// <item>Difficulty (IWAVE): each dynamic adjustment (lava troll speed and pull, enemy timings,
///   ...) steps toward its end value after a number of waves set by the difficulty setting; the
///   lava rises.</item>
/// <item>The bridge burns in wave 3 (LAVAB processes) and the troll's flames start a wave later.</item>
/// <item>WNRM: reset the wave's counters, next BCD wave number, clean up the previous wave, show
///   "WAVE n", step the wave table (cliffs appear or vanish), start the wave type's routine.</item>
/// <item>A pause showing the wave's messages (WAVDEL), a wait until the previous wave's last
///   process is gone (WATWAV), then the Bounders, Hunters and Shadow Lords arrive one per second
///   (WCREATE) and the in-play loop starts (WBEGIN, EMY2).</item>
/// </list>
/// The original uses coroutines whose return addresses live in the process block (PWRTS,
/// PWRTS2): each JMP [PWRTS,U] continues here when it is one of these resume points. Reading the
/// difficulty setting (RCMSA), the previous wave's clean-up, cliff changes (WCLFEW) and the wave
/// type routines are still original: they leave with the return address pushed and resume here.
/// </summary>
public sealed class WaveSetup
{
    public enum L
    {
        Init, InitNap, Next, Settings, Previous, NumberNap, Cliff1, Cliff2, Cliff3, Cliff4, Started,
        MessageNap, DelayBack, WaitBack, Delayed, Waited, Bounders, Hunters, Lords, CreateNap,
        IntroDelay, GladDelay, PteroDelay, PteroWait, Intro, Team, Gladiator, Pterodactyls,
    }

    private readonly Scheduler _scheduler;
    private readonly TextOut _text;

    // Process block
    private readonly int _pWprev, _pWave, _pBaits, _pDelay, _pWrts, _pWrts2, _pIntel, _pMsgA, _pMsg0, _pMsg1, _pMsg5;
    private readonly int _pBaitN, _link, _posX, _posY, _velX, _frame, _joyT, _nap, _dist, _decision, _horse, _egg, _chase;

    // Wave table and adjustment table offsets
    private readonly int _wStatus, _wPersue, _wLord, _wLen, _dyWEnd, _dyWTim, _dyWInc, _dyWLen;

    private readonly ushort _wavBcd, _wavRts, _wavTbl, _wtbEnd, _wtbRst, _trSmall, _ga1, _rcmsa, _dyTbl, _dyEnd, _dynAdj;
    private readonly ushort _safram, _tBridge, _tTroll, _pPrev, _pExec, _pDummy, _lavaB, _lavaF;
    private readonly ushort _eggs1, _eggs2, _wSmart, _wEnemy, _emyTim, _plyG1, _plyG2, _gOver;
    private readonly ushort _wClfTb, _wClfT2, _wClfT3, _wClfT4, _wClfEw, _wJsrTb, _p4Dec, _p5Dec, _p6Dec;
    private readonly ushort _creem, _nEnemy, _nRider, _buzard, _baisbl, _baitbl, _cBait, _nBait, _dBait, _emy2;
    private readonly ushort _vNapTpc, _vCuProc, _vCrProc, _outPhr, _outBcd;
    private readonly int _baiterStarts, _floor, _eLeft, _eRight, _emyId, _waveMsg, _white, _wbPer, _wbCls;
    private readonly ushort _wIntro, _wCoop, _wGlad, _wPtero, _wavRt2, _wCoScr, _wSuScr, _wGlScr, _pterWv;
    private readonly ushort _sply1Lives, _sply2Lives, _plyD1, _plyD2, _p1Dec, _p2Dec;
    private readonly ushort _intro1, _intro2, _coop1, _coop2, _coop3, _coop4, _surv1, _surv3, _surv4, _surv5;
    private readonly ushort _glad1, _glad2, _glad3, _glad4, _glad5, _glad6, _pter1;
    private readonly int _wPten, _joyTime;
    private readonly byte _pteId;
    private readonly Scoring _scoring;
    private readonly Dictionary<L, ushort> _address = [];

    private Asm a = null!;

    public WaveSetup(Symbols s, Scheduler scheduler, TextOut text, Scoring scoring)
    {
        _scheduler = scheduler;
        _text = text;
        _scoring = scoring;
        _pWprev = s["PWPREV"]; _pWave = s["PWAVE"]; _pBaits = s["PBAITS"]; _pDelay = s["PDELAY"]; _pWrts = s["PWRTS"];
        _pWrts2 = s["PWRTS2"]; _pIntel = s["PINTEL"]; _pMsgA = s["PMSGA"]; _pMsg0 = s["PMSG0"]; _pMsg1 = s["PMSG1"];
        _pMsg5 = s["PMSG5"]; _pBaitN = s["PBAITN"]; _link = s["PLINK"]; _posX = s["PPOSX"]; _posY = s["PPOSY"];
        _velX = s["PVELX"]; _frame = s["PFRAME"]; _joyT = s["PJOYT"]; _nap = s["PNAP"]; _dist = s["PDIST"];
        _decision = s["PDECSN"]; _horse = s["PHORSE"]; _egg = s["PEGG"]; _chase = s["PCHASE"];
        _wStatus = s["WSTATUS"]; _wPersue = s["WPERSUE"]; _wLord = s["WLORD"]; _wLen = s["WLEN"];
        _dyWEnd = s["DYWEND"]; _dyWTim = s["DYWTIM"]; _dyWInc = s["DYWINC"]; _dyWLen = s["DYWLEN"];
        _wavBcd = s["WAVBCD"]; _wavRts = s["WAVRTS"]; _wavTbl = s["WAVTBL"]; _wtbEnd = s["WTBEND"]; _wtbRst = s["WTBRST"];
        _trSmall = s["TRSMALL"]; _ga1 = s["GA1"]; _rcmsa = s["RCMSA"]; _dyTbl = s["DYTBL"]; _dyEnd = s["DYEND"];
        _dynAdj = s["DYNADJ"]; _safram = s["SAFRAM"]; _tBridge = s["TBRIDGE"]; _tTroll = s["TTROLL"];
        _pPrev = s["PPREV"]; _pExec = s["PEXEC"]; _pDummy = s["PDUMMY"]; _lavaB = s["LAVAB"]; _lavaF = s["LAVAF"];
        _eggs1 = s["EGGS1"]; _eggs2 = s["EGGS2"]; _wSmart = s["WSMART"]; _wEnemy = s["WENEMY"]; _emyTim = s["EMYTIM"];
        _plyG1 = s["PLYG1"]; _plyG2 = s["PLYG2"]; _gOver = s["GOVER"];
        _wClfTb = s["WCLFTB"]; _wClfT2 = s["WCLFT2"]; _wClfT3 = s["WCLFT3"]; _wClfT4 = s["WCLFT4"]; _wClfEw = s["WCLFEW"];
        _wJsrTb = s["WJSRTB"]; _p4Dec = s["P4DEC"]; _p5Dec = s["P5DEC"]; _p6Dec = s["P6DEC"];
        _creem = s["CREEM"]; _nEnemy = s["NENEMY"]; _nRider = s["NRIDER"]; _buzard = s["BUZARD"];
        _baisbl = s["BAISBL"]; _baitbl = s["BAITBL"]; _cBait = s["CBAIT"]; _nBait = s["NBAIT"]; _dBait = s["DBAIT"];
        _emy2 = s["EMY2"];
        _vNapTpc = s["VNAPTPC"]; _vCuProc = s["VCUPROC"]; _vCrProc = s["VCRPROC"]; _outPhr = s["OUTPHR"]; _outBcd = s["OUTBCD"];
        _baiterStarts = s["BAISND"] - s["BAISBL"];
        _floor = (int)s.Value("FLOOR"); _eLeft = (int)s.Value("ELEFT"); _eRight = (int)s.Value("ERIGHT");
        _emyId = (int)s.Value("EMYID"); _waveMsg = (int)s.Value("MSW06"); _white = (int)s.Value("WHI") * 0x11;
        _wbPer = (int)s.Value("WBPER"); _wbCls = (int)s.Value("WBCLS");
        _wIntro = s["WINTRO"]; _wCoop = s["WCOOP"]; _wGlad = s["WGLAD"]; _wPtero = s["WPTERO"]; _wavRt2 = s["WAVRT2"];
        _wCoScr = s["WCOSCR"]; _wSuScr = s["WSUSCR"]; _wGlScr = s["WGLSCR"]; _pterWv = s["PTERWV"];
        _sply1Lives = (ushort)(s["SPLY1"] + 6); _sply2Lives = (ushort)(s["SPLY2"] + 6);
        _plyD1 = s["PLYD1"]; _plyD2 = s["PLYD2"]; _p1Dec = s["P1DEC"]; _p2Dec = s["P2DEC"];
        _intro1 = s["INTRO1"]; _intro2 = s["INTRO2"]; _coop1 = s["COOP1"]; _coop2 = s["COOP2"]; _coop3 = s["COOP3"];
        _coop4 = s["COOP4"]; _surv1 = s["SURV1"]; _surv3 = s["SURV3"]; _surv4 = s["SURV4"]; _surv5 = s["SURV5"];
        _glad1 = s["GLAD1"]; _glad2 = s["GLAD2"]; _glad3 = s["GLAD3"]; _glad4 = s["GLAD4"]; _glad5 = s["GLAD5"];
        _glad6 = s["GLAD6"]; _pter1 = s["PTER1"];
        _wPten = s["WPTEN"]; _joyTime = s["PJOYT"]; _pteId = (byte)s.Value("PTEID");

        // Resume points. Instruction sizes: a variable is 2 bytes direct / 3 extended, n,U 2 or 3.
        int V(ushort v) => Symbols.AddressCost(v) == 0 ? 2 : 3;
        int I(int offset) => offset is >= -16 and <= 15 ? 2 : 3;
        void At(L l, ushort address) => _address[l] = address;
        At(L.Init, s["CIA"]);
        At(L.InitNap, (ushort)(s["CIA"] + V(_wavBcd) + 3 + I(_pWprev) + 3 + I(_pWave) + 2 + I(_pBaits) + 5));
        At(L.Next, s["IWAVE"]);
        At(L.Settings, (ushort)(s["IWAVE.1S"] + 3 + 3));        // LDX #GA1 / JSR RCMSA
        int previous = s["WNRM"] + V(_eggs1) + V(_eggs2) + V(_wSmart) + 2 + V(_wEnemy) + 2 + V(_emyTim);
        for (int m = 0; m < 6; m++) previous += I(_pMsg0 + 4 * m);
        previous += V(_wavBcd) + 2 + 1 + V(_wavBcd) + 3;           // ... DAA / STA / JSR [PWPREV,U]
        At(L.Previous, (ushort)previous);
        At(L.NumberNap, (ushort)(previous + 3 + 5));             // JSR PATC11 / PCNAP 1
        At(L.Cliff1, (ushort)(s["WNRM.1S"] + I(_pWave) + 2 + 2 + 2 + 2 + 3 + 3));
        At(L.Cliff2, (ushort)(_address[L.Cliff1] + 6));
        At(L.Cliff3, (ushort)(_address[L.Cliff1] + 12));
        At(L.Cliff4, (ushort)(_address[L.Cliff1] + 18));
        At(L.Started, (ushort)(s["WNRM.44S"] + 2 + 2 + 3 + 2));   // LDB / ANDB / LDX / JSR [B,X]
        At(L.MessageNap, (ushort)(s["REWMSG.2S"] + 5));
        At(L.DelayBack, (ushort)(s["WAVDEL.1S"] + 2));           // BSR REWMSG
        At(L.WaitBack, (ushort)(s["WATWAV.1S"] + 2));
        At(L.Delayed, (ushort)(s["WPAUS2"] + 3));                // JSR WAVDEL
        At(L.Waited, s["WPAUS3"]);                               // JSR WATWAV
        At(L.Bounders, s["WPAUS3.2S"]);                          // each after a JSR WCREATE
        At(L.Hunters, s["WPAUS3.3S"]);
        At(L.Lords, s["WPAUS3.4S"]);
        At(L.CreateNap, (ushort)(s["WCREATE.10S"] + 5));
        At(L.Intro, _wIntro);
        At(L.Team, _wCoop);
        At(L.Gladiator, _wGlad);
        At(L.Pterodactyls, _wPtero);
        At(L.IntroDelay, (ushort)(_wIntro + 3 + 3 + 2 + 3));     // LDX # / JSR WAVMSG / LDA # / JSR WAVDEL
        At(L.PteroDelay, (ushort)(_wPtero + 3 + 3 + 2 + 3));
        At(L.PteroWait, (ushort)(_wPtero + 3 + 3 + 2 + 3 + 3));  // JSR WATWAV
        At(L.GladDelay, (ushort)(_wGlad + V(_sply1Lives) + 2 + V(_sply2Lives) + 2 + 3 + I(_pWprev) + 2
                                 + V(s["PLYG1"]) + V(s["PLYG2"]) + 3 + 3 + 2 + 3));
    }

    public ushort Address(L label) => _address[label];

    public IReadOnlyDictionary<L, ushort> Addresses => _address;

    // ------------------------------------------------------------------ driver

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
            case L.Init:
                a.Clr(a.M(_wavBcd));                             // the wave starts at zero
                a.LddI(_wavRts);
                a.Std(a.U(_pWprev));                             // no previous wave to complete
                a.LddI(_wavTbl - _wLen);
                a.Std(a.U(_pWave));
                a.LdaI(_baiterStarts - 1);
                a.Sta(a.U(_pBaits));
                return (default, Nap(t, 32, L.InitNap));

            case L.InitNap:
                a.Bra();
                return Bridge(t);

            case L.Next:
                a.Lda(a.M(_trSmall));                            // smaller transporter area?
                if (!a.Br(a.Eq)) a.Dec(a.M(_trSmall));
                a.LdxI(_ga1);                                    // .1S: the difficulty setting
                return (default, Leave(t, 7 + Symbols.AddressCost(_rcmsa), _rcmsa, L.Settings));

            case L.Settings: return Settings(t);
            case L.Previous: return Previous(t);

            case L.NumberNap:
                a.Jsr();
                WaveNumber(t, show: true);                       // its return is in WaveNumber
                t.S += 2;
                return WaveTable(t);

            case L.Cliff1: return Cliffs(t, 1);
            case L.Cliff2: return Cliffs(t, 2);
            case L.Cliff3: return Cliffs(t, 3);
            case L.Cliff4: return Cliffs(t, 4);
            case L.Started: return Pause(t);
            case L.MessageNap: return NextMessage(t);

            case L.DelayBack:
                a.Dec(a.U(_pDelay));
                if (a.Br(a.Ne)) return Messages(t, L.DelayBack);
                return JumpVia(t, _pWrts);

            case L.Delayed:
                Coroutine(t, _address[L.Waited]);                // JSR WATWAV / LDX ,S++
                a.Stx(a.U(_pWrts));
                return WaitLoop(t);

            case L.WaitBack:
                a.Lda(a.M(_gOver));                              // game over: no more waiting
                if (!a.Br(a.Eq))
                {
                    a.Ldx(a.U(_link));                           // a process after this one?
                    if (a.Br(a.Ne)) return Messages(t, L.WaitBack);
                }
                return JumpVia(t, _pWrts);                       // .2S

            case L.Waited:
                CallEraseMessages(t);                            // JSR EMSGS
                LoadWaveByte(t, 0);                              // Bounders: upper nibble
                for (int i = 0; i < 4; i++) a.Lsra();
                if (a.Br(a.Eq)) return (L.Bounders, null);
                return Create(t, _p4Dec, L.Bounders);

            case L.Bounders:
                LoadWaveByte(t, 0);                              // Hunters: lower nibble
                a.AndaI(0x0F);
                if (a.Br(a.Eq)) return (L.Hunters, null);
                return Create(t, _p5Dec, L.Hunters);

            case L.Hunters:
                a.Ldx(a.U(_pWave));
                a.Lda(a.X(_wLord));                              // Shadow Lords: upper nibble
                for (int i = 0; i < 4; i++) a.Lsra();
                if (a.Br(a.Eq)) return (L.Lords, null);
                return Create(t, _p6Dec, L.Lords);

            case L.Lords: return Begin(t);
            case L.Intro: return Intro(t);
            case L.Team: return Team(t);
            case L.Gladiator: return Gladiator(t);
            case L.Pterodactyls: return Pterodactyls(t);

            case L.IntroDelay:
                CallMessage(t, _intro2);
                a.LdaI(90 / 6);
                a.Jmp();                                         // JMP WPAUS2
                return Delay(t, L.Delayed);

            case L.GladDelay:
                CallMessage(t, _glad2);
                CallMessage(t, _glad3);
                a.LdaI(90 / 6);
                a.Jmp();
                return Delay(t, L.Delayed);

            case L.PteroDelay:
                Coroutine(t, _address[L.PteroWait]);             // JSR WATWAV
                a.Stx(a.U(_pWrts));
                return WaitLoop(t);

            case L.PteroWait:
                a.LdxI(_pterWv);                                 // SECCR PTERWV,PTEID
                a.LddI((_pteId << 8) | 0xFF);
                t.Cycles += 7 + Symbols.AddressCost(_vCrProc) + 4;
                _scheduler.Create(t, afterRunning: true);
                t.Cycles += 5;
                a.Ldx(a.U(_pWave));
                a.Lda(a.X(_wPten));                              // the wave's pterodactyls
                a.Sta(a.Y(_joyTime));
                a.Jmp();                                         // JMP WPAUS3
                return (L.Waited, null);
            case L.CreateNap: return NextEnemy(t);
        }
        throw new InvalidOperationException($"unknown label {label}");
    }

    // ------------------------------------------------------------------ difficulty and lava

    /// <summary>After RCMSA (A = difficulty): step each dynamic adjustment whose time has come; raise the lava.</summary>
    private (L, ushort?) Settings(Tx t)
    {
        t.S--;                                                   // STA ,-S: the difficulty
        t.Write(t.S, t.A);
        t.CC = Logic8(t.CC, t.A);
        t.Cycles += 6;
        a.LdxI(_dyTbl);                                          // ROM: the adjustment table
        t.Y = _dynAdj;                                           // LDY #DYNADJ: RAM
        t.CC = Logic16(t.CC, t.Y);
        t.Cycles += 4;
        do
        {
            a.Ldb(a.Y(2));                                       // .2S: time to change the value?
            bool change = a.Br(a.Eq);
            if (!change)
            {
                a.Dec(a.Y(2));
                change = !a.Br(a.Ne);
            }
            if (change)
            {
                a.Ldb(a.S(0));                                   // .4S: the difficulty
                a.AddbI(_dyWTim * 2);
                a.Asrb();                                        // nibble offset; carry = lower nibble
                a.LdbBX();
                if (!a.Br(Cs(t.CC)))
                {
                    for (int i = 0; i < 4; i++) a.Lsrb();
                }
                a.AndbI(0x0F);                                   // .5S: waves until the next step
                a.Stb(a.Y(2));
                a.Ldb(a.X(_dyWInc));
                a.Sex();
                bool down = a.Br(a.Mi);
                a.Addd(a.Y(0));                                  // the new value
                a.Cmpd(a.X(_dyWEnd));
                bool keep;
                if (!down)
                {
                    keep = a.Br(a.Lt);
                    if (!keep) a.Bra();
                }
                else
                {
                    keep = a.Br(a.Gt);                           // .6S
                }
                if (!keep) a.Ldd(a.X(_dyWEnd));                  // .7S: capped
                a.Std(a.Y(0));                                   // .8S
            }
            a.Leax(_dyWLen);                                     // .15S
            a.Leay(3);
            a.CmpxI(_dyEnd);
        }
        while (a.Br(a.Lo));
        t.A = t.Read(t.S);                                       // PULS A
        t.S++;
        t.Cycles += 6;
        a.Lda(a.M(_safram));                                     // raise the lava if it can
        a.CmpaI(0xE0);
        if (!a.Br(a.Ls))
        {
            a.SubaI(5);
            a.Sta(a.M(_safram));
        }
        return Bridge(t);
    }

    /// <summary>IWAVE2: the bridge burns when its countdown ends; a wave later the troll's flames start.</summary>
    private (L, ushort?) Bridge(Tx t)
    {
        a.Lda(a.M(_tBridge));
        if (!a.Br(a.Eq))
        {
            a.Dec(a.M(_tBridge));
            if (a.Br(a.Ne)) return Normal(t);
            StartBridge(t);                                      // burning left to right
            a.LddI(-0x20);
            a.Std(a.Y(_dist));
            a.LdaI(1);
            a.Sta(a.Y(_velX));
            a.LdaI(1);
            a.Sta(a.Y(_joyT));
            StartBridge(t);                                      // and right to left
            a.LddI(0x13F);
            a.Std(a.Y(_dist));
            a.LdaI(7);
            a.Sta(a.Y(_velX));
            a.LdaI(-1);
            a.Sta(a.Y(_joyT));
            a.Bra();
            return Normal(t);
        }
        a.Lda(a.M(_tTroll));                                     // WTROLL
        if (a.Br(a.Eq)) return Normal(t);
        a.Dec(a.M(_tTroll));
        if (a.Br(a.Ne)) return Normal(t);
        a.LdxI(_lavaF);                                          // the left lava flame
        a.LddI(0x69FF);
        a.Ldu(a.M(_pDummy));                                     // second in the process list
        CreateAfter(t);
        a.LddI(_floor + 16);
        a.Std(a.Y(_posY));
        a.LddI(_eLeft + 30);
        a.Std(a.Y(_posX));
        a.Clr(a.Y(_frame));
        a.LdxI(_lavaF);                                          // the right one
        a.LddI(0x69FF);
        CreateAfter(t);
        a.Ldu(a.M(_pExec));
        a.LddI(_floor + 16);
        a.Std(a.Y(_posY));
        a.LddI(_eRight - 30);
        a.Std(a.Y(_posX));
        a.LdaI(2);
        a.Sta(a.Y(_frame));
        a.LdaI(4);                                               // out of step with the other
        a.Sta(a.Y(_nap));
        return Normal(t);
    }

    /// <summary>JSR STBRID: a bridge-burning process (LAVAB) before this one.</summary>
    private void StartBridge(Tx t)
    {
        a.Jsr();
        a.Ldu(a.M(_pPrev));
        a.LdxI(_lavaB);
        a.LddI(0x6AFF);
        CreateAfter(t);
        a.Ldu(a.M(_pExec));
        a.LddI(_floor);
        a.Std(a.Y(_posY));
        a.Clr(a.Y(_frame));
        a.Rts();
    }

    /// <summary>JSR VCUPROC.</summary>
    private void CreateAfter(Tx t)
    {
        t.Cycles += 7 + Symbols.AddressCost(_vCuProc) + 4;
        _scheduler.Create(t, afterRunning: false);
        t.Cycles += 5;
    }

    // ------------------------------------------------------------------ the next wave

    /// <summary>WNRM: reset the wave's counters and messages, next wave number, previous wave's clean-up.</summary>
    private (L, ushort?) Normal(Tx t)
    {
        a.Clr(a.M(_eggs1));
        a.Clr(a.M(_eggs2));
        a.Clr(a.M(_wSmart));                                     // no smart enemies yet
        a.LdaI(255);
        a.Sta(a.M(_wEnemy));
        a.LdaI(1);
        a.Sta(a.M(_emyTim));                                     // no enemy slow-down
        for (int m = 0; m < 6; m++)
            a.Clr(a.U(_pMsg0 + 4 * m));
        a.Lda(a.M(_wavBcd));
        a.AddaI(0x01);
        a.Daa();
        a.Sta(a.M(_wavBcd));
        ushort cleanUp = t.Read16(t.U + _pWprev);                // JSR [PWPREV,U]
        int jsr = 7 + Symbols.IndirectCost(_pWprev);
        if (cleanUp == _wCoScr || cleanUp == _wSuScr || cleanUp == _wGlScr)
        {
            t.Cycles += jsr;                                     // its JMP WAVDEL pops the return address
            return cleanUp == _wCoScr ? EndTeam(t) : cleanUp == _wSuScr ? EndSurvival(t) : EndGladiator(t);
        }
        if (cleanUp != _wavRts) return (default, Leave(t, jsr, cleanUp, L.Previous));
        t.Cycles += jsr + 5;                                     // WAVRTS: nothing to do
        return Previous(t);
    }

    /// <summary>After the clean-up: PATC11 (gladiator counts, erase the messages), then nap.</summary>
    private (L, ushort?) Previous(Tx t)
    {
        a.Jsr();                                                 // JSR PATC11
        a.Clr(a.M(_plyG1));
        a.Clr(a.M(_plyG2));
        a.Jmp();                                                 // JMP EMSGS
        EraseMessages(t);
        t.S += 2;
        return (default, Nap(t, 1, L.NumberNap));
    }

    /// <summary>After "WAVE n": step the wave table (wrapping to WTBRST) and change the cliffs that differ.</summary>
    private (L, ushort?) WaveTable(Tx t)
    {
        a.LddI(_wavRts);
        a.Std(a.U(_pWprev));
        a.Ldx(a.U(_pWave));
        a.Lda(a.X(_wStatus));                                    // the old cliff state
        a.Leax(_wLen);
        a.CmpxI(_wtbEnd);
        if (!a.Br(a.Lo)) a.LdxI(_wtbRst);
        a.Stx(a.U(_pWave));                                      // .1S
        t.A ^= t.Read(t.X + _wStatus);                           // EORA WSTATUS,X: cliffs changed?
        t.CC = Logic8(t.CC, t.A);
        t.Cycles += 4 + Symbols.IndexCost(_wStatus);
        a.AndaI(_wbCls);
        if (a.Br(a.Eq)) return (L.Cliff4, null);                 // .4S
        a.Ldb(a.X(_wStatus));                                    // the new state
        return Cliffs(t, 0);
    }

    /// <summary>JSR WCLFEW for each cliff from <paramref name="done"/>+1; a changing one runs the original.</summary>
    private (L, ushort?) Cliffs(Tx t, int done)
    {
        ushort[] tables = [_wClfTb, _wClfT2, _wClfT3, _wClfT4];
        L[] after = [L.Cliff1, L.Cliff2, L.Cliff3, L.Cliff4];
        for (int i = done; i < 4; i++)
        {
            a.LdxI(tables[i]);
            if ((t.A & t.Read(t.X)) != 0)
                return (default, Leave(t, 8, _wClfEw, after[i])); // this cliff appears or vanishes
            a.Jsr();                                             // unchanged: PSHS D,X / BITA ,X / BEQ / PULS D,X,PC
            t.Cycles += 9;
            a.Bita(a.X(0));
            a.Br(a.Eq);
            t.Cycles += 11 - 5;
            a.Rts();
        }
        return done == 4 ? Persue(t) : (L.Cliff4, null);         // .4S
    }

    /// <summary>.4S: early pursuit, then the wave type's routine (JSR [B,X] into WJSRTB).</summary>
    private (L, ushort?) Persue(Tx t)
    {
        a.Ldx(a.U(_pWave));
        a.Lda(a.X(_wStatus));
        a.BitaI(_wbPer);
        if (!a.Br(a.Eq))
        {
            a.Lda(a.X(_wPersue));
            a.AndaI(0x0F);
            a.Sta(a.M(_wSmart));
        }
        a.Ldb(a.X(_wStatus));                                    // .44S
        a.AndbI(0x0E);
        a.LdxI(_wJsrTb);
        ushort routine = t.Read16(t.X + (sbyte)t.B);
        int jsr = 7 + 1 + 3;                                     // JSR [B,X]
        if (routine == _wIntro || routine == _wGlad || routine == _wPtero || routine == _wCoop)
        {
            t.Cycles += jsr;
            t.Push16(_address[L.Started]);
            return (routine == _wIntro ? L.Intro : routine == _wGlad ? L.Gladiator
                : routine == _wPtero ? L.Pterodactyls : L.Team, null);
        }
        if (routine != _wavRts) return (default, Leave(t, jsr, routine, L.Started));
        t.Cycles += jsr + 5;
        return Pause(t);
    }

    // ------------------------------------------------------------------ pauses and messages

    /// <summary>WPAUSE: show the messages for 30 x (one pass per message) ticks (JSR WAVDEL).</summary>
    private (L, ushort?) Pause(Tx t)
    {
        a.LdaI(180 / 6);
        return Delay(t, L.Delayed);                              // WPAUS2
    }

    /// <summary>JSR WAVDEL (A = passes): rewrite the messages A times, then continue at <paramref name="returnTo"/>.</summary>
    private (L, ushort?) Delay(Tx t, L returnTo)
    {
        Coroutine(t, _address[returnTo]);                        // JSR WAVDEL / LDX ,S++
        a.Stx(a.U(_pWrts));
        a.Sta(a.U(_pDelay));
        return Messages(t, L.DelayBack);
    }

    /// <summary>WATWAV.1S: rewrite the messages until this is the last process (or the game is over).</summary>
    private (L, ushort?) WaitLoop(Tx t) => Messages(t, L.WaitBack);

    /// <summary>JSR to a coroutine that pops its return address with LDX ,S++ (or LDD ,S++).</summary>
    private static void Coroutine(Tx t, ushort returnTo)
    {
        t.X = returnTo;
        t.CC = Logic16(t.CC, t.X);
        t.Cycles += 8 + 8;
    }

    /// <summary>BSR REWMSG: rewrite "WAVE n" and each message, one per tick; return through PWRTS2.</summary>
    private (L, ushort?) Messages(Tx t, L returnTo)
    {
        t.Cycles += 7 + 7;                                       // BSR REWMSG / PULS D
        t.D = _address[returnTo];
        a.Std(a.U(_pWrts2));
        a.Lda(a.M(_gOver));                                      // game over?
        if (a.Br(a.Eq)) return JumpVia(t, _pWrts2);
        a.LdaI(_pMsg1);                                          // start at message 1
        a.Sta(a.U(_pMsgA));
        a.Lda(a.U(_pMsg0));                                      // "WAVE n" up?
        if (!a.Br(a.Eq))
        {
            a.Jsr();
            WaveNumber(t, show: true);
            t.S += 2;
        }
        return (default, Nap(t, 1, L.MessageNap));               // .2S
    }

    /// <summary>After REWMSG's nap: the next message, or return through PWRTS2.</summary>
    private (L, ushort?) NextMessage(Tx t)
    {
        a.Lda(a.U(_pMsgA));
        a.CmpaI(_pMsg5);                                         // the last one done?
        if (a.Br(a.Hi)) return JumpVia(t, _pWrts2);
        a.LeayAU();
        a.AddaI(4);                                              // PMSG2-PMSG1
        a.Sta(a.U(_pMsgA));
        t.Cycles += 7;                                           // BSR WMSG2
        WriteMessage(t);
        a.Bra();
        return (default, Nap(t, 1, L.MessageNap));
    }

    /// <summary>WMSG2 (with its return): message Y (number, colour, screen address), if any.</summary>
    private void WriteMessage(Tx t)
    {
        a.Lda(a.Y(0));
        if (a.Br(a.Eq)) { t.Cycles += 5; return; }               // WVMRTS
        a.Ldb(a.Y(1));
        a.Ldx(a.Y(2));
        t.Cycles += 3 + Symbols.AddressCost(_outPhr) + 4;        // JMP OUTPHR / JMP PHROUT
        _text.Write(t, phrase: true, smallFont: false);
    }

    /// <summary>JMP [offset,U]: continue at a resume point of this process, else in the original.</summary>
    private (L, ushort?) JumpVia(Tx t, int offset)
    {
        ushort target = t.Read16(t.U + offset);
        t.Cycles += 3 + Symbols.IndirectCost(offset);
        foreach (var (label, address) in _address)
            if (address == target && label is L.DelayBack or L.WaitBack or L.Delayed or L.Waited
                    or L.Bounders or L.Hunters or L.Lords or L.Previous or L.IntroDelay or L.GladDelay
                    or L.PteroDelay or L.PteroWait)
                return (label, null);
        return (default, target);
    }

    /// <summary>WAVEN / WAVEN2 (with the return of its final BCD output): show or erase "WAVE n".</summary>
    private void WaveNumber(Tx t, bool show)
    {
        if (show)
        {
            a.LddI((_waveMsg << 8) | _white);
            a.Sta(a.U(_pMsg0));                                  // this message is up
            a.Bra();
        }
        else
        {
            a.LddI(_waveMsg << 8);
            a.Clr(a.U(_pMsg0));
        }
        a.LdxI(0x3A60);                                          // WAVEN3
        t.Cycles += 7 + Symbols.AddressCost(_outPhr) + 4;        // JSR OUTPHR / JMP PHROUT
        _text.Write(t, phrase: true, smallFont: false);
        a.Lda(a.M(_wavBcd));
        a.BitaI(0xF0);
        if (!a.Br(a.Ne)) a.OraI(0xF0);                           // blank a leading zero
        t.Cycles += 3 + Symbols.AddressCost(_outBcd) + 4;        // JMP OUTBCD / JMP BCDOUT
        _text.WriteBcd(t);
    }

    /// <summary>JSR EMSGS.</summary>
    private void CallEraseMessages(Tx t)
    {
        a.Jsr();
        EraseMessages(t);
        t.S += 2;
    }

    /// <summary>EMSGS (with its return): erase "WAVE n" and every message still up.</summary>
    private void EraseMessages(Tx t)
    {
        a.Lda(a.U(_pMsg0));
        if (!a.Br(a.Eq))
        {
            a.Jsr();
            WaveNumber(t, show: false);
            t.S += 2;
        }
        for (int m = 1; m <= 5; m++)
        {
            a.LeayU(_pMsg0 + 4 * m);
            if (m < 5) t.Cycles += 7;                            // BSR .1S (the last falls in)
            EraseMessage(t);
        }
    }

    /// <summary>EMSGS.1S (with its return): erase message Y if it is up.</summary>
    private void EraseMessage(Tx t)
    {
        a.Lda(a.Y(0));
        if (a.Br(a.Eq)) { t.Cycles += 5; return; }
        a.Clr(a.Y(0));                                           // forget this message
        a.Clrb();
        a.Ldx(a.Y(2));
        t.Cycles += 3 + Symbols.AddressCost(_outPhr) + 4;
        _text.Write(t, phrase: true, smallFont: false);
    }

    // ------------------------------------------------------------------ enemies

    /// <summary>LDA [PWAVE,U].</summary>
    private void LoadWaveByte(Tx t, int offset)
    {
        t.A = t.Read(t.Read16(t.U + _pWave) + offset);
        t.CC = Logic8(t.CC, t.A);
        t.Cycles += 4 + Symbols.IndirectCost(_pWave);
    }

    /// <summary>JSR WCREATE: A enemies with decision block <paramref name="decision"/>, one per second.</summary>
    private (L, ushort?) Create(Tx t, ushort decision, L returnTo)
    {
        a.LdxI(decision);
        t.Cycles += 8;                                           // JSR WCREATE
        a.Sta(a.U(_pDelay));
        t.D = _address[returnTo];                                // LDD ,S++
        t.CC = Logic16(t.CC, t.D);
        t.Cycles += 8;
        a.Std(a.U(_pWrts2));
        a.Stx(a.U(_pIntel));
        return (default, Nap(t, 61, L.CreateNap));               // .10S
    }

    /// <summary>After WCREATE's nap: an enemy comes through a transporter (CREEM); then the next or return.</summary>
    private (L, ushort?) NextEnemy(Tx t)
    {
        a.LdxI(_creem);                                          // SECCR CREEM,EMYID
        a.LddI((_emyId << 8) | 0xFF);
        t.Cycles += 7 + Symbols.AddressCost(_vCrProc) + 4;
        _scheduler.Create(t, afterRunning: true);
        t.Cycles += 5;
        a.Inc(a.M(_nEnemy));
        a.Inc(a.M(_nRider));
        a.Ldd(a.U(_pIntel));
        a.Std(a.Y(_decision));
        a.Ldd(a.M(_buzard));                                     // a buzzard
        a.Std(a.Y(_horse));
        a.LdaI(4);                                               // eggs to lay at most
        a.Sta(a.Y(_egg));
        a.Clr(a.Y(_chase));
        a.Lda(a.M(_tBridge));                                    // first or second wave?
        if (!a.Br(a.Eq))
        {
            a.LdaI(2);
            a.Sta(a.M(_emyTim));                                 // slow the enemies down
        }
        a.Dec(a.U(_pDelay));                                     // .20S
        if (a.Br(a.Ne)) return (default, Nap(t, 61, L.CreateNap));
        return JumpVia(t, _pWrts2);
    }

    /// <summary>WBEGIN: the wave's pursuers, the baiter timetable, then the in-play loop (EMY2).</summary>
    private (L, ushort?) Begin(Tx t)
    {
        a.Lda(a.M(_gOver));
        if (!a.Br(a.Eq))
        {
            a.Ldx(a.U(_pWave));
            a.Lda(a.X(_wPersue));
            a.AndaI(0x0F);
            a.Sta(a.M(_wSmart));
        }
        a.Lda(a.U(_pBaits));                                     // .5S
        if (!a.Br(a.Eq)) a.Dec(a.U(_pBaits));
        a.LdxI(_baisbl);                                         // .6S
        t.A = t.Read(t.X + (sbyte)t.A);                          // LDA A,X: the timetable start
        t.CC = Logic8(t.CC, t.A);
        t.Cycles += 5;
        a.Sta(a.U(_pBaitN));
        a.Inca();
        t.A = Asl8(ref t.CC, t.A);                               // ASLA
        t.Cycles += 2;
        a.LdxI(_baitbl);
        t.D = t.Read16(t.X + (sbyte)t.A);                        // LDD A,X: the first send-off time
        t.CC = Logic16(t.CC, t.D);
        t.Cycles += 6;
        a.Std(a.M(_cBait));
        a.Clr(a.M(_nBait));
        a.Clr(a.M(_dBait));
        return (default, _emy2);
    }

    // ------------------------------------------------------------------ wave types

    /// <summary>WINTRO: the two introduction messages, a second apart.</summary>
    private (L, ushort?) Intro(Tx t)
    {
        CallMessage(t, _intro1);
        a.LdaI(90 / 6);
        return Delay(t, L.IntroDelay);
    }

    /// <summary>WPTERO: "pterodactyl beware", then once the last wave is gone the pterodactyls (PTERWV).</summary>
    private (L, ushort?) Pterodactyls(Tx t)
    {
        CallMessage(t, _pter1);
        a.LdaI(180 / 6);
        return Delay(t, L.PteroDelay);
    }

    /// <summary>WCOOP: with both players alive a team wave (3,000 each if neither kills the other), else WAVSUR.</summary>
    private (L, ushort?) Team(Tx t)
    {
        a.Lda(a.M(_sply2Lives));
        bool survival = a.Br(a.Eq);
        if (!survival)
        {
            a.Lda(a.M(_sply1Lives));
            survival = a.Br(a.Eq);
        }
        if (survival)
        {
            a.LddI(_wSuScr);                                     // WAVSUR: survival wave
            a.Std(a.U(_pWprev));
            a.Clr(a.M(_plyD1));
            a.Clr(a.M(_plyD2));
            a.LdxI(_surv1);
            a.Jmp();                                             // JMP WAVMSG
            WaveMessage(t);
        }
        else
        {
            a.LddI(_wCoScr);
            a.Std(a.U(_pWprev));
            a.Clr(a.M(_plyG1));
            a.Clr(a.M(_plyG2));
            CallMessage(t, _coop1);
            a.LdxI(_coop2);
            a.Jmp();
            WaveMessage(t);
        }
        t.S += 2;                                                // WAVMSG's RTS returns from the JSR [B,X]
        return Pause(t);
    }

    /// <summary>WGLAD: with both players alive, the gladiator wave's three messages; else nothing.</summary>
    private (L, ushort?) Gladiator(Tx t)
    {
        a.Lda(a.M(_sply1Lives));
        bool skip = a.Br(a.Eq);
        if (!skip)
        {
            a.Lda(a.M(_sply2Lives));
            skip = a.Br(a.Eq);
        }
        if (skip)
        {
            t.Cycles += 5;                                       // WAVRT2: RTS
            t.S += 2;
            return Pause(t);
        }
        a.LddI(_wGlScr);
        a.Std(a.U(_pWprev));
        a.LdaI(-1);                                              // the first to kill the other scores
        a.Sta(a.M(_plyG1));
        a.Sta(a.M(_plyG2));
        CallMessage(t, _glad1);
        a.LdaI(90 / 6);
        return Delay(t, L.GladDelay);
    }

    /// <summary>WCOSCR: the team bonus, 3,000 to each living player unless they fought each other.</summary>
    private (L, ushort?) EndTeam(Tx t)
    {
        a.Lda(a.M(_plyG1));
        a.Ora(a.M(_plyG2));
        if (!a.Br(a.Eq))
        {
            a.LdxI(_coop3);                                      // no bonus
            a.Bra();
            return EndMessage(t);
        }
        a.Lda(a.M(_sply1Lives));                                 // .1S
        if (!a.Br(a.Eq)) Bonus(t, _p1Dec);
        a.Lda(a.M(_sply2Lives));                                 // .2S
        if (!a.Br(a.Eq)) Bonus(t, _p2Dec);
        a.LdxI(_coop4);                                          // .3S: bonus awarded
        return EndMessage(t);
    }

    /// <summary>WSUSCR: 3,000 to the surviving player if they did not die this wave.</summary>
    private (L, ushort?) EndSurvival(Tx t)
    {
        a.LdxI(_surv3);                                          // assume no points
        a.Lda(a.M(_sply1Lives));
        if (!a.Br(a.Eq))
        {
            a.Lda(a.M(_plyD1));
            if (a.Br(a.Ne)) return EndMessage(t);
            Bonus(t, _p1Dec);
            a.LdxI(_surv4);
            a.Bra();
            return EndMessage(t);
        }
        a.Lda(a.M(_sply2Lives));                                 // .1S
        if (a.Br(a.Eq)) return EndMessage(t);
        a.Lda(a.M(_plyD2));
        if (a.Br(a.Ne)) return EndMessage(t);
        Bonus(t, _p2Dec);
        a.LdxI(_surv5);
        a.Bra();
        return EndMessage(t);
    }

    /// <summary>WGLSCR: the gladiator bounty message; no more bounties.</summary>
    private (L, ushort?) EndGladiator(Tx t)
    {
        a.Lda(a.M(_plyG1));
        if (a.Br(a.Pl))
        {
            a.LdxI(_glad6);                                      // .2S: player 2 got the bounty
        }
        else
        {
            a.LdxI(_glad5);
            a.Lda(a.M(_plyG2));
            if (!a.Br(a.Pl))
            {
                a.LdxI(_glad4);                                  // no one did
                a.Bra();
            }
        }
        CallMessage(t);                                          // .3S
        a.Clr(a.M(_plyG1));
        a.Clr(a.M(_plyG2));
        a.LdaI(120 / 6);
        a.Jmp();                                                 // JMP WAVDEL
        return Delay(t, L.Previous);
    }

    /// <summary>ENDTS: message X, then a two-second WAVDEL back to the wave set-up.</summary>
    private (L, ushort?) EndMessage(Tx t)
    {
        CallMessage(t);
        a.LdaI(120 / 6);
        a.Jmp();                                                 // JMP WAVDEL
        return Delay(t, L.Previous);
    }

    /// <summary>LDX #decision / LDA # / JSR SCRHUN: 3,000 points.</summary>
    private void Bonus(Tx t, ushort decision)
    {
        a.LdxI(decision);
        a.LdaI(0x30);
        t.Cycles += 8;
        if (!_scoring.Add(t, tens: false)) throw new HandBackException();   // an extra mount: original
        t.Cycles += 5;
    }

    /// <summary>LDX #table / JSR WAVMSG.</summary>
    private void CallMessage(Tx t, ushort table)
    {
        a.LdxI(table);
        CallMessage(t);
    }

    /// <summary>JSR WAVMSG with X = the message's table entry.</summary>
    private void CallMessage(Tx t)
    {
        a.Jsr();
        WaveMessage(t);
        t.S += 2;
    }

    /// <summary>WAVMSG (with its return): put message X in the first free slot and show it.</summary>
    private void WaveMessage(Tx t)
    {
        a.LeayU(_pMsg1 - 4);
        do
        {
            a.Leay(4);
            a.Tst(a.Y(0));
        }
        while (a.Br(a.Ne));
        a.Lda(a.M(_gOver));
        if (a.Br(a.Eq)) { t.Cycles += 5; return; }               // WVMRTS
        a.Ldd(a.X(0));
        a.Std(a.Y(0));
        a.Ldd(a.X(2));
        a.Std(a.Y(2));
        WriteMessage(t);                                         // falls into WMSG2
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>PCNAP n.</summary>
    private ushort Nap(Tx t, byte ticks, L resume)
    {
        a.LdaI(ticks);
        t.Cycles += 7 + Symbols.AddressCost(_vNapTpc) + 4;
        t.Push16(_address[resume]);
        return _scheduler.Nap(t, fromJsr: true) ?? throw new InvalidOperationException("nap without a continuation");
    }

    /// <summary>A JSR to code that is not native: leave with the return address pushed.</summary>
    private ushort Leave(Tx t, int jsrCycles, ushort target, L returnTo)
    {
        t.Cycles += jsrCycles;
        t.Push16(_address[returnTo]);
        return target;
    }
}
