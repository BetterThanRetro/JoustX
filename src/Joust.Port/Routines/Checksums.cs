using static Joust.Emulation.Alu6809;

namespace Joust.Port.Routines;

/// <summary>
/// The anti-tamper checksums (JOUSTRV4.ASM, JZAP and KZAP).
/// <list type="bullet">
/// <item>JZAP (once per game start): sums every byte from $8FFF down through the address wrap to
///   $D000 (about 48 KB); a wrong sum quietly bumps LXPOS2+1. Its loop (ADDA ,-X / CMPX / BNE)
///   runs natively in chunks that end before the next interrupt edge, leaving the loop's state in
///   the registers, so the interrupt still arrives on the same instruction.</item>
/// <item>KZAP (a background process): sums one byte per tick from PRAM up to $7FFF, then checks
///   against KZAP1 and starts over. A wrong sum corrupts a random RAM byte (RADDR, left to the
///   original).</item>
/// </list>
/// </summary>
public sealed class Checksums
{
    private const int LoopCycles = 6 + 4 + 3;                    // ADDA ,-X / CMPX #$D000 / BNE

    public enum L { KzapStart, KzapNap, KzapFailed }

    private readonly Scheduler _scheduler;
    private readonly int _pRam;
    private readonly ushort _kzap1, _raddr, _vNapTpc;
    private readonly Dictionary<L, ushort> _address = [];

    public Checksums(Symbols s, Scheduler scheduler)
    {
        _scheduler = scheduler;
        _pRam = s["PRAM"];
        _kzap1 = s["KZAP1"]; _raddr = s["RADDR"]; _vNapTpc = s["VNAPTPC"];
        JzapLoop = s["JZAP.1S"];
        int I(int offset) => offset is >= -16 and <= 15 ? 2 : 3;
        _address[L.KzapStart] = s["KZAP"];
        _address[L.KzapNap] = (ushort)(s["KZAP.1S"] + 5);
        // after JSR RADDR: LDX PRAM,U / LDA ,X++ / ADDA / STA / STX / BPL / CMPA KZAP1 / BEQ / JSR
        _address[L.KzapFailed] = (ushort)(_address[L.KzapNap] + I(_pRam) + 2 + I(_pRam + 2) * 2 + I(_pRam)
                                          + 2 + 3 + 2 + 3);
    }

    /// <summary>JZAP.1S, the head of JZAP's summing loop.</summary>
    public ushort JzapLoop { get; }

    public ushort Address(L label) => _address[label];

    /// <summary>
    /// At JZAP.1S: run as many iterations as fit before the next interrupt edge. Returns the loop
    /// head (more to do) or the instruction after it (done), or null if not one iteration fits.
    /// </summary>
    public ushort? Sum(Tx t)
    {
        int k = 0;
        while (true)
        {
            if (!t.CanRun(LoopCycles)) break;
            t.X--;                                               // ADDA ,-X
            t.A = Add8(ref t.CC, t.A, t.Read(t.X), withCarry: false);
            Sub16(ref t.CC, t.X, 0xD000);                        // CMPX #$D000
            t.Cycles += LoopCycles;
            k++;
            if (t.X == 0xD000) return (ushort)(JzapLoop + 2 + 3 + 2);   // BNE falls through
        }
        return k == 0 ? null : JzapLoop;
    }

    // ------------------------------------------------------------------ KZAP process

    public ushort? Run(Tx t, L start)
    {
        var a = new Asm(t);
        var mark = t.Mark();
        ushort exit = start switch
        {
            L.KzapStart => Restart(t, a),
            L.KzapNap => NextByte(t, a),
            _ => AfterFailure(t, a),
        };
        if (!t.CanRun(0))
        {
            t.Rollback(mark);
            return null;
        }
        return exit;
    }

    /// <summary>KZAP: start over at PRAM = 0 with a zero sum.</summary>
    private ushort Restart(Tx t, Asm a)
    {
        a.Clr(a.U(_pRam));
        a.Clr(a.U(_pRam + 1));
        a.Clr(a.U(_pRam + 2));
        return Nap(t, a);
    }

