using static Joust.Emulation.Alu6809;

namespace Joust.Port.Routines;

/// <summary>
/// The death routines each decision block names (DDEAD), called with X = the victor (0 for the
/// lava) and U = the dead bird (JOUSTRV4.ASM):
/// <list type="bullet">
/// <item>DEATH4, pterodactyl: dying sound, then its PJOY becomes the "dead pterodactyl" show.</item>
/// <item>DEATH3, enemy: dying sound, one fewer (smart) enemy, and its rider drops as an egg
///       (a new egg process just after it) that will hatch as the next smarter enemy. If the
///       rider has no eggs left, the egg is scored at once.</item>
/// <item>DEATH1/DEATH2, player: in a gladiator wave a player-on-player kill pays the bounty
///       (3,000); otherwise the enemies may be slowed. Then the death puff, 50 points, the
///       death sound, the player stops being a target, a life is used and, if none are left,
///       game over.</item>
/// </list>
/// Bodies exclude the final RTS. Rare paths (an extra mount from a score) hand back.
/// </summary>
public sealed class Deaths
{
    private readonly Scheduler _scheduler;
    private readonly Ground _ground;
    private readonly Scoring _scoring;
    private readonly Combat _combat;

    // Process block offsets
    private readonly int _decision, _joyTime, _joy, _chase, _priority, _dist, _picH, _colY1, _posY, _posX;
    private readonly int _feet, _velY, _velX, _bumpX, _bumpY, _timX, _frame, _eggs, _prDir;

    // Decision block offsets
    private readonly int _glad, _const, _score;

    // Variables and constants
    private readonly ushort _eggs1, _eggs2, _died1, _died2, _baiterDelay, _glad1, _glad2, _gameOver, _troll;
    private readonly ushort _enemyTime, _target1, _target2, _targetTime1, _targetTime2, _smart, _enemies;
    private readonly ushort _score1Lives, _score2Lives, _head, _free, _zapPointer;
    private readonly ushort _pteroKill, _sndPtero, _sndEnemy, _sndBounty, _sndPlayer;
    private readonly ushort _startEgg, _scoreInAir, _deathPuff, _gameOverAll, _gameOverOne;
    private readonly ushort _hardest, _p3, _p4, _death1, _death2, _death3, _death4;
    private readonly byte _eggId, _scoreId, _playerId, _bountyMessage;

    public Deaths(Symbols s, Scheduler scheduler, Ground ground, Scoring scoring, Combat combat)
    {
        _scheduler = scheduler;
        _ground = ground;
        _scoring = scoring;
        _combat = combat;
        _decision = s["PDECSN"]; _joyTime = s["PJOYT"]; _joy = s["PJOY"]; _chase = s["PCHASE"];
        _priority = s["PPRI"]; _dist = s["PDIST"]; _picH = s["PPICH"]; _colY1 = s["PCOLY1"];
        _posY = s["PPOSY"]; _posX = s["PPOSX"]; _feet = s["PFEET"]; _velY = s["PVELY"]; _velX = s["PVELX"];
        _bumpX = s["PBUMPX"]; _bumpY = s["PBUMPY"]; _timX = s["PTIMX"]; _frame = s["PFRAME"]; _eggs = s["PEGG"];
        _prDir = s["PRDIR"];
        _glad = s["DGLAD"]; _const = s["DCONST"]; _score = s["DSCORE"];
        _eggs1 = s["EGGS1"]; _eggs2 = s["EGGS2"]; _died1 = s["PLYD1"]; _died2 = s["PLYD2"];
        _baiterDelay = s["DBAIT"]; _glad1 = s["PLYG1"]; _glad2 = s["PLYG2"]; _gameOver = s["GOVER"];
        _troll = s["TTROLL"]; _enemyTime = s["EMYTIM"]; _target1 = s["TARPLY"]; _target2 = s["TARPL2"];
        _targetTime1 = s["TARTM1"]; _targetTime2 = s["TARTM2"]; _smart = s["NSMART"]; _enemies = s["NENEMY"];
        _score1Lives = (ushort)(s["SPLY1"] + 6); _score2Lives = (ushort)(s["SPLY2"] + 6);
        _head = s["PDUMMY"]; _free = s["PFREE"]; _zapPointer = s["LZAP1"];
        _pteroKill = s["PTEKLL"]; _sndPtero = s["SNPTED"]; _sndEnemy = s["SNEDIE"]; _sndBounty = s["SNBOUN"];
        _sndPlayer = s["SNPDIE"];
        _startEgg = s["STEGG"]; _scoreInAir = s["SCRAIR"]; _deathPuff = s["DEATH"];
        _gameOverAll = s["GAMOVR"]; _gameOverOne = s["GAMOV1"];
        _hardest = s["P6DEC"]; _p3 = s["P3DEC"]; _p4 = s["P4DEC"];
        _death1 = s["DEATH1"]; _death2 = s["DEATH2"]; _death3 = s["DEATH3"]; _death4 = s["DEATH4"];
        _eggId = (byte)s.Value("EGGID"); _scoreId = (byte)s.Value("SCRID"); _playerId = (byte)s.Value("PLYID");
        _bountyMessage = (byte)s.Value("MSGTH3");
    }

