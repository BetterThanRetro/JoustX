using static Joust.Emulation.Alu6809;

namespace Joust.Port.Routines;

/// <summary>
/// Attract mode's drawing (ATT.ASM). WRANIB plots one pixel of the Williams logo outline: X =
/// pixel column, Y = row, A = colour nibble. It selects RAM reads, sets the pixel's nibble if
/// it is still blank, and selects ROM reads again.
/// </summary>
/// <para>
/// LINE (the logo's Bresenham line drawer) runs natively from COUNTS, its per-pixel loop head:
/// whole pixels (and FILLDN's downward fill, one byte at a time) up to the next interrupt edge,
/// stopping at COUNTS, FILLDN.1S or COUNTS.6S so the interrupt arrives on the same instruction.
/// LINE's set-up and the off-screen exit (RNGERR) stay original.
/// </para>
public sealed class Attract(Symbols s)
{
    private readonly ushort _drruc = s["DRRUC"], _rruc = s["RRUC"];

    public enum L { Pixel, Fill, AfterFill }

    private readonly ushort _counts = s["COUNTS"], _fillLoop = s["FILLDN.1S"], _afterFill = s["COUNTS.6S"];
    private readonly ushort _contrl = s["CONTRL"], _xloc = s["XLOC"], _yloc = s["YLOC"], _quad = s["QUAD"];
    private readonly ushort _errcnt = s["ERRCNT"], _absdx = s["ABSDX"], _absdy = s["ABSDY"], _save = s["SAVE"];
    private readonly ushort _ccolor = s["CCOLOR"], _fildwn = s["FILDWN"], _count = s["COUNT"], _hcount = s["HCOUNT"];

    public ushort Address(L label) => label switch
    {
        L.Pixel => _counts,
        L.Fill => _fillLoop,
        _ => _afterFill,
    };

    /// <summary>
    /// Runs the line loop from <paramref name="start"/>: one segment (a pixel, a fill byte, or the
    /// pixel's tail after the fill) at a time while it fits before the next interrupt edge. Returns
    /// where it stopped (or LINE's return address), or null if not one segment fits.
    /// </summary>
    public ushort? Line(Tx t, L start)
    {
        var a = new Asm(t);
        L at = start;
        bool progress = false;
        while (true)
        {
            var mark = t.Mark();
            (L? next, ushort? exit) = at switch
            {
                L.Pixel => Pixel(t, a),
                L.Fill => FillByte(t, a),
                _ => Tail(t, a),
            };
            if (next == null || !t.CanRun(0))
            {
                t.Rollback(mark);
                return progress ? Address(at) : null;
            }
            progress = true;
            if (exit != null) return exit;
            at = next.Value;
        }
    }

    /// <summary>
    /// COUNTS: plot the pixel at XLOC,YLOC, then fill down or go on to the tail. A null next
    /// segment means the pixel is off the screen (RNGERR, left to the original).
    /// </summary>
    private (L?, ushort?) Pixel(Tx t, Asm a)
    {
        a.Std(a.M(_errcnt));
        t.S--;                                                   // PSHS CC
        t.Write(t.S, t.CC);
        t.Cycles += 6;
        a.Clr(a.M(_drruc));                                      // read the screen
        a.Clr(a.M(_contrl));
        a.Ldd(a.M(_xloc));
        a.Asra();
        a.Rorb();                                                // byte column; carry = right pixel
        a.TfrBA();
        a.Ldb(a.M((ushort)(_yloc + 1)));
        t.X = t.D;                                               // TFR D,X
        t.Cycles += 6;
        a.LdbI(0xF0);
        if (!a.Br(!Cs(t.CC))) a.Comb();                          // BCC .3S
        a.TfrBA();                                               // .3S
        a.Comb();
        a.Std(a.M(_save));                                       // SAVE = pixel mask, keep mask
        a.Anda(a.M(_ccolor));
        t.B &= t.ReadRam(t.X);                                   // ANDB ,X
        t.CC = Logic8(t.CC, t.B);
        t.Cycles += 4;
        a.PshsB();
        a.TfrAB();
        t.B |= t.Read(t.S);                                      // ORB ,S+
        t.S++;
        t.CC = Logic8(t.CC, t.B);
        t.Cycles += 6;
        a.CmpxI(0x9800);
        if (a.Br(a.Hs)) return (null, null);                     // RNGERR
        t.Write(t.X, t.B);                                       // STB ,X
        t.CC = Logic8(t.CC, t.B);
        t.Cycles += 4;
        a.Tst(a.M(_fildwn));
        if (a.Br(a.Pl)) return Tail(t, a);
        t.Cycles += 8;                                           // JSR FILLDN
        t.Push16(_afterFill);
        t.Push16(t.X);                                           // PSHS X
        t.Cycles += 7;
        a.Leax(1);
        return (L.Fill, null);
    }

