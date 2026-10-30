using Joust.Emulation;
using static Joust.Emulation.Alu6809;

namespace Joust.Port.Routines;

/// <summary>
/// The IRQ handler (SYSTEM.ASM, IRQ), entered on each 4 ms CB1 interrupt (lines 0, 64, 128, 192).
///
/// It feeds the watchdog, stirs the random seed and selects ROM for the blitter's reads. On the
/// line-0 interrupt it also copies the palette (RAMCOL) to colour RAM, counts minutes of game
/// time, and debounces the coin door switches. Then it plays the frame's DMA table to the
/// blitter one screen quadrant at a time, but only for quadrants the beam is not in; the
/// interrupt at the next quadrant boundary carries on. When all four quadrants are done the
/// table is freed and the other one becomes current.
///
/// The handler reads the beam counter and clears the PIA interrupt flags itself, so it only runs
/// natively when no interrupt edge arrives while it runs. Coin and advance switch presses, the
/// once-a-minute audit and anything unusual hand the call back to the original.
/// </summary>
public sealed class InterruptHandler
{
    private readonly ushort _irqStk, _wdog, _random, _drruc, _rruc, _piaA, _piaB, _cpiaB, _ramCol, _cram;
    private readonly ushort _gover, _minute, _swSlam, _swT0, _swDeb, _swTemp;
    private readonly ushort _iTbl, _isqCnt, _isqPtr, _dmaInt, _dTbl1, _dTbl2, _dma;
    private readonly byte _wData;

    public InterruptHandler(Symbols s)
    {
        _irqStk = s["IRQSTK"]; _wdog = s["WDOG"]; _random = s["RANDOM"]; _drruc = s["DRRUC"]; _rruc = s["RRUC"];
        _piaA = s["PIAA"]; _piaB = s["PIAB"]; _cpiaB = s["CPIAB"]; _ramCol = s["RAMCOL"]; _cram = s["CRAM"];
        _gover = s["GOVER"]; _minute = s["MINUTE"]; _swSlam = s["SWSLAM"]; _swT0 = s["SWT0"];
        _swDeb = s["SWDEB"]; _swTemp = s["SWTEMP"];
        _iTbl = s["ITBL"]; _isqCnt = s["ISQCNT"]; _isqPtr = s["ISQPTR"]; _dmaInt = s["DMAINT"];
        _dTbl1 = s["DTBL1"]; _dTbl2 = s["DTBL2"]; _dma = s["DMA"];
        _wData = (byte)s.Value("WDATA");
    }

    /// <summary>Runs the handler through its RTI; returns the interrupted PC, or null to hand back.</summary>
    public ushort? Run(Tx t)
    {
        var c = t.Context;
        if ((t.CC & Cpu6809.FlagI) == 0 ||c.BoardIrq.A || c.Cpu.FirqLine)
            return null;                                         // only the plain 4 ms interrupt
        var a = new Asm(t);
        byte VideoCounter() => c.VideoCounter(t.Cycles + t.StallCycles);

        a.Sts(a.M(_irqStk));
        a.LdaI(_wData);                                          // tickle the watchdog
        a.Sta(a.M(_wdog));
        a.Inc(a.M(_random));
        a.Dec(a.M((ushort)(_random + 1)));
        a.Lda(a.M(_drruc));                                      // the blitter reads ROM
        a.OraI(1);
        a.Sta(a.M(_rruc));
        t.IoRead(_piaB);                                         // LDB PIAB: clear the 4 ms flag
        t.Cycles += 5;
        t.B = VideoCounter();                                    // LDB VSCAN
        t.CC = Logic8(t.CC, t.B);
        t.Cycles += 5;
        a.AndbI(0xC0);
        if (!a.Lbr(a.Ne) && !TopOfFrame(t, a))
            return null;
        return PlayDma(t, a, VideoCounter);
    }

