using static Joust.Emulation.Alu6809;

namespace Joust.Port.Routines;

/// <summary>
/// Bird-against-bird contact and the joust (JOUSTRV4.ASM). HITEM tests one pair (U and X):
/// first their collision boxes, then pixel-exact against each bird's current collision data.
/// On contact (OSTHIT) the pair's ids decide what happens:
/// <list type="bullet">
/// <item>a player touching an egg collects it (EGGSCR);</item>
/// <item>two enemies (or two pterodactyls) just thud and bounce;</item>
/// <item>a player against a pterodactyl only wins with the lance in its open mouth, facing it;</item>
/// <item>otherwise the higher lance wins (lance height = position + skid offset, PLANTZ); equal
///       heights bounce apart. The loser dies through its decision block's death routine and
///       the winner scores the loser's value (OSTWIN).</item>
/// </list>
/// The death routines are not native yet: OSTWIN leaves for them and resumes natively at the
/// return addresses (OSTWIN@dead, @score, OSTXT3@win, OSTPYP@win).
/// Methods are routine bodies excluding the final RTS. Those returning ushort? give the address
/// to continue at when leaving for original code, or null when they reached their RTS.
/// </summary>
public sealed class Combat
{
    private readonly int _posX, _posY, _velX, _velY, _bumpX, _bumpY, _face, _picH, _colY1, _colY2, _colX;
    private readonly int _id, _lantz, _image, _decision, _rider, _joy, _ppvelX, _prDir, _eggs, _feet, _dist, _pc, _nap;
    private readonly int _ddead, _dvalur, _dvalue, _dconst, _deggs;
    private readonly ushort _colObj, _colDx, _autoff, _eggHit, _eggValues, _sndEgg, _sndPlayerThud, _sndEnemyThud;
    private readonly ushort _riders, _enemies, _ostwin;
    private readonly byte _ptero, _attackFrames;
    private readonly Physics _physics;
    private readonly Ground _ground;
    private readonly Scoring _scoring;

    /// <summary>Resume addresses after the calls the joust makes into original code.</summary>
    public ushort OstwinAfterDeath { get; }
    public ushort OstwinAfterScore { get; }
    public ushort ULosesAfterWin { get; }
    public ushort XLosesAfterWin { get; }

    public Combat(Symbols s, Physics physics, Ground ground, Scoring scoring)
    {
        _posX = s["PPOSX"]; _posY = s["PPOSY"]; _velX = s["PVELX"]; _velY = s["PVELY"];
        _bumpX = s["PBUMPX"]; _bumpY = s["PBUMPY"]; _face = s["PFACE"]; _picH = s["PPICH"];
        _colY1 = s["PCOLY1"]; _colY2 = s["PCOLY2"]; _colX = s["PCOLX"];
        _id = s["PID"]; _lantz = s["PLANTZ"]; _image = s["PIMAGE"]; _decision = s["PDECSN"];
        _rider = s["PRIDER"]; _joy = s["PJOY"]; _ppvelX = s["PPVELX"]; _prDir = s["PRDIR"];
        _eggs = s["PEGG"]; _feet = s["PFEET"]; _dist = s["PDIST"]; _pc = s["PPC"]; _nap = s["PNAP"];
        _ddead = s["DDEAD"]; _dvalur = s["DVALUR"]; _dvalue = s["DVALUE"]; _dconst = s["DCONST"]; _deggs = s["DEGGS"];
        _colObj = s["COLOBJ"]; _colDx = s["COLDX"]; _autoff = s["AUTOFF"]; _eggHit = s["EGGHIT"];
        _eggValues = s["EGGVAL"]; _sndEgg = s["SNEGG"]; _sndPlayerThud = s["SNPTHD"]; _sndEnemyThud = s["SNETHD"];
        _riders = s["NRIDER"]; _enemies = s["NENEMY"]; _ostwin = s["OSTWIN"];
        _ptero = (byte)(0x80 + s.Value("PTEID"));
        _attackFrames = (byte)(s.Value("FLY2") - s.Value("FLY1"));
        _physics = physics;
        _ground = ground;
        _scoring = scoring;

        // OSTWIN: PSHS X,U / LDA PID,U / ANDA / STA PID,U / LDD #0 / STD PRIDER,U / LDY PDECSN,U /
        // LDD #AUTOFF / STD PJOY,U / JSR [DDEAD,Y]  (2+2+2+2+3+3+3+3+3+3)
        OstwinAfterDeath = (ushort)(_ostwin + 26);
        // LDU 2,S / LDY PDECSN,U / LDA DVALUE,Y / LDX ,S / LDX PDECSN,X / JSR [DVALUR,Y]  (2+3+3+2+2+3)
        OstwinAfterScore = (ushort)(OstwinAfterDeath + 15);
        ULosesAfterWin = (ushort)(s["OSTXT3"] + 2 + 3);          // LDX COLOBJ / JSR OSTWIN
        XLosesAfterWin = (ushort)(s["OSTPYP"] + 2 + 2 + 3);      // LDX COLOBJ / EXG X,U / JSR OSTWIN
    }

    private static int Ix(int offset) => Symbols.IndexCost(offset);
    private static int Ext(ushort address) => Symbols.AddressCost(address);

    // ------------------------------------------------------------------ HITEM

