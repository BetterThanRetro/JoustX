using static Joust.Emulation.Alu6809;

namespace Joust.Port.Routines;

/// <summary>
/// The enemy "joystick" routines (JOUSTRV4.ASM): what the bird process calls through PJOY for
/// enemies, returning A = direction (-1/0/+1) and B = flap, also stored in CURJOY.
///
/// Each routine is a state machine whose state is the PJOY address itself: a routine that wants
/// to continue next tick (flap for two ticks, hold level flight, ...) stores the label to resume
/// at in PJOY. The families:
/// <list type="bullet">
/// <item>LINET: dumb line tracking (every wave's starting enemies) until the wave needs smarter ones.</item>
/// <item>BOUNDR (Bounder), B2UNDR (Hunter), SHADOW (Shadow Lord): seek the targeted player
///   vertically, hold level flight, steer around cliffs, and flap out of the lava.</item>
/// <item>PTERO: the pterodactyl's line tracking and attack run (with the V4 patches).</item>
/// <item>SEEKE: a buzzard fetching its rider; AUTOFF / PTEOFF: fly off screen.</item>
/// </list>
///
/// The decisions hinge on exact flags (including original quirks such as reading a stale X),
/// so this is ported instruction by instruction with <see cref="Asm"/>; each label is a method
/// and jumps are calls. The rare exits that do not return (a buzzard getting its rider, leaving
/// the screen) hand the call back to the original.
/// </summary>
public sealed class EnemyAi
{
    private readonly Ground _ground;

    // Process block
    private readonly int _posX, _posY, _velX, _velY, _face, _joy, _joyT, _lavT, _dist, _state;
    private readonly int _pVelX, _rDir, _bumpX, _decision, _chase, _id, _feet, _egg, _image, _accX;

    // Decision block
    private readonly int _dSmart;

    // Variables
    private readonly ushort _curJoy, _nSmart, _wSmart, _lntLav, _pPrev, _tarPly, _tarTm1, _tarPl2, _tarTm2;
    private readonly ushort _boDnRg, _boDnDi, _boDnVy, _boUpRg, _boUpDi, _boUpWd, _boUpWu, _boLeTm;
    private readonly ushort _huDnRg, _huDnDi, _huDnVy, _huUpRg, _huUpDi, _huUpWu, _huUpWd, _huUpVy, _huLeTm;
    private readonly ushort _shDnRg, _shUpRg, _shUpVy, _shUpTm, _shLeTm, _shClTm;
    private readonly ushort _bckX, _bckY, _snPte;

    // Constants
    private readonly int _lavId, _line1, _line2, _line3, _lineBand, _eLeft, _eRight, _attackFrame;
    private const int DyLen = 0x14 - 6, B2YLen = 0x14 - 6, B2XLen = 27 + 4, ShYLen = 0x14 - 6, ShXLen = 27 + 4;

    // PJOY values (labels)
    private readonly ushort _linet, _lntOfp, _boDn1, _boDn2, _boUp1, _boUp2, _boLev1, _boLev2, _boLav1, _boLav2;
    private readonly ushort _b2Dn1, _b2Dn2, _b2Up1, _b2Up2, _b2Lev1, _b2Lev2, _b2Av, _b2Up3a, _b2Up4;
    private readonly ushort _shadow, _shUp1, _shDn2, _shLep1, _shLep2, _shLev1, _shLev2, _shAv, _shUp3a, _shUp4;
    private readonly ushort _ptero, _pteAtk, _pteUp2, _seeke, _seekF2, _mounLp, _autoff, _autOfp;

    private readonly Dictionary<ushort, Action> _entries = [];

    /// <summary>The routine's labels that can be stored in PJOY, by name.</summary>
    public static readonly string[] Labels =
    [
        "LINET", "LNTOFP", "BOUNDR", "BODN1", "BODN2", "BOUP1", "BOUP2", "BOLEV1", "BOLEV2", "BOLAV1", "BOLAV2",
        "B2UNDR", "B2DN1", "B2DN2", "B2UP1", "B2UP2", "B2LEV1", "B2LEV2", "B2AV", "B2UP3A", "B2UP4",
        "SHADOW", "SHUP1", "SHDN2", "SHLEP1", "SHLEP2", "SHLEV1", "SHLEV2", "SHAV", "SHUP3A", "SHUP4",
        "PTERO", "PTEATK", "PTEUP2", "PTEOFF", "SEEKE", "SEEKF2", "MOUNLP", "AUTOFF", "AUTOFP",
    ];

    private Asm a = null!;                                       // the call being run

    public EnemyAi(Symbols s, Ground ground)
    {
        _ground = ground;
        if (s.Value("DEBUG") != 0)
            throw new NotSupportedException("the AI is ported for the ROM build (DEBUG = 0)");

        _posX = s["PPOSX"]; _posY = s["PPOSY"]; _velX = s["PVELX"]; _velY = s["PVELY"]; _face = s["PFACE"];
        _joy = s["PJOY"]; _joyT = s["PJOYT"]; _lavT = s["PLAVT"]; _dist = s["PDIST"]; _state = s["PSTATE"];
        _pVelX = s["PPVELX"]; _rDir = s["PRDIR"]; _bumpX = s["PBUMPX"]; _decision = s["PDECSN"];
        _chase = s["PCHASE"]; _id = s["PID"]; _feet = s["PFEET"]; _egg = s["PEGG"]; _image = s["PIMAGE"];
        _accX = s["PACCX"];
        _dSmart = s["DSMART"];

        _curJoy = s["CURJOY"]; _nSmart = s["NSMART"]; _wSmart = s["WSMART"]; _lntLav = s["LNTLAV"];
        _pPrev = s["PPREV"]; _tarPly = s["TARPLY"]; _tarTm1 = s["TARTM1"]; _tarPl2 = s["TARPL2"]; _tarTm2 = s["TARTM2"];
        _boDnRg = s["BODNRG"]; _boDnDi = s["BODNDI"]; _boDnVy = s["BODNVY"]; _boUpRg = s["BOUPRG"];
        _boUpDi = s["BOUPDI"]; _boUpWd = s["BOUPWD"]; _boUpWu = s["BOUPWU"]; _boLeTm = s["BOLETM"];
        _huDnRg = s["HUDNRG"]; _huDnDi = s["HUDNDI"]; _huDnVy = s["HUDNVY"]; _huUpRg = s["HUUPRG"];
        _huUpDi = s["HUUPDI"]; _huUpWu = s["HUUPWU"]; _huUpWd = s["HUUPWD"]; _huUpVy = s["HUUPVY"]; _huLeTm = s["HULETM"];
        _shDnRg = s["SHDNRG"]; _shUpRg = s["SHUPRG"]; _shUpVy = s["SHUPVY"]; _shUpTm = s["SHUPTM"];
        _shLeTm = s["SHLETM"]; _shClTm = s["SHCLTM"];
        _bckX = s["BCKXTB"]; _bckY = s["BCKYTB"]; _snPte = s["SNPTE"];

        _lavId = (int)s.Value("LAVID");
        _line1 = (int)s.Value("AOFFL1"); _line2 = (int)s.Value("AOFFL2"); _line3 = (int)s.Value("AOFFL3");
        _lineBand = (int)s.Value("AOFFUD");
        _eLeft = (int)s.Value("ELEFT"); _eRight = (int)s.Value("ERIGHT");
        _attackFrame = (int)(s.Value("FLY3") - s.Value("FLY1"));

        _linet = s["LINET"]; _lntOfp = s["LNTOFP"];
        _boDn1 = s["BODN1"]; _boDn2 = s["BODN2"]; _boUp1 = s["BOUP1"]; _boUp2 = s["BOUP2"];
        _boLev1 = s["BOLEV1"]; _boLev2 = s["BOLEV2"]; _boLav1 = s["BOLAV1"]; _boLav2 = s["BOLAV2"];
        _b2Dn1 = s["B2DN1"]; _b2Dn2 = s["B2DN2"]; _b2Up1 = s["B2UP1"]; _b2Up2 = s["B2UP2"];
        _b2Lev1 = s["B2LEV1"]; _b2Lev2 = s["B2LEV2"]; _b2Av = s["B2AV"]; _b2Up3a = s["B2UP3A"]; _b2Up4 = s["B2UP4"];
        _shadow = s["SHADOW"]; _shUp1 = s["SHUP1"]; _shDn2 = s["SHDN2"]; _shLep1 = s["SHLEP1"]; _shLep2 = s["SHLEP2"];
        _shLev1 = s["SHLEV1"]; _shLev2 = s["SHLEV2"]; _shAv = s["SHAV"]; _shUp3a = s["SHUP3A"]; _shUp4 = s["SHUP4"];
        _ptero = s["PTERO"]; _pteAtk = s["PTEATK"]; _pteUp2 = s["PTEUP2"];
        _seeke = s["SEEKE"]; _seekF2 = s["SEEKF2"]; _mounLp = s["MOUNLP"]; _autoff = s["AUTOFF"]; _autOfp = s["AUTOFP"];

        Action[] bodies =
        [
            Linet, LntOfp, Boundr, BoDn1, BoDn2, BoUp1, BoUp2, BoLev1, BoLev2, BoLav1, BoLav2,
            B2Undr, B2Dn1, B2Dn2, B2Up1, B2Up2, B2Lev1, B2Lev2, B2Av, B2Up3a, B2Up4,
            Shadow, ShUp1, ShDn2, ShLep1, ShLep2, ShLev1, ShLev2, ShAv, ShUp3a, ShUp4,
            Ptero, PteAtk, PteUp2, PteOff, Seeke, SeekF2, MounLp, Autoff, AutOfp,
        ];
        for (int i = 0; i < Labels.Length; i++)
            _entries[s[Labels[i]]] = bodies[i];
        _symbols = s;
    }

