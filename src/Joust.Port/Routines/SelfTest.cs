using static Joust.Emulation.Alu6809;

namespace Joust.Port.Routines;

/// <summary>
/// The power-on self-test's loops (T12REV3.ASM), which run with interrupts masked and feed the
/// watchdog as they go. Each runs whole iterations while they fit before the next interrupt edge
/// and stops at its loop head, so the machine's timing is unchanged.
/// <list type="bullet">
/// <item>RAMTST: fill $0000-$BFFF from a pseudo-random sequence (RAM3), then read it back and
///   compare (RAM7). A mismatch (RERROR) and the front-door diagnostics' advance-button check
///   (DP = $FF) are left to the original.</item>
/// <item>ROMTST: sum each ROM part (ROM1).</item>
/// <item>DELAY: the busy-wait counting X down from $5800, A times (DELA2), then JMP ,Y.</item>
/// </list>
/// </summary>
public sealed class SelfTest(Symbols s)
{
    private const int FillMax = 21 + 8 + 8 + 2 + 3 + 20 + 8 + 4 + 3 + 6 + 3;   // (+ TFR U,D / LDX #0)
    private const int CheckMax = 21 + 10 + 3 + 8 + 2 + 3 + 20 + 8 + 4 + 3;
    private const int SumCycles = 6 + 5 + 8 + 5 + 8 + 3;
    private const int DelayMax = 5 + 2 + 5 + 4 + 3 + 2 + 3 + 3;

    private readonly ushort _wdog = s["WDOG"];
    private readonly byte _wdata = (byte)s["WDATA"];
    private readonly ushort _fill = s["RAM3"], _check = s["RAM7"], _checked = (ushort)(s["RAM17"] + 2 + 3 + 2);
    private readonly ushort _sum = s["ROM1"], _delay = s["DELA2"];

    /// <summary>RAM3: write the next pseudo-random word; after $BFFF go on to the read-back (RAM7).</summary>
    public ushort? Fill(Tx t)
    {
        var a = new Asm(t);
        int k = 0;
        while (t.CanRun(FillMax) && !(PageEnd(t.X) && t.DP == 0xFF))
        {
            NextSeed(a);
            t.Write16(t.X, t.D);                                 // STD ,X++
            t.X += 2;
            t.CC = Logic16(t.CC, t.D);
            t.Cycles += 8;
            Watchdog(t, a);
            Sub16(ref t.CC, t.X, 0xC000);                        // CMPX #$C000
            t.Cycles += 4;
            k++;
            if (!a.Br(a.Ne))
            {
                t.D = t.U;                                       // TFR U,D: the seed again
                t.Cycles += 6;
                a.LdxI(0);
                return Check(t) ?? _check;
            }
        }
        return k == 0 ? null : _fill;
    }

    /// <summary>RAM7: compare the next word with the sequence; after $BFFF leave at TFR D,U.</summary>
    public ushort? Check(Tx t)
    {
        var a = new Asm(t);
        int k = 0;
        while (t.CanRun(CheckMax) && !(PageEnd(t.X) && t.DP == 0xFF))
        {
            var (d, cc, cycles) = (t.D, t.CC, t.Cycles);
            NextSeed(a);
            Sub16(ref t.CC, t.D, t.Read16(t.X));                 // CMPD ,X++
            t.X += 2;
            t.Cycles += 10;
            if (a.Br(a.Ne))
            {
                (t.D, t.CC, t.Cycles) = (d, cc, cycles);         // RERROR: left to the original
                t.X -= 2;
                break;
            }
            Watchdog(t, a);
            Sub16(ref t.CC, t.X, 0xC000);
            t.Cycles += 4;
            k++;
            if (!a.Br(a.Ne)) return _checked;
        }
        return k == 0 ? null : _check;
    }

    private static bool PageEnd(ushort x) => (byte)(x + 2) == 0;

    /// <summary>RAM3-RAM5 (RAM7-RAM9): one step of the shift-register sequence in D.</summary>
    private static void NextSeed(Asm a)
    {
        a.Comb();
        a.BitbI(9);
        if (!a.Br(a.Ne))
        {
            a.Comb();
            a.Rora();
            a.Rorb();
            a.Bra();
            return;
        }
        a.Comb();
        a.BitbI(9);
        if (!a.Br(a.Ne))
        {
            a.Rora();
            a.Rorb();
            a.Bra();
            return;
        }
        a.Lsra();
        a.Rorb();
    }

    /// <summary>EXG X,D / TSTB / BNE: every 256 bytes feed the watchdog (DP is not $FF here); EXG X,D.</summary>
    private void Watchdog(Tx t, Asm a)
    {
        ushort d = t.D;
        t.D = t.X;                                               // EXG X,D
        t.X = d;
        t.Cycles += 8;
        a.Tstb();
        if (!a.Br(a.Ne))
        {
            a.LdbI(_wdata);
            a.Stb(a.M(_wdog));
            t.B = t.DP;                                          // TFR DP,B
            t.Cycles += 6;
            a.CmpbI(0xFF);
            a.Br(true);                                          // BNE RAM6C
            a.Clrb();
        }
        d = t.D;
        t.D = t.X;                                               // EXG X,D
        t.X = d;
        t.Cycles += 8;
    }

    /// <summary>ROM1: add the next byte of the part; at the part's end leave at CMPB 1,X.</summary>
    public ushort? Sum(Tx t)
    {
        var a = new Asm(t);
        int k = 0;
        while (t.CanRun(SumCycles))
        {
            t.B = Add8(ref t.CC, t.B, t.Read(t.U), withCarry: false);   // ADDB ,U+
            t.U++;
            t.Cycles += 6;
            a.Sta(a.M(_wdog));
            Sub8(ref t.CC, (byte)(t.U >> 8), t.Read(t.X + 2));   // EXG D,U / CMPA 2,X / EXG D,U
            t.Cycles += 8 + 5 + 8;
            k++;
            if (!a.Br(a.Ne)) return (ushort)(_sum + 13);
        }
        return k == 0 ? null : _sum;
    }

    /// <summary>DELA2: count X down to zero, A times (LDX #$5800 each time), then JMP ,Y.</summary>
    public ushort? Delay(Tx t)
    {
        var a = new Asm(t);
        int k = 0;
        while (t.CanRun(DelayMax))
        {
            a.Leax(-1);
            a.LdbI(_wdata);
            a.Stb(a.M(_wdog));
            a.CmpxI(0);
            k++;
            if (a.Br(a.Ne)) continue;
            a.Deca();
            if (!a.Br(a.Ne))
            {
                t.Cycles += 3;                                   // JMP ,Y
                return t.Y;
            }
            a.LdxI(0x5800);                                      // DELA1
        }
        return k == 0 ? null : _delay;
    }
}
