namespace Joust.Port.Routines;

/// <summary>
/// DEATH (JOUSTRV4.ASM, "KEN LANTZ SPECIAL EFFECTS, PLAYER POOF"): the puff where a rider was
/// killed, three images over about a second (5, 8 and 12 ticks), kept on screen (X 4..288);
/// then any cliff it overlapped is redrawn and the process ends.
/// </summary>
public sealed class DeathPuff
{
    public enum L { Start, Frame2, Frame3, End }

    private readonly Ground _ground;
    private readonly Scheduler _scheduler;
    private readonly int _posX, _posY;
    private readonly ushort _poof1, _poof2, _poof3, _boxX, _boxY, _bckRefresh, _vNapTpc, _vSucide, _vWr1Cls, _vCl1Cls;
    private readonly Dictionary<L, ushort> _address = [];

    public DeathPuff(Symbols s, Ground ground, Scheduler scheduler)
    {
        _ground = ground;
        _scheduler = scheduler;
        _posX = s["PPOSX"]; _posY = s["PPOSY"];
        _poof1 = s["POOF1"]; _poof2 = s["POOF2"]; _poof3 = s["POOF3"];
        _boxX = s["BCKXTB"]; _boxY = s["BCKYTB"]; _bckRefresh = s["BCKRFS"];
        _vNapTpc = s["VNAPTPC"]; _vSucide = s["VSUCIDE"]; _vWr1Cls = s["VWR1CLS"]; _vCl1Cls = s["VCL1CLS"];

        // LDY var is 3 bytes direct / 4 extended; STD n,U 2 or 3; JSR 3; PCNAP 5.
        int Vy(ushort v) => Symbols.AddressCost(v) == 0 ? 3 : 4;
        int I(int offset) => offset is >= -16 and <= 15 ? 2 : 3;
        _address[L.Start] = s["DEATH"];
        _address[L.Frame2] = (ushort)(s["DEATH.1S"] + I(_posX) + Vy(_poof1) + 3 + 5);
        _address[L.Frame3] = (ushort)(_address[L.Frame2] + Vy(_poof1) + 3 + Vy(_poof2) + 3 + 5);
        _address[L.End] = (ushort)(_address[L.Frame3] + Vy(_poof2) + 3 + Vy(_poof3) + 3 + 5);
    }

    public ushort Address(L label) => _address[label];

    public ushort? Run(Tx t, L start)
    {
        var a = new Asm(t);
        ushort exit = start switch
        {
            L.Start => Begin(t, a),
            L.Frame2 => NextFrame(t, a, _poof1, _poof2, 8, L.Frame3),
            L.Frame3 => NextFrame(t, a, _poof2, _poof3, 12, L.End),
            _ => Finish(t, a),
        };
        return t.CanRun(0) ? exit : null;
    }

    /// <summary>Keep the puff on screen, show frame 1 for 5 ticks.</summary>
    private ushort Begin(Tx t, Asm a)
    {
        a.Ldd(a.U(_posX));
        a.CmpdI(4);                                              // let the player see the explosion
        if (!a.Br(a.Gt)) a.LddI(4);
        a.CmpdI(288);                                            // .2S
        if (!a.Br(a.Lt)) a.LddI(288);
        a.Std(a.U(_posX));                                       // .1S
        a.Ldy(a.M(_poof1));
        Image(t, write: true);
        return Nap(t, a, 5, L.Frame2);
    }

    /// <summary>Erase the last frame, show the next one.</summary>
    private ushort NextFrame(Tx t, Asm a, ushort last, ushort next, byte ticks, L resume)
    {
        a.Ldy(a.M(last));
        Image(t, write: false);
        a.Ldy(a.M(next));
        Image(t, write: true);
        return Nap(t, a, ticks, resume);
    }

    /// <summary>Erase frame 3, redraw a cliff it overlapped, end.</summary>
    private ushort Finish(Tx t, Asm a)
    {
        a.Ldy(a.M(_poof3));
        Image(t, write: false);
        a.Ldx(a.U(_posX));
        a.Ldy(a.U(_posY));
        a.Lda(a.Y(_boxY - 15));
        a.Ora(a.Y(_boxY - 13));
        a.Anda(a.X(_boxX));
        a.Ora(a.M(_bckRefresh));
        a.Sta(a.M(_bckRefresh));
        t.Cycles += 3 + Symbols.AddressCost(_vSucide) + 4;       // JMP VSUCIDE
        return _scheduler.Suicide(t) ?? throw new InvalidOperationException("suicide without a continuation");
    }

    /// <summary>JSR WRREGY / JSR CLREGY: queue image Y (write or erase) at the process's position.</summary>
    private void Image(Tx t, bool write)
    {
        t.Cycles += 8 + 7 + Symbols.AddressCost(write ? _vWr1Cls : _vCl1Cls) + 4;   // JSR / JSR VxR1CLS / JMP
        _ground.Allocate(t, write, blocks: 1);
        t.Cycles += 5 + 3;                                       // RTS / BRA WRHOR2
        _ground.QueueImage(t);
        t.Cycles += 5;
    }

    /// <summary>PCNAP n.</summary>
    private ushort Nap(Tx t, Asm a, byte ticks, L resume)
    {
        a.LdaI(ticks);
        t.Cycles += 7 + Symbols.AddressCost(_vNapTpc) + 4;
        t.Push16(_address[resume]);
        return _scheduler.Nap(t, fromJsr: true) ?? throw new InvalidOperationException("nap without a continuation");
    }
}
