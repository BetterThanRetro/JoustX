using Joust.Emulation;
using static Joust.Emulation.Alu6809;

namespace Joust.Port.Routines;

/// <summary>
/// Joust's flight physics and background collision (JOUSTRV4.ASM), operating on a process
/// block addressed by U.
///
/// Units: X position is a 16-bit pixel column with an 8-bit fraction in PVELX+2; Y is the
/// pixel row PPOSY+1 with an 8-bit fraction in PPOSY+2. PVELY is signed 8.8 pixels per tick
/// (negative = up). PVELX is an index (-8..8, step 2) into the FLYX speed table.
///
/// Each method is the body of one original routine, excluding its final RTS, and charges the
/// original's cycles to the transaction. A method returning false means "hand this call to the
/// original" and must be called on a transaction that is then discarded.
/// </summary>
public sealed class Physics
{
    // Process block offsets
    private readonly int _posX, _posY, _velY, _velX, _bumpX, _bumpY, _picH, _colY2, _timeUp, _id;

    // Variables and tables
    private readonly ushort _gravity, _colDx, _bckRefresh, _curJoy, _troll, _lavaTrolls, _thud;
    private readonly ushort _landX, _landY, _boxX, _boxY;
    private readonly ushort _clif1L, _clif1R, _clif2, _clif3L, _clif3R, _clif4, _clif5;
    private readonly short _eRight, _eLeft;
    private readonly byte _ceiling, _floor, _maxVx, _playerAlive, _enemyAlive;
    private readonly SoundQueue _sound;

    public SoundQueue Sound => _sound;

    public Physics(Symbols s)
    {
        _posX = s["PPOSX"]; _posY = s["PPOSY"]; _velY = s["PVELY"]; _velX = s["PVELX"];
        _bumpX = s["PBUMPX"]; _bumpY = s["PBUMPY"]; _picH = s["PPICH"]; _colY2 = s["PCOLY2"];
        _timeUp = s["PTIMUP"]; _id = s["PID"];
        _gravity = s["GRAV"]; _colDx = s["COLDX"]; _bckRefresh = s["BCKRFS"]; _curJoy = s["CURJOY"];
        _troll = s["TTROLL"]; _lavaTrolls = s["LAVNBR"]; _thud = s["SNCTHD"];
        _landX = s["LNDXTB"]; _landY = s["LNDYTB"]; _boxX = s["BCKXTB"]; _boxY = s["BCKYTB"];
        _clif1L = s["CLIF1L"]; _clif1R = s["CLIF1R"]; _clif2 = s["CLIF2"]; _clif3L = s["CLIF3L"];
        _clif3R = s["CLIF3R"]; _clif4 = s["CLIF4"]; _clif5 = s["CLIF5"];
        _eRight = (short)s.Value("ERIGHT"); _eLeft = (short)s.Value("ELEFT");
        _ceiling = (byte)s.Value("CEILNG"); _floor = (byte)s.Value("FLOOR"); _maxVx = (byte)s.Value("MAXVX");
        _playerAlive = (byte)(0x80 + s.Value("PLYID")); _enemyAlive = (byte)(0x80 + s.Value("EMYID"));
        _sound = new SoundQueue(s);
    }

    private static int Ix(int offset) => Symbols.IndexCost(offset);
    private static int Ext(ushort address) => Symbols.AddressCost(address);

    // ------------------------------------------------------------------ WRAPX

    /// <summary>
    /// WRAPX: move by B pixels plus any pending bump (applied at most 3 pixels per tick), then
    /// wrap the X position around the -10..292 playfield.
    /// </summary>
    public void WrapX(Tx t)
    {
        int bumpAddr = t.U + _bumpX;
        byte bump = t.Read(bumpAddr);
        t.A = bump;
        t.CC = Logic8(t.CC, bump);
        t.Cycles += 4 + Ix(_bumpX) + 3;                          // LDA PBUMPX,U / BEQ
        if (bump != 0)
        {
            t.Cycles += 3;                                       // BLT
            byte step = Lt(t.CC) ? (byte)0xFD : (byte)3;         // -3 or +3 pixels this tick
            Sub8(ref t.CC, bump, step);
            t.Cycles += 2 + 3;                                   // CMPA #+-3 / BLE or BGE
            bool takeRest = step == 3 ? Le(t.CC) : !Lt(t.CC);    // |bump| <= 3: use it all
            if (takeRest)
            {
                // WRPBRM: what's left of the bump fits in this tick
                t.B = Add8(ref t.CC, t.B, t.Read(bumpAddr), withCarry: false);
                t.Write(bumpAddr, 0);
                t.CC = Clr(t.CC);
                t.Cycles += 4 + Ix(_bumpX) + 6 + Ix(_bumpX);
            }
            else
            {
                t.B = Add8(ref t.CC, t.B, step, withCarry: false);
                t.A = Sub8(ref t.CC, t.A, step);
                t.Write(bumpAddr, t.A);
                t.CC = Logic8(t.CC, t.A);
                t.Cycles += 2 + 2 + 4 + Ix(_bumpX) + 3;
            }
        }

        // WRPWRD
        t.A = (t.B & 0x80) != 0 ? (byte)0xFF : (byte)0;          // SEX
        t.CC = Logic16(t.CC, t.D);
        t.D = Add16(ref t.CC, t.D, t.Read16(t.U + _posX));
        t.Cycles += 2 + 6 + Ix(_posX);
        Sub16(ref t.CC, t.D, (ushort)_eRight);
        t.Cycles += 5 + 3;                                       // CMPD #ERIGHT / BLE
        if (!Le(t.CC))
        {
            t.D = Add16(ref t.CC, t.D, (ushort)-(_eRight - _eLeft + 1));
            t.Cycles += 4;
        }
        Sub16(ref t.CC, t.D, (ushort)_eLeft);
        t.Cycles += 5 + 3;                                       // CMPD #ELEFT / BGE
        if (Lt(t.CC))
        {
            t.D = Add16(ref t.CC, t.D, (ushort)(_eRight - _eLeft + 1));
            t.Cycles += 4;
        }
        t.Write16(t.U + _posX, t.D);
        t.CC = Logic16(t.CC, t.D);
        t.Cycles += 5 + Ix(_posX);
    }

