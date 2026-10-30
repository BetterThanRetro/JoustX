using static Joust.Emulation.Alu6809;

namespace Joust.Port.Routines;

/// <summary>
/// Entering play through a transporter, and dying in the lava (JOUSTRV4.ASM, "BEING
/// CREATED/RE-CREATED", PLAYR / PLYLI2 / PLYINT, ADGFLR).
/// <list type="number">
/// <item>Queue (CREP1/CREP2, CREEM, CRELP): a player loses a life and takes a number; enemies take
///   a number too but wait until no player is queued. One bird is served per tick.</item>
/// <item>Choice (CREPLY, CREALL, FREET, GOTRn): a player prefers a transporter on a level nobody
///   occupies; otherwise a random transporter with nobody near it (FREET marks a transporter
///   crowded when an object, projected by its speed, is inside its safety zone).</item>
/// <item>Fade-in (GOTTR, TREFF, TREFF2): 30 frames of the transporter lit in the bird's colour,
///   the bird growing out of it for the last 20.</item>
/// <item>Waiting (TREFF2.40S): the bird stands, the transporter blinking, until its first move
///   or its time runs out; then it joins the bird process (PLYINT, PLYRS2).</item>
/// <item>Lava (ADGFLR): the bird sinks, dies when it is under, and is created again.</item>
/// </list>
/// A state machine over the labels and resume points, like the egg. Losing a life (DECLIV,
/// which also updates the audits), a joystick routine that is not native, and a death routine
/// that is not native leave with the return address pushed and resume here.
/// </summary>
public sealed class Transporter
{
    public enum L
    {
        Create, CreateLives, CreateEnemy, QueueNap, FadeNap, WaitNap, WaitJoy,
        Start, StartNap, StartLives, Reborn, Lava, SinkNap, Sunk, SunkNap,
    }

    private readonly Ground _ground;
    private readonly Scheduler _scheduler;
    private readonly Eggs _eggs;
    private readonly Deaths _deaths;
    private readonly BirdProcess _bird;

    // Process block
    private readonly int _link, _id, _posX, _posY, _velX, _velY, _timX, _timeUp, _frame, _accX, _bumpX, _bumpY;
    private readonly int _feet, _lantz, _state, _dist, _picH, _picR, _rider, _decision, _joy;

    // Decision block
    private readonly int _dJoy, _dPlyr, _dStart, _dSnCre, _dSnTref, _dConst, _dTreff, _dDead, _dCre;

    // DMA block, transporter tables
    private readonly int _wcDma, _wcX, _wcY, _wcLenX, _wcLenY, _wcDest, _wcSrc, _wcLen;
    private readonly int _fxRev, _fxMin, _fxMax, _fLevel, _ftLen, _tImage, _tPosX, _tPosY, _tCurUse;

    private readonly ushort _curJoy, _random, _pDummy, _pExec, _dmaFree, _dmaEndWrite;
    private readonly ushort _nPServ, _lPServ, _nEServ, _lEServ, _area1, _area2, _area3, _trSmall;
    private readonly ushort _curTr1, _curTr3, _stTr1, _stTr2, _stTr3, _stTr4, _tarPly, _tarPl2, _tarTim, _tarTm1, _tarTm2;
    private readonly ushort _trEny, _trPly, _tr1, _tr2, _tr3, _tr4, _plyBr, _snPLav, _snELav;
    private readonly ushort _decLiv, _plyRs2, _plyRLp, _stPly, _stEnmy, _death1, _death2, _death3, _death4;
    private readonly ushort _vNapTpc, _vSucide, _vSnd, _vRand, _vWr1All;
    private readonly int _playerId, _eggId, _stand, _land5, _floor, _pl1, _pl2, _eLeft, _eRight;
    private readonly Dictionary<L, ushort> _address = [];

    private Asm a = null!;

