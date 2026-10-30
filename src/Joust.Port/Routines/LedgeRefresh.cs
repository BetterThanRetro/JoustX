using static Joust.Emulation.Alu6809;

namespace Joust.Port.Routines;

/// <summary>
/// LEDGE (JOUSTRV4.ASM): the secondary process that redraws the background cliffs whose bits are
/// set in BCKRFS, one cliff per wake-up. The big cliff (bit 7) also redraws its two side pieces,
/// a tick apart. Every entry is a resume after one of its naps.
/// </summary>
public sealed class LedgeRefresh
{
    public enum L { Idle, Left, Right }

    private readonly Scheduler _scheduler;
    private readonly int _pram, _wcDma, _wcY, _wcLenY, _wcDest, _wcSrc, _wcLen;
    private readonly ushort _bckRfs, _clif5, _dmaFree, _dmaEndWrite, _vNapTpc, _vWr1All, _noBckr;
    private readonly Dictionary<L, ushort> _address = [];

    public LedgeRefresh(Symbols s, Scheduler scheduler)
    {
        _scheduler = scheduler;
        _pram = s["PRAM"];
        _wcDma = s["WCDMA"]; _wcY = s["WCY"]; _wcLenY = s["WCLENY"]; _wcDest = s["WCDEST"]; _wcSrc = s["WCSRC"]; _wcLen = s["WCLEN"];
        _bckRfs = s["BCKRFS"]; _clif5 = s["CLIF5"]; _dmaFree = s["DMAFRE"];
        _dmaEndWrite = (ushort)(s["DMAEND"] + s["WRITE"]);
        _vNapTpc = s["VNAPTPC"]; _vWr1All = s["VWR1ALL"];
        _noBckr = s["NOBCKR"];
        // After PCNAP n (LDA # / JSR VNAPTPC, 5 bytes). Each side piece is LDY CLIF5 / LEAY n,Y /
        // BSR BCKYUP, and the right one is followed by BRA NOBCKR, just before BCKYUP. LEAY 12,Y
        // fits a 5-bit offset (2 bytes); LEAY 18,Y needs an 8-bit one (3 bytes).
        int ldy = 3 + Symbols.AddressCost(_clif5);
        ushort right = (ushort)(s["BCKYUP"] - 2 - 2 - 3 - ldy);
        _address[L.Idle] = (ushort)(_noBckr + 5);
        _address[L.Right] = right;
        _address[L.Left] = (ushort)(right - 5 - 2 - 2 - ldy);
    }

    public ushort Address(L label) => _address[label];

    public ushort? Run(Tx t, L start)
    {
        var a = new Asm(t);
        var mark = t.Mark();
        ushort exit = start switch
        {
            L.Idle => Idle(t, a),
            L.Left => Side(t, a, 2 + 6 - 2 + 6, L.Right),
            _ => Side(t, a, 2 + 6 - 2 + 6 + 6, null),
        };
        if (!t.CanRun(0))
        {
            t.Rollback(mark);
            return null;
        }
        return exit;
    }

    /// <summary>NOBCKR after its nap: find the next cliff to refresh and draw it.</summary>
    private ushort Idle(Tx t, Asm a)
    {
        a.Lda(a.M(_bckRfs));                                     // any background to refresh?
        if (a.Br(a.Eq)) return Nap(t, a, 2, L.Idle);
        a.Ldd(a.U(_pram + 1));                                   // cliff offset, cliff bit
        do
        {
            a.AddaI(2);                                          // BCKNBT: next cliff
            a.Aslb();
            if (!a.Br(a.Ne))
            {
                a.Incb();
                a.Clra();
            }
            a.Bitb(a.M(_bckRfs));                                // BCKN0: this cliff to refresh?
        }
        while (a.Br(a.Eq));
        a.Std(a.U(_pram + 1));
        a.Eorb(a.M(_bckRfs));
        a.Stb(a.M(_bckRfs));                                     // its refresh bit off
        t.Y = t.Read16(t.Read16(t.U + _pram));                   // LDY [PRAM,U]: the cliff
        t.CC = Logic16(t.CC, t.Y);
        t.Cycles += 6 + Symbols.IndirectCost(_pram);
        a.Leay(2 + 6 - 2);                                       // past collision data and full size
        CallBackground(t, a);
        a.Ldb(a.U(_pram + 2));
        if (a.Br(a.Pl)) return Nap(t, a, 2, L.Idle);             // not the big cliff
        return Nap(t, a, 1, L.Left);
    }

    /// <summary>One of the big cliff's side pieces, then nap.</summary>
    private ushort Side(Tx t, Asm a, int offset, L? next)
    {
        a.Ldy(a.M(_clif5));
        a.Leay(offset);
        CallBackground(t, a);
        if (next is { } n) return Nap(t, a, 1, n);
        a.Bra();
        return Nap(t, a, 2, L.Idle);
    }

    /// <summary>BSR BCKYUP: queue background image Y through WR1ALL (the quadrant of its bottom line).</summary>
    private void CallBackground(Tx t, Asm a)
    {
        t.Cycles += 7;
        a.Ldb(a.Y(_wcLenY));                                     // lowest line
        a.EorbI(4);
        a.Addb(a.Y(_wcY));
        t.Cycles += 7 + Symbols.AddressCost(_vWr1All) + 4;       // JSR VWR1ALL / JMP WR1ALL
        a.AndbI(0xC0);                                           // the quadrant's write list
        a.LdxI(_dmaEndWrite);
        a.Abx();
        a.Ldd(a.M(_dmaFree));
        t.Write16(t.Read16(t.X), t.D);                           // STD [,X]
        t.CC = Logic16(t.CC, t.D);
        t.Cycles += 8;
        a.AdddI(8);
        a.Std(a.X(0));
        a.AdddI(2);
        a.Ldx(a.M(_dmaFree));
        a.Std(a.M(_dmaFree));
        t.Cycles += 5;                                           // RTS
        a.LdaI(0x0A);                                            // write the background object
        a.Sta(a.X(_wcDma));
        foreach (int field in new[] { _wcDest, _wcSrc, _wcLen })
        {
            a.Ldd(a.Y(field));
            a.Std(a.X(field));
        }
        t.Cycles += 5;
    }

    /// <summary>PCNAP n: LDA #n / JSR VNAPTPC, resuming at <paramref name="resume"/>.</summary>
    private ushort Nap(Tx t, Asm a, byte ticks, L resume)
    {
        a.LdaI(ticks);
        t.Cycles += 7 + Symbols.AddressCost(_vNapTpc) + 4;
        t.Push16(_address[resume]);
        return _scheduler.Nap(t, fromJsr: true) ?? throw new InvalidOperationException("nap without a continuation");
    }
}