    /// <summary>
    /// HITEM: do U and X touch, and if so, joust. Returns with carry set when U is out of the
    /// sweep (dead, or bounced off an equal), clear otherwise.
    /// </summary>
    public ushort? Hit(Tx t)
    {
        ushort other = t.X;
        t.Write16(t.Var(_colObj), other);
        t.CC = Logic16(t.CC, other);
        t.D = t.Read16(t.U + _posX);
        t.CC = Logic16(t.CC, t.D);
        t.D = Sub16(ref t.CC, t.D, t.Read16(other + _colX));
        t.Cycles += 5 + Ext(_colObj) + 5 + Ix(_posX) + 6 + Ix(_colX) + 3;   // ... BGT
        if (Gt(t.CC)) return Miss(t);                            // U is right of X's box
        t.D = t.Read16(other + _posX);
        t.CC = Logic16(t.CC, t.D);
        Sub16(ref t.CC, t.D, t.Read16(t.U + _colX));
        t.Cycles += 5 + Ix(_posX) + 7 + Ix(_colX) + 3;
        if (Gt(t.CC)) return Miss(t);                            // X is right of U's box
        t.D = Sub16(ref t.CC, t.D, t.Read16(t.U + _posX));
        t.Write16(t.Var(_colDx), t.D);                           // X offset between the birds
        t.CC = Logic16(t.CC, t.D);
        t.Cycles += 6 + Ix(_posX) + 5 + Ext(_colDx);

        t.D = t.Read16(other + _colY1);                          // A = X bottom, B = X top
        t.CC = Logic16(t.CC, t.D);
        Sub8(ref t.CC, t.B, t.Read(t.U + _colY1));
        t.Cycles += 5 + Ix(_colY1) + 4 + Ix(_colY1) + 3;         // ... BHI
        if (Hi(t.CC)) return Miss(t);                            // X's top below U's bottom
        Sub8(ref t.CC, t.A, t.Read(t.U + _colY2));
        t.Cycles += 4 + Ix(_colY2) + 3;                          // CMPA / BLO
        if (Cs(t.CC)) return Miss(t);                            // X's bottom above U's top

        // Boxes overlap: align the two collision tables vertically and scan them.
        t.Y = t.Read16(t.Read16(other + _picH));
        t.CC = Logic16(t.CC, t.Y);
        t.X = t.Read16(t.Read16(t.U + _picH));
        t.CC = Logic16(t.CC, t.X);
        t.B = Sub8(ref t.CC, t.B, t.Read(t.U + _colY2));
        t.B = Asl8(ref t.CC, t.B);
        t.B = Asl8(ref t.CC, t.B);                               // rows x 4 bytes (max difference $1F)
        t.Cycles += 6 + Symbols.IndirectCost(_picH) + 5 + Symbols.IndirectCost(_picH)
                    + 4 + Ix(_colY2) + 2 + 2 + 3;                // ... BMI
        if (!Mi(t.CC))
        {
            t.X = (ushort)(t.X + t.B);                           // ABX
            t.Cycles += 3 + 3;
        }
        else
        {
            t.B = Neg8(ref t.CC, t.B);
            t.Y = (ushort)(t.Y + (sbyte)t.B);                    // LEAY B,Y
            t.CC = Set(t.CC, Z, t.Y == 0);
            t.Cycles += 2 + 5;
        }
        t.Cycles += 8;                                           // JSR BPCOL
        _physics.ScanLines(t);
        t.Cycles += 5 + 3;                                       // RTS / BCS OSTHIT
        if (Cs(t.CC)) return Contact(t);
        return Done(t);
    }

    /// <summary>HITEM2: back to the other bird, carry clear.</summary>
    private ushort? Done(Tx t)
    {
        t.X = t.Read16(t.Var(_colObj));
        t.CC = Logic16(t.CC, t.X);
        t.Cycles += 5 + Ext(_colObj);
        return Miss(t);
    }

    private static ushort? Miss(Tx t)
    {
        t.CC = (byte)(t.CC & ~C);                                // HITEM1: ANDCC #$FE
        t.Cycles += 3;
        return null;
    }

    // ------------------------------------------------------------------ the joust

    /// <summary>OSTHIT: what kind of contact is this?</summary>
    private ushort? Contact(Tx t)
    {
        t.X = t.Read16(t.Var(_colObj));
        t.CC = Logic16(t.CC, t.X);
        t.A = t.Read(t.U + _id);
        t.CC = Logic8(t.CC, t.A);
        t.A = (byte)(t.A & t.Read(t.X + _id));                   // id bits both share
        t.CC = Logic8(t.CC, t.A);
        t.CC = Logic8(t.CC, (byte)(t.A & 0x01));
        t.Cycles += 5 + Ext(_colObj) + 4 + Ix(_id) + 4 + Ix(_id) + 2 + 3;
        if ((t.A & 0x01) == 0)
        {
            t.CC = Logic8(t.CC, (byte)(t.A & 0x02));
            t.Cycles += 2 + 5;                                   // BITA #$02 / LBEQ PLYEGG
            if ((t.A & 0x02) != 0)
                throw new HandBackException();                   // SWI: "fictitious collision"
            t.Cycles += 1;
            t.Cycles += 8;                                       // PLYEGG: JSR EGGSCR
            t.Push16(0);                                         // (its return address; dead after)
            CollectEgg(t);
            t.Cycles += 4;                                       // JMP HITEM2
            return Done(t);
        }
        t.CC = Logic8(t.CC, (byte)(t.A & 0x04));
        t.Cycles += 2 + 3;
        if ((t.A & 0x04) != 0)
            return EnemiesCollide(t);                            // enemy vs enemy: no kill
        t.CC = Logic8(t.CC, (byte)(t.A & 0x08));
        t.Cycles += 2 + 3;
        if ((t.A & 0x08) == 0 && PterodactylSpeared(t))
            return XLoses(t);
        return Lances(t);
    }

