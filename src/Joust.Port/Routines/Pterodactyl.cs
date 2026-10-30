namespace Joust.Port.Routines;

/// <summary>
/// SRCADP (JOUSTRV4.ASM), called once per tick by the flying pterodactyl: pick its frame's image,
/// keep it above the floor line, update its collision box and write it (WNORIA). At the end of
/// the wave a baiter stops being one and every pterodactyl is sent off (PTEOFF). Then CKPTER:
/// on touching a cliff's box it jumps to BCKCOL. Body excluding the final RTS.
/// </summary>
public sealed class Pterodactyl(Symbols s, Ground ground, Physics physics)
{
    private const byte Lowest = 0xD3 - 1;                        // minimum position in Y

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
