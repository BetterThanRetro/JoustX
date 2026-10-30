using static Joust.Emulation.Alu6809;

namespace Joust.Port.Routines;

/// <summary>
/// Game over (JOUSTRV4.ASM, GAMOVR and GAMOV1).
/// <list type="bullet">
/// <item>GAMOVR (once per game): the game state becomes "over", the start-switch monitor starts,
///   the credits are shown (DCREDIT, still original), every enemy and egg falls back to dumb line
///   tracking, then "THY GAME IS OVER" is shown for 88 ticks and the high-score check (GAMEND,
///   other ROM) takes over.</item>
/// <item>GAMOV1 (a player out of lives): that player's "PLAYER n GAME OVER", rewritten every 8
///   ticks for its time or until the whole game is over, then erased.</item>
/// </list>
/// </summary>
public sealed class GameOver
{
    public enum L { Start, StartNap, Credits, WaitNap, Player, PlayerNap }

    private readonly Scheduler _scheduler;
    private readonly TextOut _text;
    private readonly int _link, _id, _decision, _joy, _pRam, _joyT, _dJoy, _dConst, _dGamo;
    private readonly ushort _crdChg, _gOver, _wSmart, _pDummy, _hsPp1, _hsEnd, _gamEnd, _gStart, _dCredit;
    private readonly ushort _vNapTpc, _vCrProc, _vSucide, _outPhr;
    private readonly byte _pteId, _msgOvr, _msgAmo, _white;
    private readonly Dictionary<L, ushort> _address = [];

    public GameOver(Symbols s, Scheduler scheduler, TextOut text)
    {
        _scheduler = scheduler;
        _text = text;
        _link = s["PLINK"]; _id = s["PID"]; _decision = s["PDECSN"]; _joy = s["PJOY"]; _pRam = s["PRAM"]; _joyT = s["PJOYT"];
        _dJoy = s["DJOY"]; _dConst = s["DCONST"]; _dGamo = s["DGAMO"];
        _crdChg = s["CRDCHG"]; _gOver = s["GOVER"]; _wSmart = s["WSMART"]; _pDummy = s["PDUMMY"];
        _hsPp1 = s["HSPP1"]; _hsEnd = (ushort)(s["HSPP22"] + 2); _gamEnd = s["GAMEND"]; _gStart = s["GSTART"];
        _dCredit = s["DCREDIT"];
        _vNapTpc = s["VNAPTPC"]; _vCrProc = s["VCRPROC"]; _vSucide = s["VSUCIDE"]; _outPhr = s["OUTPHR"];
        _pteId = (byte)s.Value("PTEID"); _msgOvr = (byte)s.Value("MSGOVR"); _msgAmo = (byte)s.Value("MSGAMO");
        _white = (byte)(s.Value("WHI") * 0x11);

        int V(ushort v) => Symbols.AddressCost(v) == 0 ? 2 : 3;
        int I(int offset) => offset is >= -16 and <= 15 ? 2 : 3;
        _address[L.Start] = s["GAMOVR"];
        _address[L.StartNap] = (ushort)(s["GAMOVR"] + 2 + V(_crdChg) + V(_gOver) + 5);
        _address[L.Credits] = (ushort)(_address[L.StartNap] + 3 + 3 + 3 + 3);   // PROCCR GSTART / JSR DCREDIT
        _address[L.WaitNap] = (ushort)(s["GOVWAT.1S"] + 3 + 5);  // JSR GOVERM / PCNAP 8
        _address[L.Player] = s["GAMOV1"];
        _address[L.PlayerNap] = (ushort)(s["GAMOV1"] + I(_decision) + I(_dConst) + 2 + 5);
    }

    public ushort Address(L label) => _address[label];

    public ushort? Run(Tx t, L start)
    {
        var a = new Asm(t);
        ushort exit = start switch
        {
            L.Start => Begin(t, a),
            L.StartNap => StartMonitor(t, a),
            L.Credits => Credits(t, a),
            L.WaitNap => Waiting(t, a),
            L.Player => PlayerMessage(t, a),
            _ => PlayerWait(t, a),
        };
        return t.CanRun(0) ? exit : null;
    }

    /// <summary>GAMOVR: no credit page, the game is over, nap 8.</summary>
    private ushort Begin(Tx t, Asm a)
    {
        a.LdaI(0x80);
        a.Sta(a.M(_crdChg));
        a.Clr(a.M(_gOver));
        return Nap(t, a, 8, L.StartNap);
    }

