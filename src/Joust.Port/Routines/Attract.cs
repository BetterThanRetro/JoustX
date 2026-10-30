using static Joust.Emulation.Alu6809;

namespace Joust.Port.Routines;

/// <summary>
/// Attract mode's drawing (ATT.ASM). WRANIB plots one pixel of the Williams logo outline: X =
/// pixel column, Y = row, A = colour nibble. It selects RAM reads, sets the pixel's nibble if
/// it is still blank, and selects ROM reads again.
/// </summary>
public sealed class Attract(Symbols s)
{
    private readonly ushort _drruc = s["DRRUC"], _rruc = s["RRUC"];

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