    /// <summary>
    /// A player against a pterodactyl: the lance (rider height + skid offset) must be within
    /// 5 pixels of its mouth (7 while it attacks), the two facing each other.
    /// </summary>
    private bool PterodactylSpeared(Tx t)
    {
        t.B = t.Read(t.U + _lantz);
        t.CC = Logic8(t.CC, t.B);
        t.B = Add8(ref t.CC, t.B, t.Read(t.U + _posY + 1), withCarry: false);
        t.B = Sub8(ref t.CC, t.B, t.Read(t.X + _posY + 1));
        t.A = t.Read(t.X + _image);
        t.CC = Logic8(t.CC, t.A);
        Sub8(ref t.CC, t.A, _attackFrames);
        t.Cycles += 4 + Ix(_lantz) + 4 + Ix(_posY + 1) + 4 + Ix(_posY + 1) + 4 + Ix(_image) + 2 + 3;
        bool attacking = !(Cs(t.CC) || Eq(t.CC));
        t.B = Sub8(ref t.CC, t.B, attacking ? (byte)(15 - 7) : (byte)(15 - 5));
        t.Cycles += 2 + 3;
        if (Mi(t.CC))
        {
            t.B = Neg8(ref t.CC, t.B);
            t.Cycles += 2;
        }
        Sub8(ref t.CC, t.B, attacking ? (byte)3 : (byte)2);
        t.Cycles += 2 + (attacking ? 3 : 0) + 3;                 // CMPB (/ BRA .14S) / BHI OSTBO
        if (Hi(t.CC)) return false;                              // missed the mouth

        t.A = t.Read(t.U + _face);
        t.CC = Logic8(t.CC, t.A);
        t.A = (byte)(t.A ^ t.Read(t.X + _face));
        t.CC = Logic8(t.CC, t.A);
        t.Cycles += 4 + Ix(_face) + 4 + Ix(_face) + 3;
        if (!Mi(t.CC)) return false;                             // not facing each other

        t.D = t.Read16(t.Var(_colDx));                           // pterodactyl X minus player X
        t.CC = Logic16(t.CC, t.D);
        bool pteroOnRight = (t.D & 0x8000) == 0;                 // BPL .12S
        t.A = t.Read(t.U + _face);
        t.CC = Logic8(t.CC, t.A);
        t.Cycles += 5 + Ext(_colDx) + 3 + 4 + Ix(_face) + 3;     // LDD / BPL / LDA PFACE,U / BPL
        bool facingLeft = (t.A & 0x80) != 0;
        if (pteroOnRight == facingLeft)
            return false;                                        // facing away from it: no kill
        t.Cycles += 4;                                           // JMP OSTPYP
        return true;
    }

    /// <summary>OSTBO: the higher lance wins; equal lances bounce apart.</summary>
    private ushort? Lances(Tx t)
    {
        t.B = t.Read(t.U + _lantz);
        t.CC = Logic8(t.CC, t.B);
        t.A = (t.B & 0x80) != 0 ? (byte)0xFF : (byte)0;          // SEX
        t.CC = Logic16(t.CC, t.D);
        t.Push16(t.D);                                           // STD ,--S
        t.CC = Logic16(t.CC, t.D);
        t.B = t.Read(t.X + _lantz);
        t.CC = Logic8(t.CC, t.B);
        t.A = (t.B & 0x80) != 0 ? (byte)0xFF : (byte)0;
        t.CC = Logic16(t.CC, t.D);
        t.D = Sub16(ref t.CC, t.D, t.Read16(t.S));               // SUBD ,S++
        t.S += 2;
        t.D = Add16(ref t.CC, t.D, t.Read16(t.X + _posY));
        t.D = Sub16(ref t.CC, t.D, t.Read16(t.U + _posY));
        t.Cycles += 4 + Ix(_lantz) + 2 + 8 + 4 + Ix(_lantz) + 2 + 9 + 6 + Ix(_posY) + 6 + Ix(_posY) + 3;
        if (t.D == 0)
        {
            // Same height: thud, both bounce, U leaves the sweep.
            t.X = _sndPlayerThud;
            t.CC = Logic16(t.CC, t.X);
            t.Cycles += 3;
            _ground.CallSound(t);
            t.X = t.Read16(t.Var(_colObj));
            t.CC = Logic16(t.CC, t.X);
            t.Cycles += 5 + Ext(_colObj) + 3;                    // ... BRA OSTXTT
            return BounceXOnTop(t);
        }
        t.Cycles += 3;                                           // BMI OSTXT3
        if (Mi(t.CC)) return ULoses(t);
        t.Cycles += 4;                                           // JMP OSTPYP
        return XLoses(t);
    }