    /// <summary>After the nap: add the next byte; at $8000 compare the sum.</summary>
    private ushort NextByte(Tx t, Asm a)
    {
        a.Ldx(a.U(_pRam));
        t.A = t.Read(t.X);                                       // LDA ,X++
        t.X += 2;
        t.CC = Logic8(t.CC, t.A);
        t.Cycles += 4 + 3;
        t.A = Add8(ref t.CC, t.A, t.Read(t.U + _pRam + 2), withCarry: false);
        t.Cycles += 4 + Symbols.IndexCost(_pRam + 2);
        a.Sta(a.U(_pRam + 2));
        a.Stx(a.U(_pRam));
        if (a.Br(a.Pl)) return Nap(t, a);                        // not yet at $8000
        a.Cmpa(a.M(_kzap1));
        if (a.Br(a.Eq)) return Restart(t, a);                    // the sum is right
        t.Cycles += 8;                                           // JSR RADDR: tamper response (original)
        t.Push16(_address[L.KzapFailed]);
        return _raddr;
    }

    // ------------------------------------------------------------------ cliff table loops

    /// <summary>
    /// WCLFEW.22S / .23S (a cliff appears): for each byte of the landing (or collision) table,
    /// replace the cliff's bit (on the stack) with the bit from the ROM copy (Y). Runs whole
    /// iterations up to the next interrupt edge; returns the loop head or the instruction after it.
    /// </summary>
    public ushort? MergeBits(Tx t, ushort loopHead, ushort end)
    {
        const int iteration = 4 + 2 + 4 + 4 + 4 + 6 + 4 + 6 + 4 + 3;
        int k = 0;
        while (t.CanRun(iteration))
        {
            byte bit = t.Read(t.S);
            t.A = Logic(ref t.CC, Com8(ref t.CC, bit));          // LDA ,S / COMA
            t.A &= t.Read(t.X);                                  // ANDA ,X
            t.CC = Logic8(t.CC, t.A);
            t.Write(t.X, t.A);                                   // STA ,X
            t.A = (byte)(bit & t.Read(t.Y));                     // LDA ,S / ANDA ,Y+
            t.Y++;
            t.A = Add8(ref t.CC, t.A, t.Read(t.X), withCarry: false);   // ADDA ,X
            t.Write(t.X, t.A);                                   // STA ,X+
            t.CC = Logic8(t.CC, t.A);
            t.X++;
            Sub16(ref t.CC, t.X, end);                           // CMPX #end
            t.Cycles += iteration;
            k++;
            if (!Cs(t.CC)) return (ushort)(loopHead + 20);       // BLO falls through
        }
        return k == 0 ? null : loopHead;
    }

    /// <summary>CLRBIT (a cliff vanishes): AND every byte of a table with A (Y bytes); RTS at the end.</summary>
    public ushort? ClearBits(Tx t, ushort loopHead)
    {
        const int iteration = 6 + 4 + 6 + 5 + 3;
        int k = 0;
        while (t.CanRun(iteration))
        {
            t.B = (byte)(t.A & t.Read(t.X));                     // TFR A,B / ANDB ,X
            t.CC = Logic8(t.CC, t.B);
            t.Write(t.X, t.B);                                   // STB ,X+
            t.X++;
            t.Y--;                                               // LEAY -1,Y
            t.CC = Set(t.CC, Z, t.Y == 0);
            t.Cycles += iteration;
            k++;
            if (t.Y == 0) return (ushort)(loopHead + 10);        // BNE falls through to the RTS
        }
        return k == 0 ? null : loopHead;
    }

    private static byte Logic(ref byte cc, byte value)
    {
        cc = Logic8(cc, value);
        return value;
    }

    /// <summary>After RADDR: BRA KZAP.</summary>
    private ushort AfterFailure(Tx t, Asm a)
    {
        a.Bra();
        return Restart(t, a);
    }

    /// <summary>.1S: PCNAP 1.</summary>
    private ushort Nap(Tx t, Asm a)
    {
        a.LdaI(1);
        t.Cycles += 7 + Symbols.AddressCost(_vNapTpc) + 4;
        t.Push16(_address[L.KzapNap]);
        return _scheduler.Nap(t, fromJsr: true) ?? throw new InvalidOperationException("nap without a continuation");
    }
}
