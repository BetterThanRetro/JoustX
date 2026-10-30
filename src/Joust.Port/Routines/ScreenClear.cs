using static Joust.Emulation.Alu6809;

namespace Joust.Port.Routines;

/// <summary>
/// The two memory-clearing loops, which feed the watchdog as they go. Each runs whole iterations
/// while they fit before the next interrupt edge and stops at its loop head.
/// <list type="bullet">
/// <item>SCCLER.10S (the screen clear): six PSHU B,X,Y per pass, from $A000 down through the
///   address wrap to $FFEC.</item>
/// <item>INIT2.1S (start-up): clear $0000-$BF7F four bytes at a time.</item>
/// </list>
/// </summary>
public sealed class ScreenClear(Symbols s)
{
    private const int PushCycles = 6 * (5 + 5) + 5 + 5 + 3;
    private const int ClearCycles = 2 + 5 + 2 + 8 + 8 + 4 + 3;

    private readonly ushort _wdog = s["WDOG"];
    private readonly byte _wdata = (byte)s["WDATA"];
    private readonly ushort _screen = s["SCCLER.10S"], _start = s["INIT2.1S"];

    /// <summary>SCCLER.10S; at U = -20 leave at PULS D,X,Y,U,PC.</summary>
    public ushort? Screen(Tx t)
    {
        var a = new Asm(t);
        int k = 0;
        while (t.CanRun(PushCycles))
        {
            for (int i = 0; i < 6; i++)
            {
                t.U -= 5;                                        // PSHU B,X,Y
                t.Write(t.U, t.B);
                t.Write16(t.U + 1, t.X);
                t.Write16(t.U + 3, t.Y);
                t.Cycles += 5 + 5;
            }
            a.Sta(a.M(_wdog));
            Sub16(ref t.CC, t.U, 0xFFEC);                        // CMPU #-20
            t.Cycles += 5;
            k++;
            if (!a.Br(a.Ne)) return (ushort)(_screen + 21);
        }
        return k == 0 ? null : _screen;
    }

    /// <summary>INIT2.1S; at $BF80 leave at INCB.</summary>
    public ushort? Start(Tx t)
    {
        var a = new Asm(t);
        int k = 0;
        while (t.CanRun(ClearCycles))
        {
            a.LdaI(_wdata);
            a.Sta(a.M(_wdog));
            a.Clra();
            for (int i = 0; i < 2; i++)
            {
                t.Write16(t.X, t.D);                             // STD ,X++
                t.X += 2;
                t.CC = Logic16(t.CC, t.D);
                t.Cycles += 8;
            }
            a.CmpxI(0xBF80);
            k++;
            if (!a.Br(a.Lo)) return (ushort)(_start + 15);
        }
        return k == 0 ? null : _start;
    }
}