    private readonly Symbols _symbols;

    /// <summary>True if <paramref name="pjoy"/> is one of the ported routines.</summary>
    public bool Handles(ushort pjoy) => _entries.ContainsKey(pjoy);

    /// <summary>Address of one of the <see cref="Labels"/>.</summary>
    public ushort Entry(string label) => _symbols[label];

    /// <summary>
    /// Runs the routine at <paramref name="pjoy"/> up to (excluding) its RTS. Throws
    /// <see cref="HandBackException"/> on the exits that are not ported.
    /// </summary>
    public void Run(Tx t, ushort pjoy)
    {
        a = new Asm(t);
        _entries[pjoy]();
    }

    /// <summary>A jump to a routine found in memory (JMP ,X / JMP [DSMART,X]).</summary>
    private void Dispatch(ushort target)
    {
        if (!_entries.TryGetValue(target, out var body)) throw new HandBackException();
        body();
    }

    // ------------------------------------------------------------------ operands

    private Asm.Ea Pos => a.U(_posX);
    private Asm.Ea PosY => a.U(_posY);
    private Asm.Ea Row => a.U(_posY + 1);
    private Asm.Ea VelX => a.U(_velX);
    private Asm.Ea VelY => a.U(_velY);
    private Asm.Ea Face => a.U(_face);
    private Asm.Ea Joy => a.U(_joy);
    private Asm.Ea JoyT => a.U(_joyT);
    private Asm.Ea Dist => a.U(_dist);
    private Asm.Ea DistRow => a.U(_dist + 1);
    private Asm.Ea PVelX => a.U(_pVelX);
    private Asm.Ea RDir => a.U(_rDir);
    private Asm.Ea BumpX => a.U(_bumpX);
    private Asm.Ea Chase => a.U(_chase);
    private Asm.Ea CurJoy => a.M(_curJoy);

    /// <summary>LDA #+-1 / STD CURJOY: move the way the bird faces (flags of the LDA PFACE/TST before).</summary>
    private void Facing(int right, int left)
    {
        if (a.Br(a.Mi)) { a.LdaI(left); a.Std(CurJoy); return; }
        a.LdaI(right);
        a.Std(CurJoy);
    }

    /// <summary>DEC PLAVT,U / BGT / ... : every LNTLAV ticks, true if a lava troll (the previous process) is after me.</summary>
    private bool TrollAfterMe()
    {
        a.Dec(a.U(_lavT));
        if (a.Br(a.Gt)) return false;
        a.Lda(a.M(_lntLav));
        a.Sta(a.U(_lavT));
        a.Ldx(a.M(_pPrev));
        a.Lda(a.X(_id));
        a.CmpaI(_lavId);
        return a.Br(a.Eq);
    }

    /// <summary>JSR SELPLY: Z clear and X = the targeted player, or Z set if there is none.</summary>
    private void CallSelPly()
    {
        a.Jsr();
        SelPly();
        a.Rts();
    }

    /// <summary>Lowest tracking line at or below the bird (A), minus <paramref name="adjust"/>.</summary>
    private void NearestLine(int adjust)
    {
        a.LdaI(_line1 - adjust);
        a.Ldb(Row);
        a.CmpbI(_line1 + _lineBand);
        if (a.Br(a.Lo)) return;
        a.LdaI(_line2 - adjust);
        a.CmpbI(_line2 + _lineBand);
        if (a.Br(a.Lo)) return;
        a.LdaI(_line3 - adjust);
    }

    // ------------------------------------------------------------------ LINET

    private void Linet()
    {
        a.Lda(a.M(_nSmart));                                     // below the wave's minimum intelligence?
        a.Cmpa(a.M(_wSmart));
        if (a.Br(a.Lo)) { LntSmt(); return; }
        if (TrollAfterMe()) { LntUp(); return; }
        NearestLine(0);
        a.Clrb();                                                // LNTTRK
        a.Suba(Row);
        if (a.Br(a.Pl)) { LntFlp(); return; }
        a.Lda(VelY);                                             // below the line: flap unless rising
        if (a.Br(a.Mi)) { LntFlp(); return; }
        LntUp();
    }

    private void LntUp()
    {
        a.LddI(_lntOfp);
        a.Std(Joy);
        a.LdbI(1);
        LntFlp();
    }

    private void LntFlp()
    {
        a.Tst(Face);
        Facing(1, -1);
    }

    private void LntOfp()
    {
        a.LddI(_linet);
        a.Std(Joy);
        a.Clrb();
        a.Bra();
        LntFlp();
    }

    private void LntSmt()
    {
        a.Inc(a.M(_nSmart));                                     // just got smarter
        a.Inc(Chase);
        a.Ldx(a.U(_decision));
        a.Ldx(a.X(_dSmart));
        a.Stx(Joy);
        a.T.Cycles += 3;                                         // JMP ,X
        Dispatch(a.T.X);
    }

