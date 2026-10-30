using static Joust.Emulation.Alu6809;

namespace Joust.Port.Routines;

/// <summary>
/// The bird process (JOUSTRV4.ASM, "PLAYER(S) MOVING RULES" and the flying loops). Players and
/// enemies run the same loop; only PJOY differs (the joystick for players, AI for enemies).
///
/// Ground: each tick read the controls; a flap press takes off. Otherwise the STATE table
/// advances on the joystick (forward/centre/back) every PACCX ticks, and every PTIMX ticks the
/// state's callback steps the bird and picks its frame; walking off a cliff starts a fall.
/// Air: two loops, wings down while flap is held (FLAPLP) and wings up while released
/// (FLIPLP). Each flap press adds lift and steering (ADDFLP); gravity is stronger with wings
/// up; touching a cliff top lands the bird in the running state matching its speed.
///
/// This is one state machine over the original labels. A slice runs from a label until the
/// bird naps (then the scheduler continues inline) or calls code that is not native yet (enemy
/// AI, lava-troll gravity, a troll spawn): then it leaves with the return address pushed, and
/// resumes at that address when the call returns. Before a step could be interrupted it stops
/// at the step's label instead, exactly as the original would be there.
/// </summary>
public sealed class BirdProcess
{
    public enum L
    {
        PlyrLp, PlyrNap, PlyrS2, Updno2After, Pl2Rit, Pl2Edg,
        StFly, StFall, StLan,
        FlapLp, FlapNap, FlapAir, FlapS2, FlapGrav, WingFk, WingFkGround, GoFlip,
        FlipLp, FlipNap, FlipAir, FlipS2, FlipGrav, GotFit, GotFitGround, GoFlap, Flast2,
    }

    private readonly Physics _physics;
    private readonly Ground _ground;
    private readonly Scheduler _scheduler;
    private readonly EnemyAi _ai;

    // Process block
    private readonly int _decision, _timeUp, _face, _accX, _state, _timX, _bumpX, _bumpY, _velX, _velY;
    private readonly int _image, _posX, _posY, _frame, _gravity, _joy;

    // Decision block (timing and sounds)
    private readonly int _napTime, _sndSkidEnd, _sndFall, _sndWingUp, _sndWingDown;

    private readonly ushort _curJoy, _bckRefresh, _landX, _landY, _boxX, _boxY, _flyX, _toRunning, _skidEnd;
    private readonly ushort _addGra, _ckGnd, _p1Joy, _p2Joy, _standR, _revR, _runR, _runSR, _skidR;
    private readonly ushort _widgetControlB, _widgetPortA, _airOverhead;
    private readonly byte _fly1, _fly3, _standFrame;
    private readonly Dictionary<L, ushort> _address = [];

    /// <summary>Return address of AIROVR's joystick call (resume point when PJOY is not native).</summary>
    public ushort AirJoyReturn { get; }