    private static int Ix(int offset) => Symbols.IndexCost(offset);
    private static int Ext(ushort address) => Symbols.AddressCost(address);

    /// <summary>Runs the death routine at <paramref name="routine"/> inline; false if it is not one of these.</summary>
    public bool Run(Tx t, ushort routine)
    {
        if (routine == _death1) { PlayerDies(t, player2: false); return true; }
        if (routine == _death2) { PlayerDies(t, player2: true); return true; }
        if (routine == _death3) { EnemyDies(t); return true; }
        if (routine == _death4) { PterodactylDies(t); return true; }
        return false;
    }

    // ------------------------------------------------------------------ pterodactyl and enemy

    /// <summary>DEATH4.</summary>
    public void PterodactylDies(Tx t)
    {
        SaveXU(t);
        t.A = 8 - 1;
        t.CC = Logic8(t.CC, t.A);
        t.Write(t.U + _joyTime, t.A);
        t.CC = Logic8(t.CC, t.A);
        t.D = _pteroKill;                                        // show a dead pterodactyl
        t.CC = Logic16(t.CC, t.D);
        t.Write16(t.U + _joy, t.D);
        t.CC = Logic16(t.CC, t.D);
        t.Cycles += 2 + 4 + Ix(_joyTime) + 3 + 5 + Ix(_joy);
        Sound(t, _sndPtero);
        RestoreXU(t);
    }