    /// <summary>OSTXTT .. OSTX12: X up, U down, U leaves the sweep (carry set).</summary>
    private ushort? BounceXOnTop(Tx t)
    {
        t.Cycles += 8;
        XOnTop(t);                                               // JSR OSTXTP
        t.Cycles += 5 + 3;                                       // RTS / BRA OSTX12
        return Carry(t);
    }

    private static ushort? Carry(Tx t)
    {
        t.CC |= C;                                               // ORCC #$01
        t.Cycles += 3;
        return null;
    }

    /// <summary>OSTXT3: U lost.</summary>
    private ushort? ULoses(Tx t)
    {
        t.X = t.Read16(t.Var(_colObj));
        t.CC = Logic16(t.CC, t.X);
        t.Cycles += 5 + Ext(_colObj) + 8;                        // LDX COLOBJ / JSR OSTWIN
        t.Push16(ULosesAfterWin);
        ushort next = Kill(t);
        return next == ULosesAfterWin ? ULosesAfterKill(t) : next; // whole kill ran inline
    }

    /// <summary>After OSTWIN in OSTXT3: a pterodactyl winner also knocks the bird away.</summary>
    public ushort? ULosesAfterKill(Tx t)
    {
        t.A = t.Read(t.X + _id);
        t.CC = Logic8(t.CC, t.A);
        Sub8(ref t.CC, t.A, _ptero);
        t.Cycles += 4 + Ix(_id) + 2 + 3;
        if (t.A == _ptero)
        {
            Swap(t);                                             // OSTXPT
            t.Cycles += 8;
            PterodactylHit(t);
            t.Cycles += 5;
            Swap(t);
            return Carry(t);
        }
        return BounceXOnTop(t);
    }

    /// <summary>OSTPYP: X lost.</summary>
    private ushort? XLoses(Tx t)
    {
        t.X = t.Read16(t.Var(_colObj));
        t.CC = Logic16(t.CC, t.X);
        t.Cycles += 5 + Ext(_colObj);
        Swap(t);
        t.Cycles += 8;                                           // JSR OSTWIN
        t.Push16(XLosesAfterWin);
        ushort next = Kill(t);
        return next == XLosesAfterWin ? XLosesAfterKill(t) : next;
    }

    /// <summary>After OSTWIN in OSTPYP.</summary>
    public ushort? XLosesAfterKill(Tx t)
    {
        Swap(t);
        t.A = t.Read(t.U + _id);
        t.CC = Logic8(t.CC, t.A);
        Sub8(ref t.CC, t.A, _ptero);
        t.Cycles += 4 + Ix(_id) + 2 + 3;
        if (t.A == _ptero)
        {
            t.Cycles += 8;                                       // OSTPX: JSR PTEBRD
            PterodactylHit(t);
            t.Cycles += 5 + 4;                                   // RTS / JMP HITEM2
            return Done(t);
        }
        t.Cycles += 7;                                           // OSTYTT: BSR OSTUTP
        UOnTop(t);
        t.Cycles += 5 + 4;
        return Done(t);
    }

    /// <summary>
    /// OSTWIN up to the death call: U (the loser) stops colliding, loses its rider and flies
    /// off. Then its decision block's death routine runs (original code for now).
    /// </summary>
    public ushort Kill(Tx t)
    {
        t.Push16(t.U);                                           // PSHS X,U
        t.Push16(t.X);
        t.A = (byte)(t.Read(t.U + _id) & 0x7F);
        t.CC = Logic8(t.CC, t.A);
        t.Write(t.U + _id, t.A);
        t.CC = Logic8(t.CC, t.A);
        t.D = 0;
        t.CC = Logic16(t.CC, 0);
        t.Write16(t.U + _rider, 0);
        t.CC = Logic16(t.CC, 0);
        t.Y = t.Read16(t.U + _decision);
        t.CC = Logic16(t.CC, t.Y);
        t.D = _autoff;
        t.CC = Logic16(t.CC, t.D);
        t.Write16(t.U + _joy, _autoff);
        t.CC = Logic16(t.CC, t.D);
        t.Cycles += 9 + 4 + Ix(_id) + 2 + 4 + Ix(_id) + 3 + 5 + Ix(_rider) + 6 + Ix(_decision)
                    + 3 + 5 + Ix(_joy) + 7 + Symbols.IndirectCost(_ddead);
        ushort death = t.Read16(t.Y + _ddead);
        t.Push16(OstwinAfterDeath);                              // JSR [DDEAD,Y]
        if (Deaths != null && Deaths.Run(t, death))
        {
            t.S += 2;                                            // its RTS
            t.Cycles += 5;
            return KillAfterDeath(t);
        }
        return death;
    }

    /// <summary>The native death routines (set after construction; they also use this class).</summary>
    public Deaths? Deaths { get; set; }

    /// <summary>OSTWIN after the death routine: the winner scores the loser's value.</summary>
    public ushort KillAfterDeath(Tx t)
    {
        t.U = t.Read16(t.S + 2);
        t.CC = Logic16(t.CC, t.U);
        t.Y = t.Read16(t.U + _decision);
        t.CC = Logic16(t.CC, t.Y);
        t.A = t.Read(t.Y + _dvalue);
        t.CC = Logic8(t.CC, t.A);
        t.X = t.Read16(t.S);
        t.CC = Logic16(t.CC, t.X);
        t.X = t.Read16(t.X + _decision);
        t.CC = Logic16(t.CC, t.X);
        t.Cycles += 6 + 6 + Ix(_decision) + 4 + Ix(_dvalue) + 5 + 5 + Ix(_decision);
        ushort scoreRoutine = t.Read16(t.Y + _dvalur);
        int jsr = 7 + Symbols.IndirectCost(_dvalur);
        bool? tens = _scoring.IsTens(scoreRoutine);
        if (tens != null)
        {
            var m = t.Mark();
            t.Cycles += jsr;
            if (_scoring.Add(t, tens.Value))
            {
                t.Cycles += 5;
                return KillAfterScore(t);
            }
            t.Rollback(m);                                       // extra mount: original scores
        }
        t.Cycles += jsr;
        t.Push16(OstwinAfterScore);
        return scoreRoutine;
    }

