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
public sealed class AddScoreRoutine(bool tens) : NativeRoutine
{
    private int _decisionScoreOffset;
    private ushort _levelPass;

    public override string Symbol => tens ? "SCRTEN" : "SCRHUN";
    public override int MaxCycles => tens ? 125 : 100;

    public override void Bind(IReadOnlyDictionary<string, long> s)
    {
        _decisionScoreOffset = Sym(s, "DSCORE");
        _levelPass = Sym(s, "LEVPAS");
    }

    public override bool Execute(NativeContext c)
    {
        var cpu = c.Cpu;
        ushort area = c.Read16(cpu.X + _decisionScoreOffset);
        byte cc = Logic16(cpu.CC, area);
        int cycles = 7 + 3;                                   // LDY DSCORE,X / BEQ
        if (area == 0)
        {
            cpu.Y = 0;
            return Finish(c, cc, cycles);                     // object has no score (enemies)
        }

        var score = new byte[10];
        for (int i = 0; i < score.Length; i++)
            score[i] = c.Read(area + i);
        byte a = cpu.A, b = cpu.B;

        if (tens)
        {
            b = a;
            a = Logic(ref cc, (byte)(a & 0xF0));              // tens digit
            b = Logic(ref cc, (byte)(b & 0x0F));              // hundreds digit
            a = Add8(ref cc, a, score[3], withCarry: false);
            score[3] = Logic(ref cc, Daa(ref cc, a));
            a = Add8(ref cc, b, score[2], withCarry: true);
            cycles += 6 + 2 + 2 + 5 + 2 + 5 + 6 + 5;
        }
        else
        {
            a = Add8(ref cc, a, score[2], withCarry: false);
            cycles += 5 + 3;                                  // ADDA 2,Y / BRA SCRCOM
        }

        // SCRCOM: ripple the BCD carry up through the higher digits.
        score[2] = Logic(ref cc, Daa(ref cc, a));
        score[4] = Inc(ref cc, score[4]);                     // mark "score changed"
        score[1] = Logic(ref cc, Daa(ref cc, Add8(ref cc, Logic(ref cc, score[1]), 0, withCarry: true)));
        a = Daa(ref cc, Add8(ref cc, Logic(ref cc, score[0]), 0, withCarry: true));
        cycles += 2 + 5 + 7 + 5 + 2 + 2 + 5 + 4 + 2 + 2 + 3;
        if ((cc & C) != 0)
        {
            a = Add8(ref cc, a, 0x10, withCarry: false);      // past 9,999,999: tag the top digit
            cycles += 2;
        }
        score[0] = Logic(ref cc, a);
        cycles += 4;

        ushort levelPass = c.Read16(c.Direct(_levelPass));
        cc = Logic16(cc, levelPass);
        cycles += 5 + 3;                                      // LDD LEVPAS / BEQ
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
            cycles += 6 + 5 + 2 + 8 + 3;
            if ((cc & C) == 0)
                return false;                                 // extra mount earned: original handles it
        }

        for (int i = 0; i < score.Length; i++)
            c.Write(area + i, score[i]);
        cpu.A = a;
        cpu.B = b;
        cpu.Y = area;
        return Finish(c, cc, cycles);
    }

    private static byte Logic(ref byte cc, byte value)
    {
        cc = Logic8(cc, value);
        return value;
    }

    private static bool Finish(NativeContext c, byte cc, int cycles)
    {
        c.Cpu.CC = cc;
        c.Return(cycles + 5);
        return true;
    }
}