    /// <summary>DEATH3.</summary>
    public void EnemyDies(Tx t)
    {
        SaveXU(t);
        Sound(t, _sndEnemy);
        int smart = t.Var(_smart);
        t.A = t.Read(smart);
        t.CC = Logic8(t.CC, t.A);
        t.A = Sub8(ref t.CC, t.A, t.Read(t.U + _chase));         // it was chasing: one fewer chaser
        t.Write(smart, t.A);
        t.CC = Logic8(t.CC, t.A);
        int enemies = t.Var(_enemies);
        t.Write(enemies, Dec(ref t.CC, t.Read(enemies)));
        t.Cycles += 4 + Ext(_smart) + 4 + Ix(_chase) + 4 + Ext(_smart) + 6 + Ext(_enemies);

        // The rider falls as an egg, a new process right after this one.
        t.A = (byte)(0x80 + _eggId);
        t.CC = Logic8(t.CC, t.A);
        t.B = t.Read(t.U + _priority);
        t.CC = Logic8(t.CC, t.B);
        t.X = _startEgg;
        t.CC = Logic16(t.CC, t.X);
        t.Cycles += 2 + 4 + Ix(_priority) + 3;
        CreateProcess(t);
        ushort egg = t.Y;
        t.D = 0;
        t.CC = Logic16(t.CC, 0);
        foreach (int field in new[] { _dist, _picH, _colY1 })    // no buzzard after it yet, nothing to erase, no early collisions
        {
            t.Write16(egg + field, 0);
            t.CC = Logic16(t.CC, 0);
            t.Cycles += 5 + Ix(field);
        }
        t.Cycles += 3;
        t.D = t.Read16(t.U + _posY);
        t.CC = Logic16(t.CC, t.D);
        t.B = Sub8(ref t.CC, t.B, 13 - 5);                       // egg bottom from the bird's bottom
        t.Write16(egg + _posY, t.D);
        t.CC = Logic16(t.CC, t.D);
        t.D = t.Read16(t.U + _posX);
        t.CC = Logic16(t.CC, t.D);
        t.D = Add16(ref t.CC, t.D, 4);
        t.Write16(egg + _posX, t.D);
        t.CC = Logic16(t.CC, t.D);
        t.Write(egg + _feet, 0);                                 // not caught in the air yet
        t.CC = Clr(t.CC);
        t.Cycles += 5 + Ix(_posY) + 2 + 5 + Ix(_posY) + 5 + Ix(_posX) + 4 + 5 + Ix(_posX) + 6 + Ix(_feet);

        // It hatches as the next smarter enemy (up to the Shadow Lord).
        t.D = t.Read16(t.U + _decision);
        t.CC = Logic16(t.CC, t.D);
        Sub16(ref t.CC, t.D, _hardest);
        t.Cycles += 5 + Ix(_decision) + 5 + 3;
        if (Cs(t.CC))
        {
            t.D = Add16(ref t.CC, t.D, (ushort)(_p4 - _p3));
            t.Cycles += 4;
        }
        t.Write16(egg + _decision, t.D);
        t.CC = Logic16(t.CC, t.D);
        t.D = t.Read16(t.U + _velY);                             // same speeds
        t.CC = Logic16(t.CC, t.D);
        t.Write16(egg + _velY, t.D);
        t.CC = Logic16(t.CC, t.D);
        t.A = t.Read(t.U + _velX);
        t.CC = Logic8(t.CC, t.A);
        t.Write(egg + _velX, t.A);
        t.CC = Logic8(t.CC, t.A);
        t.Cycles += 5 + Ix(_decision) + 5 + Ix(_velY) + 5 + Ix(_velY) + 4 + Ix(_velX) + 4 + Ix(_velX);
        foreach (int field in new[] { _bumpX, _bumpY, _timX, _frame })
        {
            t.Write(egg + field, 0);
            t.CC = Clr(t.CC);
            t.Cycles += 6 + Ix(field);
        }
        t.A = t.Read(t.U + _eggs);                               // eggs this rider can still give
        t.CC = Logic8(t.CC, t.A);
        t.Write(egg + _eggs, t.A);
        t.CC = Logic8(t.CC, t.A);
        byte left = Dec(ref t.CC, t.A);
        t.Write(egg + _eggs, left);
        t.Cycles += 4 + Ix(_eggs) + 4 + Ix(_eggs) + 6 + Ix(_eggs) + 3;
        if (left == 0)
        {
            t.X = egg;                                           // TFR Y,X
            t.U = t.Read16(t.S);                                 // the victor
            t.CC = Logic16(t.CC, t.U);
            t.Cycles += 6 + 5 + 3;
            if (t.U != 0)
            {
                t.Cycles += 8;                                   // JSR EGGSCR: scored at once
                t.Push16(0);
                _combat.CollectEgg(t);
            }
        }
        RestoreXU(t);
    }

    // ------------------------------------------------------------------ players