    /// <summary>PULS X,U,PC at the end of OSTWIN.</summary>
    public ushort KillAfterScore(Tx t)
    {
        t.X = t.Read16(t.S);
        t.U = t.Read16(t.S + 2);
        ushort pc = t.Read16(t.S + 4);
        t.S += 6;
        t.Cycles += 11;
        return pc;
    }

    /// <summary>OSTHT2: enemies touching just thud and bounce (pterodactyls knock buzzards).</summary>
    private ushort? EnemiesCollide(Tx t)
    {
        t.X = _sndEnemyThud;
        t.CC = Logic16(t.CC, t.X);
        t.Cycles += 3;
        _ground.CallSound(t);
        t.X = t.Read16(t.Var(_colObj));
        t.CC = Logic16(t.CC, t.X);
        t.A = t.Read(t.U + _id);
        t.CC = Logic8(t.CC, t.A);
        Sub8(ref t.CC, t.A, _ptero);
        t.Cycles += 5 + Ext(_colObj) + 4 + Ix(_id) + 2 + 3;
        bool uPtero = t.A == _ptero;
        t.A = t.Read(t.X + _id);
        t.CC = Logic8(t.CC, t.A);
        Sub8(ref t.CC, t.A, _ptero);
        t.Cycles += 4 + Ix(_id) + 2 + 3;
        bool xPtero = t.A == _ptero;
        if (uPtero == xPtero)
        {
            t.Cycles += 8;                                       // OSTH11: JSR OSTBMP
            Bump(t);
            t.Cycles += 5 + 4;
            return Done(t);
        }
        if (uPtero)
        {
            t.Cycles += 8;                                       // OSTPX: JSR PTEBRD
            PterodactylHit(t);
            t.Cycles += 5 + 4;
            return Done(t);
        }
        Swap(t);                                                 // OSTH13
        t.Cycles += 8;
        PterodactylHit(t);
        t.Cycles += 5;
        Swap(t);
        t.Cycles += 4;
        return Done(t);
    }

    // ------------------------------------------------------------------ eggs

