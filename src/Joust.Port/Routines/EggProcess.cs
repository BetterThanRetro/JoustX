using static Joust.Emulation.Alu6809;

namespace Joust.Port.Routines;

/// <summary>
/// The egg process (JOUSTRV4.ASM), from the moment a beaten rider drops to its score display:
/// <list type="number">
/// <item>Falling (EGGLPA): tumble and fall; land on a cliff top when slow enough (each bounce
///       halves and reverses the fall, and slows the sideways speed); sinking into the lava
///       ends it.</item>
/// <item>Waiting (EGGLN2): sit for EGGWT ticks (it falls again if the ledge burns away), then
///       hatch when the wave has room for another enemy.</item>
/// <item>Hatching: a buzzard process is created flying in from the far side at the egg's
///       level, the egg wiggles through its hatching frames (EGGTBL) and the little man stands
///       up (EGGLLP) to wait for his ride.</item>
/// <item>Collected (EGGHIT, after EGGSCR): the score floats where the egg was, delayed so it
///       doesn't overlap a nearby score; a caught-in-the-air bonus shows a second score.</item>
/// </list>
/// A state machine over the original labels and the return addresses of its naps, like the
/// bird process; it stops at a label when the next step might be interrupted.
/// </summary>
public sealed class EggProcess
{
    public enum L
    {
        Fall, FallNap, Back, Settle, Wait, WaitNap, NextFrame, Hatched, Idle, IdleNap,
        Hit, HitNap, Delay, DelayNap, Show, Score, ScoreNap, ScoreInAir, Done,
    }

    private readonly Physics _physics;
    private readonly Ground _ground;
    private readonly Scheduler _scheduler;
    private readonly Eggs _eggs;
    private readonly TextOut _text;

    // Process block
    private readonly int _link, _id, _priority, _posX, _posY, _velX, _velY, _bumpX, _timX, _frame, _face, _rider;
    private readonly int _feet, _colY1, _colY2, _joyTime, _eggCount, _chase, _prDir, _dist, _horse, _decision, _joy;
    private readonly int _picH, _state, _lavaTime;

    // Decision block
    private readonly int _playerImages;

    private readonly ushort _enemyTime, _eggWait, _riders, _enemies, _wantEnemies, _gameOver, _head;
    private readonly ushort _landX, _landY, _boxX, _boxY, _sndHatch, _startFly, _buzzard, _seek, _wiggle;
    private readonly ushort _vectorSuicide, _textVector;
    private readonly short _eLeft, _eRight;
    private readonly byte _floor, _enemyId, _scoreId, _caughtId, _msg500, _eggColour;
    private readonly Dictionary<L, ushort> _address = [];

    public EggProcess(Symbols s, Physics physics, Ground ground, Scheduler scheduler, Eggs eggs, TextOut text)
    {
        _physics = physics;
        _ground = ground;
        _scheduler = scheduler;
        _eggs = eggs;
        _text = text;
        _link = s["PLINK"]; _id = s["PID"]; _priority = s["PPRI"]; _posX = s["PPOSX"]; _posY = s["PPOSY"];
        _velX = s["PVELX"]; _velY = s["PVELY"]; _bumpX = s["PBUMPX"]; _timX = s["PTIMX"]; _frame = s["PFRAME"];
        _face = s["PFACE"]; _rider = s["PRIDER"]; _feet = s["PFEET"]; _colY1 = s["PCOLY1"]; _colY2 = s["PCOLY2"];
        _joyTime = s["PJOYT"]; _eggCount = s["PEGG"]; _chase = s["PCHASE"]; _prDir = s["PRDIR"]; _dist = s["PDIST"];
        _horse = s["PHORSE"]; _decision = s["PDECSN"]; _joy = s["PJOY"]; _picH = s["PPICH"]; _state = s["PSTATE"];
        _lavaTime = s["PLAVT"];
        _playerImages = s["DPLYR"];
        _enemyTime = s["EMYTIM"]; _eggWait = s["EGGWT"]; _riders = s["NRIDER"]; _enemies = s["NENEMY"];
        _wantEnemies = s["WENEMY"]; _gameOver = s["GOVER"]; _head = s["PDUMMY"];
        _landX = s["LNDXTB"]; _landY = s["LNDYTB"]; _boxX = s["BCKXTB"]; _boxY = s["BCKYTB"];
        _sndHatch = s["SNEGGH"]; _startFly = s["STFLY2"]; _buzzard = s["BUZARD"]; _seek = s["SEEKE"]; _wiggle = s["EGGTBL"];
        _vectorSuicide = s["VSUCIDE"]; _textVector = s["OUTP35"];
        _eLeft = (short)s.Value("ELEFT"); _eRight = (short)s.Value("ERIGHT");
        _floor = (byte)s.Value("FLOOR"); _enemyId = (byte)s.Value("EMYID"); _scoreId = (byte)s.Value("SCRID");
        _caughtId = (byte)s.Value("CATID"); _msg500 = (byte)s.Value("MSG500"); _eggColour = (byte)(s.Value("EGGCOL") * 0x11);

        // Resume addresses: after JSR VNAPTPC (PCNAP n = LDA #n / JSR = 5 bytes).
        void At(L l, string label, int offset = 0) => _address[l] = (ushort)(s[label] + offset);
        At(L.Fall, "EGGLPA");
        At(L.FallNap, "EGGLPA", 3 + 2 + 3);                      // JSR WEGG / LDA EMYTIM / JSR VNAPTPC
        At(L.Back, "EGGBCK");
        At(L.Settle, "EGGLND");
        At(L.Wait, "EGGLN2");
        At(L.WaitNap, "EGGLN2", 3 + 5);                          // JSR WEGG / PCNAP 12
        At(L.NextFrame, "EGGNXF");                               // also EGGHCH's nap return
        At(L.Hatched, "EGGLLP", -(3 + 3 + 4 + 3 + 4 + 3));      // back over JSR CEGG ... JSR WRREGY
        At(L.Idle, "EGGLLP");
        At(L.IdleNap, "EGGLLP", 5);
        At(L.Hit, "EGGHIT");
        At(L.HitNap, "EGGHIT.10S", 5);
        At(L.Delay, "EGGHIT.101S");
        At(L.DelayNap, "EGGHIT.101S", 5);
        At(L.Show, "EGGHIT.112S");
        At(L.Score, "EGGSC2");
        At(L.ScoreNap, "EGGSC2", 3 + 3 + 3 + 2 + 3 + 5);        // LDX / LDD / DEC / BEQ / JSR OUTP35 / PCNAP 2
        At(L.ScoreInAir, "SCRAIR");
        At(L.Done, "EGGDON");
    }