    // ------------------------------------------------------------------ ADDGRA / ADDGRX

    /// <summary>ADDGRA: add gravity (+B, the wings-up offset) to the Y velocity, then ADDGRX.</summary>
    public bool AddGravity(Tx t)
    {
        t.B = Add8(ref t.CC, t.B, t.Read(t.Var(_gravity)), withCarry: false);
        t.A = (t.B & 0x80) != 0 ? (byte)0xFF : (byte)0;          // SEX
        t.CC = Logic16(t.CC, t.D);
        t.D = Add16(ref t.CC, t.D, t.Read16(t.U + _velY));
        t.Write16(t.U + _velY, t.D);
        t.CC = Logic16(t.CC, t.D);
        t.Cycles += 4 + Ext(_gravity) + 2 + 6 + Ix(_velY) + 5 + Ix(_velY);
        return Integrate(t);
    }

    /// <summary>
    /// ADDGRX: move by velocity D (Y) and the FLYX table entry for PVELX (X). Bounces off the
    /// ceiling; reaching the lava is handed back (the original sleeps through the sinking).
    /// </summary>
    public bool Integrate(Tx t)
    {
        t.D = Add16(ref t.CC, t.D, t.Read16(t.U + _posY + 1));
        t.A = Add8(ref t.CC, t.A, t.Read(t.U + _bumpY), withCarry: false);
        t.Write(t.U + _bumpY, 0);
        t.CC = Clr(t.CC);
        t.Cycles += 6 + Ix(_posY + 1) + 4 + Ix(_bumpY) + 6 + Ix(_bumpY);

        Sub8(ref t.CC, t.A, _ceiling);
        t.Cycles += 2 + 3;                                       // CMPA #CEILNG / BHI
        if (!Hi(t.CC))
        {
            t.Write(t.U + _bumpY, Inc(ref t.CC, t.Read(t.U + _bumpY)));
            t.D = t.Read16(t.U + _velY);
            t.CC = Logic16(t.CC, t.D);
            t.Cycles += 6 + Ix(_bumpY) + 5 + Ix(_velY) + 3;      // INC / LDD / BPL
            if (Mi(t.CC))
            {
                // Rising into the ceiling: reverse the vertical velocity.
                t.A = Com8(ref t.CC, t.A);
                t.B = Neg8(ref t.CC, t.B);
                t.A = Sbc8(ref t.CC, t.A, 0xFF);
                t.Write16(t.U + _velY, t.D);
                t.CC = Logic16(t.CC, t.D);
                t.Cycles += 2 + 2 + 2 + 5 + Ix(_velY);
            }
            t.D = (ushort)(_ceiling << 8);
            t.CC = Logic16(t.CC, t.D);
            t.Cycles += 3 + 3;                                   // LDD #CEILNG*256 / BRA
        }

        Sub8(ref t.CC, t.A, (byte)(_floor + 7));
        t.Cycles += 2 + 3;                                       // CMPA #FLOOR+7 / BHS
        if (!Cs(t.CC))
            return false;                                        // into the lava: original handles it

        t.Write16(t.U + _posY + 1, t.D);
        t.CC = Logic16(t.CC, t.D);
        byte speedIndex = t.Read(t.U + _velX);
        t.A = speedIndex;
        t.CC = Logic8(t.CC, speedIndex);
        t.D = t.Read16(t.X + (sbyte)speedIndex);                 // FLYX table: 8.8 pixels per tick
        t.CC = Logic16(t.CC, t.D);
        t.B = Add8(ref t.CC, t.B, t.Read(t.U + _velX + 2), withCarry: false);
        t.Write(t.U + _velX + 2, t.B);
        t.CC = Logic8(t.CC, t.B);
        t.A = Add8(ref t.CC, t.A, 0, withCarry: true);
        t.B = t.A;                                               // TFR A,B: whole pixels to move
        t.Cycles += 5 + Ix(_posY + 1) + 4 + Ix(_velX) + 5 + 1 + 4 + Ix(_velX + 2)
                    + 4 + Ix(_velX + 2) + 2 + 6 + 4;             // ... ADCA #0 / TFR / JMP WRAPX
        WrapX(t);
        return true;
    }

    // ------------------------------------------------------------------ ADDFLP

