namespace Joust.Assembler;

internal enum Op6800Kind
{
    Inherent,
    Branch,
    Memory8,         // imm8 / dir / idx / ext
    Memory16,        // imm16 / dir / idx / ext (CPX, LDS, LDX)
    Store,           // dir / idx / ext
    Modify,          // idx = op+$60, ext = op+$70 (no direct mode on the 6800)
    Jump,            // JSR: idx $AD / ext $BD
}

internal readonly record struct Op6800(Op6800Kind Kind, int Opcode);

/// <summary>The Motorola 6800 instruction set (the sound board's 6808), as AS assembles it.</summary>
internal static class Instructions6800
{
    public static readonly Dictionary<string, Op6800> Table = Build();

    private static Dictionary<string, Op6800> Build()
    {
        var t = new Dictionary<string, Op6800>(StringComparer.OrdinalIgnoreCase);
        void Inh(string n, int op) => t[n] = new Op6800(Op6800Kind.Inherent, op);

        Inh("NOP", 0x01); Inh("TAP", 0x06); Inh("TPA", 0x07); Inh("INX", 0x08); Inh("DEX", 0x09);
        Inh("CLV", 0x0A); Inh("SEV", 0x0B); Inh("CLC", 0x0C); Inh("SEC", 0x0D); Inh("CLI", 0x0E);
        Inh("SEI", 0x0F); Inh("SBA", 0x10); Inh("CBA", 0x11); Inh("TAB", 0x16); Inh("TBA", 0x17);
        Inh("DAA", 0x19); Inh("ABA", 0x1B); Inh("TSX", 0x30); Inh("INS", 0x31); Inh("PULA", 0x32);
        Inh("PULB", 0x33); Inh("DES", 0x34); Inh("TXS", 0x35); Inh("PSHA", 0x36); Inh("PSHB", 0x37);
        Inh("RTS", 0x39); Inh("RTI", 0x3B); Inh("WAI", 0x3E); Inh("SWI", 0x3F);

        string[] unary = ["NEG", "", "", "COM", "LSR", "", "ROR", "ASR", "ASL", "ROL", "DEC", "", "INC", "TST", "JMP", "CLR"];
        for (int i = 0; i < unary.Length; i++)
        {
            if (unary[i] == "") continue;
            t[unary[i]] = new Op6800(Op6800Kind.Modify, i);
            if (unary[i] == "JMP") continue;
            Inh(unary[i] + "A", 0x40 + i);
            Inh(unary[i] + "B", 0x50 + i);
        }
        t["LSL"] = t["ASL"]; t["LSLA"] = t["ASLA"]; t["LSLB"] = t["ASLB"];

        string[] cond = ["RA", "", "HI", "LS", "CC", "CS", "NE", "EQ", "VC", "VS", "PL", "MI", "GE", "LT", "GT", "LE"];
        for (int i = 0; i < cond.Length; i++)
            if (cond[i] != "") t["B" + cond[i]] = new Op6800(Op6800Kind.Branch, 0x20 + i);
        t["BHS"] = t["BCC"]; t["BLO"] = t["BCS"];
        t["BSR"] = new Op6800(Op6800Kind.Branch, 0x8D);

        void Acc(string suffix, int bas)
        {
            // Motorola names: SUBA, ..., LDAA, STAA, EORA, ADCA, ORAA, ADDA (and the B forms).
            string[] names = ["SUB", "CMP", "SBC", "", "AND", "BIT", "LDA", "STA", "EOR", "ADC", "ORA", "ADD"];
            for (int i = 0; i < names.Length; i++)
            {
                if (names[i] == "") continue;
                var kind = names[i] == "STA" ? Op6800Kind.Store : Op6800Kind.Memory8;
                t[names[i] + suffix] = new Op6800(kind, bas + i);
            }
        }
        Acc("A", 0x80);
        Acc("B", 0xC0);
        // AS also accepts the 6809-style names (LDA, STB, ORA ...).
        foreach (var (motorola, alias) in new[]
        {
            ("LDAA", "LDA"), ("STAA", "STA"), ("ORAA", "ORA"), ("LDAB", "LDB"), ("STAB", "STB"), ("ORAB", "ORB"),
        })
            t[alias] = t[motorola];

        t["CPX"] = new Op6800(Op6800Kind.Memory16, 0x8C);
        t["LDS"] = new Op6800(Op6800Kind.Memory16, 0x8E);
        t["STS"] = new Op6800(Op6800Kind.Store, 0x8F);
        t["LDX"] = new Op6800(Op6800Kind.Memory16, 0xCE);
        t["STX"] = new Op6800(Op6800Kind.Store, 0xCF);
        t["JSR"] = new Op6800(Op6800Kind.Jump, 0x8D);
        return t;
    }
}
