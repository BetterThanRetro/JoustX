using static Joust.Emulation.Alu6809;

namespace Joust.Port.Routines;

/// <summary>
/// CYCLE (T12REV3.ASM, started through VCYCLE): the process that cycles the colours of the
/// high-score table. It keeps eight colours in PRAM; every 4 ticks the highlighted one (white,
/// $E8) moves along, and at the halfway point the list rotates by one. The colours are copied to
/// RAMCOL+8.., which the interrupt handler loads into the palette.
/// </summary>
public sealed class ColorCycle
{
    public enum L { Start, Tick }

    private readonly Scheduler _scheduler;
    private readonly int _pram, _timX, _image, _dist;
    private readonly ushort _clrTab, _ramCol, _vNapTpc;
    private readonly Dictionary<L, ushort> _address = [];

    public ColorCycle(Symbols s, Scheduler scheduler)
    {
        _scheduler = scheduler;
        _pram = s["PRAM"]; _timX = s["PTIMX"]; _image = s["PIMAGE"]; _dist = s["PDIST"];
        _clrTab = s["CLRTAB"]; _ramCol = s["RAMCOL"]; _vNapTpc = s["VNAPTPC"];
        _address[L.Start] = s["CYCLE"];
        _address[L.Tick] = (ushort)(s["CYCLE.2S"] + 5);          // after PCNAP 4
    }

    public ushort Address(L label) => _address[label];

    public ushort? Run(Tx t, L start)
    {
        var a = new Asm(t);
        var mark = t.Mark();
        if (start == L.Start) Setup(t, a);
        else Tick(t, a);
        ushort exit = Nap(t, a);
        if (!t.CanRun(0))
        {
            t.Rollback(mark);
            return null;
        }
        return exit;
    }

    /// <summary>CYCLE: copy the eight colours of CLRTAB into PRAM and start with the first.</summary>
    private void Setup(Tx t, Asm a)
    {
        a.LdaI(_pram);
        a.LdxI(_clrTab);
        a.Ldb(a.X(0));
        a.Stb(a.U(_image));
        do
        {
            LdbXPlus(t);                                         // .1S
            StbAU(t);
            a.Inca();
            a.CmpaI(_pram + 8);
        }
        while (a.Br(a.Ne));
        a.Clra();
        a.Sta(a.U(_timX));
        a.LeayU(_pram + 7);
        a.Sty(a.U(_dist));
    }

    /// <summary>.2S after its nap: restore the last colour, move the highlight on, rotate at the halfway point.</summary>
    private void Tick(Tx t, Asm a)
    {
        a.LdbI(_pram);
        a.Addb(a.U(_timX));
        a.Lda(a.U(_image));
        StaBU(t);                                                // put the highlighted colour back
        a.Lda(a.U(_timX));
        a.Inca();
        a.CmpaI(0x08);
        if (!a.Br(a.Ne)) a.Clra();
        a.CmpaI(0x04);                                           // .4S
        if (!a.Br(a.Ne))
        {
            t.X = (ushort)(t.U + _pram);                         // LEAX PRAM,U
            t.CC = Set(t.CC, Z, t.X == 0);
            t.Cycles += 4 + Symbols.IndexCost(_pram);
            LdbXPlus(t);
            a.PshsB();
            do
            {
                LdbXPlus(t);                                     // .6S: rotate the list by one
                a.Stb(a.X(-2));
                a.Cmpx(a.U(_dist));
            }
            while (a.Br(a.Le));
            a.PulsB();
            a.Stb(a.X(-1));
        }
        a.Sta(a.U(_timX));                                       // .7S
        a.AddaI(_pram);
        t.B = t.Read(t.U + (sbyte)t.A);                          // LDB A,U: remember this colour
        t.CC = Logic8(t.CC, t.B);
        t.Cycles += 5;
        a.Stb(a.U(_image));
        a.LdbI(0xE8);                                            // and show it white
        StbAU(t);
        a.LdxI(_ramCol + 8);
        a.LeayU(_pram);
        do
        {
            t.A = t.Read(t.Y);                                   // .3S: LDA ,Y+ / STA ,X+
            t.Y++;
            t.CC = Logic8(t.CC, t.A);
            t.Write(t.X, t.A);
            t.X++;
            t.Cycles += 6 + 6;
            a.CmpxI(_ramCol + 15);
        }
        while (a.Br(a.Le));
        a.Bra();                                                 // BRA .2S
    }

    private static void LdbXPlus(Tx t)
    {
        t.B = t.Read(t.X);
        t.X++;
        t.CC = Logic8(t.CC, t.B);
        t.Cycles += 6;
    }

    private static void StbAU(Tx t)
    {
        t.Write(t.U + (sbyte)t.A, t.B);
        t.CC = Logic8(t.CC, t.B);
        t.Cycles += 5;
    }

    private static void StaBU(Tx t)
    {
        t.Write(t.U + (sbyte)t.B, t.A);
        t.CC = Logic8(t.CC, t.A);
        t.Cycles += 5;
    }

    /// <summary>.2S: PCNAP 4.</summary>
    private ushort Nap(Tx t, Asm a)
    {
        a.LdaI(4);
        t.Cycles += 7 + Symbols.AddressCost(_vNapTpc) + 4;
        t.Push16(_address[L.Tick]);
        return _scheduler.Nap(t, fromJsr: true) ?? throw new InvalidOperationException("nap without a continuation");
    }
}
