using static Joust.Emulation.Alu6809;

namespace Joust.Port.Routines;

/// <summary>
/// The egg's helper routines (JOUSTRV4.ASM). An egg is a process like a bird: it falls with
/// gravity (ADDEGG), tumbles (WEGG picks the frame from its speeds), bounces on cliffs, waits
/// and hatches. Images low on the screen are clipped at the lava (CLIPER). Bodies exclude the
/// final RTS.
/// </summary>
public sealed class Eggs
{
    private readonly Physics _physics;
    private readonly Ground _ground;

    // Process block
    private readonly int _posX, _posY, _velX, _velY, _bumpY, _colY1, _colY2, _colX, _picH, _state;

    // DMA block
    private readonly int _wcX, _wcY, _wcLen, _wcLenY;

    private readonly ushort _gravity, _flyX, _bckRefresh, _boxX, _boxY, _eggImages, _fallLeft, _fallRight;
    private readonly byte _ceiling, _floor;

    public Eggs(Symbols s, Physics physics, Ground ground)
    {
        _physics = physics;
        _ground = ground;
        _posX = s["PPOSX"]; _posY = s["PPOSY"]; _velX = s["PVELX"]; _velY = s["PVELY"]; _bumpY = s["PBUMPY"];
        _colY1 = s["PCOLY1"]; _colY2 = s["PCOLY2"]; _colX = s["PCOLX"]; _picH = s["PPICH"]; _state = s["PSTATE"];
        _wcX = s["WCX"]; _wcY = s["WCY"]; _wcLen = s["WCLEN"]; _wcLenY = s["WCLENY"];
        _gravity = s["GRAV"]; _flyX = s["FLYX"]; _bckRefresh = s["BCKRFS"]; _boxX = s["BCKXTB"]; _boxY = s["BCKYTB"];
        _eggImages = s["EGGI"]; _fallLeft = s["EGFLFT"]; _fallRight = s["EGFRIT"];
        _ceiling = (byte)s.Value("CEILNG"); _floor = (byte)s.Value("FLOOR");
    }

    private static int Ix(int offset) => Symbols.IndexCost(offset);
    private static int Ext(ushort address) => Symbols.AddressCost(address);