    public BirdProcess(Symbols s, Physics physics, Ground ground, Scheduler scheduler, EnemyAi ai)
    {
        _physics = physics;
        _ground = ground;
        _scheduler = scheduler;
        _ai = ai;
        _decision = s["PDECSN"]; _timeUp = s["PTIMUP"]; _face = s["PFACE"]; _accX = s["PACCX"];
        _state = s["PSTATE"]; _timX = s["PTIMX"]; _bumpX = s["PBUMPX"]; _bumpY = s["PBUMPY"];
        _velX = s["PVELX"]; _velY = s["PVELY"]; _image = s["PIMAGE"]; _posX = s["PPOSX"]; _posY = s["PPOSY"];
        _frame = s["PFRAME"]; _gravity = s["PADGRA"]; _joy = s["PJOY"];
        _napTime = s["DTIME"]; _sndSkidEnd = s["DSNSK2"]; _sndFall = s["DSNFAL"];
        _sndWingUp = s["DSNWU"]; _sndWingDown = s["DSNWD"];
        _curJoy = s["CURJOY"]; _bckRefresh = s["BCKRFS"];
        _landX = s["LNDXTB"]; _landY = s["LNDYTB"]; _boxX = s["BCKXTB"]; _boxY = s["BCKYTB"];
        _flyX = s["FLYX"]; _toRunning = s["FRCONV"]; _skidEnd = s["PLYLR"];
        _addGra = s["ADDGRA"]; _ckGnd = s["CKGND"]; _p1Joy = s["P1JOY"]; _p2Joy = s["P2JOY"];
        _standR = s["STANDR"]; _revR = s["REVR"]; _runR = s["RUNR"]; _runSR = s["RUNSR"]; _skidR = s["SKIDR"];
        _widgetControlB = s["WCPIAB"]; _widgetPortA = s["WPIAA"]; _airOverhead = s["AIROVR"];
        _fly1 = (byte)s.Value("FLY1"); _fly3 = (byte)s.Value("FLY3"); _standFrame = (byte)s.Value("STAND");

        // Labels, and the return addresses of the calls that can leave a slice. Instruction
        // sizes: JSR ext 3, JSR [n,R] 3, JSR [,X] 2, LDX PDECSN,U 2, LDA [DTIME,X] 3,
        // LDD CURJOY 2, LDX/STA with an 8-bit offset 3, LDA 6,X 2, LDA ,X+ 2, LDX #n 3,
        // TSTB 1, CLRB 1, short branches 2. (Tests check the opcodes at each return address.)
        void At(L l, string label, int offset = 0) => _address[l] = (ushort)(s[label] + offset);
        At(L.PlyrLp, "PLYRLP");
        At(L.PlyrNap, "PLYRLP", 2 + 3 + 3);
        At(L.PlyrS2, "PLYRS2");
        At(L.Updno2After, "UPDNO2", 3 + 2 + 3 + 2 + 3 + 2 + 3 + 2);
        At(L.Pl2Rit, "PL2RIT");
        At(L.Pl2Edg, "PL2EDG");
        At(L.StFly, "STFLY");
        At(L.StFall, "STFALL");
        At(L.StLan, "STLAN");
        At(L.FlapLp, "FLAPLP");
        At(L.FlapNap, "FLAPLP", 3 + 2 + 3 + 3);
        At(L.FlapAir, "FLAPLP", 3 + 2 + 3 + 3 + 3);
        At(L.FlapS2, "FLAPS2");
        At(L.FlapGrav, "FLAPST", 3 + 3);
        At(L.WingFk, "WINGFK");
        At(L.WingFkGround, "WINGFK", 3 + 3);
        At(L.GoFlip, "GOFLIP");
        At(L.FlipLp, "FLIPLP");
        At(L.FlipNap, "FLIPLP", 3 + 2 + 3 + 3);
        At(L.FlipAir, "FLIPLP", 3 + 2 + 3 + 3 + 3);
        At(L.FlipS2, "FLIPS2");
        At(L.FlipGrav, "FLIPST", 3 + 3);
        At(L.GotFit, "GOTFIT");
        At(L.GotFitGround, "GOTFIT", 3 + 3);
        At(L.GoFlap, "GOFLAP");
        At(L.Flast2, "FLAST2");
        AirJoyReturn = (ushort)(_airOverhead + 3);
    }

    public ushort Address(L label) => _address[label];

    public IReadOnlyDictionary<L, ushort> Addresses => _address;

    private static int Ix(int offset) => Symbols.IndexCost(offset);
    private static int Ext(ushort address) => Symbols.AddressCost(address);

    // ------------------------------------------------------------------ driver