    /// <summary>
    /// EGGSCR (entered by JSR, return address already pushed; this performs its PULS X,U,PC):
    /// a player (U) collects an egg (X). Successive eggs score 250, 500, 750 then 1000; an egg
    /// caught before it bounced scores 500 more. The egg's rider is out of the wave, its
    /// buzzard (if one was coming for it) flies off, and the egg process wakes to show the score.
    /// </summary>
    public ushort CollectEgg(Tx t)
    {
        ushort egg = t.X, player = t.U;
        t.Push16(player);                                        // PSHS X,U
        t.Push16(egg);
        t.X = _sndEgg;
        t.CC = Logic16(t.CC, t.X);
        t.Cycles += 9 + 3;
        _ground.CallSound(t);
        t.Y = t.Read16(t.U + _decision);
        t.CC = Logic16(t.CC, t.Y);
        t.A = t.Read(t.Y + _dconst);                             // the player's score colour
        t.CC = Logic8(t.CC, t.A);
        t.X = egg;
        t.CC = Logic16(t.CC, t.X);
        t.Write(egg + _ppvelX, t.A);
        t.CC = Logic8(t.CC, t.A);
        t.Y = t.Read16(t.Y + _deggs);                            // the player's egg counter
        t.CC = Logic16(t.CC, t.Y);
        t.B = t.Read(t.Y);
        t.CC = Logic8(t.CC, t.B);
        Sub8(ref t.CC, t.B, 4);
        t.Cycles += 6 + Ix(_decision) + 4 + Ix(_dconst) + 5 + 4 + Ix(_ppvelX) + 6 + Ix(_deggs) + 4 + 2 + 3;
        if (Cs(t.CC))
        {
            t.B = Inc(ref t.CC, t.B);                            // up to 4
            t.Cycles += 2;
        }
        t.Write(t.Y, t.B);
        t.CC = Logic8(t.CC, t.B);
        t.Write(egg + _prDir, t.B);
        t.CC = Logic8(t.CC, t.B);
        t.B = Asl8(ref t.CC, t.B);
        t.B = Add8(ref t.CC, t.B, t.Read(t.Y), withCarry: false); // x3: EGGVAL entries are 3 bytes
        t.Y = (ushort)(_eggValues - 3);
        t.CC = Logic16(t.CC, t.Y);
        t.Y = (ushort)(t.Y + (sbyte)t.B);
        t.CC = Set(t.CC, Z, t.Y == 0);
        t.A = t.Read(t.Y);
        t.CC = Logic8(t.CC, t.A);
        t.X = t.Read16(t.U + _decision);
        t.CC = Logic16(t.CC, t.X);
        t.Cycles += 4 + 4 + Ix(_prDir) + 2 + 4 + 4 + 5 + 4 + 5 + Ix(_decision);
        Score(t, t.Read16(t.Y + 1), jsrCycles: 11);              // JSR [1,Y]

        t.Y = t.Read16(t.S);                                     // the egg again
        t.CC = Logic16(t.CC, t.Y);
        t.Write(t.Y + _eggs, 0);
        t.CC = Clr(t.CC);
        t.A = t.Read(t.Y + _feet);                               // has the egg bounced yet?
        t.CC = Logic8(t.CC, t.A);
        t.Cycles += 6 + 6 + Ix(_eggs) + 4 + Ix(_feet) + 3;
        if (t.A == 0)
        {
            int flag = t.Y + _eggs;
            t.Write(flag, Com8(ref t.CC, t.Read(flag)));         // caught in the air
            t.A = 5;
            t.CC = Logic8(t.CC, 5);
            t.Cycles += 6 + Ix(_eggs) + 2;
            Score(t, _scoring.Hundreds, jsrCycles: 8);           // +500
        }
        int riders = t.Var(_riders);
        t.Write(riders, Dec(ref t.CC, t.Read(riders)));
        t.X = t.Read16(t.S);
        t.CC = Logic16(t.CC, t.X);
        t.Y = t.Read16(t.X + _dist);                             // a buzzard coming for this egg?
        t.CC = Logic16(t.CC, t.Y);
        t.Cycles += 6 + Ext(_riders) + 5 + 6 + Ix(_dist) + 3;
        if (t.Y != 0)
        {
            int enemies = t.Var(_enemies);
            t.Write(enemies, Dec(ref t.CC, t.Read(enemies)));
            t.D = _autoff;
            t.CC = Logic16(t.CC, t.D);
            t.Write16(t.Y + _joy, _autoff);                      // it flies off instead
            t.CC = Logic16(t.CC, t.D);
            t.Cycles += 6 + Ext(_enemies) + 3 + 5 + Ix(_joy);
        }
        // EGGWAK: the egg process shows the score next
        t.D = _eggHit;
        t.CC = Logic16(t.CC, t.D);
        t.Write16(t.X + _pc, _eggHit);
        t.CC = Logic16(t.CC, t.D);
        t.A = 4;
        t.CC = Logic8(t.CC, 4);
        t.Write(t.X + _nap, 4);
        t.CC = Logic8(t.CC, 4);
        t.A = (byte)(t.Read(t.X + _id) & 0x7F);                  // no longer collides
        t.CC = Logic8(t.CC, t.A);
        t.Write(t.X + _id, t.A);
        t.CC = Logic8(t.CC, t.A);
        t.Cycles += 3 + 5 + Ix(_pc) + 2 + 4 + Ix(_nap) + 4 + Ix(_id) + 2 + 4 + Ix(_id);
        return KillAfterScore(t);                                // PULS X,U,PC
    }

    /// <summary>A score call made inline; anything unusual (an extra mount) hands the whole call back.</summary>
    private void Score(Tx t, ushort routine, int jsrCycles)
    {
        bool? tens = _scoring.IsTens(routine);
        t.Cycles += jsrCycles;
        if (tens == null || !_scoring.Add(t, tens.Value))
            throw new HandBackException();
        t.Cycles += 5;
    }

    private static bool Gt(byte cc) => !Eq(cc) && !Lt(cc);

    // ------------------------------------------------------------------ bounces

    /// <summary>OSTXUP: bird X was on top: bump it up 2 and send it upward at half speed.</summary>
    public void BumpUp(Tx t)
    {
        t.A = 0xFE;
        t.CC = Logic8(t.CC, t.A);
        t.Write(t.X + _bumpY, t.A);
        t.CC = Logic8(t.CC, t.A);
        t.D = t.Read16(t.X + _velY);
        t.CC = Logic16(t.CC, t.D);
        t.Cycles += 2 + 4 + Ix(_bumpY) + 5 + Ix(_velY) + 3;      // ... BLE
        if (!Le(t.CC)) HalveNegated(t);
    }

    /// <summary>OSTXDN: bird X was underneath: bump it down 2 and send it downward at half speed.</summary>
    public void BumpDown(Tx t)
    {
        t.A = 2;
        t.CC = Logic8(t.CC, t.A);
        t.Write(t.X + _bumpY, t.A);
        t.CC = Logic8(t.CC, t.A);
        t.D = t.Read16(t.X + _velY);
        t.CC = Logic16(t.CC, t.D);
        t.Cycles += 2 + 4 + Ix(_bumpY) + 5 + Ix(_velY) + 3;      // ... BGE
        if (Lt(t.CC)) HalveNegated(t);
    }

    /// <summary>PVELY,X = -PVELY,X / 2 (COMA / NEGB / SBCA #-1 / ASRA / RORB / STD).</summary>
    private void HalveNegated(Tx t)
    {
        t.A = Com8(ref t.CC, t.A);
        t.B = Neg8(ref t.CC, t.B);
        t.A = Sbc8(ref t.CC, t.A, 0xFF);
        t.A = Asr8(ref t.CC, t.A);
        t.B = Ror8(ref t.CC, t.B);
        t.Write16(t.X + _velY, t.D);
        t.CC = Logic16(t.CC, t.D);
        t.Cycles += 5 * 2 + 5 + Ix(_velY);
    }