    /// <summary>DEATH1 / DEATH2 and SPDIE.</summary>
    public void PlayerDies(Tx t, bool player2)
    {
        int eggs = t.Var(player2 ? _eggs2 : _eggs1);
        t.Write(eggs, 0);                                        // the egg score restarts at 250
        t.CC = Clr(t.CC);
        int died = t.Var(player2 ? _died2 : _died1);
        t.Write(died, Inc(ref t.CC, t.Read(died)));              // spoils a survival wave
        t.Cycles += 6 + Ext(_eggs1) + 6 + Ext(_died1) + (player2 ? 0 : 3);

        // SPDIE
        t.B = 5 * 60 / 8;                                        // remove baiter pterodactyls soon
        t.CC = Logic8(t.CC, t.B);
        t.Write(t.Var(_baiterDelay), t.B);
        t.CC = Logic8(t.CC, t.B);
        t.CC = Set(t.CC, Z, t.X == 0);                           // LEAX ,X
        t.Cycles += 2 + 4 + Ext(_baiterDelay) + 4 + 3;
        if (t.X != 0)
        {
            t.Y = t.Read16(t.X + _decision);
            t.CC = Logic16(t.CC, t.Y);
            t.Cycles += 6 + Ix(_decision) + 3;
            bool byEnemy = t.Y == 0;
            if (!byEnemy)
            {
                t.Y = t.Read16(t.Y + _glad);                     // only players have a gladiator counter
                t.CC = Logic16(t.CC, t.Y);
                t.Cycles += 6 + Ix(_glad) + 3;
                byEnemy = t.Y == 0;
            }
            if (byEnemy)
            {
                SlowEnemies(t);
            }
            else
            {
                byte kills = Inc(ref t.CC, t.Read(t.Y));         // player-on-player kills
                t.Write(t.Y, kills);
                t.Cycles += 6 + 3;
                if (!Le(t.CC)) { }                               // BGT SPDIE2
                else if (Eq(t.CC))
                {
                    t.Cycles += 3;
                    Bounty(t);                                   // the gladiator wave's bounty
                }
                else
                {
                    t.Write(t.Y, Dec(ref t.CC, kills));          // keep it positive
                    t.Cycles += 3 + 6 + 3;
                }
            }
        }
        PuffAndAccounting(t);
    }

    /// <summary>SPDIE3: a player killed by an enemy in waves 1-3 slows the enemies down.</summary>
    private void SlowEnemies(Tx t)
    {
        t.A = t.Read(t.Var(_gameOver));
        t.CC = Logic8(t.CC, t.A);
        t.Cycles += 4 + Ext(_gameOver) + 3;
        if (!Mi(t.CC)) return;                                   // attract mode
        t.A = t.Read(t.Var(_troll));
        t.CC = Logic8(t.CC, t.A);
        t.Cycles += 4 + Ext(_troll) + 3;
        if (t.A == 0) return;
        t.A = 2;
        t.CC = Logic8(t.CC, 2);
        t.Write(t.Var(_enemyTime), 2);
        t.CC = Logic8(t.CC, 2);
        t.Cycles += 2 + 4 + Ext(_enemyTime);
    }

    /// <summary>SPDGLA: the winning gladiator scores 3,000, shown where the loser died.</summary>
    private void Bounty(Tx t)
    {
        foreach (var g in new[] { _glad1, _glad2 })
        {
            t.Write(t.Var(g), 0);
            t.CC = Clr(t.CC);
            t.Cycles += 6 + Ext(g);
        }
        t.A = 0x80;                                              // the true winning gladiator
        t.CC = Logic8(t.CC, t.A);
        t.Write(t.Y, 0x80);
        t.CC = Logic8(t.CC, t.A);
        t.X = t.Read16(t.X + _decision);
        t.CC = Logic16(t.CC, t.X);
        t.Push16(t.X);                                           // PSHS X
        t.A = 0x30;
        t.CC = Logic8(t.CC, t.A);
        t.Cycles += 2 + 4 + 5 + Ix(_decision) + 7 + 2 + 8;
        if (!_scoring.Add(t, tens: false)) throw new HandBackException();
        t.Cycles += 5;
        t.X = _scoreInAir;
        t.CC = Logic16(t.CC, t.X);
        t.D = (ushort)((_eggId << 8) | 0xFF);
        t.CC = Logic16(t.CC, t.D);
        t.Cycles += 3 + 3;
        CreateProcess(t);
        CopyPosition(t, xOffset: 6);
        t.A = _bountyMessage;
        t.CC = Logic8(t.CC, t.A);
        t.X = t.Read16(t.S);                                     // PULS X: the winner's decision block
        t.S += 2;
        t.B = t.Read(t.X + _const);                              // in the winner's colour
        t.CC = Logic8(t.CC, t.B);
        t.Write16(t.Y + _prDir, t.D);
        t.CC = Logic16(t.CC, t.D);
        t.Cycles += 2 + 7 + 4 + Ix(_const) + 5 + Ix(_prDir);
        Sound(t, _sndBounty);
        t.Cycles += 3;                                           // BRA SPDIE2
    }