    // ------------------------------------------------------------------ BOUNDR

    private void JmpBoundr()
    {
        a.Jmp();
        Boundr();
    }

    private void Boundr()
    {
        if (TrollAfterMe()) { BoDn1a(); return; }
        CallSelPly();
        if (a.Br(a.Eq)) { a.Jmp(); BoLev(); return; }
        a.Ldd(a.X(_posY));
        a.Subd(PosY);
        if (a.Lbr(a.Lt)) { BoUnUp(); return; }
        a.Cmpd(a.M(_boDnRg));                                    // long or short range seek
        if (a.Br(a.Lt)) { a.Jmp(); BoLev(); return; }
        a.Ldd(a.M(_boDnDi));                                     // BODN: distance to go down
        a.Std(Dist);
        a.LddI(_boDn1);
        a.Std(Joy);
        a.Bra();
        BoDn10();
    }

    private void BoDn1()
    {
        a.Ldd(a.U(_state));                                      // on the ground?
        if (a.Br(a.Ne)) { JmpBoundr(); return; }
        a.Ldd(VelY);
        if (a.Br(a.Mi)) { BoUp1b(); return; }
        a.Addd(Dist);
        if (a.Br(a.Pl)) { JmpBoundr(); return; }
        a.Std(Dist);
        BoDn10();
    }

    private void BoDn10()
    {
        a.Ldd(VelY);
        a.Subd(a.M(_boDnVy));                                    // falling not too fast?
        if (a.Br(a.Mi)) { BoUp1b(); return; }
        BoDn1a();
    }

    private void BoDn1a()
    {
        a.LddI(_boDn2);
        a.Std(Joy);
        a.LdaI(2);
        a.Bra();
        BoUp12();
    }

    private void BoDn2()
    {
        a.Ldd(VelY);
        if (!a.Br(a.Mi))
        {
            a.Addd(Dist);
            if (a.Br(a.Pl)) { JmpBoundr(); return; }
            a.Std(Dist);
        }
        a.LdbI(1);                                               // BODN22: wings down
        a.Dec(JoyT);
        if (!a.Br(a.Gt))
        {
            a.LddI(_boDn1);
            a.Std(Joy);
            a.Clrb();
        }
        a.Bra();                                                 // BODN2A
        BoDirL();
    }

    private void BoUnUp()
    {
        a.Cmpd(a.M(_boUpRg));
        if (a.Br(a.Gt)) { BoLev(); return; }
        a.Ldx(Pos);                                              // BOUP: cliff in the way?
        a.Ldy(PosY);
        a.Lda(a.X(_bckX));
        a.Anda(a.Y(_bckY - DyLen));
        if (a.Br(a.Ne)) { BoLev(); return; }
        a.Ldd(a.M(_boUpDi));
        a.Std(Dist);
        a.Bra();
        BoUp1a();
    }

    private void BoUp1()
    {
        a.Ldd(VelY);
        if (!a.Br(a.Pl))
        {
            a.Addd(Dist);
            if (a.Br(a.Mi)) { JmpBoundr(); return; }
            a.Std(Dist);
        }
        a.Dec(JoyT);                                             // BOUP11: time to flap?
        if (a.Br(a.Gt)) { BoUp1b(); return; }
        BoUp1a();
    }

    private void BoUp1a()
    {
        a.LddI(_boUp2);
        a.Std(Joy);
        a.Lda(a.M(_boUpWd));
        BoUp12();
    }

    private void BoUp12()
    {
        a.Sta(JoyT);                                             // wing down time
        a.LdbI(1);
        a.Bra();
        BoDirL();
    }

    private void BoUp1b()
    {
        a.Clrb();
        BoDirL();
    }

    private void BoDirL()
    {
        a.Lda(Row);                                              // below CLIF5?
        a.CmpaI(0xD3);
        if (a.Br(a.Lo)) { BoDir(); return; }
        a.Lda(VelY);
        if (a.Lbr(a.Pl)) { BoLava(); return; }                   // falling into the lava: go up
        BoDir();
    }

    private void BoDir()
    {
        a.Lda(Face);
        Facing(1, -1);
    }

    private void BoUp2()
    {
        a.Ldd(VelY);
        if (!a.Br(a.Pl))
        {
            a.Addd(Dist);
            if (a.Br(a.Mi)) { JmpBoundr(); return; }
            a.Std(Dist);
        }
        a.LdbI(1);                                               // BOUP22
        a.Dec(JoyT);
        if (!a.Br(a.Gt))
        {
            a.Lda(a.M(_boUpWu));
            a.Sta(JoyT);
            a.LddI(_boUp1);
            a.Std(Joy);
            a.Clrb();
        }
        a.Bra();                                                 // BOUP2A
        BoDirL();
    }

    private void BoLev()
    {
        a.LddI(_boLev1);                                         // level flight on this line
        a.Std(Joy);
        a.Ldd(PosY);
        a.Std(Dist);
        a.Lda(a.X(_velX));                                       // and the player's velocity (X may be stale)
        a.Sta(PVelX);
        a.Lda(a.M(_boLeTm));
        a.Sta(JoyT);
        BoLev1();
    }

    private void BoLev1()
    {
        a.Dec(JoyT);
        if (a.Br(a.Le)) { JmpBoundr(); return; }                 // BOBRA2
        a.Ldb(Row);
        a.CmpbI(0xD3);
        if (!a.Br(a.Lo) && LavaLure(3 * 18 + 9)) { BoFast(); return; }
        a.Lda(VelY);                                             // .3S: falling?
        if (a.Br(a.Mi)) { BoLevA(); return; }
        a.Ldb(DistRow);
        a.Cmpb(Row);
        if (a.Br(a.Lo)) { BoLevA(); return; }
        BoFast();
    }

    /// <summary>Below CLIF5: true to go up fast, false to let a nearby player lure the bird on.</summary>
    private bool LavaLure(int range)
    {
        CallSelPly();
        if (a.Br(a.Eq)) return true;
        a.Ldd(a.X(_posX));                                       // is the player close by?
        a.AdddI(range);
        a.Cmpd(Pos);
        if (a.Br(a.Lt)) return true;
        a.AdddI(-2 * range);
        a.Cmpd(Pos);
        return a.Br(a.Gt);
    }

    private void BoFast()
    {
        a.LddI(_boLev2);
        a.Std(Joy);
        a.LdbI(1);
        a.Bra();
        BoLevB();
    }

    private void BoLev2()
    {
        a.LddI(_boLev1);
        a.Std(Joy);
        BoLevA();
    }

    private void BoLevA()
    {
        a.Clrb();
        BoLevB();
    }

    private void BoLevB()
    {
        CopyPlayerRarely(longBranches: false);
        BoDir3();
    }

    /// <summary>Do not copy the player's moves too often: on the same velocity, sometimes turn around.</summary>
    private void CopyPlayerRarely(bool longBranches)
    {
        a.Lda(PVelX);
        a.Cmpa(VelX);
        if (longBranches ? a.Lbr(a.Ne) : a.Br(a.Ne)) return;
        a.Dec(RDir);
        if (longBranches ? a.Lbr(a.Mi) : a.Br(a.Mi)) return;
        a.Clr(RDir);
        a.Com(Face);
    }

    private void BoDir3()
    {
        a.Jmp();
        BoDir();
    }