    /// <summary>
    /// ADDEGG: gravity and movement for an egg. Like a bird's, but always full gravity, a
    /// 7-pixel collision box, and its own wrap-around (4..288) after the usual one.
    /// </summary>
    public void Fall(Tx t)
    {
        t.B = t.Read(t.Var(_gravity));
        t.CC = Logic8(t.CC, t.B);
        t.A = (t.B & 0x80) != 0 ? (byte)0xFF : (byte)0;          // SEX
        t.CC = Logic16(t.CC, t.D);
        t.D = Add16(ref t.CC, t.D, t.Read16(t.U + _velY));
        t.Write16(t.U + _velY, t.D);
        t.CC = Logic16(t.CC, t.D);
        t.D = Add16(ref t.CC, t.D, t.Read16(t.U + _posY + 1));
        t.A = Add8(ref t.CC, t.A, t.Read(t.U + _bumpY), withCarry: false);
        t.Write(t.U + _bumpY, 0);
        t.CC = Clr(t.CC);
        Sub8(ref t.CC, t.A, _ceiling);
        t.Cycles += 4 + Ext(_gravity) + 2 + 6 + Ix(_velY) + 5 + Ix(_velY) + 6 + Ix(_posY + 1)
                    + 4 + Ix(_bumpY) + 6 + Ix(_bumpY) + 2 + 3;  // ... CMPA #CEILNG / BHI
        if (!Hi(t.CC))
        {
            int bump = t.U + _bumpY;
            t.Write(bump, Inc(ref t.CC, t.Read(bump)));
            t.D = t.Read16(t.U + _velY);
            t.CC = Logic16(t.CC, t.D);
            t.Cycles += 6 + Ix(_bumpY) + 5 + Ix(_velY) + 3;      // INC / LDD / BPL
            if (Mi(t.CC))
            {
                t.A = Com8(ref t.CC, t.A);                       // bounce off the ceiling
                t.B = Neg8(ref t.CC, t.B);
                t.A = Sbc8(ref t.CC, t.A, 0xFF);
                t.Write16(t.U + _velY, t.D);
                t.CC = Logic16(t.CC, t.D);
                t.Cycles += 2 + 2 + 2 + 5 + Ix(_velY);
            }
            t.D = (ushort)(_ceiling << 8);                       // ADEDWN (falls into ADECEI)
            t.CC = Logic16(t.CC, t.D);
            t.Cycles += 3;
        }

        // ADECEI
        t.Write16(t.U + _posY + 1, t.D);
        t.CC = Logic16(t.CC, t.D);
        t.Write(t.U + _colY1, t.A);                              // 7-pixel collision box
        t.CC = Logic8(t.CC, t.A);
        t.A = Sub8(ref t.CC, t.A, 7 - 1);
        t.Write(t.U + _colY2, t.A);
        t.CC = Logic8(t.CC, t.A);
        t.A = t.Read(t.U + _velX);
        t.CC = Logic8(t.CC, t.A);
        t.X = _flyX;
        t.CC = Logic16(t.CC, t.X);
        t.D = t.Read16(t.X + (sbyte)t.A);
        t.CC = Logic16(t.CC, t.D);
        t.B = Add8(ref t.CC, t.B, t.Read(t.U + _velX + 2), withCarry: false);
        t.Write(t.U + _velX + 2, t.B);
        t.CC = Logic8(t.CC, t.B);
        t.A = Add8(ref t.CC, t.A, 0, withCarry: true);
        t.B = t.A;
        t.Cycles += 5 + Ix(_posY + 1) + 4 + Ix(_colY1) + 2 + 4 + Ix(_colY2) + 4 + Ix(_velX) + 3 + 6
                    + 4 + Ix(_velX + 2) + 4 + Ix(_velX + 2) + 2 + 6 + 8; // ... TFR / JSR WRAPX
        _physics.WrapX(t);
        t.Cycles += 5;

        // The egg's own wrap-around: 4..288.
        Sub16(ref t.CC, t.D, 288);
        t.Cycles += 5 + 3;
        if (!Le(t.CC))
        {
            t.D = Add16(ref t.CC, t.D, unchecked((ushort)(4 - 288)));
            t.Cycles += 4;
        }
        Sub16(ref t.CC, t.D, 4);
        t.Cycles += 5 + 3;
        if (Lt(t.CC))
        {
            t.D = Add16(ref t.CC, t.D, 288 - 4);
            t.Cycles += 4;
        }
        t.Write16(t.U + _posX, t.D);
        t.CC = Logic16(t.CC, t.D);
        t.D = Add16(ref t.CC, t.D, 8 - 1 - 1);
        t.Write16(t.U + _colX, t.D);
        t.CC = Logic16(t.CC, t.D);
        t.Cycles += 5 + Ix(_posX) + 4 + 5 + Ix(_colX);
    }

    /// <summary>
    /// WEGG: queue the egg's image. The frame shows it tumbling the way it moves: level when
    /// slow, tilted forward or back when falling or rising faster than $80.
    /// </summary>
    public void Draw(Tx t)
    {
        t.Cycles += 8 + 4;                                       // JSR VWR1CLS / JMP
        _ground.Allocate(t, write: true, blocks: 1);
        t.Cycles += 5;
        t.A = t.Read(t.U + _velX);
        t.CC = Logic8(t.CC, t.A);
        t.Cycles += 4 + Ix(_velX) + 3;                           // LDA PVELX,U / BPL WEGRIT
        bool left = Mi(t.CC);
        t.Y = left ? _fallLeft : _fallRight;
        t.CC = Logic16(t.CC, t.Y);
        t.Cycles += 4 + (left ? 3 : 0);                          // LDY # (/ BRA WEGY)
        t.D = t.Read16(t.U + _velY);
        t.CC = Logic16(t.CC, t.D);
        t.Cycles += 5 + Ix(_velY) + 3;                           // LDD PVELY,U / BMI
        int frameOffset;
        if (Mi(t.CC))
        {
            t.D = Add16(ref t.CC, t.D, 0x0080);
            t.Cycles += 4 + 3;                                   // ADDD / BGT WEGUP
            frameOffset = !Eq(t.CC) && !Lt(t.CC) ? 0 : 1;
        }
        else
        {
            t.D = Sub16(ref t.CC, t.D, 0x0080);
            t.Cycles += 4 + 3;                                   // SUBD / BGT WEGD2
            frameOffset = !Eq(t.CC) && !Lt(t.CC) ? 2 : 0;
        }
        t.A = t.Read(t.Y + frameOffset);
        t.CC = Logic8(t.CC, t.A);
        t.Cycles += (frameOffset == 0 ? 4 : 5) + (frameOffset == 1 ? 0 : 3); // LDA (/ BRA WEGPAR)
        DrawFrame(t);
    }

