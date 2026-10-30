using static Joust.Emulation.Alu6809;

namespace Joust.Port.Routines;

/// <summary>
/// The collision sweeps (JOUSTRV4.ASM), each a process that runs every tick:
/// <list type="bullet">
/// <item>PLYCOL: each live player (U) against every live object after it in the process list
///       (X); also counts down the "time until the enemies target this player" timers.</item>
/// <item>OPPCOL: each live object against every later live object sharing id bit 0
///       (enemies and pterodactyls against each other).</item>
/// </list>
/// HITEM runs inline for each pair. When it returns with carry set, U is done (it died or
/// bounced off) and the sweep moves on to the next U. A kill leaves for the original death
/// routine; the sweep resumes at the return address after BSR HITEM.
/// Stops at a loop label when the next step might be interrupted, like the other slices.
/// </summary>
public sealed class Sweeps
{
    public enum L { PlyTop, PlyAfterNap, PlyOuter, Ply35, Ply35After, Ply45, Ply45After, OppTop, OppOuter, OppIn, OppAfter }

    private readonly Combat _combat;
    private readonly Scheduler _scheduler;
    private readonly int _link, _id;
    private readonly ushort _target1, _target2, _colU;
    private readonly byte _livePlayer;
    private readonly Dictionary<L, ushort> _address = [];

    public Sweeps(Symbols s, Combat combat, Scheduler scheduler)
    {
        _combat = combat;
        _scheduler = scheduler;
        _link = s["PLINK"];
        _id = s["PID"];
        _target1 = s["TARTM1"];
        _target2 = s["TARTM2"];
        _colU = s["COLU"];
        _livePlayer = (byte)(0x80 + s.Value("PLYID"));

        // PCNAP 1 = LDA #1 / JSR VNAPTPC (5 bytes). Inner loops: LDX PLINK,X / BEQ / LDA PID,X /
        // BPL / BSR HITEM (10 bytes); OPPCOL's adds ANDA PID,U / BITA #1 / BEQ (16 bytes).
        void At(L l, string label, int offset = 0) => _address[l] = (ushort)(s[label] + offset);
        At(L.PlyTop, "PLYCOL");
        At(L.PlyAfterNap, "PLYCOL", 5);
        At(L.PlyOuter, "PLYCOL.25S");
        At(L.Ply35, "PLYCOL.35S");
        At(L.Ply35After, "PLYCOL.35S", 10);
        At(L.Ply45, "PLYCOL.45S");
        At(L.Ply45After, "PLYCOL.45S", 10);
        At(L.OppTop, "OPPCOL");
        At(L.OppOuter, "OPPCOL.25S");
        At(L.OppIn, "OPPCOL.45S");
        At(L.OppAfter, "OPPCOL.45S", 16);
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
                // Something rare inside this pair's joust: let the original run it from here.
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
            case L.PlyTop:
            case L.OppTop:
                t.A = 1;
                t.CC = Logic8(t.CC, 1);
                t.Cycles += 2 + 8 + 4;                           // PCNAP 1: LDA / JSR VNAPTPC / JMP NAPTPC
                t.Push16(_address[label == L.PlyTop ? L.PlyAfterNap : L.OppOuter]);
                return (default, _scheduler.Nap(t, fromJsr: true));

            case L.PlyAfterNap:
                CountDown(t, _target1);
                CountDown(t, _target2);
                return (L.PlyOuter, null);

            case L.PlyOuter:
            case L.OppOuter:
            {
                bool players = label == L.PlyOuter;
                t.U = t.Read16(t.U + _link);                     // next U
                t.CC = Logic16(t.CC, t.U);
                t.Cycles += 5 + Ix(_link) + 3;
                if (t.U == 0) return (players ? L.PlyTop : L.OppTop, null);
                t.A = t.Read(t.U + _id);
                t.CC = Logic8(t.CC, t.A);
                t.Cycles += 4 + Ix(_id) + 3;
                if (players)
                {
                    Sub8(ref t.CC, t.A, _livePlayer);
                    t.Cycles += 2;
                    if (t.A != _livePlayer) return (label, null);
                }
                else if (!Mi(t.CC))
                {
                    return (label, null);                        // not colliding
                }
                t.Write16(t.Var(_colU), t.U);
                t.CC = Logic16(t.CC, t.U);
                t.X = t.U;                                       // LEAX ,U
                t.CC = Set(t.CC, Z, t.X == 0);
                t.Cycles += 5 + Ext(_colU) + 4;
                return (players ? L.Ply35 : L.OppIn, null);
            }

            case L.Ply35:
            case L.Ply45:
            case L.OppIn:
            {
                t.X = t.Read16(t.X + _link);                     // next X
                t.CC = Logic16(t.CC, t.X);
                t.Cycles += 5 + Ix(_link) + 3;
                if (t.X == 0)
                    return (label == L.Ply35 ? L.PlyTop : label == L.Ply45 ? L.PlyOuter : L.OppOuter, null);
                t.A = t.Read(t.X + _id);
                t.CC = Logic8(t.CC, t.A);
                t.Cycles += 4 + Ix(_id) + 3;
                if (!Mi(t.CC)) return (label, null);             // not colliding
                if (label == L.OppIn)
                {
                    t.A = (byte)(t.A & t.Read(t.U + _id));
                    t.CC = Logic8(t.CC, t.A);
                    t.CC = Logic8(t.CC, (byte)(t.A & 0x01));
                    t.Cycles += 4 + Ix(_id) + 2 + 3;
                    if ((t.A & 0x01) == 0) return (label, null); // not both enemy-kind
                }
                L after = label == L.Ply35 ? L.Ply35After : label == L.Ply45 ? L.Ply45After : L.OppAfter;
                t.Cycles += 7;                                   // BSR HITEM
                t.Push16(_address[after]);
                ushort? exit = _combat.Hit(t);
                if (exit != null) return (default, exit);
                t.S += 2;
                t.Cycles += 5;
                return (after, null);
            }

            case L.Ply35After:
                t.Cycles += 3;                                   // BCS .20S
                return (Cs(t.CC) ? L.PlyOuter : L.Ply45, null);

            case L.Ply45After:
            case L.OppAfter:
                t.Cycles += 3;                                   // BCC .40S
                if (!Cs(t.CC)) return (label == L.Ply45After ? L.Ply45 : L.OppIn, null);
                t.Cycles += 3;                                   // BRA .20S
                return (label == L.Ply45After ? L.PlyOuter : L.OppOuter, null);
        }
        throw new InvalidOperationException($"unknown label {label}");
    }

    /// <summary>LDA timer / BEQ / DEC timer.</summary>
    private static void CountDown(Tx t, ushort timer)
    {
        int a = t.Var(timer);
        t.A = t.Read(a);
        t.CC = Logic8(t.CC, t.A);
        t.Cycles += 4 + Ext(timer) + 3;
        if (t.A != 0)
        {
            t.Write(a, Dec(ref t.CC, t.A));
            t.Cycles += 6 + Ext(timer);
        }
    }
}