    public ushort Address(L label) => _address[label];

    public IReadOnlyDictionary<L, ushort> Addresses => _address;

    private static int Ix(int offset) => Symbols.IndexCost(offset);
    private static int Ext(ushort address) => Symbols.AddressCost(address);

    public ushort? Run(Tx t, L start)
    {
        var label = start;
        int startCycles = t.Cycles;
        while (true)
        {
            var mark = t.Mark();
            (L Next, ushort? Exit) step;
            try
            {
                step = Step(t, label);
            }
            catch (HandBackException)
            {
                t.Rollback(mark);
                if (t.Cycles == startCycles) throw;
                return _address[label];
            }
            if (!t.CanRun(0))
            {
                t.Rollback(mark);
                return t.Cycles == startCycles ? null : _address[label];
            }
            if (step.Exit != null) return step.Exit;
            label = step.Next;
        }
    }

    private (L, ushort?) Step(Tx t, L label)
    {
        switch (label)
        {
            case L.Fall:
                Call(t, () => _eggs.Draw(t));                    // JSR WEGG
                t.A = t.Read(t.Var(_enemyTime));                 // falls at the enemies' pace
                t.CC = Logic8(t.CC, t.A);
                t.Cycles += 4 + Ext(_enemyTime);
                return (default, Nap(t, _address[L.FallNap]));

            case L.FallNap: return Falling(t);
            case L.Back: return HitCliffs(t);

            case L.Settle:
                t.A = t.Read(t.Var(_eggWait));                   // how long before it may hatch
                t.CC = Logic8(t.CC, t.A);
                t.Write(t.U + _joyTime, t.A);
                t.CC = Logic8(t.CC, t.A);
                t.Cycles += 4 + Ext(_eggWait) + 4 + Ix(_joyTime);
                return (L.Wait, null);

            case L.Wait:
                Call(t, () => _eggs.Draw(t));
                return (default, NapFor(t, 12, _address[L.WaitNap]));

            case L.WaitNap: return Waiting(t);
            case L.NextFrame: return HatchFrame(t);

            case L.Hatched:
                Call(t, () => _eggs.Erase(t));                   // JSR CEGG
                t.Y = t.Read16(t.U + _decision);
                t.CC = Logic16(t.CC, t.Y);
                t.Y = t.Read16(t.Read16(t.Y + _playerImages));   // LDY [DPLYR,Y]
                t.CC = Logic16(t.CC, t.Y);
                t.Y = (ushort)(t.Y + 24);                        // the standing little man
                t.CC = Set(t.CC, Z, t.Y == 0);
                t.Write16(t.U + _picH, t.Y);
                t.CC = Logic16(t.CC, t.Y);
                t.Cycles += 6 + Ix(_decision) + 6 + Symbols.IndirectCost(_playerImages) + 5 + 6 + Ix(_picH);
                DrawStanding(t);
                return (L.Idle, null);

            case L.Idle:
                return (default, NapFor(t, 8, _address[L.IdleNap]));

            case L.IdleNap:
                t.Y = t.Read16(t.U + _picH);
                t.CC = Logic16(t.CC, t.Y);
                t.Cycles += 6 + Ix(_picH);
                DrawStanding(t);
                t.Cycles += 3;                                   // BRA EGGLLP
                return (L.Idle, null);

            case L.Hit: return Collected(t);
            case L.HitNap: return ScoreDelay(t);

            case L.Delay:
                return (default, NapFor(t, 1, _address[L.DelayNap]));

            case L.DelayNap: return AfterDelay(t);

            case L.Show:
                t.Cycles += 3;                                   // BRA EGGSC2
                return (L.Score, null);

            case L.Score: return ShowScore(t);

            case L.ScoreNap:
                t.Cycles += 3;                                   // BRA EGGSC2
                return (L.Score, null);

            case L.ScoreInAir:
                t.A = 60 / 2;                                    // one second
                t.CC = Logic8(t.CC, t.A);
                t.Write(t.U + _joyTime, t.A);
                t.CC = Logic8(t.CC, t.A);
                t.Cycles += 2 + 4 + Ix(_joyTime) + 7;            // ... BSR EGAREA
                _eggs.ScoreArea(t);
                t.Cycles += 5;
                return (L.Score, null);

            case L.Done:
                Call(t, () => _eggs.Erase(t));
                return (default, Suicide(t));
        }
        throw new InvalidOperationException($"unknown label {label}");
    }