    /// <summary>FILLDN.1S: fill the next byte down unless it already has colour in the pixel.</summary>
    private (L?, ushort?) FillByte(Tx t, Asm a)
    {
        a.Ldd(a.M(_save));
        a.Anda(a.M((ushort)(_ccolor + 1)));
        t.S--;                                                   // PSHS A
        t.Write(t.S, t.A);
        t.Cycles += 6;
        a.Lda(a.M(_save));
        t.A &= t.ReadRam(t.X);                                   // ANDA ,X
        t.CC = Logic8(t.CC, t.A);
        t.Cycles += 4;
        if (!a.Br(a.Ne))
        {
            t.B &= t.ReadRam(t.X);                               // ANDB ,X
            t.Cycles += 4;
            t.B |= t.Read(t.S);                                  // ORB ,S+
            t.S++;
            t.CC = Logic8(t.CC, t.B);
            t.Cycles += 6;
            t.Write(t.X, t.B);                                   // STB ,X
            t.Cycles += 4;
            byte row = Inc(ref t.CC, (byte)t.X);                 // EXG D,X / INCB / EXG X,D
            t.X = (ushort)((t.X & 0xFF00) | row);
            t.Cycles += 8 + 2 + 8;
            if (a.Br(a.Ne)) return (L.Fill, null);
            t.S--;                                               // PSHS A
            t.Write(t.S, t.A);
            t.Cycles += 6;
        }
        t.A = t.Read(t.S);                                       // .2S: PULS A,X,PC
        t.X = t.Read16(t.S + 1);
        ushort pc = t.Read16(t.S + 3);
        t.S += 5;
        t.Cycles += 5 + 5;
        return pc == _afterFill ? (L.AfterFill, null) : (L.AfterFill, pc);
    }

    /// <summary>COUNTS.6S: read ROM again, count the pixel, and step in X or Y (or finish).</summary>
    private (L?, ushort?) Tail(Tx t, Asm a)
    {
        a.LdaI(1);
        a.Sta(a.M(_drruc));
        a.Sta(a.M(_contrl));
        a.Ldd(a.M(_count));
        a.SubdI(1);
        if (!a.Br(a.Ne))
        {
            a.Tst(a.M(_hcount));
            if (a.Br(a.Eq))
            {
                t.CC = t.Read(t.S);                              // DONE: PULS CC,PC
                ushort pc = t.Read16(t.S + 1);
                t.S += 3;
                t.Cycles += 5 + 3;
                return (L.Pixel, pc);
            }
            a.Dec(a.M(_hcount));
        }
        a.Std(a.M(_count));                                      // .1S
        t.CC = t.Read(t.S);                                      // PULS CC
        t.S++;
        t.Cycles += 6;
        if (a.Br(Cs(t.CC)))
        {
            t.Cycles += 8;                                       // MOVX: JSR LFTRT
            a.LdaI(0x40);                                        // RLMASK
            a.Bita(a.M(_quad));
            bool right = a.Br(a.Ne);
            a.Ldd(a.M(_xloc));
            if (right) a.AdddI(1);
            else { a.SubdI(1); a.Bra(); }
            a.Std(a.M(_xloc));
            t.Cycles += 5;                                       // RTS
            a.Ldd(a.M(_errcnt));                                 // STP1
            a.Addd(a.M(_absdy));
        }
        else
        {
            t.Cycles += 8;                                       // MOVY: JSR UPDN
            a.Tst(a.M(_quad));
            bool down = a.Br(a.Mi);
            a.Ldd(a.M(_yloc));
            if (down) a.AdddI(1);
            else { a.SubdI(1); a.Bra(); }
            a.Std(a.M(_yloc));
            t.Cycles += 5;                                       // RTS
            a.Ldd(a.M(_errcnt));
            a.Addd(a.M(_absdx));
            a.Bra();
        }
        return (L.Pixel, null);
    }

    // ------------------------------------------------------------------ stripe generator

