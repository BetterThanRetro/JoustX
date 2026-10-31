namespace Joust.Port.Routines;

/// <summary>
/// SRCADP (JOUSTRV4.ASM), called once per tick by the flying pterodactyl: pick its frame's image,
/// keep it above the floor line, update its collision box and write it (WNORIA). At the end of
/// the wave a baiter stops being one and every pterodactyl is sent off (PTEOFF). Then CKPTER:
/// on touching a cliff's box it jumps to BCKCOL. Body excluding the final RTS.
/// </summary>
public sealed class Pterodactyl(Symbols s, Ground ground, Physics physics, BirdProcess bird, Scheduler scheduler)
{
    private const byte Lowest = 0xD3 - 1;                        // minimum position in Y

    /// <summary>Entry points of the flight loop: PTEFLY, its nap's resume point, and PTESTF.</summary>
    public enum L { Fly, Nap, Step }

    private readonly int _accX = s["PACCX"], _frame = s["PFRAME"];
    private readonly ushort _pteFly = s["PTEFLY"], _pteStep = s["PTESTF"], _pteSeq = s["PTESEQ"], _flyXp = s["FLYXP"];
    private readonly ushort _addGrx = s["ADDGRX"], _vNapTpc = s["VNAPTPC"];

    public ushort Address(L label) => label switch
    {
        L.Fly => _pteFly,
        L.Nap => (ushort)(_pteFly + 5),                          // after PCNAP 1
        _ => _pteStep,
    };

    /// <summary>Return address of JSR ADDGRX in PTESTF.</summary>
    private ushort AfterIntegrate =>                             // LDX #FLYXP / LDD PVELY,U / JSR ADDGRX
        (ushort)(s["PTESTF.30S"] + 3 + (_velY is >= -16 and <= 15 ? 2 : 3) + 3);

    /// <summary>
    /// PTEFLY / PTESTF: each tick, nap; fly (AIROVR, which runs the pterodactyl's PJOY brain);
    /// step the wing animation through PTESEQ; move (ADDGRX, no gravity); then SRCADP draws it
    /// and checks the cliffs. Returns where execution continues, or null to hand the call back.
    /// </summary>
    public ushort? Run(Tx t, L start)
    {
        var label = start;
        int startCycles = t.Cycles;
        while (true)
        {
            var mark = t.Mark();
            var (next, exit) = Step(t, label);
            if (!t.CanRun(0))
            {
                t.Rollback(mark);                                // this step could be interrupted
                return t.Cycles == startCycles ? null : Address(label);
            }
            if (exit != null) return exit;
            label = next;
        }
    }

    private (L Next, ushort? Exit) Step(Tx t, L label)
    {
        var a = new Asm(t);
        switch (label)
        {
            case L.Fly:
                a.LdaI(1);                                       // PCNAP 1
                t.Cycles += 7 + Symbols.AddressCost(_vNapTpc) + 4;
                t.Push16(Address(L.Nap));
                return (default, scheduler.Nap(t, fromJsr: true) ?? throw new InvalidOperationException("nap without a continuation"));

            case L.Nap:
            {
                t.Cycles += 8;                                   // JSR AIROVR
                t.Push16(_pteStep);
                ushort? exit = bird.AirOverhead(t);
                if (exit != null) return (default, exit);        // its PJOY brain is original: AIROVR@joy returns to PTESTF
                t.S += 2;
                t.Cycles += 5;
                return (L.Step, null);
            }

            default:
            {
                a.Dec(a.U(_accX));                               // frame holding time
                if (!a.Br(a.Gt))
                {
                    a.Ldb(a.U(_frame));                          // next frame
                    a.SubbI(2);
                    if (!a.Br(a.Pl)) a.LdbI((4 - 1) * 2);        // restart the sequence
                    a.Stb(a.U(_frame));                          // .20S
                    a.LdxI(_pteSeq);
                    t.D = t.Read16(t.X + t.B);                   // LDD B,X
                    t.CC = Emulation.Alu6809.Logic16(t.CC, t.D);
                    t.Cycles += 6;
                    a.Sta(a.U(_image));                          // image to show
                    a.Stb(a.U(_accX));                           // time till the next change
                }
                a.LdxI(_flyXp);                                  // .30S: velocity only, no gravity
                a.Ldd(a.U(_velY));
                var m = t.Mark();
                t.Cycles += 8;                                   // JSR ADDGRX
                if (!physics.Integrate(t))
                {
                    t.Rollback(m);                               // into the lava: the original handles it
                    t.Cycles += 8;
                    t.Push16(AfterIntegrate);
                    return (default, _addGrx);
                }
                t.Cycles += 5 + 7;                               // RTS / BSR SRCADP
                SourceAndCheck(t);
                t.Cycles += 5 + 3;                               // RTS / BRA PTEFLY
                return (L.Fly, null);
            }
        }
    }