    // ------------------------------------------------------------------ falling

    /// <summary>After the falling nap: erase, fall, refresh, then land, bounce or keep falling.</summary>
    private (L, ushort?) Falling(Tx t)
    {
        Call(t, () => _eggs.Erase(t));                           // JSR CEGG
        Call(t, () => _eggs.Fall(t));                            // JSR ADDEGG
        Call(t, () => _eggs.RefreshBackground(t));               // JSR EGGBKG
        LandBits(t, rowOffset: 0);
        t.Cycles += 3;                                           // BNE EGGBON
        if (t.A == 0) return HitCliffs(t);

        // EGGBON: bounce on the cliff top
        t.Write(t.U + _feet, t.A);                               // no more caught-in-the-air bonus
        t.CC = Logic8(t.CC, t.A);
        t.Cycles += 4 + Ix(_feet) + 8;                           // STA / JSR LND18
        if (!_physics.Land(t)) throw new HandBackException();    // a lava troll would appear
        t.Cycles += 5;
        t.Write(t.U + _colY1, t.B);
        t.CC = Logic8(t.CC, t.B);
        t.B = Sub8(ref t.CC, t.B, 7 - 1);
        t.Write(t.U + _colY2, t.B);
        t.CC = Logic8(t.CC, t.B);
        t.A = t.Read(t.U + _velX);
        t.CC = Logic8(t.CC, t.A);
        t.Cycles += 4 + Ix(_colY1) + 2 + 4 + Ix(_colY2) + 4 + Ix(_velX) + 3;
        if (t.A != 0)
        {
            t.Cycles += 3;                                       // BMI EGGVXM
            if (Mi(t.CC))
            {
                t.A = Add8(ref t.CC, t.A, 2, withCarry: false);  // slow down sideways
                t.Cycles += 2;
            }
            else
            {
                t.A = Add8(ref t.CC, t.A, 0xFE, withCarry: false);
                t.Cycles += 2 + 3;
            }
        }
        t.Write(t.U + _velX, t.A);
        t.CC = Logic8(t.CC, t.A);
        t.D = t.Read16(t.U + _velY);
        t.CC = Logic16(t.CC, t.D);
        t.Cycles += 4 + Ix(_velX) + 5 + Ix(_velY) + 3;
        if (Mi(t.CC)) return HitCliffs(t);                       // already going up
        t.A = Asr8(ref t.CC, t.A);                               // bounce up at a quarter speed
        t.B = Ror8(ref t.CC, t.B);
        t.A = Asr8(ref t.CC, t.A);
        t.B = Ror8(ref t.CC, t.B);
        t.A = Com8(ref t.CC, t.A);
        t.B = Neg8(ref t.CC, t.B);
        t.A = Sbc8(ref t.CC, t.A, 0xFF);
        t.Write16(t.U + _velY, t.D);
        t.CC = Logic16(t.CC, t.D);
        Sub16(ref t.CC, t.D, unchecked((ushort)-0x20));
        t.Cycles += 7 * 2 + 5 + Ix(_velY) + 5 + 3;
        if (Lt(t.CC)) return HitCliffs(t);                       // still too fast to settle
        t.A = t.Read(t.U + _velX);
        t.CC = Logic8(t.CC, t.A);
        t.Cycles += 4 + Ix(_velX) + 3;
        if (t.A != 0) return HitCliffs(t);
        return (L.Settle, null);                                 // EGGLND
    }