    /// <summary>WEGPAR: show egg image frame A (an offset into the egg images) and clip it.</summary>
    public void DrawFrame(Tx t)
    {
        t.Y = t.Read16(_eggImages);
        t.CC = Logic16(t.CC, t.Y);
        t.Y = (ushort)(t.Y + (sbyte)t.A);
        t.CC = Set(t.CC, Z, t.Y == 0);
        t.Write16(t.U + _picH, t.Y);
        t.CC = Logic16(t.CC, t.Y);
        t.Cycles += 6 + Ext(_eggImages) + 5 + 6 + Ix(_picH) + 8; // LDY EGGI / LEAY A,Y / STY / JSR WRHOR2
        _ground.QueueImage(t);
        t.Cycles += 5 + 4;                                       // RTS / JMP CLIPER
        Clip(t);
    }

    /// <summary>
    /// CLIPER: a queued image reaching below the floor is cut at the lava line; one wholly
    /// below it becomes a harmless 1-byte write off screen.
    /// </summary>
    public void Clip(Tx t)
    {
        t.A = (byte)(t.Read(t.X + _wcLenY) ^ 4);                 // height (SC1 XOR-4 undone)
        t.CC = Logic8(t.CC, t.A);
        t.A = Add8(ref t.CC, t.A, t.Read(t.X + _wcY), withCarry: false);
        Sub8(ref t.CC, t.A, (byte)(_floor + 1));
        t.Cycles += 4 + Ix(_wcLenY) + 2 + 4 + Ix(_wcY) + 2 + 3;
        if (Cs(t.CC)) return;                                    // bottom above the floor
        t.A = t.Read(t.X + _wcY);
        t.CC = Logic8(t.CC, t.A);
        Sub8(ref t.CC, t.A, (byte)(_floor + 1));
        t.Cycles += 4 + Ix(_wcY) + 2 + 3;
        if (Cs(t.CC))
        {
            // CLPIT: cut the height at the floor
            t.A = Neg8(ref t.CC, t.A);
            t.A = Add8(ref t.CC, t.A, (byte)(_floor + 1), withCarry: false);
            t.A = (byte)(t.A ^ 4);
            t.CC = Logic8(t.CC, t.A);
            t.Write(t.X + _wcLenY, t.A);
            t.CC = Logic8(t.CC, t.A);
            t.Cycles += 2 + 2 + 2 + 4 + Ix(_wcLenY);
            return;
        }
        t.A = 0xF0;                                              // all below: write off screen
        t.CC = Logic8(t.CC, t.A);
        t.Write(t.X + _wcX, t.A);
        t.CC = Logic8(t.CC, t.A);
        t.D = 0x0404;                                            // 1x1 (XOR-4)
        t.CC = Logic16(t.CC, t.D);
        t.Write16(t.X + _wcLen, t.D);
        t.CC = Logic16(t.CC, t.D);
        t.Cycles += 2 + 4 + Ix(_wcX) + 3 + 5 + Ix(_wcLen);
    }

