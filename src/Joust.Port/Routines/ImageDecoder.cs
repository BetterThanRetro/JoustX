using static Joust.Emulation.Alu6809;

namespace Joust.Port.Routines;

/// <summary>
/// The compacted-image decoder (SYSTEM.ASM, UNCOM): a bit stream of run lengths and 3-bit colour
/// nibbles, plotted by WRPIX straight into video RAM (it reads the screen, so the RAM read latch is
/// selected for each run). Runs natively from UNCLP (decode the next code word) and WRPIX.33S (the
/// next pixel of a run), one code word or pixel at a time up to the next interrupt edge, stopping
/// at either label. UNCOM's own entry stays original.
/// </summary>
public sealed class ImageDecoder(Symbols s)
{
    public enum L { Decode, Run }

    private readonly ushort _decode = s["UNCLP"], _run = s["WRPIX.33S"];
    private readonly ushort _drruc = s["DRRUC"], _rruc = s["RRUC"];

    /// <summary>UNCLP.15S (BCC .10S), followed by the BSR REST calls.</summary>
    private readonly ushort _lengthDone = s["UNCLP.15S"];

    public ushort Address(L label) => label == L.Decode ? _decode : _run;

    public ushort? Run(Tx t, L start)
    {
        var a = new Asm(t);
        L at = start;
        bool progress = false;
        while (true)
        {
            var mark = t.Mark();
            (L next, ushort? exit) = at == L.Decode ? Decode(t, a) : Pixel(t, a);
            if (!t.CanRun(0))
            {
                t.Rollback(mark);
                return progress ? Address(at) : null;
            }
            progress = true;
            if (exit != null) return exit;
            at = next;
        }
    }

    /// <summary>UNCLP: read a code word; end the line, end the image, or start a run.</summary>
    private (L, ushort?) Decode(Tx t, Asm a)
    {
        a.Ldb(a.S(0));                                           // the compacted bits left
        a.Clra();
        do
        {
            a.Inca();                                            // .10S: one more length bit
            NextBit(t, a);
        }
        while (a.Br(!Cs(t.CC)));                                 // .15S: BCC .10S
        Rest(t, a, (ushort)(_lengthDone + 2 + 2));
        a.SubaI(2);
        a.Sta(a.S(1));                                           // the run length
        a.LdaI(3);
        Rest(t, a, (ushort)(_lengthDone + 2 + 2 + 2 + 2 + 2 + 2)); // the colour
        a.Stb(a.S(0));
        a.Ldb(a.S(1));
        if (!a.Br(a.Ne))
        {
            a.AndaI(0x07);
            if (a.Br(a.Eq))
            {
                t.D = t.Read16(t.S);                             // .25S: PULS D,X,Y,PC
                t.X = t.Read16(t.S + 2);
                t.Y = t.Read16(t.S + 4);
                ushort pc = t.Read16(t.S + 6);
                t.S += 8;
                t.Cycles += 5 + 8;
                return (L.Decode, pc);
            }
            a.Ldx(a.S(2));                                       // end of line: back to its start
            a.Leay(1);
            a.Bra();
            return (L.Decode, null);
        }
        a.AndaI(0x07);                                           // .30S
        if (!a.Br(a.Eq)) a.AddaI(8 - 1);
        t.S -= 4;                                                // WRPIX: PSHS D,Y
        t.Write16(t.S, t.D);
        t.Write16(t.S + 2, t.Y);
        t.Cycles += 5 + 4;
        a.Lda(a.M(_drruc));                                      // read the screen
        a.AndaI(~0x01);
        a.Sta(a.M(_drruc));
        a.Sta(a.M(_rruc));
        return (L.Run, null);
    }

    /// <summary>WRPIX.33S: OR the colour into the pixel at X,Y; at the end of the run read ROM again.</summary>
    private (L, ushort?) Pixel(Tx t, Asm a)
    {
        t.D = t.X;                                               // TFR X,D
        t.Cycles += 6;
        a.Rora();
        a.Rorb();                                                // byte column; carry = right pixel
        a.TfrBA();
        a.Ldb(a.S(3));                                           // the row
        t.Y = t.D;                                               // TFR D,Y
        t.Cycles += 6;
        a.Lda(a.S(0));                                           // the colour nibble
        if (!a.Br(Cs(t.CC)))
            for (int i = 0; i < 4; i++) a.Asla();                // left pixel: high nibble
        t.A |= t.ReadRam(t.Y);                                   // .10S: ORA ,Y
        t.CC = Logic8(t.CC, t.A);
        t.Cycles += 4;
        t.Write(t.Y, t.A);                                       // STA ,Y
        t.Cycles += 4;
        a.Leax(1);
        a.Dec(a.S(1));
        if (a.Br(a.Ne)) return (L.Run, null);
        a.Lda(a.M(_drruc));                                      // read ROM again
        a.OraI(0x01);
        a.Sta(a.M(_drruc));
        a.Sta(a.M(_rruc));
        t.D = t.Read16(t.S);                                     // PULS D,Y
        t.Y = t.Read16(t.S + 2);
        t.S += 4;
        t.Cycles += 5 + 4;
        a.Bra();
        return (L.Decode, null);
    }

    /// <summary>BSR REST: shift A more bits (A = how many) of the stream into A, after a leading 1.</summary>
    private static void Rest(Tx t, Asm a, ushort returnAddress)
    {
        t.Cycles += 7;                                           // BSR REST
        t.Push16(returnAddress);
        t.S--;                                                   // PSHS A
        t.Write(t.S, t.A);
        t.Cycles += 6;
        a.LdaI(1);
        do
        {
            NextBit(t, a);                                       // .10S
            a.Rola();                                            // .15S
            a.Dec(a.S(0));
        }
        while (a.Br(a.Ne));
        t.S++;                                                   // LEAS 1,S
        t.Cycles += 5;
        a.Rts();
    }

    /// <summary>ASLB / BNE / LDB ,U+ / ASLB / INCB: the next stream bit into carry.</summary>
    private static void NextBit(Tx t, Asm a)
    {
        a.Aslb();
        if (a.Br(a.Ne)) return;
        t.B = t.ReadRom(t.U);                                    // LDB ,U+ (the image is in ROM)
        t.U++;
        t.CC = Logic8(t.CC, t.B);
        t.Cycles += 6;
        a.Aslb();
        a.Incb();
    }
}