    /// <summary>
    /// ADDFLP: a flap. The longer the wings were up (PTIMUP, 0..255 ticks) the less lift:
    /// lift = 96*t/256 - 96 added to PVELY. The joystick nudges PVELX by 2, capped at +-MAXVX.
    /// </summary>
    public void Flap(Tx t)
    {
        t.B = t.Read(t.U + _timeUp);
        t.CC = Logic8(t.CC, t.B);
        t.A = 256 * 96 / 255;
        t.CC = Logic8(t.CC, t.A);
        t.D = (ushort)(t.A * t.B);                               // MUL
        t.CC = Set(Set(t.CC, Z, t.D == 0), C, (t.B & 0x80) != 0);
        t.B = t.A;
        t.A = 0;
        t.CC = Clr(t.CC);
        t.D = Sub16(ref t.CC, t.D, 96);
        t.D = Add16(ref t.CC, t.D, t.Read16(t.U + _velY));
        t.Write16(t.U + _velY, t.D);
        t.CC = Logic16(t.CC, t.D);
        t.A = t.Read(t.Var(_curJoy));
        t.CC = Logic8(t.CC, t.A);
        t.A = Asl8(ref t.CC, t.A);
        t.A = Add8(ref t.CC, t.A, t.Read(t.U + _velX), withCarry: false);
        t.Cycles += 4 + Ix(_timeUp) + 2 + 11 + 6 + 2 + 4 + 6 + Ix(_velY) + 5 + Ix(_velY)
                    + 4 + Ext(_curJoy) + 2 + 4 + Ix(_velX) + 3;  // ... ADDA PVELX,U / BLT

        bool store;
        if (Lt(t.CC))
        {
            Sub8(ref t.CC, t.A, (byte)-_maxVx);
            store = !Lt(t.CC);
        }
        else
        {
            Sub8(ref t.CC, t.A, _maxVx);
            store = !(!Eq(t.CC) && !Lt(t.CC));                   // BGT not taken
        }
        t.Cycles += 2 + 3;
        if (store)
        {
            t.Write(t.U + _velX, t.A);
            t.CC = Logic8(t.CC, t.A);
            t.Cycles += 4 + Ix(_velX);
        }
    }

    // ------------------------------------------------------------------ CKGND

    /// <summary>
    /// CKGND: has the object landed? The landing tables (per column x per row bits, one bit
    /// per cliff) say whether its feet are at a cliff top; if so it snaps to that height and
    /// returns EQ. Otherwise the box tables decide whether a pixel collision check is needed.
    /// </summary>
    public bool CheckGround(Tx t)
    {
        t.X = t.Read16(t.U + _posX);
        t.CC = Logic16(t.CC, t.X);
        t.Y = t.Read16(t.U + _posY);
        t.CC = Logic16(t.CC, t.Y);
        t.A = (byte)(t.Read(t.X + _landX) & t.Read(t.Y + _landY));
        t.CC = Logic8(t.CC, t.A);
        t.Cycles += 5 + Ix(_posX) + 6 + Ix(_posY) + 4 + Ix(_landX) + 4 + Ix(_landY) + 3;
        if (t.A != 0)
            return Land(t);

        t.A = (byte)(t.Read(t.X + _boxX) & t.Read(t.Y + _boxY));
        t.CC = Logic8(t.CC, t.A);
        t.Cycles += 4 + Ix(_boxX) + 4 + Ix(_boxY) + 3;
        if (t.A != 0)
        {
            t.Cycles += 8;                                       // JSR BCKCOL
            BackgroundCollision(t);
            t.Cycles += 5;                                       // its RTS
        }
        t.A = 1;                                                 // NE: still in the air
        t.CC = Logic8(t.CC, 1);
        t.Cycles += 2;
        return true;
    }

    /// <summary>
    /// LND18: snap onto the top of the cliff whose landing bit is set (A = landing bits).
    /// False (hand back) where the original would spawn a lava troll.
    /// </summary>
    public bool Land(Tx t)
    {
        byte bits = t.A;
        byte top;
        Sub8(ref t.CC, bits, 0x08);
        t.Cycles += 2 + 3;                                       // CMPA #$08 / BLO
        if (Cs(t.CC))
        {
            Sub8(ref t.CC, bits, 0x02);
            t.Cycles += 2 + 3 + 3;                               // CMPA #$02 / BEQ / BHI
            top = Eq(t.CC) ? (byte)(0x51 - 1)                    // CLIF2
                : Hi(t.CC) ? (byte)(0x81 - 1)                    // CLIF3R
                : (byte)(0x45 - 1);                              // CLIF1L & CLIF1R
            if (Eq(t.CC)) t.Cycles -= 3;                         // BHI not reached
        }
        else
        {
            t.Cycles += 3;                                       // BEQ LNDB3
            if (Eq(t.CC))
            {
                top = 0x8A - 1;                                  // CLIF3L & CLIF3R
            }
            else
            {
                Sub8(ref t.CC, bits, 0x20);
                t.Cycles += 2 + 3;                               // CMPA #$20 / BLO
                if (Cs(t.CC))
                {
                    top = 0xA3 - 1;                              // CLIF4
                }
                else
                {
                    t.CC = Logic8(t.CC, (byte)(bits & 0x20));    // BITA #$20
                    t.Cycles += 2 + 3;                           // BNE
                    if (!Eq(t.CC))
                        top = 0xD3 - 1;                          // CLIF5 (the bottom ledge)
                    else
                        return LavaTrollZone(t);
                }
            }
        }
        t.B = top;
        t.Write(t.U + _posY + 1, top);
        t.A = 0;
        t.CC = Clr(t.CC);                                        // EQ: landed
        t.Cycles += 2 + 4 + Ix(_posY + 1) + 2;
        return true;
    }