    /// <summary>EGGBCK: bounce off cliff sides and undersides; sinking below the floor ends the egg.</summary>
    private (L, ushort?) HitCliffs(Tx t)
    {
        t.X = t.Read16(t.U + _posX);
        t.CC = Logic16(t.CC, t.X);
        t.Y = t.Read16(t.U + _posY);
        t.CC = Logic16(t.CC, t.Y);
        t.A = (byte)(t.Read(t.X + _boxX) & t.Read(t.X + _boxX - 10) & t.Read(t.Y + _boxY + 6) & t.Read(t.Y + _boxY + 13));
        t.CC = Logic8(t.CC, t.A);
        t.Cycles += 5 + Ix(_posX) + 6 + Ix(_posY) + 4 * (4 + 4) + 3;
        if (t.A != 0)
        {
            t.Cycles += 8;                                       // JSR BCKCOL
            _physics.BackgroundCollision(t);
            t.Cycles += 5;
        }
        t.B = t.Read(t.U + _posY + 1);
        t.CC = Logic8(t.CC, t.B);
        Sub8(ref t.CC, t.B, (byte)(_floor + 4));
        t.Cycles += 4 + Ix(_posY + 1) + 2 + 3;
        if (Cs(t.CC)) return (L.Fall, null);                     // still above the lava
        int riders = t.Var(_riders);
        t.Write(riders, Dec(ref t.CC, t.Read(riders)));          // a scrambled egg: one rider fewer
        t.Cycles += 6 + Ext(_riders) + 4;                        // DEC / JMP EGGDN2
        return (default, Suicide(t));
    }

    /// <summary>
    /// Landing bits under the egg: LNDXTB,X | LNDXTB-2,X, and LNDXTB-9,X, and LNDYTB+row,Y,
    /// ignoring the lava-troll bit.
    /// </summary>
    private void LandBits(Tx t, int rowOffset)
    {
        t.X = t.Read16(t.U + _posX);
        t.CC = Logic16(t.CC, t.X);
        t.Y = t.Read16(t.U + _posY);
        t.CC = Logic16(t.CC, t.Y);
        t.A = (byte)((t.Read(t.X + _landX) | t.Read(t.X + _landX - 2)) & t.Read(t.X + _landX - 9)
                     & t.Read(t.Y + _landY + rowOffset) & 0x7F);
        t.CC = Logic8(t.CC, t.A);
        t.Cycles += 5 + Ix(_posX) + 6 + Ix(_posY) + 4 * (4 + 4) + 2;
    }

    // ------------------------------------------------------------------ waiting and hatching

    /// <summary>After the 12-tick wait: still on land? time to hatch? room for another enemy?</summary>
    private (L, ushort?) Waiting(Tx t)
    {
        LandBits(t, rowOffset: 1);
        t.Cycles += 5;                                           // LBEQ STEGG
        if (t.A == 0)
        {
            t.Cycles += 1;
            return (L.Fall, null);                               // the ledge burned away
        }
        int wait = t.U + _joyTime;
        t.Write(wait, Dec(ref t.CC, t.Read(wait)));
        t.Cycles += 6 + Ix(_joyTime) + 3;
        if (t.Read(wait) != 0) return (L.Wait, null);
        t.Write(wait, Inc(ref t.CC, 0));                         // stays due
        t.A = t.Read(t.Var(_enemies));
        t.CC = Logic8(t.CC, t.A);
        Sub8(ref t.CC, t.A, t.Read(t.Var(_wantEnemies)));
        t.Cycles += 6 + Ix(_joyTime) + 4 + Ext(_enemies) + 4 + Ext(_wantEnemies) + 3;
        if (!Cs(t.CC)) return (L.Wait, null);                    // the wave is full for now
        Hatch(t);
        return (L.NextFrame, null);
    }