    /// <summary>Runs the bird from <paramref name="start"/>; returns where execution continues, or null.</summary>
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
                return t.Cycles == startCycles ? null : _address[label];
            }
            if (exit != null) return exit;
            label = next;
        }
    }

    private (L Next, ushort? Exit) Step(Tx t, L label)
    {
        switch (label)
        {
            case L.PlyrLp:
                LoadNapTime(t);
                return (default, Nap(t, _address[L.PlyrNap]));

            case L.PlyrNap:
            {
                ushort? exit = ReadControls(t, _address[L.PlyrS2]);
                return exit != null ? (default, exit) : (L.PlyrS2, null);
            }

            case L.PlyrS2: return OnGround(t);

            case L.Updno2After:
                t.Write(t.U + _image, t.A);                      // remember the frame
                t.CC = Logic8(t.CC, t.A);
                t.A = t.Read(t.U + _face);
                t.CC = Logic8(t.CC, t.A);
                t.Cycles += 4 + Ix(_image) + 4 + Ix(_face) + 3;
                if (Mi(t.CC))
                {
                    // facing left: mirror the step and the speed
                    int velX = t.U + _velX;
                    t.Write(velX, Neg8(ref t.CC, t.Read(velX)));
                    t.B = Neg8(ref t.CC, t.B);
                    t.Cycles += 6 + Ix(_velX) + 2;
                }
                return (L.Pl2Rit, null);

            case L.Pl2Rit: return Walk(t);

            case L.Pl2Edg:
                t.Cycles += 7;                                   // BSR SRCADH
                _ground.SelectImage(t, storeImage: false, onGround: true);
                t.Cycles += 5 + 7;                               // RTS / BSR WPLYR
                _ground.DrawObject(t, write: true);
                t.Cycles += 5 + 4;                               // RTS / JMP PLYRLP
                return (L.PlyrLp, null);

            case L.StFly: return TakeOff(t);
            case L.StFall: return Fall(t);
            case L.StLan: return Land(t);

            case L.FlapLp:
            case L.FlipLp:
                t.Cycles += 8;                                   // JSR WPLYR
                _ground.DrawObject(t, write: true);
                t.Cycles += 5;
                LoadNapTime(t);
                return (default, Nap(t, _address[label == L.FlapLp ? L.FlapNap : L.FlipNap]));

            case L.FlapNap:
            case L.FlipNap:
            {
                L air = label == L.FlapNap ? L.FlapAir : L.FlipAir;
                t.Cycles += 8;                                   // JSR AIROVR
                t.Push16(_address[air]);
                ushort? exit = AirOverhead(t);
                if (exit != null) return (default, exit);
                t.S += 2;
                t.Cycles += 5;
                return (air, null);
            }

            case L.FlapAir:
                t.CC = Logic8(t.CC, t.B);                        // flap still held?
                t.Cycles += 2 + 3;
                return (t.B == 0 ? L.GoFlip : L.FlapS2, null);

            case L.FlipAir:
                t.CC = Logic8(t.CC, t.B);                        // flap pressed again?
                t.Cycles += 2 + 3;
                return (t.B != 0 ? L.GoFlap : L.FlipS2, null);

            case L.FlapS2:
                t.B = 0;                                         // wings down: no extra gravity
                t.CC = Clr(t.CC);
                t.Cycles += 2;
                return AfterGravity(t, Gravity(t, L.FlapGrav), L.FlapGrav);

            case L.FlipS2:
                t.B = 4;                                         // wings up: falls faster
                t.CC = Logic8(t.CC, 4);
                t.Cycles += 2;
                return AfterGravity(t, Gravity(t, L.FlipGrav), L.FlipGrav);

            case L.FlapGrav:
                CountDownWingTime(t);                            // minimum wings-down time after a flap
                t.A = _fly1;
                t.CC = Logic8(t.CC, t.A);
                t.Cycles += 2;
                return (L.WingFk, null);

            case L.FlipGrav:
                if (CountDownWingTime(t))
                {
                    t.A = _fly1;                                 // still showing wings down
                    t.CC = Logic8(t.CC, t.A);
                    t.Cycles += 2 + 3;
                }
                else
                {
                    t.A = _fly3;                                 // wings up
                    t.CC = Logic8(t.CC, t.A);
                    t.Cycles += 2;
                }
                return (L.GotFit, null);

            case L.WingFk:
            case L.GotFit:
            {
                L after = label == L.WingFk ? L.WingFkGround : L.GotFitGround;
                t.Cycles += 8;                                   // JSR SRCADA
                _ground.SelectImage(t, storeImage: true, onGround: false);
                t.Cycles += 5;
                var m = t.Mark();
                t.Cycles += 8;                                   // JSR CKGND
                if (_physics.CheckGround(t))
                {
                    t.Cycles += 5;
                    return (after, null);
                }
                t.Rollback(m);                                   // a lava troll may appear: original
                return (default, Leave(t, 8, _ckGnd, _address[after]));
            }

            case L.WingFkGround:
            case L.GotFitGround:
                t.Cycles += 3;                                   // BNE: still flying?
                if (!Eq(t.CC))
                    return (label == L.WingFkGround ? L.FlapLp : L.FlipLp, null);
                t.Cycles += 3;                                   // BRA STLAN
                return (L.StLan, null);

            case L.GoFlip:
                PlaySound(t, _sndWingUp);
                ClearByte(t, _timeUp);                           // restart the flap timer
                t.Cycles += 3;
                return (L.FlipS2, null);

            case L.GoFlap:
                t.Cycles += 8;                                   // JSR ADDFLP
                _physics.Flap(t);
                t.Cycles += 5;
                t.B = 0;
                t.CC = Clr(t.CC);
                t.Cycles += 2;
                return AfterGravity(t, Gravity(t, L.Flast2), L.Flast2);

            case L.Flast2:
            {
                PlaySound(t, _sndWingDown);
                ClearByte(t, _timeUp);
                t.B = t.Read(t.U + _accX);                       // were the wings allowed up?
                t.CC = Logic8(t.CC, t.B);
                t.A = 5;                                         // minimum wings-down time
                t.CC = Logic8(t.CC, 5);
                t.Write(t.U + _accX, 5);
                t.CC = Logic8(t.CC, 5);
                t.CC = Logic8(t.CC, t.B);                        // TSTB
                t.Cycles += 4 + Ix(_accX) + 2 + 4 + Ix(_accX) + 2 + 3; // ... TSTB / BLE WINGDN
                bool wingsWereUp = Le(t.CC);
                t.A = wingsWereUp ? _fly1 : _fly3;               // otherwise fake one wings-up frame
                t.CC = Logic8(t.CC, t.A);
                t.Cycles += 2 + (wingsWereUp ? 0 : 3);           // LDA (/ BRA WINGFK)
                return (L.WingFk, null);
            }
        }
        throw new InvalidOperationException($"unknown label {label}");
    }

    private static (L, ushort?) AfterGravity(Tx t, ushort? exit, L next) => exit != null ? (default, exit) : (next, null);

    // ------------------------------------------------------------------ ground

    /// <summary>PLYRS2 .. UPDNO2: the ground step after reading the controls (A = joystick, B = flap).</summary>
    private (L, ushort?) OnGround(Tx t)
    {
        byte timeUp = t.Read(t.U + _timeUp);
        t.CC = Logic8(t.CC, timeUp);
        t.Cycles += 6 + Ix(_timeUp) + 3;                         // TST PTIMUP,U / BNE
        if (timeUp == 0)
        {
            t.CC = Logic8(t.CC, t.B);
            t.Cycles += 2 + 5;                                   // TSTB / LBNE STFLY
            if (t.B != 0)
            {
                t.Cycles += 1;
                return (L.StFly, null);                          // flap pressed: take off
            }
        }
        t.Write(t.U + _timeUp, t.B);                             // PLNFLY: wait for flap release
        t.CC = Logic8(t.CC, t.B);
        t.B = t.Read(t.U + _face);
        t.CC = Logic8(t.CC, t.B);
        t.Cycles += 4 + Ix(_timeUp) + 4 + Ix(_face) + 3;
        if (Mi(t.CC))
        {
            t.A = Neg8(ref t.CC, t.A);                           // joystick relative to facing
            t.Cycles += 2;
        }

        // PLFRIT: time to move along the state table?
        int accX = t.U + _accX;
        t.B = t.Read(accX);
        t.CC = Logic8(t.CC, t.B);
        t.Cycles += 4 + Ix(_accX) + 3;
        bool update = t.B == 0;
        if (!update)
        {
            byte left = Dec(ref t.CC, t.Read(accX));
            t.Write(accX, left);
            t.Cycles += 6 + Ix(_accX) + 3;
            update = left == 0;
        }
        if (update)
        {
            // UPDSTA: next state for back (-1) / centre (0) / forward (+1)
            t.X = t.Read16(t.U + _state);
            t.CC = Logic16(t.CC, t.X);
            t.A = Add8(ref t.CC, t.A, 4, withCarry: false);
            t.B = t.Read(t.X + (sbyte)t.A);
            t.CC = Logic8(t.CC, t.B);
            t.Cycles += 5 + Ix(_state) + 2 + 5 + 3;
            if (t.B != 0)
            {
                t.A = 8;
                t.CC = Logic8(t.CC, 8);
                t.Write(accX, 8);
                t.CC = Logic8(t.CC, 8);
                t.Write(t.U + _timX, 0);
                t.CC = Clr(t.CC);
                t.X = (ushort)(t.X + (sbyte)t.B);
                t.CC = Set(t.CC, Z, t.X == 0);
                t.Write16(t.U + _state, t.X);
                t.CC = Logic16(t.CC, t.X);
                Sub16(ref t.CC, t.X, _skidEnd);
                t.Cycles += 2 + 4 + Ix(_accX) + 6 + Ix(_timX) + 5 + 5 + Ix(_state) + 4 + 3;
                if (t.X == _skidEnd)
                    PlaySound(t, _sndSkidEnd);                   // end of the skid
            }
        }

        // UPDNON: time to move?
        int timX = t.U + _timX;
        byte wait = Dec(ref t.CC, t.Read(timX));
        t.Write(timX, wait);
        t.Cycles += 6 + Ix(_timX) + 3;
        if (!Le(t.CC))
        {
            t.B = t.Read(t.U + _bumpX);
            t.CC = Logic8(t.CC, t.B);
            t.Cycles += 4 + Ix(_bumpX) + 3;
            if (t.B == 0)
                return (L.PlyrLp, null);
            Erase(t);                                            // only a bump to apply
            t.B = 0;
            t.CC = Clr(t.CC);
            t.Cycles += 2 + 3;
            return (L.Pl2Rit, null);
        }

        // UPDNO2: step with the state's callback
        Erase(t);
        t.D = t.Read16(t.Var(_curJoy));
        t.CC = Logic16(t.CC, t.D);
        t.X = t.Read16(t.U + _state);
        t.CC = Logic16(t.CC, t.X);
        t.A = t.Read(t.X + 6);                                   // fictitious speed (for bumps)
        t.CC = Logic8(t.CC, t.A);
        t.Write(t.U + _velX, t.A);
        t.CC = Logic8(t.CC, t.A);
        t.A = t.Read(t.X);                                       // wait before the next step
        t.X++;
        t.CC = Logic8(t.CC, t.A);
        t.Write(timX, t.A);
        t.CC = Logic8(t.CC, t.A);
        t.Cycles += 5 + Ext(_curJoy) + 5 + Ix(_state) + 5 + 4 + Ix(_velX) + 6 + 4 + Ix(_timX);
        ushort callback = t.Read16(t.X);
        Action<Tx>? step = StateCallback(callback);
        if (step == null)
            return (default, Leave(t, 10, callback, _address[L.Updno2After]));
        t.Cycles += 10;                                          // JSR [,X]
        step(t);
        t.Cycles += 5;
        return (L.Updno2After, null);
    }

    private Action<Tx>? StateCallback(ushort address)
    {
        if (address == _standR) return _ground.Stand;
        if (address == _revR) return _ground.Reverse;
        if (address == _runR) return _ground.Run;
        if (address == _runSR) return _ground.StopRunning;
        if (address == _skidR) return _ground.Skid;
        return null;
    }

    /// <summary>PL2RIT: move by B, then check the bird is still on a cliff and not walking into CLIF3U.</summary>
    private (L, ushort?) Walk(Tx t)
    {
        t.Cycles += 8;                                           // JSR WRAPX
        _physics.WrapX(t);
        t.Cycles += 5;
        t.X = t.Read16(t.U + _posX);
        t.CC = Logic16(t.CC, t.X);
        t.Y = t.Read16(t.U + _posY);
        t.CC = Logic16(t.CC, t.Y);
        t.A = (byte)(t.Read(t.X + _landX) & t.Read(t.Y + _landY + 1) & 0x7F);
        t.CC = Logic8(t.CC, t.A);
        t.Cycles += 5 + Ix(_posX) + 6 + Ix(_posY) + 4 + Ix(_landX) + 4 + Ix(_landY + 1) + 2 + 5;
        if (t.A == 0)
        {
            t.Cycles += 1;
            return (L.StFall, null);                             // walked off the edge
        }
        t.A = (byte)(t.Read(t.X + _boxX + 6) & t.Read(t.Y + _boxY));
        t.CC = Logic8(t.CC, t.A);
        t.Cycles += 4 + Ix(_boxX + 6) + 4 + Ix(_boxY) + 3;
        if (t.A != 0)
        {
            // Bumped into CLIF3U: refresh it, face left, bounce back.
            t.A = (byte)(t.A | t.Read(t.Var(_bckRefresh)));
            t.CC = Logic8(t.CC, t.A);
            t.Write(t.Var(_bckRefresh), t.A);
            t.CC = Logic8(t.CC, t.A);
            t.Write(t.U + _face, 0);
            t.CC = Clr(t.CC);
            t.A = t.Read(t.U + _bumpX);
            t.CC = Logic8(t.CC, t.A);
            t.Cycles += 4 + Ext(_bckRefresh) + 4 + Ext(_bckRefresh) + 6 + Ix(_face) + 4 + Ix(_bumpX) + 3;
            if (Mi(t.CC))
            {
                t.A = Neg8(ref t.CC, t.A);
                t.Cycles += 2;
            }
            t.A = Add8(ref t.CC, t.A, 3, withCarry: false);
            t.Write(t.U + _bumpX, t.A);
            t.CC = Logic8(t.CC, t.A);
            t.Cycles += 2 + 4 + Ix(_bumpX);
        }
        return (L.Pl2Edg, null);
    }

    // ------------------------------------------------------------------ take-off, falling, landing

    /// <summary>STFLY: erase, jump 1 pixel up with a small upward speed, normal gravity, then FLAST2.</summary>
    private (L, ushort?) TakeOff(Tx t)
    {
        Erase(t);
        t.D = unchecked((ushort)-0x80);
        t.CC = Logic16(t.CC, t.D);
        t.Write16(t.U + _velY, t.D);
        t.CC = Logic16(t.CC, t.D);
        int y = t.U + _posY + 1;
        t.Write(y, Dec(ref t.CC, t.Read(y)));
        t.Cycles += 3 + 5 + Ix(_velY) + 6 + Ix(_posY + 1);
        StartFlying(t);
        int accX = t.U + _accX;
        t.Write(accX, Inc(ref t.CC, t.Read(accX)));            // show the wings up for one frame
        t.Cycles += 6 + Ix(_accX) + 4;                           // INC / JMP FLAST2
        return (L.Flast2, null);
    }

    /// <summary>STFLY2 / shared part of STFALL: clear fractions and bumps, select normal gravity.</summary>
    private void StartFlying(Tx t)
    {
        ClearByte(t, _posY + 2);
        ClearByte(t, _posX + 2);
        ClearByte(t, _bumpY);
        t.D = 0;
        t.CC = Logic16(t.CC, 0);
        t.Write16(t.U + _state, 0);                              // no ground state: flying
        t.CC = Logic16(t.CC, 0);
        t.D = _addGra;
        t.CC = Logic16(t.CC, t.D);
        t.Write16(t.U + _gravity, t.D);
        t.CC = Logic16(t.CC, t.D);
        t.Cycles += 3 + 5 + Ix(_state) + 3 + 5 + Ix(_gravity);
    }

    /// <summary>STFALL: walked off a cliff (or skidded off: falling sound). Fall from rest.</summary>
    private (L, ushort?) Fall(Tx t)
    {
        t.A = t.Read(t.U + _frame);
        t.CC = Logic8(t.CC, t.A);
        t.Cycles += 4 + Ix(_frame) + 3;
        if (Mi(t.CC))
            PlaySound(t, _sndFall);                              // a skidding fall
        ClearByte(t, _posY + 2);
        ClearByte(t, _posX + 2);
        ClearByte(t, _bumpY);
        t.A = 10;                                                // slow to raise the wings
        t.CC = Logic8(t.CC, 10);
        t.Write(t.U + _accX, 10);
        t.CC = Logic8(t.CC, 10);
        t.D = 0;
        t.CC = Logic16(t.CC, 0);
        t.Write16(t.U + _velY, 0);
        t.CC = Logic16(t.CC, 0);
        t.Write16(t.U + _state, 0);
        t.CC = Logic16(t.CC, 0);
        t.D = _addGra;
        t.CC = Logic16(t.CC, t.D);
        t.Write16(t.U + _gravity, t.D);
        t.CC = Logic16(t.CC, t.D);
        t.Cycles += 2 + 4 + Ix(_accX) + 3 + 5 + Ix(_velY) + 5 + Ix(_state) + 3 + 5 + Ix(_gravity);
        ClearByte(t, _timeUp);
        t.A = t.Read(t.Var((ushort)(_curJoy + 1)));             // flap held?
        t.CC = Logic8(t.CC, t.A);
        t.Cycles += 4 + Ext((ushort)(_curJoy + 1)) + 3;
        if (t.A != 0) return (L.FlapS2, null);
        t.Cycles += 3;                                           // BRA FLIPS2
        return (L.FlipS2, null);
    }

    /// <summary>STLAN: face the way it was flying and run at the speed it landed with.</summary>
    private (L, ushort?) Land(Tx t)
    {
        t.A = t.Read(t.U + _velX);
        t.CC = Logic8(t.CC, t.A);
        t.Cycles += 4 + Ix(_velX) + 3;                           // LDA / BEQ
        if (t.A != 0)
        {
            t.Write(t.U + _face, 0);
            t.CC = Clr(t.CC);
            t.CC = Logic8(t.CC, t.A);                            // TSTA
            t.Cycles += 6 + Ix(_face) + 2 + 3;
            if (Mi(t.CC))
            {
                int face = t.U + _face;
                t.Write(face, Com8(ref t.CC, t.Read(face)));
                t.A = Neg8(ref t.CC, t.A);
                t.Cycles += 6 + Ix(_face) + 2;
            }
        }
        // STLDIR: speed 0, 2, 4, 6, 8 -> standing or running states
        t.X = _toRunning;
        t.CC = Logic16(t.CC, t.X);
        t.X = t.Read16(t.X + (sbyte)t.A);
        t.CC = Logic16(t.CC, t.X);
        t.Write16(t.U + _state, t.X);
        t.CC = Logic16(t.CC, t.X);
        ClearByte(t, _timX);
        ClearByte(t, _accX);
        t.A = 1;
        t.CC = Logic8(t.CC, 1);
        t.Write(t.U + _timeUp, 1);
        t.CC = Logic8(t.CC, 1);
        t.D = 0;
        t.CC = Logic16(t.CC, 0);
        t.Write16(t.U + _velY, 0);
        t.CC = Logic16(t.CC, 0);
        t.A = _standFrame;
        t.CC = Logic8(t.CC, t.A);
        t.Write(t.U + _image, t.A);
        t.CC = Logic8(t.CC, t.A);
        t.Cycles += 3 + 5 + 1 + 5 + Ix(_state) + 2 + 4 + Ix(_timeUp) + 3 + 5 + Ix(_velY)
                    + 2 + 4 + Ix(_image) + 4;                    // ... JMP PL2EDG
        return (L.Pl2Edg, null);
    }

    // ------------------------------------------------------------------ air helpers

    /// <summary>LDA PACCX,U / BLE / DEC PACCX,U: true if the wings-down timer was still running.</summary>
    private bool CountDownWingTime(Tx t)
    {
        int accX = t.U + _accX;
        t.A = t.Read(accX);
        t.CC = Logic8(t.CC, t.A);
        t.Cycles += 4 + Ix(_accX) + 3;
        if (Le(t.CC)) return false;
        t.Write(accX, Dec(ref t.CC, t.Read(accX)));
        t.Cycles += 6 + Ix(_accX);
        return true;
    }

    /// <summary>
    /// LDX #FLYX / JSR [PADGRA,U]: gravity and movement, inline for normal gravity and the lava
    /// troll's pull; sinking into the lava leaves the slice.
    /// </summary>
    private ushort? Gravity(Tx t, L returnTo)
    {
        t.X = _flyX;
        t.CC = Logic16(t.CC, t.X);
        t.Cycles += 3;
        ushort target = t.Read16(t.U + _gravity);
        int jsr = 7 + Symbols.IndirectCost(_gravity);
        if (target == _addGra)
        {
            var m = t.Mark();
            t.Cycles += jsr;
            if (_physics.AddGravity(t))
            {
                t.Cycles += 5;
                return null;
            }
            if (Lava != null)
            {
                t.Push16(_address[returnTo]);                    // ADGFLR never returns: the JSR's address stays
                return Lava.LavaFromGravity(t);
            }
            t.Rollback(m);
        }
        else if (Troll != null && target == Troll.PullAddress)
        {
            var m = t.Mark();
            t.Cycles += jsr;
            try
            {
                if (Troll.Pull(t))
                {
                    t.Cycles += 5;
                    return null;
                }
            }
            catch (HandBackException)
            {
            }
            t.Rollback(m);                                       // dragged into the lava: original
        }
        return Leave(t, jsr, target, _address[returnTo]);
    }

    /// <summary>The lava death (ADGFLR), where ADDGRA goes when the bird reaches the lava.</summary>
    public Transporter? Lava { get; set; }

    /// <summary>The lava troll, whose pull (ADDLAV) replaces a gripped bird's gravity.</summary>
    public LavaTroll? Troll { get; set; }

    /// <summary>
    /// AIROVR: read the controls, face the joystick direction, count ticks in the air
    /// (saturating at 255), erase the bird. Returns D = controls. Null when it ran to its RTS.
    /// </summary>
    public ushort? AirOverhead(Tx t)
    {
        ushort? exit = ReadControls(t, AirJoyReturn);
        if (exit != null) return exit;
        AirAfterControls(t);
        return null;
    }

    /// <summary>The rest of AIROVR after JSR [PJOY,U] returns (excluding its RTS).</summary>
    public void AirAfterControls(Tx t)
    {
        t.CC = Logic8(t.CC, t.A);                                // TSTA
        t.Cycles += 2 + 3;
        if (t.A != 0)
        {
            int face = t.U + _face;
            t.Write(face, 0);
            t.CC = Clr(t.CC);
            t.CC = Logic8(t.CC, t.A);
            t.Cycles += 6 + Ix(_face) + 2 + 3;
            if (Mi(t.CC))
            {
                t.Write(face, Com8(ref t.CC, t.Read(face)));
                t.Cycles += 6 + Ix(_face);
            }
        }
        // AIRTIM
        int timeUp = t.U + _timeUp;
        byte ticks = Inc(ref t.CC, t.Read(timeUp));
        t.Write(timeUp, ticks);
        t.Cycles += 6 + Ix(_timeUp) + 3;
        if (ticks == 0)
        {
            t.Write(timeUp, Dec(ref t.CC, ticks));               // saturate
            t.Cycles += 6 + Ix(_timeUp);
        }
        Erase(t);
        t.D = t.Read16(t.Var(_curJoy));
        t.CC = Logic16(t.CC, t.D);
        t.Cycles += 5 + Ext(_curJoy);
    }

    /// <summary>Pops the return address (an RTS) and returns it as the place to continue.</summary>
    public static ushort Return(Tx t)
    {
        ushort pc = t.Read16(t.S);
        t.S += 2;
        t.Cycles += 5;
        return pc;
    }

    // ------------------------------------------------------------------ controls

    /// <summary>
    /// JSR [PJOY,U]: the player's joystick or the enemy AI inline; anything else (attract-mode
    /// pilots, or an AI exit that is not ported) leaves the slice.
    /// </summary>
    public ushort? ReadControls(Tx t, ushort returnTo)
    {
        ushort target = t.Read16(t.U + _joy);
        int jsr = 7 + Symbols.IndirectCost(_joy);
        if (target == _p1Joy || target == _p2Joy)
        {
            t.Cycles += jsr;
            Joystick(t, player1: target == _p1Joy);
            t.Cycles += 5;
            return null;
        }
        if (_ai.Handles(target))
        {
            var m = t.Mark();
            try
            {
                t.Cycles += jsr;
                t.Push16(returnTo);
                _ai.Run(t, target);
                t.S += 2;
                t.Cycles += 5;
                return null;
            }
            catch (HandBackException)
            {
                t.Rollback(m);
            }
        }
        return Leave(t, jsr, target, returnTo);
    }

    /// <summary>
    /// P1JOY / P2JOY: switch the widget multiplexer to this player and read left/right/flap.
    /// Returns A = -1/0/+1 (left/none/right), B = 1 if flap is pressed; also in CURJOY.
    /// </summary>
    public void Joystick(Tx t, bool player1)
    {
        t.A = t.Read(_widgetControlB);
        t.CC = Logic8(t.CC, t.A);
        t.A = player1 ? (byte)(t.A | 0x08) : (byte)(t.A & 0xF7);
        t.CC = Logic8(t.CC, t.A);
        t.Cycles += 5 + 2 + (player1 ? 3 : 0);                   // LDA / ORA|ANDA (/ BRA P2SAM)
        t.IoWrite(_widgetControlB, t.A);                         // CB2 selects the player
        t.CC = Logic8(t.CC, t.A);
        if ((t.A & 0x30) == 0x30) t.Mux = (t.A & 0x08) != 0;
        t.A = t.Context.PeekWidget(t.Mux);
        t.IoRead(_widgetPortA);
        t.CC = Logic8(t.CC, t.A);
        t.B = 0;
        t.CC = Clr(t.CC);
        t.CC = Logic8(t.CC, (byte)(t.A & 0x04));                 // BITA #$04: flap
        t.Cycles += 5 + 5 + 2 + 2 + 3;
        if ((t.A & 0x04) != 0)
        {
            t.B = Inc(ref t.CC, t.B);
            t.Cycles += 2;
        }
        t.A = (byte)(t.A & 0x03);                                // bit0 left, bit1 right
        t.CC = Logic8(t.CC, t.A);
        t.A = Asr8(ref t.CC, t.A);
        t.A = Sbc8(ref t.CC, t.A, 0);                            // 1 -> -1, 2 -> +1
        t.Write16(t.Var(_curJoy), t.D);
        t.CC = Logic16(t.CC, t.D);
        t.Cycles += 2 + 2 + 2 + 5 + Ext(_curJoy);
    }

    // ------------------------------------------------------------------ small helpers

    /// <summary>LDX PDECSN,U / LDA [DTIME,X]: this bird's ticks per move (difficulty and speed).</summary>
    private void LoadNapTime(Tx t)
    {
        t.X = t.Read16(t.U + _decision);
        t.CC = Logic16(t.CC, t.X);
        t.A = t.Read(t.Read16(t.X + _napTime));
        t.CC = Logic8(t.CC, t.A);
        t.Cycles += 5 + Ix(_decision) + 4 + Symbols.IndirectCost(_napTime);
    }

    /// <summary>JSR VNAPTPC: sleep A ticks, resuming at <paramref name="returnTo"/>; the walk continues inline.</summary>
    private ushort Nap(Tx t, ushort returnTo)
    {
        t.Cycles += 8 + 4;                                       // JSR VNAPTPC / JMP NAPTPC
        t.Push16(returnTo);
        return _scheduler.Nap(t, fromJsr: true) ?? throw new InvalidOperationException("nap without a continuation");
    }

    /// <summary>A JSR to code that is not native: leave the slice there with the return address pushed.</summary>
    private static ushort Leave(Tx t, int jsrCycles, ushort target, ushort returnTo)
    {
        t.Cycles += jsrCycles;
        t.Push16(returnTo);
        return target;
    }

    /// <summary>JSR CPLYR: queue the erase of the bird's current image.</summary>
    private void Erase(Tx t)
    {
        t.Cycles += 8;
        _ground.DrawObject(t, write: false);
        t.Cycles += 5;
    }

    /// <summary>LDX PDECSN,U / LDX sound,X / JSR VSND.</summary>
    private void PlaySound(Tx t, int soundOffset)
    {
        t.X = t.Read16(t.U + _decision);
        t.CC = Logic16(t.CC, t.X);
        t.X = t.Read16(t.X + soundOffset);
        t.CC = Logic16(t.CC, t.X);
        t.Cycles += 5 + Ix(_decision) + 5 + Ix(soundOffset);
        _ground.CallSound(t);
    }

    /// <summary>CLR offset,U.</summary>
    private static void ClearByte(Tx t, int offset)
    {
        t.Write(t.U + offset, 0);
        t.CC = Clr(t.CC);
        t.Cycles += 6 + Ix(offset);
    }
}