    /// <summary>LNDB7 from LDA PID,U: starts a lava troll under the bird (see <see cref="LavaTroll.Spawn"/>).</summary>
    public Action<Tx>? SpawnTroll { get; set; }

    /// <summary>LNDB7: over the lava. If a troll can grab this bird, it is spawned.</summary>
    private bool LavaTrollZone(Tx t)
    {
        t.Cycles += 3;                                           // BRA LNDB7
        t.A = t.Read(t.Var(_troll));
        t.CC = Logic8(t.CC, t.A);
        t.Cycles += 4 + Ext(_troll) + 3;
        if (t.A == 0)
        {
            t.A = t.Read(t.Var(_lavaTrolls));
            t.CC = Logic8(t.CC, t.A);
            t.Cycles += 4 + Ext(_lavaTrolls) + 3;
            if (t.A == 0)
            {
                byte id = t.Read(t.U + _id);
                if (id == _playerAlive || id == _enemyAlive)
                {
                    if (SpawnTroll == null) return false;        // spawn a lava troll: original
                    SpawnTroll(t);
                    return true;
                }
                t.A = id;
                Sub8(ref t.CC, id, _playerAlive);
                Sub8(ref t.CC, id, _enemyAlive);
                t.Cycles += 4 + Ix(_id) + 2 + 3 + 2 + 3;
            }
        }
        t.B = t.Read(t.U + _posY + 1);                           // LNDB7C: NE, don't land
        t.CC = Logic8(t.CC, t.B);
        t.Cycles += 4 + Ix(_posY + 1);
        return true;
    }

    // ------------------------------------------------------------------ BCKCOL

    private enum Side { Top, Left, Right, Bottom }

    /// <summary>
    /// BCKCOL: A holds the box-collision bit of the cliff being touched. Do a pixel check
    /// against that cliff and, on contact, bounce: off the top (velocity up, halved), the
    /// bottom (velocity down, halved) or a side (speed reversed and reduced, bumped 6 pixels
    /// away), then play the thud. Body excluding the final RTS (which may be SND's).
    /// </summary>
    public void BackgroundCollision(Tx t)
    {
        byte bit = t.A;
        t.B = bit;
        t.A = (byte)(bit | t.Read(t.Var(_bckRefresh)));          // ask for the cliff to be redrawn
        t.CC = Logic8(t.CC, t.A);
        t.Write(t.Var(_bckRefresh), t.A);
        t.CC = Logic8(t.CC, t.A);
        t.CC = Logic8(t.CC, t.B);                                // TSTB
        t.Cycles += 6 + 4 + Ext(_bckRefresh) + 4 + Ext(_bckRefresh) + 2 + 5;  // ... LBMI

        if (Mi(t.CC)) { t.Cycles += 1; CliffCenter(t, _clif5, 54, 211); return; }
        Sub8(ref t.CC, t.B, 0x08);
        t.Cycles += 2 + 3;                                       // CMPB #$08 / BLO
        if (Cs(t.CC))
        {
            Sub8(ref t.CC, t.B, 0x02);
            t.Cycles += 2 + 3;                                   // CMPB #$02 / BEQ
            if (Eq(t.CC)) { CliffRight(t, _clif1R, 252, 69); return; }
            t.Cycles += 5;                                       // LBHI
            if (Hi(t.CC)) { t.Cycles += 1; CliffCenter(t, _clif2, 86, 81); return; }
            CliffLeft(t, _clif1L, unchecked((ushort)-32), 69, branchToSetup: true);
            return;
        }
        t.Cycles += 5;                                           // LBEQ BCKB3
        if (Eq(t.CC)) { t.Cycles += 1; CliffRight(t, _clif3R, 202, 129, branchToSetup: false); return; }
        t.B = (byte)(t.B & 0x70);
        t.CC = Logic8(t.CC, t.B);
        Sub8(ref t.CC, t.B, 0x20);
        t.Cycles += 2 + 2 + 5;                                   // ANDB / CMPB #$20 / LBHI
        if (Hi(t.CC)) { t.Cycles += 1; CliffCenter(t, _clif4, 106, 163); return; }
        t.Cycles += 3;                                           // BLO BCKB4
        if (Cs(t.CC)) { CliffLeft(t, _clif3L, unchecked((ushort)-32), 138, branchToSetup: false); return; }
        t.Cycles += 4;                                           // JMP BCKB5
        CliffUpperRight(t);
    }

    /// <summary>LDY [cliff] / LDD #left / LDX #top: the cliff's collision data and placement.</summary>
    private void SetupCliff(Tx t, ushort cliff, ushort left, ushort top)
    {
        t.Y = t.Read16(t.Read16(cliff));
        t.CC = Logic16(t.CC, t.Y);
        t.D = left;
        t.CC = Logic16(t.CC, t.D);
        t.X = top;
        t.CC = Logic16(t.CC, t.X);
        t.Cycles += 11 + 3 + 3;
    }