    private void BoLav1()
    {
        a.Lda(Row);
        a.CmpaI(0xD3);
        if (a.Br(a.Lo)) { BoLav4(); return; }
        a.Lda(VelY);
        if (a.Br(a.Mi)) { BoLav4(); return; }
        BoLava();
    }

    private void BoLava()
    {
        a.LddI(_boLav2);
        a.Std(Joy);
        a.LdbI(1);
        a.Bra();
        BoDir3();
    }

    private void BoLav2()
    {
        a.LddI(_boLav1);
        a.Std(Joy);
        a.Clrb();
        a.Bra();
        BoDir3();
    }

    private void BoLav4()
    {
        a.Ldx(a.U(_decision));                                   // back to the brains
        a.T.Cycles += 3 + Symbols.IndirectCost(_dSmart);        // JMP [DSMART,X]
        Dispatch(a.T.Read16(a.T.X + _dSmart));
    }

    // ------------------------------------------------------------------ B2UNDR (hunter)

    private void JmpB2Undr()
    {
        a.Jmp();
        B2Undr();
    }

    private void B2Undr()
    {
        if (TrollAfterMe()) { B2Dn1a(); return; }
        CallSelPly();
        if (a.Br(a.Eq)) { a.Jmp(); B2Lev(); return; }
        a.Ldd(a.X(_posY));
        a.Subd(PosY);
        if (a.Lbr(a.Lt)) { B2UnUp(); return; }
        a.Subd(a.M(_huDnRg));
        if (a.Br(a.Lt)) { a.Jmp(); B2Lev(); return; }
        a.Ldd(a.M(_huDnDi));                                     // B2DNST
        a.Std(Dist);
        a.LddI(_b2Dn2);
        a.Std(Joy);
        a.Bra();
        B2Dn1b();
    }

    private void B2Dn1()
    {
        a.Ldd(a.U(_state));
        if (a.Br(a.Ne)) { JmpB2Undr(); return; }
        a.Ldd(VelY);
        if (a.Br(a.Mi)) { B2Dn1b(); return; }
        a.Addd(Dist);
        if (a.Br(a.Pl)) { JmpB2Undr(); return; }
        a.Std(Dist);
        a.Ldd(VelY);
        a.Subd(a.M(_huDnVy));
        if (a.Br(a.Mi)) { B2Dn1b(); return; }
        B2Dn1a();
    }

    private void B2Dn1a()
    {
        a.LddI(_b2Dn2);
        a.Std(Joy);
        a.LdaI(2);
        a.Sta(JoyT);
        a.LdbI(1);
        a.Jmp();
        B2DirL();
    }

    private void B2Dn2()
    {
        a.Ldd(VelY);
        if (!a.Br(a.Mi))
        {
            a.Addd(Dist);
            if (a.Br(a.Pl)) { JmpB2Undr(); return; }
            a.Std(Dist);
        }
        a.LdbI(1);                                               // B2DN22
        a.Dec(JoyT);
        if (a.Br(a.Gt)) { a.Jmp(); B2DirL(); return; }           // B2DIR2
        a.LddI(_b2Dn1);
        a.Std(Joy);
        B2Dn1b();
    }

    private void B2Dn1b()
    {
        a.Clrb();
        a.Jmp();                                                 // B2DIR2
        B2DirL();
    }

    private void B2UnUp()
    {
        a.Cmpd(a.M(_huUpRg));
        if (a.Br(a.Gt)) { a.Jmp(); B2Lev(); return; }
        a.Ldx(Pos);                                              // B2UP: cliff in the way?
        a.Ldy(PosY);
        a.Lda(a.X(_bckX));
        a.Anda(a.Y(_bckY - B2YLen));
        if (a.Lbr(a.Ne)) { B2Up3(); return; }
        B2UpSt();
    }

    private void B2UpSt()
    {
        a.Ldd(a.M(_huUpDi));
        a.Std(Dist);
        a.Bra();
        B2Up2d();
    }

    private void B2Up2()
    {
        a.Ldd(VelY);
        if (!a.Br(a.Pl))
        {
            a.Addd(Dist);
            if (a.Br(a.Mi)) { JmpB2Undr(); return; }
            a.Std(Dist);
        }
        a.LdbI(1);                                               // B2UP22
        a.Dec(JoyT);
        if (a.Br(a.Gt)) { a.Bra(); B2DirL(); return; }           // B2UP2A
        B2Up2d();
    }

    private void B2Up2d()
    {
        a.Lda(a.M(_huUpWu));
        a.Sta(JoyT);
        a.LddI(_b2Up1);
        a.Std(Joy);
        a.Clrb();
        a.Bra();                                                 // B2UP2A
        B2DirL();
    }

    private void B2Lev()
    {
        a.LddI(_b2Lev1);
        a.Std(Joy);
        a.Ldd(PosY);
        a.Std(Dist);
        a.Lda(a.X(_velX));
        a.Sta(PVelX);
        a.Lda(a.M(_huLeTm));
        a.Sta(JoyT);
        B2Lev1();
    }

    private void B2Lev1()
    {
        a.Dec(JoyT);
        if (a.Lbr(a.Le)) { JmpB2Undr(); return; }                // B2BRA2
        a.Lda(VelY);
        if (a.Lbr(a.Mi)) { B2LevA(); return; }
        a.Ldb(Row);
        a.CmpbI(0xD3);
        if (!a.Br(a.Lo) && LavaLure(2 * 18 + 9)) { B2Fast(); return; }
        a.Ldb(DistRow);                                          // .3S: above the line?
        a.Cmpb(Row);
        if (a.Lbr(a.Lo)) { B2LevA(); return; }
        B2Fast();
    }

    private void B2Fast()
    {
        a.LddI(_b2Lev2);
        a.Std(Joy);
        a.LdbI(1);
        B2Le11();
    }

    private void B2Le11()
    {
        a.Lda(PVelX);
        a.Cmpa(VelX);
        if (a.Br(a.Ne)) { B2Dir(); return; }
        a.Dec(RDir);
        if (a.Br(a.Mi)) { B2Dir(); return; }
        a.Clr(RDir);
        a.Com(Face);
        a.Bra();
        B2Dir();
    }

    private void B2DirL()
    {
        a.Lda(Row);
        a.CmpaI(0xD3);
        if (a.Br(a.Lo)) { B2Dir(); return; }
        a.Lda(VelY);
        if (a.Br(a.Mi)) { B2Dir(); return; }
        a.Jmp();
        BoLava();                                                // the standard way to avoid the lava
    }

    private void B2Dir()
    {
        if (HeadingIntoCliff(B2XLen))
        {
            a.LddI(_b2Av);                                       // B2DICL: slow down
            a.Std(Joy);
            a.LdaI(8);
            a.Sta(JoyT);
            a.LdbI(1);
        }
        B2DirA();
    }

    /// <summary>
    /// B2DIR / SHDIR: look ahead along the flight path (velocity Y * 8 rows, <paramref name="reach"/>
    /// columns); on a cliff, face away and return true.
    /// </summary>
    private bool HeadingIntoCliff(int reach)
    {
        a.Lda(VelX);
        if (a.Br(a.Eq)) return false;
        bool right = a.Br(a.Pl);
        a.PshsB();
        a.Ldd(VelY);
        for (int i = 0; i < 3; i++)
        {
            a.Aslb();
            a.Rola();
        }
        a.PulsB();
        a.Ldy(PosY);
        a.Ldx(Pos);
        a.LeayAY();
        a.Lda(a.X(_bckX + (right ? reach : -reach)));
        a.Anda(a.Y(_bckY));
        if (a.Br(a.Eq)) return false;
        if (right)
        {
            a.LdaI(-1);
            a.Sta(Face);
        }
        else
        {
            a.Clr(Face);
            a.Bra();
        }
        return true;
    }