    /// <summary>
    /// OSTLR: push the two birds apart sideways using COLDX (which side X is on): each one's
    /// speed points away from the other (slowed by 2), each gets a sideways bump from the
    /// other's speed, and they turn to face away from each other.
    /// </summary>
    public void BounceApart(Tx t)
    {
        t.X = t.Read16(t.Var(_colObj));
        t.CC = Logic16(t.CC, t.X);
        t.D = t.Read16(t.Var(_colDx));
        t.CC = Logic16(t.CC, t.D);
        t.Cycles += 5 + Ext(_colObj) + 5 + Ext(_colDx) + 3;      // ... BEQ
        if (t.D == 0) return;                                    // dead centre: no sideways bounce
        t.Cycles += 3;                                           // BPL OSTURT
        if (!Mi(t.CC))
        {
            // OSTURT: X on the right goes right, U goes left
            AwayRight(t, mover: t.X, bumped: t.U);
            t.Write(t.X + _face, 0);
            t.CC = Clr(t.CC);
            t.Cycles += 6 + Ix(_face);
            AwayLeft(t, mover: t.U, bumped: t.X);
            t.A = 0xFF;
            t.CC = Logic8(t.CC, t.A);
            t.Write(t.U + _face, t.A);
            t.CC = Logic8(t.CC, t.A);
            t.Cycles += 2 + 4 + Ix(_face);
            return;
        }
        // OSTXLF: U on the right; X goes left, U goes right
        AwayLeft(t, mover: t.X, bumped: t.U);
        t.A = 0xFF;
        t.CC = Logic8(t.CC, t.A);
        t.Write(t.X + _face, t.A);
        t.CC = Logic8(t.CC, t.A);
        t.Cycles += 2 + 4 + Ix(_face);
        AwayRight(t, mover: t.U, bumped: t.X);
        t.Write(t.U + _face, 0);
        t.CC = Clr(t.CC);
        t.Cycles += 6 + Ix(_face) + 3;                           // CLR PFACE,U / BRA OSTNLR
    }

    /// <summary>Mover heads left (speed &lt;= 0, slowed by 2); the other bird is bumped by (2-speed)/2.</summary>
    private void AwayLeft(Tx t, ushort mover, ushort bumped)
    {
        t.A = t.Read(mover + _velX);
        t.CC = Logic8(t.CC, t.A);
        t.Cycles += 4 + Ix(_velX) + 3;                           // LDA / BLE
        if (!Le(t.CC))
        {
            t.A = Neg8(ref t.CC, t.A);
            t.A = Add8(ref t.CC, t.A, 2, withCarry: false);
            t.Write(mover + _velX, t.A);
            t.CC = Logic8(t.CC, t.A);
            t.Cycles += 2 + 2 + 4 + Ix(_velX);
        }
        t.A = Neg8(ref t.CC, t.A);
        t.A = Add8(ref t.CC, t.A, 2, withCarry: false);
        t.A = Asr8(ref t.CC, t.A);
        t.Write(bumped + _bumpX, t.A);
        t.CC = Logic8(t.CC, t.A);
        t.Cycles += 2 + 2 + 2 + 4 + Ix(_bumpX);
    }

    /// <summary>Mover heads right (speed &gt;= 0, slowed by 2); the other bird is bumped by -(speed+2)/2.</summary>
    private void AwayRight(Tx t, ushort mover, ushort bumped)
    {
        t.A = t.Read(mover + _velX);
        t.CC = Logic8(t.CC, t.A);
        t.Cycles += 4 + Ix(_velX) + 3;                           // LDA / BGE
        if (Lt(t.CC))
        {
            t.A = Neg8(ref t.CC, t.A);
            t.A = Sub8(ref t.CC, t.A, 2);
            t.Write(mover + _velX, t.A);
            t.CC = Logic8(t.CC, t.A);
            t.Cycles += 2 + 2 + 4 + Ix(_velX);
        }
        t.A = Add8(ref t.CC, t.A, 2, withCarry: false);
        t.A = Neg8(ref t.CC, t.A);
        t.A = Asr8(ref t.CC, t.A);
        t.Write(bumped + _bumpX, t.A);
        t.CC = Logic8(t.CC, t.A);
        t.Cycles += 2 + 2 + 2 + 4 + Ix(_bumpX);
    }

    private static void Swap(Tx t)
    {
        (t.X, t.U) = (t.U, t.X);                                 // EXG X,U
        t.Cycles += 8;
    }

    /// <summary>OSTUTP: U goes up, X goes down, then bounce apart sideways.</summary>
    public void UOnTop(Tx t)
    {
        t.X = t.Read16(t.Var(_colObj));
        t.CC = Logic16(t.CC, t.X);
        t.Cycles += 5 + Ext(_colObj);
        Swap(t);
        t.Cycles += 8; BumpUp(t); t.Cycles += 5;                 // JSR OSTXUP
        Swap(t);
        t.Cycles += 8; BumpDown(t); t.Cycles += 5;               // JSR OSTXDN
        t.Cycles += 3;                                           // BRA OSTLR
        BounceApart(t);
    }

