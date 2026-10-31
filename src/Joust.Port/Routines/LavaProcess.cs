using static Joust.Emulation.Alu6809;

namespace Joust.Port.Routines;

/// <summary>
/// Ken Lantz's lava (SYSTEM.ASM, LAVA): a process that raises the lava one line every 20 ticks
/// at the start of a game (NEXTLN fills a line from each edge until it meets a cliff), then keeps
/// it bubbling: each tick a random spot either swells into a bubble in the lava (with its sound)
/// or bursts on the surface (BURSTR), three or four frames apart. LAVA's own entry stays
/// original; every other label and resume point is native.
/// </summary>
public sealed class LavaProcess
{
    public enum L
    {
        NextLine, Line, LeftLoop, LeftEnd, RightLoop, RightEnd, Special,
        Check, Bubble, Bubble2, Bubble3, Burster, Burst2, Burst3, Burst4,
    }

    // STB ,X / LEAX $100,X / CMPX # / BHI / LDA ,X / ANDA # / BEQ, and STB ,X / LEAX / LDA ,X / BEQ.
    private const int LeftCycles = 4 + 8 + 4 + 3 + 4 + 2 + 3, RightCycles = 4 + 8 + 4 + 3;

    private const byte Lava = 0x44, LavaLeft = 0x40, LavaRight = 0x04;  // CLR3 in both, left, right nybble

    private readonly Scheduler _scheduler;
    private readonly SoundQueue _sound;
    private readonly int _pFrame;
    private readonly ushort _saFrm2, _saFram, _lxPos2, _drruc, _rruc, _snBub, _random, _vRand, _snd, _vNapTpc;
    private readonly Dictionary<L, ushort> _address = [];

    public LavaProcess(Symbols s, Scheduler scheduler, SoundQueue sound)
    {
        _scheduler = scheduler;
        _sound = sound;
        _pFrame = s["PFRAME"];
        _saFrm2 = s["SAFRM2"]; _saFram = s["SAFRAM"]; _lxPos2 = s["LXPOS2"];
        _drruc = s["DRRUC"]; _rruc = s["RRUC"]; _snBub = s["SNBUB"];
        _random = s["RANDOM"]; _vRand = s["VRAND"]; _snd = s["SND"]; _vNapTpc = s["VNAPTPC"];

        // Resume points from instruction sizes: a variable is 2 bytes direct / 3 extended; n,R is
        // 2 bytes with no or a 5-bit offset, 3 with 8 bits, 4 with 16; PCNAP is 5.
        int V(ushort v) => Symbols.AddressCost(v) == 0 ? 2 : 3;
        int I(int offset) => offset is >= -16 and <= 15 ? 2 : offset is >= -128 and <= 127 ? 3 : 4;
        ushort check = s["CHECK"], burster = s["BURSTR"];
        _address[L.NextLine] = s["NEXTLN"];
        _address[L.Line] = (ushort)(s["NEXTLN"] + 5);
        _address[L.LeftLoop] = s["NEXTLN.1S"];
        _address[L.LeftEnd] = s["SP1"];
        _address[L.RightLoop] = s["SP1.1S"];
        _address[L.RightEnd] = s["SP2"];
        _address[L.Special] = s["SPECIAL"];
        _address[L.Check] = check;
        _address[L.Bubble] = (ushort)(check + V((ushort)(_saFrm2 + 1)) + V(_saFram) + 2 + 5);
        int swell = _address[L.Bubble] + V(_vRand) + I(_pFrame - 1) + 2 + V(_lxPos2) + 2 + 2;
        _address[L.Bubble2] = (ushort)(swell + I(_pFrame - 2) + 2 + 2 + V((ushort)(_saFrm2 + 1)) + 1 + 2 + I(_pFrame + 4)
                                       + 2 + 2 + 3 + V(_snd) + 5);
        _address[L.Bubble3] = (ushort)(_address[L.Bubble2] + I(_pFrame + 4) + 2 + I(0) + I(0x100) + 2 + I(-1) + I(1) + 5);
        _address[L.Burster] = burster;
        _address[L.Burst2] = (ushort)(burster + V((ushort)(_saFrm2 + 1)) + 1 + I(_pFrame + 4) + 2 + I(1) + 5);
        _address[L.Burst3] = (ushort)(_address[L.Burst2] + I(_pFrame + 4) + 2 + I(1) + I(0) + I(-1) + 5);
        _address[L.Burst4] = (ushort)(_address[L.Burst3] + I(_pFrame + 4) + 2 + I(0) + I(-0x101) + I(0xFF) + I(-1) + 5);
    }