    /// <summary>
    /// SPDIE2 onwards: the death puff, 50 points, the death sound, no longer a target, one
    /// life fewer, and game over for this player or both.
    /// </summary>
    private void PuffAndAccounting(Tx t)
    {
        t.X = _deathPuff;
        t.CC = Logic16(t.CC, t.X);
        t.D = 0x2000;
        t.CC = Logic16(t.CC, t.D);
        t.Cycles += 3 + 3;
        CreateProcess(t);
        CopyPosition(t, xOffset: 0);
        t.X = t.Read16(t.U + _decision);
        t.CC = Logic16(t.CC, t.X);
        t.A = 0x50;
        t.CC = Logic8(t.CC, t.A);
        t.Cycles += 5 + Ix(_decision) + 2 + 8;
        if (!_scoring.Add(t, tens: true)) throw new HandBackException();   // 50 points for dying
        t.Cycles += 5;
        t.Cycles += 8;
        CheckRomIntegrity(t);                                    // JSR LZAP
        t.Cycles += 5;
        Sound(t, _sndPlayer);

        // No longer a target for the enemies.
        ushort target1 = t.Read16(t.Var(_target1));
        Sub16(ref t.CC, t.U, target1);
        t.Cycles += 7 + Ext(_target1) + 3;
        if (t.U == target1)
        {
            t.D = t.Read16(t.Var(_target2));
            t.CC = Logic16(t.CC, t.D);
            t.Write16(t.Var(_target1), t.D);
            t.CC = Logic16(t.CC, t.D);
            t.A = t.Read(t.Var(_targetTime2));
            t.CC = Logic8(t.CC, t.A);
            t.Write(t.Var(_targetTime1), t.A);
            t.CC = Logic8(t.CC, t.A);
            t.Cycles += 5 + Ext(_target2) + 5 + Ext(_target1) + 4 + Ext(_targetTime2) + 4 + Ext(_targetTime1);
        }
        t.D = 0;
        t.CC = Logic16(t.CC, 0);
        t.Write16(t.Var(_target2), 0);
        t.CC = Logic16(t.CC, 0);
        t.Cycles += 3 + 5 + Ext(_target2);

        // One life fewer.
        t.X = t.Read16(t.U + _decision);
        t.CC = Logic16(t.CC, t.X);
        t.X = t.Read16(t.X + _score);
        t.CC = Logic16(t.CC, t.X);
        byte lives = Dec(ref t.CC, t.Read(t.X + 6));
        t.Write(t.X + 6, lives);
        t.Cycles += 5 + Ix(_decision) + 5 + Ix(_score) + 7 + 3;
        if (lives != 0) return;
        t.A = t.Read(t.Var(_gameOver));
        t.CC = Logic8(t.CC, t.A);
        t.Cycles += 4 + Ext(_gameOver) + 3;
        if (!Mi(t.CC)) return;                                   // attract mode: already over
        t.A = (byte)(t.Read(t.Var(_score1Lives)) | t.Read(t.Var(_score2Lives)));
        t.CC = Logic8(t.CC, t.A);
        t.Cycles += 4 + Ext(_score1Lives) + 4 + Ext(_score2Lives) + 3;
        if (t.A == 0)
        {
            // Both players out: the game-over process goes first in the list.
            ushort u = t.U;
            t.Push16(u);
            t.U = t.Read16(t.Var(_head));
            t.CC = Logic16(t.CC, t.U);
            t.X = _gameOverAll;
            t.CC = Logic16(t.CC, t.X);
            t.D = 0x3400;
            t.CC = Logic16(t.CC, t.D);
            t.Cycles += 7 + 5 + Ext(_head) + 3 + 3;
            CreateProcess(t);
            t.U = t.Read16(t.S);                                 // PULS U
            t.S += 2;
            t.Cycles += 7;
            return;
        }
        // This player's "game over" message for 3 seconds.
        t.X = _gameOverOne;
        t.CC = Logic16(t.CC, t.X);
        t.D = (ushort)((_playerId << 8) | 0xFF);
        t.CC = Logic16(t.CC, t.D);
        t.Cycles += 3 + 3;
        CreateProcess(t);
        t.A = 3 * 60 / 8;
        t.CC = Logic8(t.CC, t.A);
        t.Write(t.Y + _joyTime, t.A);
        t.CC = Logic8(t.CC, t.A);
        t.X = t.Read16(t.U + _decision);
        t.CC = Logic16(t.CC, t.X);
        t.Write16(t.Y + _decision, t.X);
        t.CC = Logic16(t.CC, t.X);
        t.Cycles += 2 + 4 + Ix(_joyTime) + 5 + Ix(_decision) + 5 + Ix(_decision);
    }