    private void B2DirA()
    {
        a.Lda(BumpX);                                            // face the bumped direction
        if (!a.Br(a.Eq)) a.Sta(Face);
        a.Lda(Face);
        Facing(1, -1);
    }

    private void B2Lev2()
    {
        a.LddI(_b2Lev1);
        a.Std(Joy);
        B2LevA();
    }

    private void B2LevA()
    {
        a.Clrb();
        a.Jmp();
        B2Le11();
    }

    private void B2Up1()
    {
        a.Ldd(VelY);
        if (!a.Br(a.Pl))
        {
            a.Addd(Dist);
            if (a.Br(a.Mi)) { JmpB2Undr(); return; }
            a.Std(Dist);
        }
        a.Dec(JoyT);                                             // B2UP11
        if (a.Br(a.Gt)) { B2Up1b(); return; }
        a.Inc(JoyT);
        a.Ldd(VelY);
        a.Cmpd(a.M(_huUpVy));
        if (a.Br(a.Lt)) { B2Up1b(); return; }
        a.LddI(_b2Up2);                                          // B2UP1A: flap
        a.Std(Joy);
        a.Lda(a.M(_huUpWd));
        a.Sta(JoyT);
        a.LdbI(1);
        a.Jmp();
        B2DirL();
    }

    private void B2Up1b()
    {
        a.Clrb();
        a.Jmp();
        B2DirL();
    }

    private void B2Av()
    {
        a.Clrb();
        a.Dec(JoyT);
        if (a.Br(a.Gt)) { B2DirA(); return; }
        JmpB2Undr();
    }

    private void B2Up3()
    {
        a.LddI(_b2Up3a);                                         // level flight, ready to go up
        a.Std(Joy);
        a.Ldd(PosY);
        a.Std(Dist);
        a.LdaI(20 + 1);
        a.Sta(JoyT);
        B2Up3a();
    }

    private void B2Up3a()
    {
        a.Ldx(Pos);
        a.Ldy(PosY);
        a.Lda(a.X(_bckX));
        a.Anda(a.Y(_bckY - B2YLen));
        if (a.Lbr(a.Eq)) { B2UpSt(); return; }
        a.Dec(JoyT);
        if (a.Lbr(a.Le)) { B2Undr(); return; }
        a.Ldd(VelY);                                             // falling fast enough?
        a.AdddI(-0x40);
        if (a.Br(a.Mi)) { B2Up3b(); return; }
        a.Ldb(DistRow);
        a.Cmpb(Row);
        if (a.Br(a.Lo)) { B2Up3b(); return; }
        a.LddI(_b2Up4);
        a.Std(Joy);
        a.LdbI(1);
        a.Jmp();
        B2DirA();
    }

    private void B2Up4()
    {
        a.LddI(_b2Up3a);
        a.Std(Joy);
        B2Up3b();
    }

    private void B2Up3b()
    {
        a.Clrb();
        a.Jmp();
        B2DirA();
    }

    // ------------------------------------------------------------------ SHADOW (shadow lord)

    private void JmpShadow()
    {
        a.Jmp();
        Shadow();
    }

    private void Shadow()
    {
        if (TrollAfterMe()) { ShUpSt(); return; }
        CallSelPly();
        if (a.Lbr(a.Eq)) { ShLev(); return; }
        a.Ldd(a.X(_posY));
        a.Subd(PosY);
        if (a.Lbr(a.Lt)) { ShUnUp(); return; }
        a.Subd(a.M(_shDnRg));
        if (a.Br(a.Lt)) { ShLep(); return; }
        a.LddI(_shDn2);                                          // SHDN: glide down
        a.Std(Joy);
        a.Clrb();
        a.Lda(Row);
        a.CmpaI(0xD3);
        if (!a.Br(a.Lo))
        {
            a.Lda(VelX);
            if (!a.Br(a.Mi)) a.Incb();                           // hot lava below: flap
        }
        a.Jmp();
        ShDirB();
    }

    private void ShUnUp()
    {
        a.Cmpd(a.M(_shUpRg));
        if (a.Br(a.Gt)) { ShLep(); return; }
        a.Ldx(Pos);
        a.Ldy(PosY);
        a.Lda(a.X(_bckX));
        a.Anda(a.Y(_bckY - ShYLen));
        if (a.Lbr(a.Ne)) { ShUp3(); return; }
        ShUpSt();
    }

    private void ShUpSt()
    {
        a.LddI(_shUp1);
        a.Std(Joy);
        ShUp0();
    }

    private void ShUp0()
    {
        a.Clrb();
        a.Jmp();
        ShDirB();
    }

    private void ShUp1()
    {
        a.LddI(_shadow);
        a.Std(Joy);
        a.Ldd(VelY);
        a.Cmpd(a.M(_shUpVy));
        if (a.Br(a.Lt)) { ShUp0(); return; }
        a.LdbI(1);
        a.Jmp();
        ShDirB();
    }

    private void ShLep()
    {
        a.LddI(_shLep1);                                         // level flight on the player's line
        a.Std(Joy);
        a.Ldb(a.X(_posY + 1));
        a.Stb(DistRow);
        a.Lda(a.X(_velX));
        a.Sta(PVelX);
        a.Lda(a.M(_shUpTm));
        a.Sta(JoyT);
        ShLep1();
    }

    private void ShLep1()
    {
        a.Dec(JoyT);
        if (a.Lbr(a.Le)) { JmpShadow(); return; }                // SHBRA2
        a.Ldb(Row);
        a.CmpbI(0xD3);                                           // close to the lava?
        if (!a.Br(a.Lo))
        {
            a.Lda(VelY);
            if (a.Br(a.Pl)) { ShFast(); return; }
        }
        a.Cmpb(DistRow);                                         // .2S
        if (a.Br(a.Ls)) { ShLepA(); return; }
        ShFast();
    }

    private void ShFast()
    {
        a.LddI(_shLep2);
        a.Std(Joy);
        a.LdbI(1);
        a.Bra();
        ShLepB();
    }

    private void ShLep2()
    {
        a.LddI(_shLep1);
        a.Std(Joy);
        ShLepA();
    }

    private void ShLepA()
    {
        a.Clrb();
        ShLepB();
    }

    private void ShLepB()
    {
        a.Lda(PVelX);
        a.Cmpa(VelX);
        if (a.Lbr(a.Ne)) { ShDirA(); return; }
        a.Dec(RDir);
        if (a.Lbr(a.Mi)) { ShDirA(); return; }
        a.Clr(RDir);
        a.Com(Face);
        a.Jmp();
        ShDirA();
    }

    private void ShLev()
    {
        a.LddI(_shLev1);                                         // level flight on this line
        a.Std(Joy);
        a.Ldb(Row);
        a.Stb(DistRow);
        a.Lda(a.M(_shLeTm));
        a.Sta(JoyT);
        ShLev1();
    }