    /// <summary>PSHS Y / JSR BCKPIX / BCC no-hit / CMPY ,S++. Returns null if no contact.</summary>
    private bool? PixelHit(Tx t)
    {
        ushort startY = t.Y;
        t.Cycles += 7 + 8;                                       // PSHS Y / JSR BCKPIX
        PixelCollision(t);
        t.Cycles += 5 + 3;                                       // RTS / BCC
        if (!Cs(t.CC))
        {
            t.Cycles += 5;                                       // LEAS 2,S (then RTS)
            return null;
        }
        Sub16(ref t.CC, t.Y, startY);
        t.Cycles += 10 + 3;                                      // CMPY ,S++ / BNE
        return Eq(t.CC);                                         // true: contact on the cliff's first line
    }

    /// <summary>LDA 4,X / 8,X / 12,X / 16,X: does the contact involve the bird's lower lines?</summary>
    private bool HitsLowerLines(Tx t)
    {
        for (int line = 1; line <= 4; line++)
        {
            t.A = t.Read(t.X + 4 * line);
            t.CC = Logic8(t.CC, t.A);
            t.Cycles += 5 + 3;
            if (Mi(t.CC)) return true;
        }
        return false;
    }

    /// <summary>
    /// Which side of the touched line the object is on: average of the cliff's line ends
    /// minus the object's, corrected by twice the X offset between them.
    /// </summary>
    private void SideOfLine(Tx t)
    {
        t.D = t.Read16(t.Y);
        t.CC = Logic16(t.CC, t.D);
        t.D = Add16(ref t.CC, t.D, t.Read16(t.Y + 2));
        t.D = Sub16(ref t.CC, t.D, t.Read16(t.X));
        t.D = Sub16(ref t.CC, t.D, t.Read16(t.X + 2));
        ushort colDx = t.Read16(t.Var(_colDx));
        t.D = Add16(ref t.CC, t.D, colDx);
        t.D = Add16(ref t.CC, t.D, colDx);
        t.Cycles += 5 + 7 + 6 + 7 + 2 * (6 + Ext(_colDx));
    }

    /// <summary>BCKB0/BCKB4 (left-edge cliffs CLIF1L, CLIF3L).</summary>
    private void CliffLeft(Tx t, ushort cliff, ushort left, ushort top, bool branchToSetup)
    {
        SetupCliff(t, cliff, left, top);
        if (branchToSetup) t.Cycles += 3;                        // BRA BCKB04
        bool? firstLine = PixelHit(t);
        if (firstLine == null) return;
        if (firstLine == true)
        {
            if (HitsLowerLines(t)) { t.Cycles += 4; Bounce(t, Side.Top); return; } // JMP BCTOP
        }
        // BCCOML
        if (BottomOfCliff(t)) { t.Cycles += 4; Bounce(t, Side.Bottom); return; } // JMP BCBOT
        SideOfLine(t);
        t.Cycles += 5;                                           // LBMI BCRIT
        if (Mi(t.CC)) { t.Cycles += 1; Bounce(t, Side.Right); return; }
        t.Cycles += 4;                                           // JMP BCBOT
        Bounce(t, Side.Bottom);
    }

    /// <summary>BCKB1/BCKB3 (right-edge cliffs CLIF1R, CLIF3U).</summary>
    private void CliffRight(Tx t, ushort cliff, ushort left, ushort top, bool branchToSetup = true)
    {
        SetupCliff(t, cliff, left, top);
        if (branchToSetup) t.Cycles += 3;                        // BRA BCKB15
        bool? firstLine = PixelHit(t);
        if (firstLine == null) return;
        if (firstLine == true)
        {
            RightTopCheck(t);
            return;
        }
        RightSides(t);
    }

    /// <summary>BCKB5 (CLIF3R, entered without the first-line test).</summary>
    private void CliffUpperRight(Tx t)
    {
        SetupCliff(t, _clif3R, 202, 129);
        t.Cycles += 8;                                           // JSR BCKPIX
        PixelCollision(t);
        t.Cycles += 5 + 3;                                       // RTS / BCC NOBCK2
        if (!Cs(t.CC)) return;
        t.B = t.Read(t.U + _posY + 1);
        t.CC = Logic8(t.CC, t.B);
        Sub8(ref t.CC, t.B, 0x89 + 4 + 6);
        t.Cycles += 4 + Ix(_posY + 1) + 2 + 3;                   // LDB / CMPB / BHI
        if (Hi(t.CC)) { RightSides(t); return; }
        t.Cycles += 3;                                           // BRA BCK15C
        RightTopCheck(t);
    }

    private void RightTopCheck(Tx t)
    {
        if (HitsLowerLines(t)) { t.Cycles += 4; Bounce(t, Side.Top); return; }
        RightSides(t);
    }

