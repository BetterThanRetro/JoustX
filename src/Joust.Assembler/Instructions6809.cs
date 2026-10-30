namespace Joust.Assembler;

internal enum OpKind
{
    Inherent,
    ImmediateOnly,   // ORCC, ANDCC, CWAI
    Branch8,
    Branch16,
    Memory8,         // imm8 / dir / idx / ext
    Memory16,        // imm16 / dir / idx / ext
    Store,           // dir / idx / ext (no immediate)
    Modify,          // NEG..CLR, JMP: dir=op, idx=op+$60, ext=op+$70
    Lea,             // indexed only
    PushS,
    PushU,
    Transfer,        // TFR, EXG
}

internal readonly record struct OpInfo(OpKind Kind, int Opcode, int Prefix = 0);

internal static class Instructions6809
{
    public static readonly Dictionary<string, OpInfo> Table = Build();

    private static Dictionary<string, OpInfo> Build()
    {
        var t = new Dictionary<string, OpInfo>(StringComparer.OrdinalIgnoreCase);
        void Inh(string n, int op, int pre = 0) => t[n] = new OpInfo(OpKind.Inherent, op, pre);

        Inh("NOP", 0x12); Inh("SYNC", 0x13); Inh("DAA", 0x19); Inh("SEX", 0x1D);
        Inh("RTS", 0x39); Inh("ABX", 0x3A); Inh("RTI", 0x3B); Inh("MUL", 0x3D);
        Inh("SWI", 0x3F); Inh("SWI2", 0x3F, 0x10); Inh("SWI3", 0x3F, 0x11);

        string[] unary = ["NEG", "", "", "COM", "LSR", "", "ROR", "ASR", "ASL", "ROL", "DEC", "", "INC", "TST", "JMP", "CLR"];
        for (int i = 0; i < unary.Length; i++)
        {
            if (unary[i] == "") continue;
            t[unary[i]] = new OpInfo(OpKind.Modify, i);
            if (unary[i] == "JMP") continue;
            Inh(unary[i] + "A", 0x40 + i);
            Inh(unary[i] + "B", 0x50 + i);
        }
        t["LSL"] = t["ASL"]; t["LSLA"] = t["ASLA"]; t["LSLB"] = t["ASLB"];

        t["ORCC"] = new OpInfo(OpKind.ImmediateOnly, 0x1A);
        t["ANDCC"] = new OpInfo(OpKind.ImmediateOnly, 0x1C);
        t["CWAI"] = new OpInfo(OpKind.ImmediateOnly, 0x3C);
        t["EXG"] = new OpInfo(OpKind.Transfer, 0x1E);
        t["TFR"] = new OpInfo(OpKind.Transfer, 0x1F);
        t["PSHS"] = new OpInfo(OpKind.PushS, 0x34);
        t["PULS"] = new OpInfo(OpKind.PushS, 0x35);
        t["PSHU"] = new OpInfo(OpKind.PushU, 0x36);
        t["PULU"] = new OpInfo(OpKind.PushU, 0x37);
        t["LEAX"] = new OpInfo(OpKind.Lea, 0x30);
        t["LEAY"] = new OpInfo(OpKind.Lea, 0x31);
        t["LEAS"] = new OpInfo(OpKind.Lea, 0x32);
        t["LEAU"] = new OpInfo(OpKind.Lea, 0x33);

        string[] cond = ["RA", "RN", "HI", "LS", "CC", "CS", "NE", "EQ", "VC", "VS", "PL", "MI", "GE", "LT", "GT", "LE"];
        for (int i = 0; i < cond.Length; i++)
        {
            t["B" + cond[i]] = new OpInfo(OpKind.Branch8, 0x20 + i);
            t["LB" + cond[i]] = new OpInfo(OpKind.Branch16, 0x20 + i, 0x10);
        }
        t["BHS"] = t["BCC"]; t["BLO"] = t["BCS"];
        t["LBHS"] = t["LBCC"]; t["LBLO"] = t["LBCS"];
        t["LBRA"] = new OpInfo(OpKind.Branch16, 0x16);
        t["LBSR"] = new OpInfo(OpKind.Branch16, 0x17);
        t["BSR"] = new OpInfo(OpKind.Branch8, 0x8D);

        void Acc(string suffix, int bas)
        {
            string[] names = ["SUB", "CMP", "SBC", "", "AND", "BIT", "LD", "ST", "EOR", "ADC", "OR", "ADD"];
            for (int i = 0; i < names.Length; i++)
            {
                if (names[i] == "") continue;
                var kind = names[i] == "ST" ? OpKind.Store : OpKind.Memory8;
                t[names[i] + suffix] = new OpInfo(kind, bas + i);
            }
        }
        Acc("A", 0x80);
        Acc("B", 0xC0);

        void Wide(string n, int op, int pre = 0) => t[n] = new OpInfo(OpKind.Memory16, op, pre);
        void St(string n, int op, int pre = 0) => t[n] = new OpInfo(OpKind.Store, op, pre);
        Wide("SUBD", 0x83); Wide("CMPX", 0x8C); Wide("LDX", 0x8E); St("STX", 0x8F);
        Wide("ADDD", 0xC3); Wide("LDD", 0xCC); St("STD", 0xCD); Wide("LDU", 0xCE); St("STU", 0xCF);
        Wide("CMPD", 0x83, 0x10); Wide("CMPY", 0x8C, 0x10); Wide("LDY", 0x8E, 0x10); St("STY", 0x8F, 0x10);
        Wide("LDS", 0xCE, 0x10); St("STS", 0xCF, 0x10);
        Wide("CMPU", 0x83, 0x11); Wide("CMPS", 0x8C, 0x11);
        St("JSR", 0x8D);
        return t;
    }

    public static readonly Dictionary<string, int> TransferRegs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["D"] = 0, ["X"] = 1, ["Y"] = 2, ["U"] = 3, ["S"] = 4, ["PC"] = 5,
        ["A"] = 8, ["B"] = 9, ["CC"] = 10, ["DP"] = 11, ["DPR"] = 11,
    };

    public static readonly Dictionary<string, int> IndexRegs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["X"] = 0x00, ["Y"] = 0x20, ["U"] = 0x40, ["S"] = 0x60,
    };
}