    /// <summary>CEGG / CLAVAT: erase the egg's (or lava troll's) last image, if any.</summary>
    public void Erase(Tx t)
    {
        t.Y = t.Read16(t.U + _picH);
        t.CC = Logic16(t.CC, t.Y);
        t.Cycles += 6 + Ix(_picH) + 3;
        if (t.Y == 0) return;
        t.Write(t.U + _picH, 0);
        t.Write(t.U + _picH + 1, 0);
        t.CC = Clr(t.CC);
        t.Cycles += 6 + Ix(_picH) + 6 + Ix(_picH + 1) + 8 + 8 + 4; // CLR / CLR / JSR CLREGY / JSR VCL1CLS / JMP
        _ground.Allocate(t, write: false, blocks: 1);
        t.Cycles += 5 + 3;                                       // RTS / BRA WRHOR2
        _ground.QueueImage(t);
        t.Cycles += 5 + 4;                                       // RTS / JMP CLIPER
        Clip(t);
    }

    /// <summary>EGGBKG: if the egg overlaps a cliff, have the cliff redrawn.</summary>
    public void RefreshBackground(Tx t)
    {
        t.X = t.Read16(t.U + _posX);
        t.CC = Logic16(t.CC, t.X);
        t.A = t.Read(t.X + _boxX);
        t.CC = Logic8(t.CC, t.A);
        t.X = t.Read16(t.U + _posY);
        t.CC = Logic16(t.CC, t.X);
        t.A = (byte)(t.A & t.Read(t.X + _boxY + 5 + 6));
        t.CC = Logic8(t.CC, t.A);
        t.A = (byte)(t.A | t.Read(t.Var(_bckRefresh)));
        t.CC = Logic8(t.CC, t.A);
        t.Write(t.Var(_bckRefresh), t.A);
        t.CC = Logic8(t.CC, t.A);
        t.Cycles += 5 + Ix(_posX) + 4 + Ix(_boxX) + 5 + Ix(_posY) + 4 + Ix(_boxY + 11)
                    + 4 + Ext(_bckRefresh) + 4 + Ext(_bckRefresh);
    }

    /// <summary>
    /// EGAREA: where to show an egg's score: byte-aligned, 5 pixels up, kept above the bottom
    /// ledge and clear of the right edge. PSTATE = screen address; the position is adjusted.
    /// </summary>
    public void ScoreArea(Tx t)
    {
        t.D = t.Read16(t.U + _posX);
        t.CC = Logic16(t.CC, t.D);
        t.B = (byte)(t.B & 0xFE);
        t.CC = Logic8(t.CC, t.B);
        t.Write(t.U + _posX + 1, t.B);
        t.CC = Logic8(t.CC, t.B);
        t.A = Ror8(ref t.CC, t.A);
        t.B = Ror8(ref t.CC, t.B);                               // byte column
        t.A = t.B;                                               // TFR B,A
        t.B = t.Read(t.U + _posY + 1);
        t.CC = Logic8(t.CC, t.B);
        t.B = Sub8(ref t.CC, t.B, 5);
        Sub8(ref t.CC, t.B, 0xD3 - 1 - 4);
        t.Cycles += 5 + Ix(_posX) + 2 + 4 + Ix(_posX + 1) + 2 + 2 + 6 + 4 + Ix(_posY + 1) + 2 + 2 + 3;
        if (!(Cs(t.CC) || Eq(t.CC)))
        {
            t.B = 0xD3 - 1 - 4;                                  // no lower than the bottom ledge
            t.CC = Logic8(t.CC, t.B);
            t.Cycles += 2;
        }
        Sub8(ref t.CC, t.A, (288 - 16) / 2);
        t.Cycles += 2 + 3;
        if (!(Cs(t.CC) || Eq(t.CC)))
        {
            t.A = (288 - 16) / 2;                                // don't wrap off the right edge
            t.CC = Logic8(t.CC, t.A);
            t.Cycles += 2;
        }
        t.Write16(t.U + _state, t.D);
        t.CC = Logic16(t.CC, t.D);
        t.Write(t.U + _posY + 1, t.B);
        t.CC = Logic8(t.CC, t.B);
        t.B = t.A;
        t.A = 0;
        t.CC = Clr(t.CC);
        t.B = Asl8(ref t.CC, t.B);
        t.A = Rol8(ref t.CC, t.A);
        t.Write16(t.U + _posX, t.D);
        t.CC = Logic16(t.CC, t.D);
        t.Cycles += 5 + Ix(_state) + 4 + Ix(_posY + 1) + 6 + 2 + 2 + 2 + 5 + Ix(_posX);
    }
}