    private void ShLev1()
    {
        a.Dec(JoyT);
        if (a.Lbr(a.Le)) { JmpShadow(); return; }
        a.Lda(VelY);
        if (a.Lbr(a.Mi)) { ShLevA(); return; }
        a.Ldb(DistRow);
        a.Cmpb(Row);
        if (a.Lbr(a.Lo)) { ShLevA(); return; }
        a.LddI(_shLev2);
        a.Std(Joy);
        a.LdbI(1);
        ShDir();
    }

    private void ShDir()
    {
        a.Lda(Row);                                              // below CLIF5?
        a.CmpaI(0xD0);
        if (!a.Br(a.Lo))
        {
            a.Lda(VelY);
            if (a.Lbr(a.Pl)) { BoLava(); return; }
        }
        if (HeadingIntoCliff(ShXLen))
        {
            a.LddI(_shAv);                                       // SHDICL: slow down
            a.Std(Joy);
            a.Lda(a.M(_shClTm));
            a.Sta(JoyT);
            a.LdbI(1);
        }
        ShDirA();
    }

    private void ShDirA() => B2DirA();

    private void ShDirB()
    {
        a.Lda(VelX);
        if (a.Br(a.Eq)) { ShDirA(); return; }
        a.Clra();
        a.Std(CurJoy);
    }

    private void ShLev2()
    {
        a.LddI(_shLev1);
        a.Std(Joy);
        ShLevA();
    }

    private void ShLevA()
    {
        a.Clrb();
        a.Jmp();
        ShDir();
    }

    private void ShAv()
    {
        a.Clrb();
        a.Dec(JoyT);
        if (a.Br(a.Gt)) { ShDirA(); return; }
        JmpShadow();
    }

    private void ShUp3()
    {
        a.LddI(_shUp3a);
        a.Std(Joy);
        a.Ldb(Row);
        a.Stb(DistRow);
        a.LdaI(20 + 1);
        a.Sta(JoyT);
        ShUp3a();
    }

    private void ShUp3a()
    {
        a.Ldx(Pos);
        a.Ldy(PosY);
        a.Lda(a.X(_bckX));
        a.Anda(a.Y(_bckY - ShYLen));
        if (a.Lbr(a.Eq)) { ShUpSt(); return; }
        a.Dec(JoyT);
        if (a.Lbr(a.Le)) { Shadow(); return; }
        a.Ldd(VelY);
        a.AdddI(-0x40);
        if (a.Br(a.Mi)) { ShUp3b(); return; }
        a.Ldb(DistRow);
        a.Cmpb(Row);
        if (a.Br(a.Lo)) { ShUp3b(); return; }
        a.LddI(_shUp4);
        a.Std(Joy);
        a.LdbI(1);
        a.Jmp();
        ShDirA();
    }

    private void ShUp4()
    {
        a.LddI(_shUp3a);
        a.Std(Joy);
        ShUp3b();
    }

    private void ShUp3b()
    {
        a.Clrb();
        a.Jmp();
        ShDirA();
    }

    private void ShDn2()
    {
        a.LddI(_shadow);
        a.Std(Joy);
        a.Clrb();
        a.Lda(VelX);
        if (a.Lbr(a.Eq)) { ShDirA(); return; }
        a.Lda(a.U(_state));                                      // on the ground?
        if (!a.Br(a.Eq))
        {
            a.Ldx(Pos);                                          // CLIF3U in the way?
            a.Lda(a.X(_bckX));
            a.Ldx(PosY);
            a.Anda(a.X(_bckY));
            if (!a.Br(a.Eq)) a.LdbI(1);
        }
        a.Clra();
        a.Std(CurJoy);
    }

    // ------------------------------------------------------------------ SELPLY

    /// <summary>
    /// SELPLY (body, without its RTS): the player to chase in X with Z clear, or Z set if none.
    /// With two players it picks the nearer by the larger of the X and Y distances.
    /// </summary>
    private void SelPly()
    {
        a.Ldx(a.M(_tarPly));
        if (a.Br(a.Eq)) { a.Orcc(Z); return; }
        a.Lda(a.M(_tarTm1));                                     // player 1 still delayed?
        if (!a.Br(a.Eq))
        {
            a.Ldx(a.M(_tarPl2));
            if (a.Br(a.Eq)) { a.Orcc(Z); return; }
            a.Lda(a.M(_tarTm2));
            if (a.Br(a.Eq)) { a.Andcc(~Z); return; }
            a.Bra();
            a.Orcc(Z);
            return;
        }
        a.Ldy(a.M(_tarPl2));                                     // .20S
        if (a.Br(a.Eq)) { a.Andcc(~Z); return; }
        a.Lda(a.M(_tarTm2));
        if (a.Br(a.Ne)) { a.Andcc(~Z); return; }
        Closeness(a.X(_posY + 1), a.X(_posX));
        Closeness(a.Y(_posY + 1), a.Y(_posX));
        a.Cmpb(a.S(1));
        a.PulsD();
        if (!a.Br(a.Lo)) a.Ldx(a.M(_tarPl2));                    // SPN2PL
        a.Andcc(~Z);                                             // SPN3PL
    }

    /// <summary>Pushes -max(|dx|, |dy|) (as unsigned bytes, the smaller is the farther) for one player.</summary>
    private void Closeness(Asm.Ea row, Asm.Ea column)
    {
        a.Ldb(row);
        a.Subb(Row);
        if (!a.Br(a.Lo)) a.Negb();
        a.StbPush();
        a.Ldd(column);
        a.Subd(Pos);
        if (!a.Br(a.Lo))
        {
            a.Coma();
            a.Comb();
            a.AdddI(-1);
        }
        a.Tsta();
        if (a.Br(a.Ne)) return;
        a.Cmpb(a.S(0));
        if (a.Br(a.Hi)) return;
        a.Stb(a.S(0));
    }

    // ------------------------------------------------------------------ PTERO

    private void Ptero()
    {
        a.Dec(PVelX);                                            // time to zoom in on the player?
        if (!a.Br(a.Ne))
        {
            a.Lda(a.U(_feet));
            a.Sta(PVelX);
            a.Clr(RDir);
            CallSelPly();
            if (a.Lbr(a.Eq)) { PteLev(); return; }
            a.Lda(a.U(_feet));
            a.CmpaI(15);                                         // minimum look-at-player time
            if (!a.Br(a.Ls)) a.Dec(a.U(_feet));
            a.Ldd(a.X(_posX));                                   // .48S: aim at the player
            a.Std(Dist);
            a.Ldb(a.X(_posY + 1));
            a.CmpbI(0xD3 - 1);                                   // never below CLIF5
            if (!a.Br(a.Lo)) a.LdbI(0xD3 - 1);
            a.SubbI(5);                                          // .98S
            a.Jsr();
            Patch4();
            a.Rts();
        }
        a.Ldb(RDir);                                             // .47S: victim's row
        if (a.Br(a.Eq)) { PteLev(); return; }
        a.Cmpb(Row);
        if (a.Lbr(a.Lo)) { PteUp(); return; }
        a.SubbI(15);
        a.Cmpb(Row);
        if (a.Lbr(a.Hi)) { PteDn(); return; }
        a.LdaI(128);                                             // attack sequence time-out
        a.Jsr();
        Patch5();
        a.Rts();
        a.LdxI(_snPte);                                          // the pterodactyl's call
        _ground.CallSound(a.T);
        a.LddI(_pteAtk);
        a.Std(Joy);
        PteAtk();
    }