    /// <summary>
    /// LZAP: an anti-tamper check. Sums 34 bytes of ROM at LZAP1; if the sum is wrong it
    /// quietly corrupts the free process list (a bootleg ROM eventually crashes).
    /// </summary>
    private void CheckRomIntegrity(Tx t)
    {
        t.X = t.Read16(_zapPointer);
        t.CC = Logic16(t.CC, t.X);
        t.D = 34;
        t.CC = Logic16(t.CC, t.D);
        t.Cycles += 6 + 3;
        do
        {
            t.A = Add8(ref t.CC, t.A, t.Read(t.X), withCarry: false);
            t.X++;
            t.B = Dec(ref t.CC, t.B);
            t.Cycles += 6 + 2 + 3;
        }
        while (t.B != 0);
        Sub8(ref t.CC, t.A, 0x32);
        t.Cycles += 2 + 3;
        if (t.A == 0x32) return;
        t.X = t.Read16(t.Read16(_free));                         // LDX [PFREE]
        t.CC = Logic16(t.CC, t.X);
        t.Write(t.X + 1, Inc(ref t.CC, t.Read(t.X + 1)));
        t.Cycles += 10 + 7;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>JSR VCUPROC: a new process after U (Y = its block).</summary>
    private void CreateProcess(Tx t)
    {
        t.Cycles += 8 + 4;                                       // JSR VCUPROC / JMP CUPROC
        _scheduler.Create(t, afterRunning: false);
        t.Cycles += 5;
    }

    /// <summary>The new process (Y) appears where U is, X shifted by <paramref name="xOffset"/>.</summary>
    private void CopyPosition(Tx t, int xOffset)
    {
        t.D = t.Read16(t.U + _posX);
        t.CC = Logic16(t.CC, t.D);
        t.Cycles += 5 + Ix(_posX);
        if (xOffset != 0)
        {
            t.D = Add16(ref t.CC, t.D, (ushort)xOffset);
            t.Cycles += 4;
        }
        t.Write16(t.Y + _posX, t.D);
        t.CC = Logic16(t.CC, t.D);
        t.D = t.Read16(t.U + _posY);
        t.CC = Logic16(t.CC, t.D);
        t.Write16(t.Y + _posY, t.D);
        t.CC = Logic16(t.CC, t.D);
        t.Cycles += 5 + Ix(_posX) + 5 + Ix(_posY) + 5 + Ix(_posY);
    }

    /// <summary>LDX #sound / JSR VSND.</summary>
    private void Sound(Tx t, ushort table)
    {
        t.X = table;
        t.CC = Logic16(t.CC, t.X);
        t.Cycles += 3;
        _ground.CallSound(t);
    }

    /// <summary>PSHS X,U.</summary>
    private static void SaveXU(Tx t)
    {
        t.Push16(t.U);
        t.Push16(t.X);
        t.Cycles += 9;
    }

    /// <summary>PULS X,U (the PC part of PULS X,U,PC is the RTS).</summary>
    private static void RestoreXU(Tx t)
    {
        t.X = t.Read16(t.S);
        t.U = t.Read16(t.S + 2);
        t.S += 4;
        t.Cycles += 11 - 5;
    }
}