    private readonly int _image = s["PIMAGE"], _face = s["PFACE"], _picH = s["PPICH"], _posX = s["PPOSX"], _posY = s["PPOSY"];
    private readonly int _velY = s["PVELY"], _colY1 = s["PCOLY1"], _colY2 = s["PCOLY2"], _colX = s["PCOLX"];
    private readonly int _chase = s["PCHASE"], _joy = s["PJOY"];
    private readonly ushort _iPtero = s["IPTERO"], _nRider = s["NRIDER"], _dBait = s["DBAIT"], _nBait = s["NBAIT"];
    private readonly ushort _pteOff = s["PTEOFF"], _bckX = s["BCKXTB"], _bckY = s["BCKYTB"];

    public void SourceAndCheck(Tx t)
    {
        var a = new Asm(t);
        a.Ldb(a.U(_image));                                      // image number
        a.Lda(a.U(_face));
        if (!a.Br(a.Pl)) a.AddbI(6);                             // facing left
        a.Ldx(a.M(_iPtero));                                     // .1S: this frame's image
        a.Abx();
        a.Stx(a.U(_picH));
        a.Ldb(a.U(_posY + 1));
        a.CmpbI(Lowest);
        if (!a.Br(a.Lo))
        {
            a.Clr(a.U(_velY));                                   // stop falling
            a.Clr(a.U(_velY + 1));
            a.LdbI(Lowest);
            a.Stb(a.U(_posY + 1));
        }
        a.Stb(a.U(_colY1));                                      // .12S: bottom line of the box
        a.SubbI(11 - 1);
        a.Stb(a.U(_colY2));                                      // top line
        a.Ldd(a.U(_posX));
        a.AdddI(28 - 1);
        a.Std(a.U(_colX));
        t.Cycles += 8;                                           // JSR WNORIA
        ground.DrawHorse(t, write: true);
        t.Cycles += 5;
        a.Lda(a.M(_nRider));                                     // wave over?
        bool sendOff = a.Br(a.Eq);
        if (!sendOff)
        {
            a.Lda(a.M(_dBait));                                  // baiters should leave?
            if (!a.Br(a.Eq))
            {
                a.Lda(a.U(_chase));                              // a baiter?
                if (!a.Br(a.Eq))
                {
                    a.Clr(a.U(_chase));
                    a.Dec(a.M(_nBait));
                    sendOff = true;                              // (falls into .48S)
                }
            }
        }
        if (sendOff)
        {
            a.Ldd(a.U(_joy));                                    // .48S: already leaving?
            a.SubdI(_pteOff);
            if (!a.Br(a.Hs))
            {
                a.LddI(_pteOff);
                a.Std(a.U(_joy));
            }
        }
        // CKPTER
        a.Ldx(a.U(_posX));
        a.Ldy(a.U(_posY));
        a.Lda(a.X(_bckX));
        a.Ora(a.X(_bckX + 28 - 18));
        a.Anda(a.Y(_bckY + 6));
        a.Anda(a.Y(_bckY + 7));
        if (a.Br(a.Eq)) return;
        t.Cycles += 4;                                           // JMP BCKCOL
        physics.BackgroundCollision(t);
    }
}