    /// <summary>The egg hatches: its buzzard sets off to fetch the little man.</summary>
    private void Hatch(Tx t)
    {
        int enemies = t.Var(_enemies);
        t.Write(enemies, Inc(ref t.CC, t.Read(enemies)));
        t.X = _sndHatch;
        t.CC = Logic16(t.CC, t.X);
        t.Cycles += 6 + Ext(_enemies) + 3;
        _ground.CallSound(t);
        t.A = _enemyId;
        t.CC = Logic8(t.CC, t.A);
        t.B = t.Read(t.U + _priority);
        t.CC = Logic8(t.CC, t.B);
        t.X = _startFly;
        t.CC = Logic16(t.CC, t.X);
        t.Cycles += 2 + 4 + Ix(_priority) + 3 + 8 + 4;
        _scheduler.Create(t, afterRunning: false);               // the buzzard (Y)
        t.Cycles += 5;
        ushort bird = t.Y;
        t.Write16(t.U + _dist, bird);                            // they know each other
        t.CC = Logic16(t.CC, bird);
        t.Write16(bird + _dist, t.U);
        t.CC = Logic16(t.CC, t.U);
        t.A = t.Read(t.U + _eggCount);
        t.CC = Logic8(t.CC, t.A);
        t.Write(bird + _eggCount, t.A);
        t.CC = Logic8(t.CC, t.A);
        t.Cycles += 6 + Ix(_dist) + 5 + Ix(_dist) + 4 + Ix(_eggCount) + 4 + Ix(_eggCount);
        foreach (int field in new[] { _chase, _face, _prDir })
        {
            t.Write(bird + field, 0);
            t.CC = Clr(t.CC);
            t.Cycles += 6 + Ix(field);
        }
        t.A = 8;                                                 // full speed
        t.CC = Logic8(t.CC, 8);
        t.Write(bird + _velX, 8);
        t.CC = Logic8(t.CC, 8);
        t.D = 0;
        t.CC = Logic16(t.CC, 0);
        t.Write16(bird + _velY, 0);
        t.Write16(bird + _rider, 0);                             // no rider yet
        t.CC = Logic16(t.CC, 0);
        t.Cycles += 2 + 4 + Ix(_velX) + 3 + 5 + Ix(_velY) + 5 + Ix(_rider);
        foreach (int field in new[] { _bumpX, _timX, _frame })
        {
            t.Write(bird + field, 0);
            t.CC = Clr(t.CC);
            t.Cycles += 6 + Ix(field);
        }
        t.D = t.Read16(_buzzard);                                // LDD BUZARD: buzzard images
        t.CC = Logic16(t.CC, t.D);
        t.Write16(bird + _horse, t.D);
        t.CC = Logic16(t.CC, t.D);
        t.D = t.Read16(t.U + _decision);
        t.CC = Logic16(t.CC, t.D);
        t.Write16(bird + _decision, t.D);
        t.CC = Logic16(t.CC, t.D);
        t.D = _seek;                                             // fetch the little man
        t.CC = Logic16(t.CC, t.D);
        t.Write16(bird + _joy, t.D);
        t.CC = Logic16(t.CC, t.D);
        t.D = t.Read16(t.U + _posX);
        t.CC = Logic16(t.CC, t.D);
        t.D = Sub16(ref t.CC, t.D, (ushort)((_eRight - _eLeft) / 2));
        t.Cycles += 5 + Ext(_buzzard) + 5 + Ix(_horse) + 5 + Ix(_decision) + 5 + Ix(_decision)
                    + 3 + 5 + Ix(_joy) + 5 + Ix(_posX) + 4 + 3;
        if (Hi(t.CC))
        {
            t.D = (ushort)(_eLeft + 1);                          // egg on the right: come from the left
            t.CC = Logic16(t.CC, t.D);
            t.Cycles += 3;
        }
        else
        {
            int face = bird + _face;
            t.Write(face, Com8(ref t.CC, t.Read(face)));         // from the right, facing left
            int vel = bird + _velX;
            t.Write(vel, Neg8(ref t.CC, t.Read(vel)));
            t.D = (ushort)(_eRight - 1);
            t.CC = Logic16(t.CC, t.D);
            t.Cycles += 6 + Ix(_face) + 6 + Ix(_velX) + 3 + 3;
        }
        t.Write16(bird + _posX, t.D);
        t.CC = Logic16(t.CC, t.D);

        // Start at the flight level for the egg's ledge.
        t.A = t.Read(t.U + _posY + 1);
        t.CC = Logic8(t.CC, t.A);
        t.B = 0x93 + 20 + 2;
        t.CC = Logic8(t.CC, t.B);
        Sub8(ref t.CC, t.A, (0xD3 + 0xA3) / 2);
        t.Cycles += 5 + Ix(_posX) + 4 + Ix(_posY + 1) + 2 + 2 + 3;
        if (!Hi(t.CC))
        {
            t.B = 0x52 + 20 + 2;
            t.CC = Logic8(t.CC, t.B);
            Sub8(ref t.CC, t.A, (0x51 + 0x81) / 2);
            t.Cycles += 2 + 2 + 3;
            if (!Hi(t.CC))
            {
                t.B = 0x45;
                t.CC = Logic8(t.CC, t.B);
                t.Cycles += 2;
            }
        }
        t.A = 0;
        t.CC = Clr(t.CC);
        t.Write16(bird + _posY, t.D);
        t.CC = Logic16(t.CC, t.D);
        t.X = _wiggle;                                           // the egg's wiggle-and-hatch show
        t.CC = Logic16(t.CC, t.X);
        t.Write16(t.U + _prDir, t.X);
        t.CC = Logic16(t.CC, t.X);
        t.Cycles += 2 + 5 + Ix(_posY) + 3 + 5 + Ix(_prDir) + 3;  // ... BRA EGGNXF
    }

