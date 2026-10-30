using Joust.Emulation;
using static Joust.Emulation.Alu6809;

namespace Joust.Port.Routines;

/// <summary>
/// SCRHUN / SCRTEN (JOUSTRV4.ASM): add points to a player's BCD score.
/// X = the player's decision block (its DSCORE entry points at the score area); A = BCD digits
/// (SCRHUN: thousands/hundreds, SCRTEN: tens/hundreds packed "backwards").
/// Score area: [0..3] BCD score (millions first), [4] "score changed" counter, [7] level-pass
/// offset, [8..9] next extra-man level.
/// When the new score earns an extra mount the original also awards the life, plays the
/// replay sound and updates the audits; that path is handed back to the original code.
/// </summary>
public sealed class Scoring(Symbols s)
{
    private readonly int _decisionScoreOffset = s["DSCORE"];
    private readonly ushort _levelPass = s["LEVPAS"];

    public ushort Hundreds { get; } = s["SCRHUN"];
    public ushort Tens { get; } = s["SCRTEN"];

    /// <summary>The routine at <paramref name="address"/> if it is a score routine, else null.</summary>
    public bool? IsTens(ushort address) => address == Tens ? true : address == Hundreds ? false : null;

    /// <summary>Body excluding the RTS. False (transaction to be discarded) when an extra mount is earned.</summary>
    public bool Add(Tx t, bool tens)
    {
        ushort area = t.Read16(t.X + _decisionScoreOffset);
        t.CC = Logic16(t.CC, area);
        t.Cycles += 7 + 3;                                       // LDY DSCORE,X / BEQ
        if (area == 0)
        {
            t.Y = 0;                                             // object has no score (enemies)
            return true;
        }

        var score = new byte[10];
        for (int i = 0; i < score.Length; i++)
            score[i] = t.Read(area + i);
        byte a = t.A, b = t.B;
        byte cc = t.CC;

        if (tens)
        {
            b = a;
            a = Logic(ref cc, (byte)(a & 0xF0));                 // tens digit
            b = Logic(ref cc, (byte)(b & 0x0F));                 // hundreds digit
            a = Add8(ref cc, a, score[3], withCarry: false);
            score[3] = Logic(ref cc, Daa(ref cc, a));
            a = Add8(ref cc, b, score[2], withCarry: true);
            t.Cycles += 6 + 2 + 2 + 5 + 2 + 5 + 6 + 5;
        }
        else
        {
            a = Add8(ref cc, a, score[2], withCarry: false);
            t.Cycles += 5 + 3;                                   // ADDA 2,Y / BRA SCRCOM
        }

        // SCRCOM: ripple the BCD carry up through the higher digits.
        score[2] = Logic(ref cc, Daa(ref cc, a));
        score[4] = Inc(ref cc, score[4]);                        // mark "score changed"
        score[1] = Logic(ref cc, Daa(ref cc, Add8(ref cc, Logic(ref cc, score[1]), 0, withCarry: true)));
        a = Daa(ref cc, Add8(ref cc, Logic(ref cc, score[0]), 0, withCarry: true));
        t.Cycles += 2 + 5 + 7 + 5 + 2 + 2 + 5 + 4 + 2 + 2 + 3;
        if ((cc & C) != 0)
        {
            a = Add8(ref cc, a, 0x10, withCarry: false);         // past 9,999,999: tag the top digit
            t.Cycles += 2;
        }
        score[0] = Logic(ref cc, a);
        t.Cycles += 4;

        ushort levelPass = t.Read16(t.Var(_levelPass));
        cc = Logic16(cc, levelPass);
        t.Cycles += 5 + Symbols.AddressCost(_levelPass) + 3;     // LDD LEVPAS / BEQ
        if (levelPass == 0)
        {
            a = (byte)(levelPass >> 8);
            b = (byte)levelPass;
        }
        else
        {
            // SCRLEV: has the score passed the next extra-man level?
            cc = Logic16(cc, (ushort)((score[1] << 8) | score[2]));
            a = Daa(ref cc, Add8(ref cc, score[1], score[7], withCarry: false));
            b = score[2];
            Sub16(ref cc, (ushort)((a << 8) | b), (ushort)((score[8] << 8) | score[9]));
            t.Cycles += 6 + 5 + 2 + 8 + 3;
            if ((cc & C) == 0)
                return false;                                    // extra mount earned: original handles it
        }

        for (int i = 0; i < score.Length; i++)
            t.Write(area + i, score[i]);
        t.A = a;
        t.B = b;
        t.Y = area;
        t.CC = cc;
        return true;
    }

    private static byte Logic(ref byte cc, byte value)
    {
        cc = Logic8(cc, value);
        return value;
    }
}

/// <summary>SCRHUN / SCRTEN as standalone hooks.</summary>
public sealed class AddScoreRoutine(bool tens) : NativeRoutine
{
    private Scoring _scoring = null!;

    public override string Symbol => tens ? "SCRTEN" : "SCRHUN";
    public override int MaxCycles => 0;                          // checked at commit

    public override void Bind(IReadOnlyDictionary<string, long> s) => _scoring = new Scoring(new Symbols(s));

    public override bool Execute(NativeContext c)
    {
        var t = new Tx(c);
        return _scoring.Add(t, tens) && t.Commit();
    }
}

/// <summary>
/// Thrown inside a transaction to hand the whole call back to the original code (for rare
/// cases not ported yet). Nothing has been applied when it is thrown.
/// </summary>
public sealed class HandBackException : Exception;