    public ushort Address(L label) => _address[label];

    public IReadOnlyDictionary<L, ushort> Addresses => _address;

    /// <summary>Runs from <paramref name="start"/> to the next nap; null to hand the call back.</summary>
    public ushort? Run(Tx t, L start)
    {
        var label = start;
        int startCycles = t.Cycles;
        while (true)
        {
            var mark = t.Mark();
            var (next, exit) = Step(t, new Asm(t), label);
            if (!t.CanRun(0))
            {
                t.Rollback(mark);                                // this step could be interrupted
                return t.Cycles == startCycles ? null : _address[label];
            }
            if (exit != null) return t.Cycles == startCycles ? null : exit;   // a loop with no pass that fits: original
            label = next;
        }
    }

    private (L, ushort?) Step(Tx t, Asm a, L label)
    {
        switch (label)
        {
            case L.NextLine:
                return (default, Nap(t, a, 20, L.Line));         // long enough for the cliffs to be in place

            case L.Line:
                a.Ldx(a.M(_saFrm2));                             // the line and where it starts
                a.LdbI(Lava);
                a.Lda(a.M(_drruc));                              // look at the screen
                a.AndaI(~0x01);
                a.Sta(a.M(_drruc));                              // this first: interrupts use this bit
                a.Sta(a.M(_rruc));
                return (L.LeftLoop, null);

            case L.LeftLoop:                                     // .1S: lava in from the left edge
            {
                int k = 0;
                while (t.CanRun(LeftCycles))
                {
                    k++;
                    a.Stb(a.X(0));
                    a.Leax(0x100);
                    a.CmpxI(0x9600);
                    if (a.Br(a.Hi)) return (L.Special, null);    // past the right edge
                    LdaScreen(t);
                    a.AndaI(0xEE);
                    if (!a.Br(a.Eq)) return (L.LeftEnd, null);   // a cliff
                }
                return k == 0 ? (default, _address[L.LeftLoop]) : (L.LeftLoop, null);
            }

            case L.LeftEnd:
                a.BitaI(0xF0);                                   // SP1: a pixel left over?
                if (!a.Br(a.Ne))
                {
                    a.OraI(LavaLeft);
                    a.Sta(a.X(0));
                }
                a.Ldd(a.M(_saFrm2));                             // .2S: the same in from the right
                a.LdaI(0x96);
                TfrDX(t);
                a.LdbI(Lava);
                return (L.RightLoop, null);

            case L.RightLoop:
            {
                int k = 0;
                while (t.CanRun(RightCycles))
                {
                    k++;
                    a.Stb(a.X(0));
                    a.Leax(-0x100);
                    LdaScreen(t);
                    if (!a.Br(a.Eq)) return (L.RightEnd, null);  // a cliff
                }
                return k == 0 ? (default, _address[L.RightLoop]) : (L.RightLoop, null);
            }

            case L.RightEnd:
                a.BitaI(0x0F);                                   // SP2: a nybble left over?
                if (!a.Br(a.Ne))
                {
                    a.OraI(LavaRight);
                    a.Sta(a.X(0));
                }
                return (L.Special, null);

            case L.Special:
                a.Lda(a.M(_drruc));                              // done with the screen: ROM back on
                a.OraI(0x01);
                a.Sta(a.M(_drruc));
                a.Sta(a.M(_rruc));
                a.Dec(a.M((ushort)(_saFrm2 + 1)));               // up one line
                return (L.Check, null);

            case L.Check:
                a.Ldb(a.M((ushort)(_saFrm2 + 1)));
                a.Cmpb(a.M(_saFram));
                if (a.Br(a.Ge)) return (L.NextLine, null);       // not up to its level yet
                return (default, Nap(t, a, 1, L.Bubble));

            case L.Bubble:
            {
                t.Cycles += 7 + Symbols.AddressCost(_vRand) + 4; // JSR VRAND / JMP RANDSYS
                Transporter.Random(t, _random);
                t.Cycles += 5;
                a.Sta(a.U(_pFrame - 1));                         // pseudo-random bubble location
                a.AndaI(0x0F);
                a.Ldx(a.M(_lxPos2));
                t.A = t.Read(t.X + (sbyte)t.A);                  // LDA A,X
                t.CC = Logic8(t.CC, t.A);
                t.Cycles += 5;
                if (a.Br(!Cs(t.CC))) return (L.Burster, null);
                a.Ldb(a.U(_pFrame - 2));                         // a bubble inside the lava
                a.AndbI(0x0F);
                t.B |= 0x02;                                     // ORB #2: stays in the lava
                t.CC = Logic8(t.CC, t.B);
                t.Cycles += 2;
                a.Addb(a.M((ushort)(_saFrm2 + 1)));
                a.Incb();
                TfrDX(t);
                a.Stx(a.U(_pFrame + 4));
                a.LdaI(LavaLeft);
                a.Sta(a.X(0));
                a.LdxI(_snBub);                                  // the sound a bubble makes
                t.Cycles += 7 + Symbols.AddressCost(_snd);
                _sound.Run(t);
                t.Cycles += 5;
                return (default, Nap(t, a, 8, L.Bubble2));
            }

            case L.Bubble2:
                a.Ldx(a.U(_pFrame + 4));
                a.LdaI(LavaRight);
                a.Sta(a.X(0));
                a.Sta(a.X(0x100));
                a.LdaI(LavaLeft);
                a.Sta(a.X(-1));
                a.Sta(a.X(1));
                return (default, Nap(t, a, 8, L.Bubble3));

            case L.Bubble3:
                a.Ldx(a.U(_pFrame + 4));
                a.LdaI(Lava);
                a.Sta(a.X(-1));
                a.Sta(a.X(0));
                a.Sta(a.X(1));
                a.Sta(a.X(0x100));
                a.LdaI(5);                                       // roll the height of the bubbles
                a.Adda(a.U(_pFrame - 2));
                a.Sta(a.U(_pFrame - 2));
                a.Bra();
                return (L.Check, null);

            case L.Burster:
                a.Ldb(a.M((ushort)(_saFrm2 + 1)));               // a bubble bursting on the surface
                a.Incb();
                a.Std(a.U(_pFrame + 4));
                TfrDX(t);
                a.Clr(a.X(1));
                return (default, Nap(t, a, 8, L.Burst2));

            case L.Burst2:
                a.Ldx(a.U(_pFrame + 4));
                a.LdbI(Lava);
                a.Stb(a.X(1));
                a.Clr(a.X(0));
                a.Stb(a.X(-1));
                return (default, Nap(t, a, 8, L.Burst3));

            case L.Burst3:
                a.Ldx(a.U(_pFrame + 4));
                a.LdbI(Lava);
                a.Stb(a.X(0));
                a.Stb(a.X(-0x101));
                a.Stb(a.X(0xFF));
                a.Clr(a.X(-1));
                return (default, Nap(t, a, 8, L.Burst4));

            default:                                             // Burst4
                a.Ldx(a.U(_pFrame + 4));
                a.Clr(a.X(-0x101));
                a.Clr(a.X(0xFF));
                a.Jmp();
                return (L.Check, null);
        }
    }

    /// <summary>LDA ,X with the RAM read latch selected.</summary>
    private static void LdaScreen(Tx t)
    {
        t.A = t.ReadRam(t.X);
        t.CC = Logic8(t.CC, t.A);
        t.Cycles += 4;
    }

    private static void TfrDX(Tx t)
    {
        t.X = t.D;
        t.Cycles += 6;
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