    /// <summary>BCCOMR</summary>
    private void RightSides(Tx t)
    {
        if (BottomOfCliff(t)) { t.Cycles += 4; Bounce(t, Side.Bottom); return; } // BCBOT2: JMP BCBOT
        SideOfLine(t);
        t.Cycles += 5;                                           // LBPL BCLFT
        if (!Mi(t.CC)) { t.Cycles += 1; Bounce(t, Side.Left); return; }
        t.Cycles += 4;                                           // JMP BCBOT
        Bounce(t, Side.Bottom);
    }

    /// <summary>BCKB27 (free-standing cliffs CLIF2, CLIF4, CLIF5).</summary>
    private void CliffCenter(Tx t, ushort cliff, ushort left, ushort top)
    {
        SetupCliff(t, cliff, left, top);
        if (cliff != _clif4) t.Cycles += 3;                      // BRA BCKB27
        bool? firstLine = PixelHit(t);
        if (firstLine == null) return;
        if (firstLine == true && HitsLowerLines(t)) { Bounce(t, Side.Top); return; }
        // BCCOM
        if (BottomOfCliff(t)) { Bounce(t, Side.Bottom); return; }
        SideOfLine(t);
        t.Cycles += 3;                                           // BPL BCLFT
        Bounce(t, Mi(t.CC) ? Side.Right : Side.Left);
    }

    /// <summary>LDA 4,Y / BMI: was the cliff's touched line its underside?</summary>
    private bool BottomOfCliff(Tx t)
    {
        t.A = t.Read(t.Y + 4);
        t.CC = Logic8(t.CC, t.A);
        t.Cycles += 5 + 3;
        return Mi(t.CC);
    }

    private void Bounce(Tx t, Side side)
    {
        int velX = t.U + _velX, velY = t.U + _velY;
        switch (side)
        {
            case Side.Right: // BCRIT: object is right of the cliff, push right
                t.A = t.Read(velX);
                t.CC = Logic8(t.CC, t.A);
                t.Cycles += 4 + Ix(_velX) + 3;                   // LDA / BGE
                if (Lt(t.CC))
                {
                    t.A = Neg8(ref t.CC, t.A);
                    t.A = Sub8(ref t.CC, t.A, 2);                // lose some speed
                    t.Write(velX, t.A);
                    t.CC = Logic8(t.CC, t.A);
                    t.Cycles += 2 + 2 + 4 + Ix(_velX);
                }
                t.A = Add8(ref t.CC, t.A, 6, withCarry: false);
                t.Cycles += 2 + 3;                               // ADDA #6 / BRA BCLFTV
                SideBump(t);
                break;

            case Side.Left: // BCLFT: object is left of the cliff, push left
                t.A = t.Read(velX);
                t.CC = Logic8(t.CC, t.A);
                t.Cycles += 4 + Ix(_velX) + 3;                   // LDA / BLE
                if (!Le(t.CC))
                {
                    t.A = Neg8(ref t.CC, t.A);
                    t.A = Add8(ref t.CC, t.A, 2, withCarry: false);
                    t.Write(velX, t.A);
                    t.CC = Logic8(t.CC, t.A);
                    t.Cycles += 2 + 2 + 4 + Ix(_velX);
                }
                t.A = Sub8(ref t.CC, t.A, 6);
                t.Cycles += 2;
                SideBump(t);
                break;

            case Side.Bottom: // BCBOT: hit the underside, send it down at half speed
                t.D = t.Read16(velY);
                t.CC = Logic16(t.CC, t.D);
                t.Cycles += 5 + Ix(_velY) + 3;                   // LDD / BPL
                if (Mi(t.CC))
                {
                    t.A = Com8(ref t.CC, t.A);
                    t.B = Neg8(ref t.CC, t.B);
                    t.A = Sbc8(ref t.CC, t.A, 0xFF);
                    t.A = Asr8(ref t.CC, t.A);
                    t.B = Ror8(ref t.CC, t.B);
                    t.Write16(velY, t.D);
                    t.CC = Logic16(t.CC, t.D);
                    t.Cycles += 2 * 5 + 5 + Ix(_velY);
                }
                t.Cycles += 3;                                   // BRA BCLFTE
                Thud(t, bumpY: 2);
                break;

            case Side.Top: // BCTOP: hit a cliff top from above, bounce up at half speed
                t.D = t.Read16(velY);
                t.CC = Logic16(t.CC, t.D);
                t.Cycles += 5 + Ix(_velY) + 3;                   // LDD / BMI
                if (!Mi(t.CC))
                {
                    t.A = Com8(ref t.CC, t.A);
                    t.B = Com8(ref t.CC, t.B);
                    t.A = Asr8(ref t.CC, t.A);
                    t.B = Ror8(ref t.CC, t.B);
                    t.Write16(velY, t.D);
                    t.CC = Logic16(t.CC, t.D);
                    t.Cycles += 4 * 2 + 5 + Ix(_velY);
                }
                t.A = 0xFE;
                t.CC = Logic8(t.CC, t.A);
                t.Write(t.U + _bumpY, t.A);
                t.CC = Logic8(t.CC, t.A);
                t.A = t.Read(velX);
                t.CC = Logic8(t.CC, t.A);
                t.Cycles += 2 + 4 + Ix(_bumpY) + 4 + Ix(_velX) + 3; // ... BNE BCUP
                if (t.A == 0)
                {
                    // Standing still on top: slide off the nearer edge.
                    SideOfLine(t);
                    t.Cycles += 3;                               // BPL BCTL
                    t.A = Mi(t.CC) ? (byte)2 : (byte)0xFE;
                    t.CC = Logic8(t.CC, t.A);
                    t.Write(velX, t.A);
                    t.CC = Logic8(t.CC, t.A);
                    t.Cycles += 2 + 4 + Ix(_velX);
                }
                PlayThud(t);
                break;
        }
    }