    public Transporter(Symbols s, Ground ground, Scheduler scheduler, Eggs eggs, Deaths deaths, BirdProcess bird)
    {
        _ground = ground;
        _scheduler = scheduler;
        _eggs = eggs;
        _deaths = deaths;
        _bird = bird;
        _link = s["PLINK"]; _id = s["PID"]; _posX = s["PPOSX"]; _posY = s["PPOSY"]; _velX = s["PVELX"];
        _velY = s["PVELY"]; _timX = s["PTIMX"]; _timeUp = s["PTIMUP"]; _frame = s["PFRAME"]; _accX = s["PACCX"];
        _bumpX = s["PBUMPX"]; _bumpY = s["PBUMPY"]; _feet = s["PFEET"]; _lantz = s["PLANTZ"]; _state = s["PSTATE"];
        _dist = s["PDIST"]; _picH = s["PPICH"]; _picR = s["PPICR"]; _rider = s["PRIDER"]; _decision = s["PDECSN"];
        _joy = s["PJOY"];
        _dJoy = s["DJOY"]; _dPlyr = s["DPLYR"]; _dStart = s["DSTART"]; _dSnCre = s["DSNCRE"]; _dSnTref = s["DSNTREF"];
        _dConst = s["DCONST"]; _dTreff = s["DTREFF"]; _dDead = s["DDEAD"]; _dCre = s["DCRE"];
        _wcDma = s["WCDMA"]; _wcX = s["WCX"]; _wcY = s["WCY"]; _wcLenX = s["WCLENX"]; _wcLenY = s["WCLENY"];
        _wcDest = s["WCDEST"]; _wcSrc = s["WCSRC"]; _wcLen = s["WCLEN"];
        _fxRev = s["FXREV"]; _fxMin = s["FXMIN"]; _fxMax = s["FXMAX"]; _fLevel = s["FLEVEL"]; _ftLen = s["FTLEN"];
        _tImage = s["TIMAGE"]; _tPosX = s["TPOSX"]; _tPosY = s["TPOSY"]; _tCurUse = s["TCURUSE"];
        _curJoy = s["CURJOY"]; _random = s["RANDOM"]; _pDummy = s["PDUMMY"]; _pExec = s["PEXEC"]; _dmaFree = s["DMAFRE"];
        _dmaEndWrite = (ushort)(s["DMAEND"] + s["WRITE"]);
        _nPServ = s["NPSERV"]; _lPServ = s["LPSERV"]; _nEServ = s["NESERV"]; _lEServ = s["LESERV"];
        _area1 = s["AREA1"]; _area2 = s["AREA2"]; _area3 = s["AREA3"]; _trSmall = s["TRSMALL"];
        _curTr1 = s["CURTR1"]; _curTr3 = s["CURTR3"]; _stTr1 = s["STTR1"]; _stTr2 = s["STTR2"]; _stTr3 = s["STTR3"];
        _stTr4 = s["STTR4"]; _tarPly = s["TARPLY"]; _tarPl2 = s["TARPL2"]; _tarTim = s["TARTIM"];
        _tarTm1 = s["TARTM1"]; _tarTm2 = s["TARTM2"];
        _trEny = s["TRENY"]; _trPly = s["TRPLY"]; _tr1 = s["TR1ID"]; _tr2 = s["TR2ID"]; _tr3 = s["TR3ID"]; _tr4 = s["TR4ID"];
        _plyBr = s["PLYBR"]; _snPLav = s["SNPLAV"]; _snELav = s["SNELAV"];
        _decLiv = s["DECLIV"]; _plyRs2 = s["PLYRS2"]; _plyRLp = s["PLYRLP"]; _stPly = s["STPLY2"]; _stEnmy = s["STENMY"];
        _death1 = s["DEATH1"]; _death2 = s["DEATH2"]; _death3 = s["DEATH3"]; _death4 = s["DEATH4"];
        _vNapTpc = s["VNAPTPC"]; _vSucide = s["VSUCIDE"]; _vSnd = s["VSND"]; _vRand = s["VRAND"]; _vWr1All = s["VWR1ALL"];
        _playerId = (int)s.Value("PLYID"); _eggId = (int)s.Value("EGGID"); _stand = (int)s.Value("STAND");
        _land5 = (int)s.Value("LAND5"); _floor = (int)s.Value("FLOOR");
        _pl1 = (int)s.Value("PL1") * 0x11; _pl2 = (int)s.Value("PL2") * 0x11;
        _eLeft = (int)s.Value("ELEFT"); _eRight = (int)s.Value("ERIGHT");

        // Instruction sizes for resume points: n,U is 2 bytes with a 5-bit offset, else 3.
        int Ix(int offset) => offset is >= -16 and <= 15 ? 2 : 3;
        void At(L l, string label, int offset = 0) => _address[l] = (ushort)(s[label] + offset);
        At(L.Create, "CREP1");
        At(L.CreateLives, "CREP1", Ix(_decision) + 3);           // LDX PDECSN,U / JSR DECLIV
        At(L.CreateEnemy, "CREEM");
        At(L.QueueNap, "CRELP", 5);                              // PCNAP 1
        At(L.FadeNap, "TREFF2", 5);
        At(L.WaitNap, "TREFF2.40S");                             // after .20S's PCNAP 1
        At(L.WaitJoy, "TREFF2.40S", 3);                          // JSR [PJOY,U]
        At(L.Start, "PLAYR");
        At(L.StartNap, "PLAYR", 3 + Ix(_posY) + 5);              // LDD #LAND5 / STD PPOSY,U / PCNAP 60
        At(L.StartLives, "PLAYR", 3 + Ix(_posY) + 5 + Ix(_decision) + 3);
        At(L.Reborn, "PLYLI2");
        At(L.Lava, "ADGFLR");
        At(L.SinkNap, "ADGLAV", 3 + 3 + 5);                      // JSR WPLYR / JSR FCLIP / PCNAP 3
        At(L.Sunk, "ADGDED");                                    // also JSR [DDEAD,Y]'s return
        At(L.SunkNap, "ADGDED", Ix(_posY + 1) * 2 + 2 + 2 + 5);  // INC / LDA / CMPA / BLO / PCNAP 30
    }

    public ushort Address(L label) => _address[label];

    public IReadOnlyDictionary<L, ushort> Addresses => _address;

    private static int IxCost(int offset) => Symbols.IndexCost(offset);

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
            case L.Create:
                a.Ldx(a.U(_decision));
                return (default, Leave(t, 8, _decLiv, _address[L.CreateLives]));

            case L.CreateLives:
                if (a.Br(a.Eq)) return (default, Suicide(t, viaJmp: true));   // PLYDIE: no lives left
                a.Lda(a.M(_nPServ));                             // take a number
                a.Sta(a.U(_timX));
                a.Inc(a.M(_nPServ));
                a.Bra();
                return (default, QueueNap(t));

            case L.CreateEnemy:
                a.Lda(a.M(_nEServ));
                a.Sta(a.U(_timX));
                a.Inc(a.M(_nEServ));
                return (default, QueueNap(t));