    /// <summary>EGGNXF: show the next hatching frame (EGGTBL: image, height, ticks); then stand up.</summary>
    private (L, ushort?) HatchFrame(Tx t)
    {
        Call(t, () => _eggs.Erase(t));
        t.Cycles += 8 + 4;                                       // JSR VWR1CLS
        _ground.Allocate(t, write: true, blocks: 1);
        t.Cycles += 5;
        t.A = t.Read(t.Read16(t.U + _prDir));                    // LDA [PRDIR,U]: the frame
        t.CC = Logic8(t.CC, t.A);
        t.Cycles += 4 + Symbols.IndirectCost(_prDir);
        Call(t, () => _eggs.DrawFrame(t));                       // JSR WEGPAR
        t.X = t.Read16(t.U + _prDir);
        t.CC = Logic16(t.CC, t.X);
        t.A = t.Read(t.U + _posY + 1);
        t.CC = Logic8(t.CC, t.A);
        t.Write(t.U + _colY1, t.A);
        t.CC = Logic8(t.CC, t.A);
        t.A = Sub8(ref t.CC, t.A, t.Read(t.X + 1));              // its height
        t.Write(t.U + _colY2, t.A);
        t.CC = Logic8(t.CC, t.A);
        t.A = t.Read(t.X + 2);                                   // how long to show it
        t.CC = Logic8(t.CC, t.A);
        t.Cycles += 5 + Ix(_prDir) + 4 + Ix(_posY + 1) + 4 + Ix(_colY1) + 5 + 4 + Ix(_colY2) + 5 + 3;
        if (t.A != 0)
        {
            // EGGHCH: next frame after A ticks
            t.X = (ushort)(t.X + 3);
            t.CC = Set(t.CC, Z, t.X == 0);
            t.Write16(t.U + _prDir, t.X);
            t.CC = Logic16(t.CC, t.X);
            t.Cycles += 5 + 5 + Ix(_prDir);
            return (default, NapAfter(t, _address[L.NextFrame]));
        }
        return (default, NapFor(t, 7, _address[L.Hatched]));
    }

    /// <summary>WRREGY: queue the standing little man (Y = image).</summary>
    private void DrawStanding(Tx t)
    {
        t.Cycles += 8 + 8 + 4;                                   // JSR WRREGY / JSR VWR1CLS / JMP
        _ground.Allocate(t, write: true, blocks: 1);
        t.Cycles += 5 + 3;                                       // RTS / BRA WRHOR2
        _ground.QueueImage(t);
        t.Cycles += 5;
    }

    // ------------------------------------------------------------------ collected

    /// <summary>EGGHIT: the egg was collected; erase it and (after a moment) show its score.</summary>
    private (L, ushort?) Collected(Tx t)
    {
        Call(t, () => _eggs.Erase(t));
        Call(t, () => _eggs.RefreshBackground(t));
        t.A = t.Read(t.Var(_gameOver));
        t.CC = Logic8(t.CC, t.A);
        t.Cycles += 4 + Ext(_gameOver) + 3;
        if (!Le(t.CC)) throw new HandBackException();            // attract mode's cycling egg score
        return (default, NapFor(t, 2, _address[L.HitNap]));
    }