    /// <summary>PATCH4: baiters aim 2 pixels lower; STB PRDIR,U.</summary>
    private void Patch4()
    {
        a.Tst(Chase);
        if (!a.Br(a.Eq)) a.AddbI(2);
        a.Stb(RDir);
    }

    /// <summary>PATCH5: STA PEGG,U; baiters slow their vertical speed to a quarter.</summary>
    private void Patch5()
    {
        a.Sta(a.U(_egg));
        a.Tst(Chase);
        if (a.Br(a.Eq)) return;
        a.Asr(VelY);
        a.Ror(a.U(_velY + 1));
        a.Asr(VelY);
        a.Ror(a.U(_velY + 1));
    }

    /// <summary>PATCH9: baiters keep their seek timer running (saturating at 1); DEC PEGG,U.</summary>
    private void Patch9()
    {
        a.Tst(Chase);
        if (!a.Br(a.Eq))
        {
            a.Dec(PVelX);
            if (!a.Br(a.Ne)) a.Inc(PVelX);
        }
        a.Dec(a.U(_egg));
    }

    private void PteAtk()
    {
        a.Jsr();
        Patch9();
        a.Rts();
        if (a.Br(a.Eq)) { PteEnd(); return; }                    // timed out
        a.Ldd(Dist);                                             // within the attack window?
        a.Subd(Pos);
        if (a.Br(a.Mi))
        {
            a.AdddI(13);                                         // .1S
            if (a.Br(a.Mi)) { PteAttack(); return; }
            PteEnd();
            return;
        }
        a.SubdI(13);
        if (a.Br(a.Pl)) { PteAttack(); return; }
        a.Bra();
        PteEnd();
    }

    /// <summary>.3S: end the attack sequence.</summary>
    private void PteEnd()
    {
        a.LddI(_ptero);
        a.Std(Joy);
        a.Clr(RDir);
        a.Clrb();
        a.Jmp();
        PteDir();
    }

    /// <summary>.2S: on the attack frame, stay on the player's centre line.</summary>
    private void PteAttack()
    {
        a.Lda(a.U(_image));
        a.CmpaI(_attackFrame);
        if (!a.Br(a.Ne))
        {
            a.LdaI(10);
            a.Sta(a.U(_accX));                                   // stay on the attack frame
        }
        a.Lda(Row);                                              // .20S
        a.Cmpa(RDir);
        if (a.Br(a.Eq))
        {
            a.Clr(VelY);                                         // PTEALE: level
            a.Bra();
            PteDir();
            return;
        }
        if (a.Br(a.Lo))
        {
            a.Lda(VelY);                                         // PTEADN: go down
            if (a.Br(a.Gt)) { PteDir(); return; }
            a.Ldd(VelY);
            a.AdddI(0x10);
            a.Std(VelY);
            a.Bra();
            PteDir();
            return;
        }
        a.Lda(VelY);                                             // go up
        if (!a.Br(a.Mi))
        {
            a.Ldd(VelY);
            a.SubdI(0x10);
            a.Std(VelY);
        }
        a.Bra();                                                 // .21S
        PteDir();
    }

    private void PteLev()
    {
        a.LdaI(_line1 - 2);
        a.Ldb(Row);
        a.CmpbI(_line1 + _lineBand);
        if (!a.Br(a.Lo))
        {
            a.LdaI(_line2 - 2);
            a.CmpbI(_line2 + _lineBand);
            a.Jsr();
            Patch6();
            a.Rts();
            a.Nop();
        }
        TrackLine(fast: false);                                  // .10S
        PteDis();
    }

    /// <summary>
    /// SUBA PPOSY+1,U and steer vertically toward the line in A: on it, quarter the vertical speed;
    /// otherwise accelerate toward it up to $100. The exits to the caller's continuation are a
    /// fall-through or a branch, charged here; <paramref name="fast"/> is PTEOFF's JMP version.
    /// </summary>
    private void TrackLine(bool fast)
    {
        a.Suba(Row);
        if (a.Br(a.Eq))
        {
            a.Ldd(VelY);                                         // .30S
            a.Asra();
            a.Rorb();
            a.Asra();
            a.Rorb();
            a.Std(VelY);
            if (fast) a.Jmp();
            return;
        }
        bool above = a.Br(a.Mi);
        a.Ldd(VelY);
        a.CmpdI(above ? -0x100 : 0x100);
        if (a.Br(above ? a.Le : a.Ge))
        {
            if (fast) a.Jmp();
            return;
        }
        if (above) a.SubdI(0x20);
        else a.AdddI(0x20);
        a.Std(VelY);
        if (fast) a.Jmp();
        else a.Bra();
    }

    /// <summary>PATCH6: track line 3, or for baiters a line between CLIF3U and the lower cliffs.</summary>
    private void Patch6()
    {
        if (!a.Br(a.Lo))
        {
            a.LdaI(_line3 - 2);
            return;
        }
        a.Tst(Chase);                                            // .10S
        if (a.Br(a.Eq)) { a.LdaI(_line2 - 2); return; }
        a.Ldd(Pos);
        a.SubdI(260 + 7);                                        // right of CLIF3U?
        if (a.Br(a.Gt)) { a.LdaI(0x88); return; }
        a.SubdI((200 - 28 - 7) - (260 + 7));                     // left of CLIF3U?
        if (a.Br(a.Gt)) { a.LdaI(_line2 - 2); return; }
        a.LdaI(0x88);                                            // .20S
    }

    /// <summary>PTEDIS: cruise at speed 4 in the bumped (or current) direction; no controls.</summary>
    private void PteDis() => PteSpeed(4);

    /// <summary>PTEDIR: fly the way it faces, speed 8 in the bumped (or current) direction.</summary>
    private void PteDir()
    {
        a.Lda(Face);
        a.Sta(VelX);
        PteSpeed(8);
    }

    private void PteSpeed(int speed)
    {
        a.Tst(BumpX);
        if (a.Br(a.Eq))
        {
            a.LdaI(speed);                                       // .30S
            a.Tst(VelX);
            if (!a.Br(a.Pl)) a.LdaI(-speed);
        }
        else if (a.Br(a.Mi))
        {
            a.LdaI(-speed);                                      // .20S
        }
        else
        {
            a.LdaI(speed);
            a.Bra();
        }
        a.Sta(VelX);                                             // .10S
        a.Sta(Face);
        a.LddI(0);
        a.Std(CurJoy);
    }

    private void PteUp()
    {
        a.Ldd(VelY);
        a.CmpdI(-0xC0);
        if (!a.Br(a.Le))
        {
            a.AdddI(-0x40);                                      // do not go up too fast
            a.Std(VelY);
        }
        PteDn2();
    }

    private void PteDn2()
    {
        a.LddI(_pteUp2);
        a.Std(Joy);
        a.LdaI(2);                                               // wing down time
        a.Sta(JoyT);
        a.Bra();
        PteDis();
    }

    private void PteUp2()
    {
        a.Dec(JoyT);
        if (a.Br(a.Gt)) { PteDis(); return; }
        a.LddI(_ptero);
        a.Std(Joy);
        a.Bra();
        PteDis();
    }