    private readonly int _pRam = s["PRAM"];
    private readonly ushort _stripeNap = (ushort)(s["WRPRTS.10S"] - 2 - 3);   // PCNAP 1 (LDA # / JSR VNAPTPC)

    /// <summary>
    /// WRPIXH / WRPIXV (the marquee page's stripe process, entered by BSR): plot 16 pixels down
    /// (horizontal edge) or across (vertical edge) with the colour cycling through 8-15, then
    /// WRPRTS: save the registers and return address in PRAM, and every third call nap a tick.
    /// Returns where the code goes next: the caller (JMP [PRAM+6,U]), or the PCNAP (original).
    /// </summary>
    public ushort? Stripe(Tx t, bool across)
    {
        var a = new Asm(t);
        NextColour(a);
        t.Write16(t.U + _pRam, t.X);                             // STX PRAM,U
        t.Cycles += 5 + Symbols.IndexCost(_pRam);
        a.Sty(a.U(_pRam + 2));
        a.Std(a.U(_pRam + 4));
        a.LdbI(16);
        do
        {
            t.Cycles += 7;                                       // BSR WRANIB
            Plot(t);
            t.Cycles += 5;                                       // its RTS
            if (across) a.Leax(1);
            else a.Leay(1);
            NextColour(a);
            a.Decb();
        }
        while (a.Br(a.Ne));
        if (!across) a.Bra();                                    // BRA WRPRTS
        t.D = t.Read16(t.S);                                     // WRPRTS: LDD ,S++
        t.S += 2;
        t.Cycles += 5 + 3;
        a.Std(a.U(_pRam + 6));
        a.Dec(a.U(_pRam + 8));
        if (!a.Br(a.Gt))
        {
            a.LdaI(3);
            a.Sta(a.U(_pRam + 8));
            return t.CanRun(0) ? _stripeNap : null;
        }
        a.Ldd(a.U(_pRam + 4));                                   // .10S
        a.Ldy(a.U(_pRam + 2));
        a.Ldx(a.U(_pRam));
        ushort back = t.Read16(t.U + _pRam + 6);                 // JMP [PRAM+6,U]
        t.Cycles += 3 + 4;
        return t.CanRun(0) ? back : null;
    }

    /// <summary>INCA / ANDA #$0F / ORA #$08.</summary>
    private static void NextColour(Asm a)
    {
        a.Inca();
        a.AndaI(0x0F);
        a.OraI(0x08);
    }

    /// <summary>WRANIB (without the RTS part of its final PULS D,X,Y,PC).</summary>
    public void Plot(Tx t)
    {
        var a = new Asm(t);
        byte colour = t.A, savedB = t.B;
        ushort x = t.X, y = t.Y;
        t.Cycles += 11;                                          // PSHS D,X,Y
        a.LdaI(0);                                               // read the screen
        a.Sta(a.M(_drruc));
        a.Sta(a.M(_rruc));
        t.D = x;                                                 // TFR X,D
        t.Cycles += 6;
        a.Lsra();
        a.Rorb();                                                // byte column; carry = right pixel
        t.A = t.B;                                               // TFR B,A
        t.Cycles += 6;
        t.B = (byte)y;                                           // LDB 5,S: the row
        t.CC = Logic8(t.CC, t.B);
        t.Cycles += 5;
        t.X = t.D;                                               // TFR D,X: the screen byte
        t.Cycles += 6;
        a.LdaI(0x0F);
        t.B = colour;                                            // LDB ,S
        t.CC = Logic8(t.CC, t.B);
        t.Cycles += 4;
        if (!a.Br(Cs(t.CC)))                                     // BCS .10S
        {
            for (int i = 0; i < 4; i++) a.Aslb();                // left pixel: high nibble
            a.LdaI(0xF0);
        }
        t.A &= t.ReadRam(t.X);                                   // .10S: ANDA ,X: still blank?
        t.CC = Logic8(t.CC, t.A);
        t.Cycles += 4;
        if (!a.Br(a.Ne))
        {
            t.B |= t.ReadRam(t.X);                               // ORB ,X
            t.CC = Logic8(t.CC, t.B);
            t.Write(t.X, t.B);                                   // STB ,X
            t.Cycles += 4 + 4;
        }
        a.LdaI(0x01);                                            // .20S: read ROM again
        a.Sta(a.M(_drruc));
        a.Sta(a.M(_rruc));
        (t.A, t.B, t.X, t.Y) = (colour, savedB, x, y);           // PULS D,X,Y,PC
        t.Cycles += 13 - 5;
    }
}