    /// <summary>
    /// Place the score, then wait long enough that it doesn't overlap a nearby score still
    /// showing (within 6 rows and 16 columns); a nearby score is also hurried along.
    /// </summary>
    private (L, ushort?) ScoreDelay(Tx t)
    {
        t.Cycles += 8;                                           // JSR EGAREA
        _eggs.ScoreArea(t);
        t.Cycles += 5;
        t.A = 1;
        t.CC = Logic8(t.CC, 1);
        t.Write(t.U + _lavaTime, 1);                             // PLAVT: the delay
        t.CC = Logic8(t.CC, 1);
        t.X = t.Read16(t.Read16(_head));                         // LDX [PDUMMY]
        t.CC = Logic16(t.CC, t.X);
        t.Cycles += 2 + 4 + Ix(_lavaTime) + 10;
        while (true)
        {
            t.X = t.Read16(t.X + _link);
            t.CC = Logic16(t.CC, t.X);
            t.Cycles += 5 + Ix(_link) + 3;
            if (t.X == 0) break;
            t.A = t.Read(t.X + _id);
            t.CC = Logic8(t.CC, t.A);
            Sub8(ref t.CC, t.A, _scoreId);
            t.Cycles += 4 + Ix(_id) + 2 + 3;
            if (t.A != _scoreId) continue;
            if (!Near(t, _posY, 6) || !Near(t, _posX, 16)) continue;

            // .33S
            t.A = t.Read(t.X + _joyTime);
            t.CC = Logic8(t.CC, t.A);
            Sub8(ref t.CC, t.A, 16 / 2);
            t.Cycles += 4 + Ix(_joyTime) + 2 + 3;
            if (!Cs(t.CC))
            {
                t.A = 16 / 2;                                    // speed up the other score
                t.CC = Logic8(t.CC, t.A);
                t.Write(t.X + _joyTime, t.A);
                t.CC = Logic8(t.CC, t.A);
                t.Y = t.Read16(t.X + _link);
                t.CC = Logic16(t.CC, t.Y);
                t.B = t.Read(t.Y + _id);
                t.CC = Logic8(t.CC, t.B);
                Sub8(ref t.CC, t.B, _caughtId);
                t.Cycles += 2 + 4 + Ix(_joyTime) + 6 + Ix(_link) + 4 + Ix(_id) + 2 + 3;
                if (t.B == _caughtId)
                {
                    t.Write(t.Y + _joyTime, t.A);                // and its caught-in-the-air bonus
                    t.CC = Logic8(t.CC, t.A);
                    t.Cycles += 4 + Ix(_joyTime);
                }
            }
            // .34S: wait until it is gone
            t.A = Asl8(ref t.CC, t.A);
            t.A = Add8(ref t.CC, t.A, t.Read(t.X + _lavaTime), withCarry: false);
            Sub8(ref t.CC, t.A, t.Read(t.U + _lavaTime));
            t.Cycles += 2 + 4 + Ix(_lavaTime) + 4 + Ix(_lavaTime) + 3;
            if (Cs(t.CC)) continue;
            t.Write(t.U + _lavaTime, t.A);
            t.CC = Logic8(t.CC, t.A);
            t.Cycles += 4 + Ix(_lavaTime) + 3;
        }
        // .100S
        t.A = _scoreId;
        t.CC = Logic8(t.CC, t.A);
        t.Write(t.U + _id, t.A);                                 // now a score process
        t.CC = Logic8(t.CC, t.A);
        t.A = 60 / 2;
        t.CC = Logic8(t.CC, t.A);
        t.Write(t.U + _joyTime, t.A);
        t.CC = Logic8(t.CC, t.A);
        t.Cycles += 2 + 4 + Ix(_id) + 2 + 4 + Ix(_joyTime);
        return (L.Delay, null);
    }

    /// <summary>|this.field - other.field| &lt; limit (16-bit), as .31S/.32S compute it.</summary>
    private bool Near(Tx t, int field, int limit)
    {
        t.D = t.Read16(t.U + field);
        t.CC = Logic16(t.CC, t.D);
        t.D = Sub16(ref t.CC, t.D, t.Read16(t.X + field));
        t.Cycles += 5 + Ix(field) + 6 + Ix(field) + 3;
        if (Mi(t.CC))
        {
            t.A = Com8(ref t.CC, t.A);
            t.B = Neg8(ref t.CC, t.B);
            t.A = Sbc8(ref t.CC, t.A, 0xFF);
            t.Cycles += 6;
        }
        t.D = Sub16(ref t.CC, t.D, (ushort)limit);
        t.Cycles += 4 + 3;
        return Lt(t.CC);
    }