    /// <summary>BCLFTV: store the sideways bump, make sure it's falling a little, then thud.</summary>
    private void SideBump(Tx t)
    {
        t.Write(t.U + _bumpX, t.A);
        t.CC = Logic8(t.CC, t.A);
        t.D = t.Read16(t.U + _velY);
        t.CC = Logic16(t.CC, t.D);
        Sub16(ref t.CC, t.D, unchecked((ushort)-0x40));
        t.Cycles += 4 + Ix(_bumpX) + 5 + Ix(_velY) + 5 + 3;     // STA / LDD / CMPD / BPL
        if (Mi(t.CC))
        {
            t.D = unchecked((ushort)-0x40);
            t.CC = Logic16(t.CC, t.D);
            t.Cycles += 3;
        }
        t.Write16(t.U + _velY, t.D);
        t.CC = Logic16(t.CC, t.D);
        t.Cycles += 5 + Ix(_velY);
        Thud(t, bumpY: 2);
    }

    /// <summary>BCLFTE: bump 2 pixels down and thud.</summary>
    private void Thud(Tx t, byte bumpY)
    {
        t.A = bumpY;
        t.CC = Logic8(t.CC, t.A);
        t.Write(t.U + _bumpY, t.A);
        t.CC = Logic8(t.CC, t.A);
        t.Cycles += 2 + 4 + Ix(_bumpY);
        PlayThud(t);
    }

    /// <summary>LDX #SNCTHD / JMP VSND: tail-call the sound queue (its RTS returns for us).</summary>
    private void PlayThud(Tx t)
    {
        t.X = _thud;
        t.CC = Logic16(t.CC, t.X);
        t.Cycles += 3 + 4 + 4;                                   // LDX / JMP VSND / JMP SND
        _sound.Run(t);
    }

    // ------------------------------------------------------------------ BCKPIX

    private enum PixState { Start, LineA, LineB, YLeft0, YLeft, XLeft0, XLeft, Miss, Hit }

    /// <summary>
    /// BCKPIX: pixel-exact collision between the object (its current horse image's collision
    /// data, PPICH) and a cliff. Collision data is a list of 4-byte lines [left, right] in
    /// pixels; D = cliff left column, X = cliff top row, Y = cliff collision data.
    /// The tables are aligned vertically, then walked line by line; once one side is known to
    /// be left of the other only that edge pair is tested (4 lines unrolled, like the
    /// original). Output: Carry set = contact, X/Y = the touching lines.
    /// </summary>
    public void PixelCollision(Tx t)
    {
        t.D = Sub16(ref t.CC, t.D, t.Read16(t.U + _posX));
        t.Write16(t.Var(_colDx), t.D);                           // X offset object -> cliff
        t.CC = Logic16(t.CC, t.D);
        t.D = t.X;                                               // cliff top row
        t.X = t.Read16(t.Read16(t.U + _picH));                   // object collision data
        t.CC = Logic16(t.CC, t.X);
        t.B = Sub8(ref t.CC, t.B, t.Read(t.U + _colY2));         // rows between the two tops
        t.Cycles += 6 + Ix(_posX) + 5 + Ext(_colDx) + 6 + 5 + Symbols.IndirectCost(_picH)
                    + 4 + Ix(_colY2) + 3;                        // ... SUBB / BMI
        if (!Mi(t.CC))
        {
            t.B = Asl8(ref t.CC, t.B);
            t.B = Asl8(ref t.CC, t.B);
            t.A = Rol8(ref t.CC, t.A);
            t.X = (ushort)(t.X + t.D);                           // skip object lines above the cliff
            t.CC = Set(t.CC, Z, t.X == 0);
        }
        else
        {
            t.B = Neg8(ref t.CC, t.B);
            t.B = Asl8(ref t.CC, t.B);
            t.B = Asl8(ref t.CC, t.B);
            t.A = Rol8(ref t.CC, t.A);
            t.Y = (ushort)(t.Y + t.D);                           // skip cliff lines above the object
            t.CC = Set(t.CC, Z, t.Y == 0);
            t.Cycles += 2;
        }
        t.Cycles += 2 + 2 + 2 + 8 + 3;                           // ASLB ASLB ROLA LEA BRA
        ScanLines(t);
    }