    /// <summary>OSTXTP: X goes up, U goes down, then bounce apart sideways.</summary>
    public void XOnTop(Tx t)
    {
        t.X = t.Read16(t.Var(_colObj));
        t.CC = Logic16(t.CC, t.X);
        t.Cycles += 5 + Ext(_colObj);
        Swap(t);
        t.Cycles += 8; BumpDown(t); t.Cycles += 5;
        Swap(t);
        t.Cycles += 8; BumpUp(t); t.Cycles += 5;
        BounceApart(t);
    }

    /// <summary>OSTBMP: a collision where nobody dies: whoever is higher goes up.</summary>
    public void Bump(Tx t)
    {
        t.B = t.Read(t.X + _posY + 1);
        t.CC = Logic8(t.CC, t.B);
        t.B = Sub8(ref t.CC, t.B, t.Read(t.U + _posY + 1));
        t.Cycles += 4 + Ix(_posY + 1) + 4 + Ix(_posY + 1) + 5;   // ... LBPL
        if (!Mi(t.CC)) { t.Cycles += 1; UOnTop(t); return; }
        t.Cycles += 5;                                           // LBEQ (never taken here)
        if (Eq(t.CC)) { t.Cycles += 1; BounceApart(t); return; }
        t.Cycles += 4;                                           // JMP OSTXTP
        XOnTop(t);
    }

    /// <summary>
    /// PTEBRD: pterodactyl (U) against bird (X). The bird is knocked hard up (-5) or down (+5)
    /// depending on whose box centre is higher, and sideways away from the pterodactyl,
    /// facing away from it.
    /// </summary>
    public void PterodactylHit(Tx t)
    {
        t.A = 0;
        t.CC = Clr(t.CC);
        t.B = t.Read(t.X + _colY1);
        t.CC = Logic8(t.CC, t.B);
        t.B = Sub8(ref t.CC, t.B, t.Read(t.U + _colY1));
        t.A = Sbc8(ref t.CC, t.A, 0);
        t.B = Add8(ref t.CC, t.B, t.Read(t.X + _colY2), withCarry: false);
        t.A = Add8(ref t.CC, t.A, 0, withCarry: true);
        t.B = Sub8(ref t.CC, t.B, t.Read(t.U + _colY2));
        t.A = Sbc8(ref t.CC, t.A, 0);                            // A:B = sum of box edges, bird - ptero
        t.Cycles += 2 + 4 + Ix(_colY1) + 4 + Ix(_colY1) + 2 + 4 + Ix(_colY2) + 2 + 4 + Ix(_colY2) + 2 + 3;
        if (Mi(t.CC))
        {
            t.Cycles += 7; BumpUp(t); t.Cycles += 5;             // BSR OSTXUP
            HardBump(t, 0xFB);
            t.Cycles += 3;                                       // BRA .2S
        }
        else
        {
            t.Cycles += 7; BumpDown(t); t.Cycles += 5;
            HardBump(t, 5);
        }

        t.D = t.Read16(t.X + _posX);
        t.CC = Logic16(t.CC, t.D);
        t.D = Sub16(ref t.CC, t.D, t.Read16(t.U + _posX));
        t.D = Add16(ref t.CC, t.D, t.Read16(t.X + _colX));
        t.D = Sub16(ref t.CC, t.D, t.Read16(t.U + _colX));
        t.Cycles += 5 + Ix(_posX) + 6 + Ix(_posX) + 6 + Ix(_colX) + 6 + Ix(_colX) + 3; // ... BPL
        bool birdOnRight = !Mi(t.CC);

        t.A = t.Read(t.X + _velX);
        t.CC = Logic8(t.CC, t.A);
        t.Cycles += 4 + Ix(_velX) + 3;
        if (birdOnRight ? Lt(t.CC) : !Le(t.CC))
        {
            if (birdOnRight)
            {
                t.A = Neg8(ref t.CC, t.A);
                t.A = Sub8(ref t.CC, t.A, 2);
            }
            else
            {
                t.A = Sub8(ref t.CC, t.A, 2);
                t.A = Neg8(ref t.CC, t.A);
            }
            t.Write(t.X + _velX, t.A);
            t.CC = Logic8(t.CC, t.A);
            t.Cycles += 2 + 2 + 4 + Ix(_velX);
        }
        t.A = t.Read(t.U + _velX);
        t.CC = Logic8(t.CC, t.A);
        t.A = Neg8(ref t.CC, t.A);
        t.A = Asl8(ref t.CC, t.A);                               // knocked by twice the ptero's speed
        t.Write(t.X + _bumpX, t.A);
        t.CC = Logic8(t.CC, t.A);
        t.Cycles += 4 + Ix(_velX) + 2 + 2 + 4 + Ix(_bumpX);
        if (birdOnRight)
        {
            t.Write(t.X + _face, 0);
            t.CC = Clr(t.CC);
            t.Cycles += 6 + Ix(_face);
        }
        else
        {
            t.A = 0xFF;
            t.CC = Logic8(t.CC, t.A);
            t.Write(t.X + _face, 0xFF);
            t.CC = Logic8(t.CC, t.A);
            t.Cycles += 2 + 4 + Ix(_face);
        }
    }

    private void HardBump(Tx t, byte bump)
    {
        t.A = bump;
        t.CC = Logic8(t.CC, t.A);
        t.Write(t.X + _bumpY, t.A);
        t.CC = Logic8(t.CC, t.A);
        t.Cycles += 2 + 4 + Ix(_bumpY);
    }
}
