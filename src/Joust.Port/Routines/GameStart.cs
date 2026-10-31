using static Joust.Emulation.Alu6809;

namespace Joust.Port.Routines;

/// <summary>
/// GSTART's polling loop (JOUSTRV4.ASM, .4S): in attract mode, every tick, check whether the
/// credits changed and whether a start button is down. Only the idle round is native: when
/// either happens the call is handed back and the original starts the market or the game.
/// </summary>
public sealed class GameStart
{
    public enum L { Wait, Poll }

    private readonly Scheduler _scheduler;
    private readonly ushort _crdChg, _widgetPortA, _vNapTpc, _wait;

    public GameStart(Symbols s, Scheduler scheduler)
    {
        _scheduler = scheduler;
        _crdChg = s["CRDCHG"]; _widgetPortA = s["WPIAA"]; _vNapTpc = s["VNAPTPC"];
        _wait = s["GSTART.4S"];
    }

    public ushort Address(L label) => label == L.Wait ? _wait : (ushort)(_wait + 5);   // after PCNAP 1

    public ushort? Run(Tx t, L start)
    {
        var a = new Asm(t);
        var mark = t.Mark();
        if (start == L.Poll)
        {
            if ((sbyte)t.Read(t.Var(_crdChg)) > 0) return null;   // credits changed: original
            byte buttons = t.Context.PeekWidget(t.Mux);
            if ((buttons & 0x30) != 0) return null;              // a start button: original
            a.Lda(a.M(_crdChg));
            a.Br(a.Le);                                          // BLE .41S
            t.B = buttons;                                       // .41S: LDB WPIAA
            t.IoRead(_widgetPortA);
            t.CC = Logic8(t.CC, t.B);
            t.Cycles += 4 + Symbols.AddressCost(_widgetPortA);
            a.AndbI(0x30);
            a.Br(a.Eq);                                          // BEQ .4S
        }
        a.LdaI(1);                                               // .4S: PCNAP 1
        t.Cycles += 7 + Symbols.AddressCost(_vNapTpc) + 4;
        t.Push16(Address(L.Poll));
        ushort exit = _scheduler.Nap(t, fromJsr: true) ?? throw new InvalidOperationException("nap without a continuation");
        if (!t.CanRun(0))
        {
            t.Rollback(mark);
            return null;
        }
        return exit;
    }
}