    /// <summary>The line-0 work: palette, game time, switch debounce. False to hand back.</summary>
    private bool TopOfFrame(Tx t, Asm a)
    {
        // Palette: PULU D,X,Y,S from RAMCOL / PSHU D,X,Y,S to colour RAM, in two halves.
        for (int half = 0; half < 2; half++)
        {
            int from = _ramCol + 8 * half;
            t.U = (ushort)from;
            t.CC = Logic16(t.CC, t.U);
            t.D = t.Read16(from);
            t.X = t.Read16(from + 2);
            t.Y = t.Read16(from + 4);
            t.S = t.Read16(from + 6);
            t.U = (ushort)(_cram + 8 * half + 8);
            t.CC = Logic16(t.CC, t.U);
            t.U -= 8;
            t.Write16(t.U, t.D);
            t.Write16(t.U + 2, t.X);
            t.Write16(t.U + 4, t.Y);
            t.Write16(t.U + 6, t.S);
            t.Cycles += 3 + 13 + 3 + 13;                         // LDU / PULU / LDU / PSHU
        }
        a.Lds(a.M(_irqStk));

        a.Lda(a.M(_gover));                                      // minutes of game time
        if (!a.Br(a.Pl))
        {
            a.Ldd(a.M(_minute));
            a.SubdI(1);
            if (!a.Br(a.Gt)) return false;                       // a minute is up: JSR AUDIT1
            a.Std(a.M(_minute));
        }

        a.Lda(a.M(_swSlam));                                     // slam switch timer
        if (!a.Br(a.Eq)) a.Dec(a.M(_swSlam));
        for (int k = 6; k >= 0; k -= 2)
        {
            a.Ldd(a.M((ushort)(_swT0 + k)));                     // shift the switch history
            a.Std(a.M((ushort)(_swT0 + k + 1)));
        }
        t.A = t.Context.PeekCoinDoor();                          // LDA PIAA (clears the CA flags)
        t.IoRead(_piaA);
        t.CC = Logic8(t.CC, t.A);
        t.Cycles += 5;
        a.Sta(a.M(_swT0));
        a.BitaI(0x40);                                           // slam switch closed?
        if (!a.Br(a.Eq))
        {
            a.LdbI(2 * 60);
            a.Stb(a.M(_swSlam));
        }
        // Debounced = T0*T1 + DEB*(T0+...+T8)
        a.Ldb(a.M(_swT0));
        for (int k = 1; k <= 8; k++)
            a.Orb(a.M((ushort)(_swT0 + k)));
        a.Andb(a.M(_swDeb));
        a.Stb(a.M(_swTemp));
        a.Anda(a.M((ushort)(_swT0 + 1)));
        a.Ora(a.M(_swTemp));
        a.Ldb(a.M(_swDeb));
        a.Sta(a.M(_swDeb));
        a.Comb();                                                // switches that just closed
        a.Andb(a.M(_swDeb));
        a.AndbI(0x3E);
        return a.Br(a.Eq);                                       // a press runs its handler: original
    }

    /// <summary>IRQ2 .. RTI: play the DMA table's quadrants the beam is clear of.</summary>
    private ushort? PlayDma(Tx t, Asm a, Func<byte> videoCounter)
    {
        a.Ldx(a.M(_iTbl));
        a.Lda(a.M(_isqCnt));                                     // last quadrant written
        a.Tst(a.X(0));
        if (a.Br(a.Eq)) return Exit(t, a);                       // the foreground is not ready
        if (!a.Br(a.Mi))
        {
            t.Write(t.X, Com8(ref t.CC, t.Read(t.X)));           // COM ,X+: table now in use
            t.X++;
            t.Cycles += 6 + 2;
            a.Stx(a.M(_isqPtr));
            a.Clra();                                            // first quadrant
        }

        while (true)
        {
            a.Sta(a.E(_isqCnt));                                 // IRQNXT
            t.A ^= videoCounter();                               // EORA >VSCAN
            t.CC = Logic8(t.CC, t.A);
            t.Cycles += 5;
            a.AndaI(0xC0);
            if (a.Br(a.Eq)) return Exit(t, a);                   // the beam is in this quadrant
            a.Ldx(a.E(_isqPtr));
            t.U = t.Read16(t.X);                                 // LDU ,X++
            t.X += 2;
            t.CC = Logic16(t.CC, t.U);
            t.Cycles += 5 + 3;
            a.Stx(a.E(_isqPtr));
            t.X = t.U;                                           // LEAX 0,U: any data?
            t.CC = Set(t.CC, Z, t.X == 0);
            t.Cycles += 4;
            if (!a.Br(a.Eq))
            {
                if (!Blit(t, a)) return null;
            }
            a.Lda(a.E(_isqCnt));                                 // IRQSKD: next quadrant
            a.AddaI(0x40);
            if (!a.Br(a.Ne)) break;
        }

        a.Ldx(a.E(_iTbl));                                       // all four done: free the table
        a.Clr(a.X(0));
        a.LdxI(_dTbl1);
        a.Com(a.E(_dmaInt));
        if (!a.Br(a.Pl)) a.LdxI(_dTbl2);
        a.Stx(a.E(_iTbl));
        return Exit(t, a);
    }