            case L.QueueNap: return Queue(t);
            case L.FadeNap: return AfterFadeFrame(t);
            case L.WaitNap: return Wait(t);
            case L.WaitJoy: return AfterJoystick(t);

            case L.Start:
                a.LddI(_land5);
                a.Std(a.U(_posY));
                return (default, PcNap(t, 60, _address[L.StartNap]));

            case L.StartNap:
                a.Ldx(a.U(_decision));
                return (default, Leave(t, 8, _decLiv, _address[L.StartLives]));

            case L.StartLives:
            {
                a.Ldu(a.M(_pExec));
                a.Ldx(a.U(_decision));
                CallStart(t);
                a.Ldx(a.U(_decision));
                a.Ldd(a.X(_dJoy));                               // the joystick routine
                a.Std(a.U(_joy));
                return (L.Reborn, null);
            }

            case L.Reborn:
                t.Cycles += 7;                                   // BSR PLYINT
                Init(t);
                t.Cycles += 5;
                CallDraw(t, write: true);                        // JSR WPLYR
                return (default, _plyRLp);                       // falls into PLYRLP

            case L.Lava: return Lava(t);
            case L.SinkNap: return Sink(t);
            case L.Sunk: return Sunk(t);

            case L.SunkNap:
                a.Ldx(a.U(_decision));                           // be created again
                t.Cycles += 3 + Symbols.IndirectCost(_dCre);     // JMP [DCRE,X]
                return (default, t.Read16(t.X + _dCre));
        }
        throw new InvalidOperationException($"unknown label {label}");
    }


    // ------------------------------------------------------------------ the queue

    /// <summary>CRELP after its nap: is it this bird's turn? Players first, by number.</summary>
    private (L, ushort?) Queue(Tx t)
    {
        a.Ldb(a.U(_timX));                                       // this bird's number
        a.Lda(a.U(_id));
        a.CmpaI(_playerId);
        if (a.Br(a.Eq)) return Player(t);
        a.Lda(a.M(_nPServ));                                     // any players holding a number?
        a.Cmpa(a.M(_lPServ));
        if (a.Br(a.Ne)) return (default, QueueNap(t));
        a.Cmpb(a.M(_lEServ));                                    // this enemy's turn?
        if (a.Br(a.Ne)) return (default, QueueNap(t));
        a.LdxI(_trEny);
        return Choose(t);
    }

    /// <summary>CREPLY: a player prefers a transporter on a level nobody is on.</summary>
    private (L, ushort?) Player(Tx t)
    {
        a.Cmpb(a.M(_lPServ));
        if (a.Br(a.Ne)) return (default, QueueNap(t));
        a.Clr(a.M(_area1));
        a.Clr(a.M(_area2));
        a.Clr(a.M(_area3));
        t.X = t.Read16(t.Read16(_pDummy));                       // LDX [PDUMMY]
        t.CC = Logic16(t.CC, t.X);
        t.Cycles += 10;
        while (true)
        {
            a.Ldx(a.X(_link));                                   // .10S
            if (a.Br(a.Eq)) break;
            a.Lda(a.X(_id));
            if (a.Br(a.Pl)) continue;                            // not on screen
            a.CmpaI(0x80 + _eggId);
            if (!a.Br(a.Ne))
            {
                a.Ldd(a.X(_dist));                               // an egg calling a bird?
                if (a.Br(a.Eq)) continue;
            }
            a.Jsr();                                             // .30S: JSR SELARE
            OccupyArea();
            a.Rts();
            a.Bra();
        }
        CopyTransporterFlags();                                  // .40S
        a.Lda(a.M(_area3));
        if (!a.Br(a.Ne))
        {
            a.Lda(a.M(_stTr4));
            if (a.Br(a.Eq)) return TryFrom(t, 4);
        }
        a.Lda(a.M(_area2));                                      // .50S
        if (!a.Br(a.Ne))
        {
            a.Lda(a.M(_stTr2));
            if (a.Br(a.Eq)) return TryFrom(t, 2);
            a.Lda(a.M(_stTr3));
            if (a.Br(a.Eq)) return TryFrom(t, 3);
        }
        a.Lda(a.M(_area1));                                      // .60S
        if (!a.Br(a.Ne))
        {
            a.Lda(a.M(_stTr1));
            if (a.Br(a.Eq)) return TryFrom(t, 1);
        }
        a.LdxI(_trPly);                                          // .70S: player safety zones
        a.Lda(a.M(_trSmall));
        if (a.Br(a.Ne)) return Choose(t);
        a.LdxI(_trEny);                                          // narrower ones
        a.Bra();
        return Choose(t);
    }

    /// <summary>SELARE (without its RTS): mark the level object X is on as occupied.</summary>
    private void OccupyArea()
    {
        a.Lda(a.X(_posY + 1));
        a.CmpaI(0xA3 + 6);
        if (!a.Br(a.Lo)) { a.Inc(a.M(_area3)); return; }
        a.CmpaI(0x51);                                           // .1S
        if (!a.Br(a.Lo)) { a.Inc(a.M(_area2)); return; }
        a.Inc(a.M(_area1));                                      // .2S
    }

    /// <summary>LDD CURTR1 / STD STTR1 / LDD CURTR3 / STD STTR3: start from the in-use flags.</summary>
    private void CopyTransporterFlags()
    {
        a.Ldd(a.M(_curTr1));
        a.Std(a.M(_stTr1));
        a.Ldd(a.M(_curTr3));
        a.Std(a.M(_stTr3));
    }

    /// <summary>CREALL: mark crowded transporters, then try one at random, else the first free.</summary>
    private (L, ushort?) Choose(Tx t)
    {
        a.Jsr();                                                 // JSR FREET
        Crowding(t);
        a.Rts();
        t.Cycles += 7 + Symbols.AddressCost(_vRand) + 4;         // JSR VRAND / JMP RANDSYS
        Random(t);
        t.Cycles += 5;
        int first;
        if (!a.Br(!Cs(t.CC)))                                    // BCC GOTR12
        {
            a.Rora();
            if (a.Br(Cs(t.CC))) first = 4;
            else { a.Bra(); first = 3; }
        }
        else
        {
            a.Rora();                                            // GOTR12
            first = a.Br(Cs(t.CC)) ? 2 : 1;
        }
        return TryFrom(t, first);
    }

    /// <summary>
    /// GOTRn: the transporters from n to 4 in turn, then 1, 2, 3 and finally 4 again; the first
    /// free one is taken (GOTTR). All crowded: back in the queue.
    /// </summary>
    private (L, ushort?) TryFrom(Tx t, int first)
    {
        var order = new List<int>();
        for (int n = first; n <= 4; n++) order.Add(n);
        order.AddRange([1, 2, 3, 4]);
        for (int i = 0; i < order.Count; i++)
        {
            int n = order[i];
            a.LdxI(Table(n));
            a.Lda(a.M(Flag(n)));
            if (i == order.Count - 1)
            {
                if (a.Br(a.Ne)) return (default, QueueNap(t));   // BNE CRELP
                return GotTransporter(t);
            }
            if (a.Br(a.Eq)) return GotTransporter(t);
        }
        throw new InvalidOperationException("unreachable");
    }

    private ushort Table(int n) => n switch { 1 => _tr1, 2 => _tr2, 3 => _tr3, _ => _tr4 };
    private ushort Flag(int n) => n switch { 1 => _stTr1, 2 => _stTr2, 3 => _stTr3, _ => _stTr4 };

    /// <summary>
    /// FREET (without its RTS): for every object on screen (eggs only if calling a bird), project
    /// its X by its speed and mark the transporter on its level crowded if it is in the safety zone.
    /// X = the safety zone table (TRPLY or TRENY).
    /// </summary>
    private void Crowding(Tx t)
    {
        t.Push16(t.X);                                           // PSHS X
        t.Cycles += 7;
        CopyTransporterFlags();
        t.U = t.Read16(t.Read16(_pDummy));                       // LDU [PDUMMY]
        t.CC = Logic16(t.CC, t.U);
        t.Cycles += 10;
        while (true)
        {
            a.Ldu(a.U(_link));                                   // CRENPR
            if (!a.Br(a.Ne))
            {
                t.X = t.Read16(t.S);                             // PULS X,PC
                t.S += 2;
                t.Cycles += 9 - 5;                               // (the PC pull is the caller's RTS)
                return;
            }
            a.Lda(a.U(_id));                                     // CREID
            if (a.Br(a.Pl)) continue;
            a.CmpaI(0x80 + _eggId);
            if (!a.Br(a.Ne))
            {
                a.Ldd(a.U(_dist));
                if (a.Br(a.Eq)) continue;
            }
            a.Ldb(a.U(_velX));                                   // .1S
            a.Ldy(a.S(0));
            t.X = t.Read16(t.Y);                                 // LDX ,Y++: speed fudge table
            t.Y += 2;
            t.CC = Logic16(t.CC, t.X);
            t.Cycles += 5 + 3;
            t.D = t.Read16(t.X + (sbyte)t.B);                    // LDD B,X
            t.CC = Logic16(t.CC, t.D);
            t.Cycles += 6;
            a.Addd(a.U(_posX));
            a.CmpdI(_eLeft);
            if (!a.Br(a.Ge)) a.AdddI(_eRight - _eLeft);
            a.CmpdI(_eRight);                                    // .10S
            if (!a.Br(a.Le)) a.AdddI(_eLeft - _eRight);
            t.X = t.D;                                           // .20S: TFR D,X
            t.Cycles += 6;
            a.Lda(a.U(_posY + 1));
            a.CmpaI(0x51 + 7);                                   // upper level?
            if (!a.Br(a.Hi))
            {
                a.Lda(a.M(_stTr1));
                if (a.Br(a.Ne)) continue;
                CheckZone(t);
                a.Bra();
                continue;
            }
            a.CmpaI(0x8A + 7);                                   // .30S: middle level?
            if (!a.Br(a.Hi))
            {
                a.T.Y = (ushort)(t.Y + _ftLen);                  // LEAY FTLEN,Y
                t.CC = Set(t.CC, Z, t.Y == 0);
                t.Cycles += 4 + IxCost(_ftLen);
                a.Lda(a.M(_stTr2));
                if (!a.Br(a.Ne)) CheckZone(t);
                a.Lda(a.M(_stTr3));                              // .35S
                if (a.Br(a.Ne)) continue;
                t.Y = (ushort)(t.Y + _ftLen);
                t.CC = Set(t.CC, Z, t.Y == 0);
                t.Cycles += 4 + IxCost(_ftLen);
                CheckZone(t);
                a.Bra();
                continue;
            }
            a.CmpaI(0xA3 + 7);                                   // .40S: lower level?
            if (a.Br(a.Lo)) continue;
            a.Lda(a.M(_stTr4));
            if (a.Br(a.Ne)) continue;
            t.Y = (ushort)(t.Y + 3 * _ftLen);
            t.CC = Set(t.CC, Z, t.Y == 0);
            t.Cycles += 4 + IxCost(3 * _ftLen);
            CheckZone(t);
            a.Bra();
        }
    }

    /// <summary>BSR TRBOND: X (projected position) inside zone Y's bounds marks its transporter crowded.</summary>
    private void CheckZone(Tx t)
    {
        t.Cycles += 7;                                           // BSR
        bool crowded;
        a.Ldb(a.Y(_fxRev));                                      // wrap-around zone?
        if (!a.Br(a.Ne))
        {
            a.Cmpx(a.Y(_fxMin));                               // far enough left?
            if (a.Br(a.Lo)) { t.Cycles += 5; return; }
            a.Cmpx(a.Y(_fxMax));                               // far enough right?
            if (a.Br(a.Hi)) { t.Cycles += 5; return; }
            a.Bra();
            crowded = true;
        }
        else
        {
            a.Cmpx(a.Y(_fxMin));                               // .10S
            crowded = a.Br(a.Hi);
            if (!crowded)
            {
                a.Cmpx(a.Y(_fxMax));
                crowded = !a.Br(a.Hi);
            }
        }
        if (crowded)
        {
            int flag = t.Read16(t.Y + _fLevel);                  // .20S: INC [FLEVEL,Y]
            t.Write(flag, Inc(ref t.CC, t.Read(flag)));
            t.Cycles += 6 + Symbols.IndirectCost(_fLevel);
        }
        t.Cycles += 5;                                           // .30S: RTS
    }


    /// <summary>RANDSYS (without its RTS): A = 0..127, carry = a random bit.</summary>
    internal static void Random(Tx t, ushort random)
    {
        int hiAddr = t.Var(random), loAddr = t.Var((ushort)(random + 1));
        byte hi = t.Read(hiAddr), lo = t.Read(loAddr);
        int mixed = lo ^ (lo >> 3);
        bool feedback = (mixed & 1) != 0;
        byte newLo = (byte)((lo >> 1) | ((hi & 1) != 0 ? 0x80 : 0));
        t.Write(hiAddr, (byte)((hi >> 1) | (feedback ? 0x80 : 0)));
        t.Write(loAddr, newLo);
        t.A = (byte)(mixed >> 1);
        t.CC = Set(Logic8(t.CC, newLo), C, (lo & 1) != 0);
        t.Cycles += 33 - 5;
    }

    private void Random(Tx t) => Random(t, _random);

    // ------------------------------------------------------------------ fade-in

    /// <summary>GOTTR: take transporter X, stand on it, play the creation sound, dress the bird.</summary>
    private (L, ushort?) GotTransporter(Tx t)
    {
        int inUse = t.Read16(t.X + _tCurUse);                    // INC [TCURUSE,X]
        t.Write(inUse, Inc(ref t.CC, t.Read(inUse)));
        t.Cycles += 6 + Symbols.IndirectCost(_tCurUse);
        a.Ldu(a.M(_pExec));
        a.Ldd(a.X(_tPosX));                                      // standing position
        a.Std(a.U(_posX));
        a.Ldd(a.X(_tPosY));
        a.Std(a.U(_posY));
        a.Stx(a.U(_state));                                      // the transporter, for now
        a.Ldx(a.U(_decision));
        a.Ldx(a.X(_dSnCre));
        a.Lda(a.U(_id));
        a.CmpaI(_playerId);
        if (!a.Br(a.Ne))
        {
            a.Inc(a.M(_lPServ));                                 // next player's number
            a.Bra();
        }
        else
        {
            a.Inc(a.M(_lEServ));                                 // .2S
        }
        _ground.CallSound(t);                                    // .3S
        a.LdaI(30);
        a.Sta(a.U(_frame));
        a.Ldy(a.U(_decision));
        LoadIndirectD(t, t.Y + _dPlyr);                          // LDD [DPLYR,Y]
        a.Std(a.U(_rider));
        a.LdaI(_stand);
        t.Cycles += 8;                                           // JSR SRCADA
        _ground.SelectImage(t, storeImage: true, onGround: false);
        t.Cycles += 5;
        return FadeFrame(t);
    }

    private void LoadIndirectD(Tx t, int pointerAt)
    {
        t.D = t.Read16(t.Read16(pointerAt));
        t.CC = Logic16(t.CC, t.D);
        t.Cycles += 5 + Symbols.IndirectCost(_dPlyr);
    }

    /// <summary>TREFF: light the transporter; in the last 20 frames grow the bird out of it.</summary>
    private (L, ushort?) FadeFrame(Tx t)
    {
        a.Ldx(a.U(_state));
        LoadIndirectY(t, t.X + _tImage);                         // LDY [TIMAGE,X]
        CallBackground(t);                                       // JSR BCKYUP
        a.Lda(a.X(0));                                           // constant fill
        a.OraI(0x10);
        a.Ldy(a.U(_decision));
        a.Ldb(a.Y(_dConst));                                     // in the bird's colour
        a.Std(a.X(0));
        a.Lda(a.U(_frame));
        a.CmpaI(20);
        if (!a.Br(a.Gt)) GrowBird(t);
        return (default, PcNap(t, 1, _address[L.FadeNap]));      // TREFF2
    }

    private void LoadIndirectY(Tx t, int pointerAt)
    {
        t.Y = t.Read16(t.Read16(pointerAt));
        t.CC = Logic16(t.CC, t.Y);
        t.Cycles += 6 + Symbols.IndirectCost(_tImage);
    }

    /// <summary>Ken Lantz's effect: show only the top of the bird, the rider in front, growing each frame.</summary>
    private void GrowBird(Tx t)
    {
        CallDraw(t, write: true);                                // JSR WPLYR (X = the horse's block)
        a.Ldb(a.U(_frame));
        a.TfrBA();
        a.Coma();                                                // height from the frame count
        a.Asra();
        a.AndaI(0x0F);
        a.EoraI(0x04);
        a.Sta(a.X(_wcLenY));
        a.TfrAB();
        a.CmpaI(3);
        if (!a.Br(a.Le)) a.LdbI(3);
        a.Stb(a.X(_wcLenY - 10));                                // .2S: the rider
        a.EoraI(0x04);
        a.Nega();
        a.Inca();
        a.Inca();
        a.AndaI(0x0F);
        a.TfrAB();
        a.Adda(a.X(_wcY));
        a.Addb(a.X(_wcY - 10));
        a.Sta(a.X(_wcY));
        a.Stb(a.X(_wcY - 10));
        a.Ldb(a.X(_wcLenY));                                     // not below the transporter
        a.EorbI(4);
        a.Addb(a.X(_wcY));
        a.Cmpb(a.U(_posY + 1));
        if (!a.Br(a.Ls))
        {
            a.Ldb(a.U(_posY + 1));
            a.Incb();
            a.Subb(a.X(_wcY));
            a.EorbI(4);
            a.Stb(a.X(_wcLenY));
        }
        a.Ldy(a.U(_decision));                                   // .3S: in the bird's colour
        a.Ldb(a.Y(_dConst));
        a.Lda(a.X(_wcDma));
        a.OraI(0x10);
        a.Std(a.X(_wcDma));
        a.Std(a.X(_wcDma - 10));
    }

    /// <summary>After TREFF2's nap: turn the horse into a box erase, then next frame or start waiting.</summary>
    private (L, ushort?) AfterFadeFrame(Tx t)
    {
        t.Cycles += 8;                                           // JSR CNORIA
        EraseHorse(t);
        a.LdaI(0x12);
        a.Sta(a.X(_wcDma));
        a.Ldd(a.U(_posX));
        a.Asra();
        a.Rorb();
        a.Stb(a.X(_wcX));
        a.LdaI(18 / 2 ^ 4);                                      // box erase width
        a.Sta(a.X(_wcLenX));
        a.Dec(a.U(_frame));
        if (a.Lbr(a.Ne)) return FadeFrame(t);
        a.Ldx(a.U(_decision));                                   // wait for the first move
        a.Ldd(a.X(_dJoy));
        a.Std(a.U(_joy));
        a.LdaI(16 * 2);
        a.Sta(a.U(_feet));
        a.Lsra();
        a.Sta(a.U(_accX));
        a.LdaI(2);
        a.Sta(a.U(_lantz));
        a.Clr(a.U(_timeUp));
        a.Clr(a.U(_timX));
        a.Bra();
        return Wait(t);
    }

    /// <summary>CNORIA (with its RTS): erase the horse image only, if any. X = the erase block.</summary>
    private void EraseHorse(Tx t)
    {
        a.Ldy(a.U(_picH));
        if (a.Br(a.Eq)) { t.Cycles += 5; return; }
        t.Cycles += 8 + 4;                                       // JSR VCL1CLS / JMP
        _ground.Allocate(t, write: false, blocks: 1);
        t.Cycles += 5 + 3;                                       // RTS / BRA WRHOR2
        _ground.QueueImage(t);
        t.Cycles += 5;
    }

    // ------------------------------------------------------------------ waiting

    /// <summary>TREFF2.40S: read the controls (JSR [PJOY,U]).</summary>
    private (L, ushort?) Wait(Tx t)
    {
        ushort? exit = _bird.ReadControls(t, _address[L.WaitJoy]);
        return exit != null ? (default, exit) : AfterJoystick(t);
    }

    /// <summary>
    /// After the controls: a move or flap starts the bird; otherwise blink the transporter, slowing
    /// down (PFEET halves every 75 ticks) until the time runs out.
    /// </summary>
    private (L, ushort?) AfterJoystick(Tx t)
    {
        a.Ldd(a.M(_curJoy));
        if (a.Br(a.Ne))
        {
            a.Ldx(a.U(_decision));                               // .51S: aborted early, stop the sound
            a.Ldx(a.X(_dSnTref));
            if (!a.Br(a.Eq)) _ground.CallSound(t);
            return Begin(t);
        }
        a.Dec(a.U(_timeUp));                                     // time to change speed?
        if (!a.Br(a.Gt))
        {
            a.LdaI(75);
            a.Sta(a.U(_timeUp));
            a.Lsr(a.U(_feet));
            if (a.Br(a.Eq)) return Begin(t);
        }
        CallDraw(t, write: true);                                // .41S
        a.Ldx(a.U(_state));
        LoadIndirectY(t, t.X + _tImage);
        CallBackground(t);
        a.Ldy(a.U(_decision));
        a.Ldy(a.Y(_dTreff));                                     // the colour effect table
        a.Leax(-10 - 10);                                        // back to the bird's blocks
        a.Ldb(a.U(_lantz));
        Blink(t);
        a.Decb();
        Blink(t);
        a.Decb();
        Blink(t);
        a.Dec(a.U(_accX));                                       // change the effect?
        if (!a.Br(a.Gt))
        {
            a.Inc(a.U(_lantz));
            a.Lda(a.U(_feet));
            a.Sta(a.U(_accX));
        }
        a.Lda(a.U(_feet));                                       // .42S
        a.Lsra();
        if (!a.Br(a.Ne))
        {
            a.Inc(a.U(_timX));
            a.Bra();
        }
        return (default, PcNap(t, 1, _address[L.WaitNap]));      // .20S
    }

    /// <summary>BSR .45S: colour DMA block X from effect entry B (cycling 7..2), then the next block.</summary>
    private void Blink(Tx t)
    {
        t.S -= 2;                                                // BSR .45S
        t.Cycles += 7;
        a.PshsB();
        a.AndbI(0x07);
        if (!a.Br(a.Ne))
        {
            a.LdbI(2);
            a.Stb(a.U(_lantz));
            a.Stb(a.S(0));
        }
        a.LdbBY();                                               // .47S
        a.Tst(a.U(_timX));
        bool recolour = true;
        if (!a.Br(a.Eq))
        {
            a.CmpbI(_pl1);
            if (a.Br(a.Eq)) recolour = false;
            else
            {
                a.CmpbI(_pl2);
                if (a.Br(a.Eq)) recolour = false;
            }
        }
        if (recolour)
        {
            a.Lda(a.X(_wcDma));                                  // .48S
            a.OraI(0x10);
            a.Std(a.X(_wcDma));
        }
        a.Leax(10);                                              // .49S
        t.B = t.Read(t.S);                                       // PULS B,PC
        t.S += 3;
        t.Cycles += 8;
    }

    /// <summary>.50S: erase, free the transporter, start the bird (PLYINT) and run with these controls.</summary>
    private (L, ushort?) Begin(Tx t)
    {
        CallDraw(t, write: false);                               // JSR CPLYR
        a.Ldx(a.U(_state));
        int inUse = t.Read16(t.X + _tCurUse);                    // DEC [TCURUSE,X]
        t.Write(inUse, Dec(ref t.CC, t.Read(inUse)));
        t.Cycles += 6 + Symbols.IndirectCost(_tCurUse);
        LoadIndirectY(t, t.X + _tImage);
        CallBackground(t);                                       // the transporter back to normal
        a.Ldu(a.M(_pExec));
        a.Ldx(a.U(_decision));
        CallStart(t);
        t.Cycles += 7;                                           // BSR PLYINT
        Init(t);
        t.Cycles += 5;
        a.Ldd(a.M(_curJoy));
        a.Bra();
        return (default, _plyRs2);
    }

    /// <summary>
    /// JSR [DSTART,X]: STPLY1/STPLY2 (the player becomes a target) or STENMY (nothing); any
    /// other routine hands back.
    /// </summary>
    private void CallStart(Tx t)
    {
        ushort target = t.Read16(t.X + _dStart);
        t.Cycles += 7 + Symbols.IndirectCost(_dStart);
        if (target == _stEnmy)
        {
            t.Cycles += 5;                                       // STENMY: RTS
            return;
        }
        if (target != _stPly) throw new HandBackException();
        a.Ldd(a.M(_tarPly));                                     // is this spot empty?
        if (!a.Br(a.Ne))
        {
            a.Stu(a.M(_tarPly));
            a.Lda(a.M(_tarTim));
            a.Sta(a.M(_tarTm1));
        }
        else
        {
            a.Stu(a.M(_tarPl2));                                 // .1S: the second spot
            a.Lda(a.M(_tarTim));
            a.Sta(a.M(_tarTm2));
        }
        t.Cycles += 5;
    }

    /// <summary>PLYINT (without the RTS of the SRCADG it jumps to): a standing bird, ready to play.</summary>
    private void Init(Tx t)
    {
        LoadIndirectD(t, t.X + _dPlyr);                          // LDD [DPLYR,X]
        a.Std(a.U(_rider));
        a.LddI(_plyBr);                                          // ground state B
        a.Std(a.U(_state));
        a.LddI(0);
        a.Std(a.U(_velY));
        foreach (int field in new[] { _velX, _timX, _frame, _timeUp, _accX, _bumpX, _bumpY })
            a.Clr(a.U(field));
        a.LdaI(0x80);                                            // collisions on
        a.Ora(a.U(_id));
        a.Sta(a.U(_id));
        a.LdaI(_stand);
        t.Cycles += 4;                                           // JMP SRCADG
        _ground.SelectImage(t, storeImage: true, onGround: true);
    }

    // ------------------------------------------------------------------ lava

    /// <summary>
    /// ADGFLR, reached from a bird's gravity (ADDGRA) inside its slice: the lava death, through
    /// its first nap. Returns where execution continues.
    /// </summary>
    public ushort LavaFromGravity(Tx t)
    {
        a = new Asm(t);
        return Lava(t).Item2 ?? throw new InvalidOperationException("the lava death naps");
    }

    /// <summary>ADGFLR: the bird hit the lava: its sound, then sink.</summary>
    private (L, ushort?) Lava(Tx t)
    {
        a.LdxI(_snPLav);
        a.Lda(a.U(_id));
        a.CmpaI(0x80 + _playerId);
        if (!a.Br(a.Eq)) a.LdxI(_snELav);
        _ground.CallSound(t);                                    // .1S
        return SinkFrame(t);
    }

    /// <summary>ADGLAV: draw the bird clipped at the lava line and nap.</summary>
    private (L, ushort?) SinkFrame(Tx t)
    {
        CallDraw(t, write: true);
        CallFloorClip(t);
        return (default, PcNap(t, 3, _address[L.SinkNap]));
    }

    /// <summary>After the nap: erase; once the bird's legs are under, it dies (no victor).</summary>
    private (L, ushort?) Sink(Tx t)
    {
        CallDraw(t, write: false);
        CallFloorClip(t);
        a.Lda(a.U(_id));                                         // dead already, or just a ride?
        if (a.Br(a.Pl)) return Sunk(t);
        a.Ldb(a.X(_wcLenY));                                     // still 7 pixels tall?
        a.CmpbI(7 ^ 4);
        if (a.Br(a.Eq)) return Sunk(t);
        a.AndaI(0x7F);                                           // now this guy is dead
        a.Sta(a.U(_id));
        a.LddI(0);
        a.Std(a.U(_picR));
        a.Std(a.U(_rider));
        a.LdxI(0);                                               // no victor
        a.Ldy(a.U(_decision));
        ushort death = t.Read16(t.Y + _dDead);
        int jsr = 7 + Symbols.IndirectCost(_dDead);
        Action? body = death == _death1 ? () => _deaths.PlayerDies(t, player2: false)
            : death == _death2 ? () => _deaths.PlayerDies(t, player2: true)
            : death == _death3 ? () => _deaths.EnemyDies(t)
            : death == _death4 ? () => _deaths.PterodactylDies(t)
            : null;
        if (body == null) return (default, Leave(t, jsr, death, _address[L.Sunk]));
        t.Cycles += jsr;
        body();
        t.Cycles += 5;
        return Sunk(t);
    }

    /// <summary>ADGDED: one pixel deeper; when all the way under, nap and be created again.</summary>
    private (L, ushort?) Sunk(Tx t)
    {
        a.Inc(a.U(_posY + 1));
        a.Lda(a.U(_posY + 1));
        a.CmpaI(_floor + 20 + 1);
        if (a.Br(a.Lo)) return SinkFrame(t);
        return (default, PcNap(t, 30, _address[L.SunkNap]));
    }

    /// <summary>JSR FCLIP: clip the horse, and the rider if any, at the lava line.</summary>
    private void CallFloorClip(Tx t)
    {
        t.Cycles += 8 + 7;                                       // JSR FCLIP / BSR CLIPER
        _eggs.Clip(t);
        t.Cycles += 5;
        a.Ldd(a.U(_picR));
        if (!a.Br(a.Eq))
        {
            a.Leax(-10);
            _eggs.Clip(t);
        }
        t.Cycles += 5;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>JSR WPLYR / JSR CPLYR.</summary>
    private void CallDraw(Tx t, bool write)
    {
        t.Cycles += 8;
        _ground.DrawObject(t, write);
        t.Cycles += 5;
    }

    /// <summary>
    /// JSR BCKYUP: queue background image Y (a cliff's transporter strip) through WR1ALL, which
    /// picks the write list of the quadrant its bottom line is in. X = the new block.
    /// </summary>
    private void CallBackground(Tx t)
    {
        t.Cycles += 8;
        a.Ldb(a.Y(_wcLenY));                                     // lowest line
        a.EorbI(4);
        a.Addb(a.Y(_wcY));
        t.Cycles += 7 + Symbols.AddressCost(_vWr1All) + 4;       // JSR VWR1ALL / JMP WR1ALL
        a.AndbI(0xC0);                                           // the quadrant's write list
        a.LdxI(_dmaEndWrite);
        a.Abx();
        a.Ldd(a.M(_dmaFree));
        t.Write16(t.Read16(t.X), t.D);                           // STD [,X]
        t.CC = Logic16(t.CC, t.D);
        t.Cycles += 8;
        a.AdddI(8);
        a.Std(a.X(0));
        a.AdddI(2);
        a.Ldx(a.M(_dmaFree));
        a.Std(a.M(_dmaFree));
        t.Cycles += 5;                                           // RTS
        a.LdaI(0x0A);                                            // write the background object
        a.Sta(a.X(_wcDma));
        foreach (int field in new[] { _wcDest, _wcSrc, _wcLen })
        {
            a.Ldd(a.Y(field));
            a.Std(a.X(field));
        }
        t.Cycles += 5;
    }

    /// <summary>PCNAP n: LDA #n / JSR VNAPTPC, resuming after it.</summary>
    private ushort PcNap(Tx t, byte ticks, ushort resume)
    {
        a.LdaI(ticks);
        t.Cycles += 7 + Symbols.AddressCost(_vNapTpc) + 4;
        t.Push16(resume);
        return _scheduler.Nap(t, fromJsr: true) ?? throw new InvalidOperationException("nap without a continuation");
    }

    private ushort QueueNap(Tx t) => PcNap(t, 1, _address[L.QueueNap]);

    /// <summary>JMP VSUCIDE (PLYDIE/EMYDIE: JMP VSUCIDE reached by a branch).</summary>
    private ushort Suicide(Tx t, bool viaJmp)
    {
        t.Cycles += 3 + Symbols.AddressCost(_vSucide) + 4;
        return _scheduler.Suicide(t) ?? throw new InvalidOperationException("suicide without a continuation");
    }

    /// <summary>A JSR to code that is not native: leave with the return address pushed.</summary>
    private static ushort Leave(Tx t, int jsrCycles, ushort target, ushort returnTo)
    {
        t.Cycles += jsrCycles;
        t.Push16(returnTo);
        return target;
    }
}