    /// <summary>PROCCR GSTART,$48 / JSR DCREDIT (original, resuming at Credits).</summary>
    private ushort StartMonitor(Tx t, Asm a)
    {
        a.LdxI(_gStart);
        a.LddI(0x4800);
        t.Cycles += 7 + Symbols.AddressCost(_vCrProc) + 4;
        _scheduler.Create(t, afterRunning: true);
        t.Cycles += 5;
        t.Cycles += 8;                                           // JSR DCREDIT
        t.Push16(_address[L.Credits]);
        return _dCredit;
    }

    /// <summary>No one is smart any more: every enemy and egg goes back to its dumb joystick routine.</summary>
    private ushort Credits(Tx t, Asm a)
    {
        a.Clr(a.M(_wSmart));
        t.X = t.Read16(t.Read16(_pDummy));                       // LDX [PDUMMY]
        t.CC = Logic16(t.CC, t.X);
        t.Cycles += 10;
        while (true)
        {
            a.Ldx(a.X(_link));                                   // GOVID
            if (a.Br(a.Eq)) break;
            a.Lda(a.X(_id));                                     // on screen?
            if (a.Br(a.Pl)) continue;
            a.CmpaI(0x80 + _pteId);                              // not pterodactyls
            if (a.Br(a.Eq)) continue;
            a.Ldy(a.X(_decision));
            a.Lda(a.X(_id));                                     // .2S
            a.Ldy(a.Y(_dJoy));                                   // .3S
            t.Write16(t.X + _joy, t.Y);                          // STY PJOY,X
            t.CC = Logic16(t.CC, t.Y);
            t.Cycles += 6 + Symbols.IndexCost(_joy);
            a.Bra();
        }
        a.LdaI(11);                                              // GOVWAT: 11 x 8 ticks
        a.Sta(a.U(_pRam));
        return ShowAndNap(t, a);
    }

    /// <summary>.1S: JSR GOVERM, PCNAP 8.</summary>
    private ushort ShowAndNap(Tx t, Asm a)
    {
        t.Cycles += 8;                                           // JSR GOVERM
        a.LdxI(0x3090);
        a.LddI((_msgOvr << 8) | _white);
        t.Cycles += 3 + Symbols.AddressCost(_outPhr) + 4;        // JMP OUTPHR
        _text.Write(t, phrase: true, smallFont: false);
        return Nap(t, a, 8, L.WaitNap);
    }

    /// <summary>After the nap: again, or clear the high-score highlights and check the high scores.</summary>
    private ushort Waiting(Tx t, Asm a)
    {
        a.Dec(a.U(_pRam));
        if (a.Br(a.Ne)) return ShowAndNap(t, a);
        a.LdxI(_hsPp1);                                          // forget old highlighted letters
        do
        {
            t.Write(t.X, 0);                                     // .2S: CLR ,X+
            t.X++;
            t.CC = Clr(t.CC);
            t.Cycles += 8;
            a.CmpxI(_hsEnd);
        }
        while (a.Br(a.Lo));
        t.Cycles += 3 + Symbols.AddressCost(_gamEnd);            // JMP GAMEND
        return _gamEnd;
    }

    /// <summary>GAMOV1: this player's game-over message in its colour, then nap 8.</summary>
    private ushort PlayerMessage(Tx t, Asm a)
    {
        a.Ldx(a.U(_decision));
        a.Ldb(a.X(_dConst));
        Message(t, a);
        return Nap(t, a, 8, L.PlayerNap);
    }

    /// <summary>Until the game is over or the time is up; then erase it and end.</summary>
    private ushort PlayerWait(Tx t, Asm a)
    {
        a.Lda(a.M(_gOver));
        if (!a.Br(a.Eq))
        {
            a.Dec(a.U(_joyT));
            if (a.Br(a.Ne)) return PlayerMessage(t, a);
        }
        a.Ldx(a.U(_decision));                                   // .1S: erase
        a.Clrb();
        Message(t, a);
        t.Cycles += 3 + Symbols.AddressCost(_vSucide) + 4;
        return _scheduler.Suicide(t) ?? throw new InvalidOperationException("suicide without a continuation");
    }

    /// <summary>BSR .10S (with its return): "PLAYER n" then "GAME OVER" in colour B.</summary>
    private void Message(Tx t, Asm a)
    {
        t.Cycles += 7;
        a.Lda(a.X(_dGamo));
        a.LdxI(0x296C);
        t.Cycles += 7 + Symbols.AddressCost(_outPhr) + 4;        // JSR OUTPHR
        _text.Write(t, phrase: true, smallFont: false);
        a.Leax(0x300);                                           // a space
        a.LdaI(_msgAmo);
        t.Cycles += 3 + Symbols.AddressCost(_outPhr) + 4;        // JMP OUTPHR
        _text.Write(t, phrase: true, smallFont: false);
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
