using static Joust.Emulation.Alu6809;

namespace Joust.Port.Routines;

/// <summary>
/// Bird-against-bird contact (JOUSTRV4.ASM). HITEM tests one pair (U and X): first their
/// collision boxes, then pixel-exact against each bird's current collision data. A real
/// contact goes to the original OSTHIT (the joust: who has the higher lance, deaths, eggs,
/// scores). The bounce routines below are what OSTHIT uses to knock the two apart.
/// Each method is the body of one original routine, excluding its final RTS.
/// </summary>
public sealed class Combat
{
    private readonly int _posX, _posY, _velX, _velY, _bumpX, _bumpY, _face, _picH, _colY1, _colY2, _colX;
    private readonly ushort _colObj, _colDx;
    private readonly Physics _physics;

    public Combat(Symbols s, Physics physics)
    {
        _posX = s["PPOSX"]; _posY = s["PPOSY"]; _velX = s["PVELX"]; _velY = s["PVELY"];
        _bumpX = s["PBUMPX"]; _bumpY = s["PBUMPY"]; _face = s["PFACE"]; _picH = s["PPICH"];
        _colY1 = s["PCOLY1"]; _colY2 = s["PCOLY2"]; _colX = s["PCOLX"];
        _colObj = s["COLOBJ"]; _colDx = s["COLDX"];
        _physics = physics;
    }

    private static int Ix(int offset) => Symbols.IndexCost(offset);
    private static int Ext(ushort address) => Symbols.AddressCost(address);

    // ------------------------------------------------------------------ HITEM

    /// <summary>
    /// HITEM: do U and X touch? Carry clear = no (the usual case). A pixel contact returns
    /// false so the original runs the joust resolution.
    /// </summary>
    public bool Hit(Tx t)
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
        if (Cs(t.CC)) return false;                              // contact: the original jousts
        t.X = t.Read16(t.Var(_colObj));                          // HITEM2
        t.CC = Logic16(t.CC, t.X);
        t.Cycles += 5 + Ext(_colObj);
        return Miss(t);
    }

    private static bool Miss(Tx t)
    {
        t.CC = (byte)(t.CC & ~C);                                // HITEM1: ANDCC #$FE
        t.Cycles += 3;
        return true;
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