    private void PteDn()
    {
        a.Ldd(VelY);
        a.CmpdI(0x100);
        if (a.Br(a.Ge)) { PteDn2(); return; }
        a.AdddI(0x10);                                           // do not go down too fast
        a.Std(VelY);
        a.Bra();
        PteDn2();
    }

    private void PteOff()
    {
        OffScreenCheck();                                        // PTEGON is not ported
        NearestLine(2);
        TrackLine(fast: true);
        PteDir();
    }

    /// <summary>Hands back once the bird is off screen (its removal naps and never returns).</summary>
    private void OffScreenCheck()
    {
        a.Ldd(Pos);
        a.CmpdI(_eLeft + 3);
        if (a.Br(a.Lt)) throw new HandBackException();
        a.CmpdI(_eRight - 3);
        if (a.Br(a.Gt)) throw new HandBackException();
    }

    // ------------------------------------------------------------------ SEEKE

    private void Seeke()
    {
        a.Ldy(Dist);                                             // the man's workspace
        a.Clrb();
        a.Lda(a.Y(_posY + 1));
        a.Cmpa(Row);
        if (a.Br(a.Eq)) { SeekF1(); return; }
        if (!a.Br(a.Lo))
        {
            a.Ldb(VelY);                                         // not too fast a drop
            a.CmpbI(2);
            if (!a.Br(a.Ge))
            {
                a.Ldx(Pos);                                      // a cliff in the way?
                a.Lda(a.X(_bckX + 18));
                a.Ldb(VelX);
                if (!a.Br(a.Pl)) a.Lda(a.X(_bckX - 18));
                a.Ldx(PosY);                                     // SEEKC2
                a.Clrb();
                a.Anda(a.X(_bckY));
                a.AndaI(0x08);                                   // CLIF3U
                if (a.Br(a.Eq)) { SeekF1(); return; }
            }
        }
        a.Clrb();                                                // SEEKBL
        a.Lda(VelY);
        a.CmpaI(-1);                                             // maximum upward speed
        if (a.Br(a.Lt)) { SeekF1(); return; }
        a.LddI(_seekF2);
        a.Std(Joy);
        a.LdaI(4);
        a.Sta(JoyT);
        a.LdbI(1);
        SeekF1();
    }

    private void SeekF1()
    {
        a.PshsB();
        a.Ldd(a.Y(_posX));                                       // which way to face?
        a.AdddI(-12);
        a.Subd(Pos);
        if (a.Br(a.Pl))
        {
            a.Lda(RDir);                                         // SEEKFR
            if (a.Br(a.Ne)) { SeekS(); return; }
            SeekR2();
            return;
        }
        a.AdddI(8 + 12);
        if (a.Br(a.Mi))
        {
            a.Lda(RDir);                                         // SEEKFL
            if (a.Br(a.Ne)) { SeekS(); return; }
            SeekL2();
            return;
        }
        a.LdaI(1);                                               // SEEKFS: within short range
        a.Sta(RDir);
        a.Ldx(a.U(_state));
        if (a.Br(a.Eq))
        {
            a.Lda(VelX);                                         // SEEKAR: slow down, do not reverse
            if (a.Br(a.Mi))
            {
                a.CmpaI(-2);                                     // SEEKL3
                if (a.Br(a.Eq)) { SeekF0(); return; }
                a.Bra();
                SeekR();
                return;
            }
            if (a.Br(a.Gt))
            {
                a.CmpaI(2);                                      // SEEKR3
                if (a.Br(a.Eq)) { SeekF0(); return; }
                a.Bra();
                SeekL();
                return;
            }
            a.Lda(Face);
            if (a.Br(a.Mi)) { SeekL(); return; }
            a.Bra();
            SeekR();
            return;
        }
        a.Lda(a.Y(_posY + 1));                                   // trapped on another cliff?
        a.Cmpb(Row);
        if (a.Br(a.Hi))
        {
            a.Lda(VelX);                                         // SEEKFJ
            if (a.Br(a.Mi)) { SeekL2(); return; }
            a.Bra();
            SeekR2();
            return;
        }
        a.Lda(VelX);
        if (a.Br(a.Eq)) { MountM(); return; }                    // got the man
        a.Lda(VelX);
        if (a.Br(a.Mi)) { SeekR(); return; }
        a.Bra();
        SeekL();
    }

    private void SeekL2()
    {
        a.Lda(VelX);
        if (a.Br(a.Mi)) { SeekF0(); return; }
        SeekL();
    }

    private void SeekL() => Steer(-1);

    private void SeekR2()
    {
        a.Lda(VelX);
        if (a.Br(a.Gt)) { SeekF0(); return; }
        SeekR();
    }

    private void SeekR() => Steer(1);

    private void SeekF0()
    {
        a.Clra();
        a.PulsB();
        a.Std(CurJoy);
    }

    private void Steer(int direction)
    {
        a.LdaI(direction);
        a.PulsB();
        a.Std(CurJoy);
    }

    /// <summary>SEEKS: the shortest way round the wrap-around screen.</summary>
    private void SeekS()
    {
        int width = _eRight - _eLeft;
        a.Ldd(a.Y(_posX));
        a.Subd(Pos);
        if (a.Br(a.Mi))
        {
            a.CmpdI(-width / 2);                                 // SEEKSM
            if (!a.Br(a.Gt)) a.AdddI(width);
        }
        else
        {
            a.CmpdI(width / 2);                                  // SEEKSP
            if (!a.Br(a.Lt))
            {
                a.SubdI(width);
                a.Bra();
            }
        }
        a.Tsta();                                                // SEEKSD
        if (a.Br(a.Mi)) { SeekL2(); return; }
        a.Bra();
        SeekR2();
    }

    private void MountM()
    {
        a.PulsB();
        a.LddI(_mounLp);
        a.Std(Joy);
        a.LdaI(5 + 1);                                           // wait a bit
        a.Sta(JoyT);
        MounLp();
    }

    private void MounLp()
    {
        a.Dec(JoyT);
        if (a.Br(a.Eq)) throw new HandBackException();           // MOUNRI: the rider mounts (not ported)
        a.LdaI(1);                                               // run to the right for your life
        a.Ldb(Face);
        if (!a.Br(a.Pl)) a.Nega();
        a.Clrb();                                                // MOUNRN
        a.Std(CurJoy);
    }

    private void SeekF2()
    {
        a.Dec(JoyT);                                             // flap
        if (a.Br(a.Eq))
        {
            a.LddI(_seeke);                                      // SEEKF3
            a.Std(Joy);
            a.LddI(0);
            a.Std(CurJoy);
            return;
        }
        a.LddI(1);
        a.Std(CurJoy);
    }

    // ------------------------------------------------------------------ AUTOFF

    private void Autoff()
    {
        OffScreenCheck();                                        // AUTSCR is not ported
        NearestLine(0);
        a.Clrb();                                                // AUTTRK
        a.Suba(Row);
        if (!a.Br(a.Pl))
        {
            a.Lda(VelY);
            if (!a.Br(a.Mi))
            {
                a.LddI(_autOfp);                                 // get off the ground or flap
                a.Std(Joy);
                a.LdbI(1);
            }
        }
        AutFlp();
    }

    private void AutFlp()
    {
        a.Lda(Face);
        Facing(1, -1);
    }

    private void AutOfp()
    {
        a.LddI(_autoff);
        a.Std(Joy);
        a.Clrb();
        a.Bra();
        AutFlp();
    }
}