    /// <summary>
    /// DEC >CPIAB .. INC >CPIAB: the quadrant's linked list of 8-byte blitter loads, each pushed
    /// onto the DMA registers (the control byte last, which starts the blit). False to hand back.
    /// </summary>
    private bool Blit(Tx t, Asm a)
    {
        byte control = (byte)(t.Read(_cpiaB) & 0x3F);            // flags already cleared by LDB PIAB
        control = Dec(ref t.CC, control);                        // disable the 4 ms interrupt
        t.IoWrite(_cpiaB, control);
        t.Cycles += 7;
        t.IoRead(_piaB);                                         // LDA PIAB
        t.Cycles += 5;
        do
        {
            if (t.U < 0x9000) return false;                      // lists live in RAM above the bank switch
            t.S = (ushort)(_dma + 8);                            // LDS #DMA+8
            t.CC = Logic16(t.CC, t.S);
            t.Cycles += 4;
            var load = new byte[8];
            for (int i = 0; i < 8; i++)
                load[i] = t.Read(t.U + i);                       // PULU CC,D,DP,X,Y
            t.U += 8;
            (t.CC, t.A, t.B, t.DP) = (load[0], load[1], load[2], load[3]);
            t.X = (ushort)((load[4] << 8) | load[5]);
            t.Y = (ushort)((load[6] << 8) | load[7]);
            for (int i = 7; i >= 0; i--)
                t.IoWrite(_dma + i, load[i]);                    // PSHS: Y, X, DP, B, A, CC
            t.S = _dma;
            t.StallCycles += JoustMachine.BlitStallCycles(load[6] ^ 4, load[7] ^ 4, load[0]);
            t.Cycles += 13 + 13;
            t.U = t.Read16(t.U);                                 // LDU ,U: next load
            t.CC = Logic16(t.CC, t.U);
            t.Cycles += 5;
        }
        while (a.Br(a.Ne));
        a.Orcc(0xFF);                                            // masks everything again
        control = Inc(ref t.CC, control);                        // re-enable the 4 ms interrupt
        t.IoWrite(_cpiaB, control);
        t.Cycles += 7;
        return true;
    }

    /// <summary>IRQRTS: restore the stack and the bank latch, then RTI.</summary>
    private ushort? Exit(Tx t, Asm a)
    {
        a.Lds(a.E(_irqStk));
        a.Lda(a.E(_drruc));
        a.Sta(a.E(_rruc));
        if (!t.Context.EdgeFree(t.Cycles + t.StallCycles + 15))
            return null;                                         // an interrupt edge would land inside
        t.CC = t.Read(t.S);
        if ((t.CC & E) == 0) return null;                        // IRQ always stacks the entire state
        t.A = t.Read(t.S + 1);
        t.B = t.Read(t.S + 2);
        t.DP = t.Read(t.S + 3);
        t.X = t.Read16(t.S + 4);
        t.Y = t.Read16(t.S + 6);
        t.U = t.Read16(t.S + 8);
        ushort pc = t.Read16(t.S + 10);
        t.S += 12;
        t.Cycles += 15;
        return pc;
    }

    private const byte E = Cpu6809.FlagE;
}