    /// <summary>
    /// BPCOL: walk two aligned collision tables (X = object lines, Y = other lines, COLDX =
    /// X offset between them). Also used by HITEM for bird-against-bird contact.
    /// </summary>
    public void ScanLines(Tx t)
    {
        ushort colDx = t.Read16(t.Var(_colDx));
        int dxCost = 6 + Ext(_colDx);
        var state = PixState.Start;
        int k = 0;
        while (true)
        {
            switch (state)
            {
                case PixState.LineA: // BP1STA
                    t.A = t.Read(t.X);
                    t.CC = Logic8(t.CC, t.A);
                    t.Cycles += 4;
                    goto case PixState.LineB;

                case PixState.LineB: // BP1STB: end of either table?
                    t.A = (byte)(t.A | t.Read(t.Y));
                    t.CC = Logic8(t.CC, t.A);
                    t.A = Ror8(ref t.CC, t.A);
                    t.Cycles += 4 + 2 + 3;
                    if (Cs(t.CC)) { state = PixState.Miss; break; }
                    t.X += 4;
                    t.CC = Set(t.CC, Z, t.X == 0);
                    t.Y += 4;
                    t.CC = Set(t.CC, Z, t.Y == 0);
                    t.Cycles += 5 + 5;
                    state = PixState.Start;
                    break;

                case PixState.Start: // BPCOL: first comparable line pair
                    t.D = t.Read16(t.X + 2);
                    t.CC = Logic16(t.CC, t.D);
                    t.Cycles += 6 + 3;
                    if (Lt(t.CC)) { state = PixState.LineB; break; }
                    t.D = Sub16(ref t.CC, t.D, t.Read16(t.Y));
                    t.Cycles += 6 + 3;
                    if (Vs(t.CC)) { state = PixState.LineA; break; }
                    t.D = Sub16(ref t.CC, t.D, colDx);
                    t.Cycles += dxCost + 3;
                    if (Lt(t.CC)) { state = PixState.YLeft; k = 4; break; }
                    t.D = t.Read16(t.X);
                    t.CC = Logic16(t.CC, t.D);
                    t.D = Sub16(ref t.CC, t.D, t.Read16(t.Y + 2));
                    t.D = Sub16(ref t.CC, t.D, colDx);
                    t.Cycles += 5 + 7 + dxCost + 3;
                    if (!Eq(t.CC) && !Lt(t.CC)) { state = PixState.XLeft; k = 4; break; }
                    state = PixState.Hit;
                    break;

                case PixState.YLeft0: // BPYX0: next group of four lines
                    t.X += 16;
                    t.CC = Set(t.CC, Z, t.X == 0);
                    t.Y += 16;
                    t.CC = Set(t.CC, Z, t.Y == 0);
                    t.Cycles += 5 + 5;
                    k = 0;
                    goto case PixState.YLeft;

                case PixState.YLeft: // cliff is left of object: compare object right vs cliff left
                    t.D = t.Read16(t.X + k + 2);
                    t.CC = Logic16(t.CC, t.D);
                    t.Cycles += 6 + 3;
                    if (Lt(t.CC)) { state = PixState.Miss; break; }
                    t.D = Sub16(ref t.CC, t.D, t.Read16(t.Y + k));
                    t.Cycles += (k == 0 ? 6 : 7) + 3;
                    if (Vs(t.CC)) { state = PixState.Miss; break; }
                    t.D = Sub16(ref t.CC, t.D, colDx);
                    t.Cycles += dxCost + 3;
                    if (Lt(t.CC))
                    {
                        if (k == 12) state = PixState.YLeft0;
                        else { k += 4; state = PixState.YLeft; }
                        break;
                    }
                    AdvanceTo(t, k);
                    state = PixState.Hit;
                    break;

                case PixState.XLeft0: // BPXY0
                    t.X += 16;
                    t.CC = Set(t.CC, Z, t.X == 0);
                    t.Y += 16;
                    t.CC = Set(t.CC, Z, t.Y == 0);
                    t.Cycles += 5 + 5;
                    k = 0;
                    goto case PixState.XLeft;

                case PixState.XLeft: // object is left of cliff: compare object left vs cliff right
                    t.D = t.Read16(t.X + k);
                    t.CC = Logic16(t.CC, t.D);
                    t.Cycles += (k == 0 ? 5 : 6) + 3;
                    if (Lt(t.CC)) { state = PixState.Miss; break; }
                    t.D = Sub16(ref t.CC, t.D, t.Read16(t.Y + k + 2));
                    t.Cycles += 7 + 3;
                    if (Vs(t.CC)) { state = PixState.Miss; break; }
                    t.D = Sub16(ref t.CC, t.D, colDx);
                    t.Cycles += dxCost + 3;
                    if (!Eq(t.CC) && !Lt(t.CC))
                    {
                        if (k == 12) state = PixState.XLeft0;
                        else { k += 4; state = PixState.XLeft; }
                        break;
                    }
                    AdvanceTo(t, k);
                    state = PixState.Hit;
                    break;

                case PixState.Miss: // BPEN: CLRA
                    t.A = 0;
                    t.CC = Clr(t.CC);
                    t.Cycles += 2;
                    return;

                case PixState.Hit: // COMA: carry set
                    t.A = Com8(ref t.CC, t.A);
                    t.Cycles += 2;
                    return;
            }
        }
    }

    /// <summary>LEAX k,X / LEAY k,Y: point at the touching lines.</summary>
    private static void AdvanceTo(Tx t, int k)
    {
        if (k == 0) return;
        t.X = (ushort)(t.X + k);
        t.CC = Set(t.CC, Z, t.X == 0);
        t.Y = (ushort)(t.Y + k);
        t.CC = Set(t.CC, Z, t.Y == 0);
        t.Cycles += 5 + 5;
    }
}