    /// <summary>After each delay tick; then maybe the caught-in-the-air bonus score process.</summary>
    private (L, ushort?) AfterDelay(Tx t)
    {
        int delay = t.U + _lavaTime;
        t.Write(delay, Dec(ref t.CC, t.Read(delay)));
        t.Cycles += 6 + Ix(_lavaTime) + 3;
        if (t.Read(delay) != 0) return (L.Delay, null);
        t.A = t.Read(t.U + _eggCount);                           // caught in the air?
        t.CC = Logic8(t.CC, t.A);
        t.Cycles += 4 + Ix(_eggCount) + 3;
        if (t.A != 0)
        {
            ushort egg = t.U;
            t.X = _address[L.Show];
            t.CC = Logic16(t.CC, t.X);
            t.D = (ushort)((_caughtId << 8) | 0xFF);
            t.CC = Logic16(t.CC, t.D);
            t.Cycles += 3 + 3 + 8 + 4;
            _scheduler.Create(t, afterRunning: false);           // the "500" just above
            t.Cycles += 5;
            t.D = (ushort)((_msg500 << 8) | _eggColour);
            t.CC = Logic16(t.CC, t.D);
            t.Write16(t.Y + _prDir, t.D);
            t.CC = Logic16(t.CC, t.D);
            Copy16(t, egg + _posX, t.Y + _posX);
            t.D = t.Read16(egg + _posY);
            t.CC = Logic16(t.CC, t.D);
            t.B = Sub8(ref t.CC, t.B, 6);
            t.Write16(t.Y + _posY, t.D);
            t.CC = Logic16(t.CC, t.D);
            t.D = t.Read16(egg + _state);
            t.CC = Logic16(t.CC, t.D);
            t.B = Sub8(ref t.CC, t.B, 6);
            t.Write16(t.Y + _state, t.D);
            t.CC = Logic16(t.CC, t.D);
            t.A = t.Read(egg + _joyTime);
            t.CC = Logic8(t.CC, t.A);
            t.Write(t.Y + _joyTime, t.A);
            t.CC = Logic8(t.CC, t.A);
            t.Write(t.Y + _lavaTime, 0);
            t.CC = Clr(t.CC);
            t.Cycles += 3 + 5 + Ix(_prDir) + 5 + Ix(_posY) + 2 + 5 + Ix(_posY) + 5 + Ix(_state) + 2
                        + 5 + Ix(_state) + 4 + Ix(_joyTime) + 4 + Ix(_joyTime) + 6 + Ix(_lavaTime);
        }
        return (default, NapFor(t, 2, _address[L.Show]));       // .111S
    }

    private static void Copy16(Tx t, int from, int to)
    {
        t.D = t.Read16(from);
        t.CC = Logic16(t.CC, t.D);
        t.Write16(to, t.D);
        t.CC = Logic16(t.CC, t.D);
        t.Cycles += 5 + 1 + 5 + 1;
    }

    /// <summary>EGGSC2: draw the score every 2 ticks for its time, then erase it and end.</summary>
    private (L, ushort?) ShowScore(Tx t)
    {
        t.X = t.Read16(t.U + _state);                            // screen address
        t.CC = Logic16(t.CC, t.X);
        t.D = t.Read16(t.U + _prDir);                            // message and colour
        t.CC = Logic16(t.CC, t.D);
        int time = t.U + _joyTime;
        t.Write(time, Dec(ref t.CC, t.Read(time)));
        t.Cycles += 5 + Ix(_state) + 5 + Ix(_prDir) + 6 + Ix(_joyTime) + 3;
        if (t.Read(time) != 0)
        {
            Print(t);
            return (default, NapFor(t, 2, _address[L.ScoreNap]));
        }
        // EGGSC3: erase it (colour 0) and end
        t.B = 0;
        t.CC = Clr(t.CC);
        t.Cycles += 2;
        Print(t);
        Call(t, () => _eggs.RefreshBackground(t));
        return (default, Suicide(t));
    }

    /// <summary>JSR OUTP35 (JMP PHR35): the 3x5 phrase.</summary>
    private void Print(Tx t)
    {
        t.Cycles += 8 + 4;
        _text.Write(t, phrase: true, smallFont: true);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>JSR to an always-native helper.</summary>
    private static void Call(Tx t, Action body)
    {
        t.Cycles += 8;
        body();
        t.Cycles += 5;
    }

    /// <summary>PCNAP n: sleep n ticks, resuming at <paramref name="returnTo"/>.</summary>
    private ushort NapFor(Tx t, byte ticks, ushort returnTo)
    {
        t.A = ticks;
        t.CC = Logic8(t.CC, ticks);
        t.Cycles += 2;
        return NapAfter(t, returnTo);
    }

    /// <summary>JSR VNAPTPC with the nap time already in A.</summary>
    private ushort NapAfter(Tx t, ushort returnTo)
    {
        t.Cycles += 8 + 4;
        t.Push16(returnTo);
        return _scheduler.Nap(t, fromJsr: true) ?? throw new InvalidOperationException("nap without a continuation");
    }

    private ushort Nap(Tx t, ushort returnTo) => NapAfter(t, returnTo);

    /// <summary>JMP VSUCIDE: the process ends.</summary>
    private ushort Suicide(Tx t)
    {
        t.Cycles += 4 + Ext(_vectorSuicide) - 1 + 4;             // JMP VSUCIDE / JMP SUCIDE
        return _scheduler.Suicide(t) ?? throw new InvalidOperationException("suicide without a continuation");
    }
}
