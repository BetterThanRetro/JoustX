namespace Joust.Emulation;

/// <summary>Memory and I/O as the 6800 sees it.</summary>
public interface IBus6800
{
    byte Read(ushort address);
    void Write(ushort address, byte value);
}

/// <summary>
/// Motorola 6800 (the sound board's 6808), with the documented cycle counts. Undefined
/// opcodes throw: the sound ROM never runs one.
/// </summary>
public sealed class Cpu6800(IBus6800 bus)
{
    private const byte C = 0x01, V = 0x02, Z = 0x04, N = 0x08, I = 0x10, H = 0x20;

    public byte A, B, CC = 0xC0 | I;
    public ushort X, SP, PC;
    public long Cycles;
    public bool IrqLine, NmiLine;
    private bool _waiting, _nmiPrevious;

    public void Reset()
    {
        CC = 0xC0 | I;
        _waiting = false;
        PC = Read16(0xFFFE);
    }

    /// <summary>Runs one instruction (or takes an interrupt); returns its cycles.</summary>
    public int Step()
    {
        if (NmiLine && !_nmiPrevious)
        {
            _nmiPrevious = true;
            return Interrupt(0xFFFC);
        }
        _nmiPrevious = NmiLine;
        if (IrqLine && (CC & I) == 0) return Interrupt(0xFFF8);
        if (_waiting)
        {
            Cycles += 1;
            return 1;
        }
        int c = Execute(Fetch());
        Cycles += c;
        return c;
    }

    private int Interrupt(ushort vector)
    {
        int c = 12;
        if (_waiting) c = 4;                                     // WAI already stacked the registers
        else PushAll();
        _waiting = false;
        CC |= I;
        PC = Read16(vector);
        Cycles += c;
        return c;
    }

    private void PushAll()
    {
        Push16(PC);
        Push16(X);
        Push(A);
        Push(B);
        Push(CC);
    }

    // ------------------------------------------------------------------ memory

    private byte Fetch() => bus.Read(PC++);
    private ushort Fetch16() { ushort v = Read16(PC); PC += 2; return v; }
    private byte Read(int a) => bus.Read((ushort)a);
    private void Write(int a, byte v) => bus.Write((ushort)a, v);
    private ushort Read16(int a) => (ushort)((Read(a) << 8) | Read((ushort)(a + 1)));
    private void Write16(int a, ushort v) { Write(a, (byte)(v >> 8)); Write((ushort)(a + 1), (byte)v); }
    private void Push(byte v) { Write(SP, v); SP--; }
    private byte Pull() { SP++; return Read(SP); }
    private void Push16(ushort v) { Push((byte)v); Push((byte)(v >> 8)); }
    private ushort Pull16() { int hi = Pull(); return (ushort)((hi << 8) | Pull()); }

    private int Direct() => Fetch();
    private int Indexed() => (ushort)(X + Fetch());
    private int Extended() => Fetch16();

    /// <summary>Operand address for the $x0-$xF column groups: 1 = direct, 2 = indexed, 3 = extended.</summary>
    private int Address(int mode) => mode switch { 1 => Direct(), 2 => Indexed(), _ => Extended() };

    // ------------------------------------------------------------------ flags

    private void Nz(byte r) => CC = (byte)((CC & ~(N | Z)) | ((r & 0x80) != 0 ? N : 0) | (r == 0 ? Z : 0));
    private void Nz16(ushort r) => CC = (byte)((CC & ~(N | Z)) | ((r & 0x8000) != 0 ? N : 0) | (r == 0 ? Z : 0));
    private void Logic(byte r) { Nz(r); CC &= unchecked((byte)~V); }
    private void Flag(byte f, bool on) => CC = on ? (byte)(CC | f) : (byte)(CC & ~f);

    private byte Add(byte a, byte b, bool carry)
    {
        int cin = carry && (CC & C) != 0 ? 1 : 0;
        int r = a + b + cin;
        Flag(H, ((a & 0x0F) + (b & 0x0F) + cin) > 0x0F);
        Flag(C, r > 0xFF);
        Flag(V, ((a ^ r) & (b ^ r) & 0x80) != 0);
        Nz((byte)r);
        return (byte)r;
    }

    private byte Sub(byte a, byte b, bool carry)
    {
        int cin = carry && (CC & C) != 0 ? 1 : 0;
        int r = a - b - cin;
        Flag(C, r < 0);
        Flag(V, ((a ^ b) & (a ^ r) & 0x80) != 0);
        Nz((byte)r);
        return (byte)r;
    }

    private byte Unary(int op, byte m)
    {
        byte r;
        switch (op)
        {
            case 0x0:                                            // NEG
                r = (byte)-m;
                Nz(r); Flag(V, r == 0x80); Flag(C, r != 0);
                return r;
            case 0x3:                                            // COM
                r = (byte)~m;
                Logic(r); CC |= C;
                return r;
            case 0x4:                                            // LSR
                r = (byte)(m >> 1);
                Flag(C, (m & 1) != 0); Nz(r); Flag(V, (CC & C) != 0);   // V = N ^ C, N = 0
                return r;
            case 0x6:                                            // ROR
                r = (byte)((m >> 1) | ((CC & C) != 0 ? 0x80 : 0));
                Flag(C, (m & 1) != 0); Nz(r); ShiftV();
                return r;
            case 0x7:                                            // ASR
                r = (byte)((m >> 1) | (m & 0x80));
                Flag(C, (m & 1) != 0); Nz(r); ShiftV();
                return r;
            case 0x8:                                            // ASL
                r = (byte)(m << 1);
                Flag(C, (m & 0x80) != 0); Nz(r); ShiftV();
                return r;
            case 0x9:                                            // ROL
                r = (byte)((m << 1) | ((CC & C) != 0 ? 1 : 0));
                Flag(C, (m & 0x80) != 0); Nz(r); ShiftV();
                return r;
            case 0xA:                                            // DEC
                r = (byte)(m - 1);
                Nz(r); Flag(V, m == 0x80);
                return r;
            case 0xC:                                            // INC
                r = (byte)(m + 1);
                Nz(r); Flag(V, m == 0x7F);
                return r;
            case 0xD:                                            // TST
                Logic(m); CC &= unchecked((byte)~C);
                return m;
            case 0xF:                                            // CLR
                CC = (byte)((CC & ~(N | V | C)) | Z);
                return 0;
        }
        throw Undefined(op);
    }

    private void ShiftV() => Flag(V, ((CC & N) != 0) ^ ((CC & C) != 0));

    private InvalidOperationException Undefined(int op) =>
        new($"undefined 6800 opcode ${op:X2} at ${(ushort)(PC - 1):X4}");

    private bool Branch(int cond) => cond switch
    {
        0x0 => true,
        0x2 => (CC & (C | Z)) == 0,                              // BHI
        0x3 => (CC & (C | Z)) != 0,                              // BLS
        0x4 => (CC & C) == 0,
        0x5 => (CC & C) != 0,
        0x6 => (CC & Z) == 0,
        0x7 => (CC & Z) != 0,
        0x8 => (CC & V) == 0,
        0x9 => (CC & V) != 0,
        0xA => (CC & N) == 0,
        0xB => (CC & N) != 0,
        0xC => ((CC & N) != 0) == ((CC & V) != 0),               // BGE
        0xD => ((CC & N) != 0) != ((CC & V) != 0),               // BLT
        0xE => (CC & Z) == 0 && ((CC & N) != 0) == ((CC & V) != 0),
        _ => (CC & Z) != 0 || ((CC & N) != 0) != ((CC & V) != 0),
    };

    // ------------------------------------------------------------------ execute

    private int Execute(byte op)
    {
        switch (op)
        {
            case 0x01: return 2;                                 // NOP
            case 0x06: CC = (byte)(A | 0xC0); return 2;          // TAP
            case 0x07: A = CC; return 2;                         // TPA
            case 0x08: X++; Flag(Z, X == 0); return 4;           // INX
            case 0x09: X--; Flag(Z, X == 0); return 4;           // DEX
            case 0x0A: CC &= unchecked((byte)~V); return 2;
            case 0x0B: CC |= V; return 2;
            case 0x0C: CC &= unchecked((byte)~C); return 2;
            case 0x0D: CC |= C; return 2;
            case 0x0E: CC &= unchecked((byte)~I); return 2;
            case 0x0F: CC |= I; return 2;
            case 0x10: A = Sub(A, B, false); return 2;           // SBA
            case 0x11: Sub(A, B, false); return 2;               // CBA
            case 0x16: B = A; Logic(B); return 2;                // TAB
            case 0x17: A = B; Logic(A); return 2;                // TBA
            case 0x19: Daa(); return 2;
            case 0x1B: A = Add(A, B, false); return 2;           // ABA
            case 0x30: X = (ushort)(SP + 1); return 4;           // TSX
            case 0x31: SP++; return 4;                           // INS
            case 0x32: A = Pull(); return 4;
            case 0x33: B = Pull(); return 4;
            case 0x34: SP--; return 4;                           // DES
            case 0x35: SP = (ushort)(X - 1); return 4;           // TXS
            case 0x36: Push(A); return 4;
            case 0x37: Push(B); return 4;
            case 0x39: PC = Pull16(); return 5;                  // RTS
            case 0x3B:                                           // RTI
                CC = (byte)(Pull() | 0xC0); B = Pull(); A = Pull(); X = Pull16(); PC = Pull16();
                return 10;
            case 0x3E: PushAll(); _waiting = true; return 9;     // WAI
            case 0x3F: PushAll(); CC |= I; PC = Read16(0xFFFA); return 12;   // SWI
            case 0x8D:                                           // BSR
            {
                sbyte d = (sbyte)Fetch();
                Push16(PC);
                PC = (ushort)(PC + d);
                return 8;
            }
            case 0xAD: { int a = Indexed(); Push16(PC); PC = (ushort)a; return 8; }   // JSR n,X
            case 0xBD: { int a = Extended(); Push16(PC); PC = (ushort)a; return 9; }  // JSR ext
            case 0x6E: PC = (ushort)Indexed(); return 4;         // JMP n,X
            case 0x7E: PC = Fetch16(); return 3;                 // JMP ext
        }

        int hi = op >> 4, lo = op & 0x0F;
        if (hi == 0x2)
        {
            sbyte d = (sbyte)Fetch();
            if (lo == 1) throw Undefined(op);
            if (Branch(lo)) PC = (ushort)(PC + d);
            return 4;
        }
        if (hi is >= 0x4 and <= 0x7)
        {
            if (lo is 0x1 or 0x2 or 0x5 or 0xB or 0xE) throw Undefined(op);
            switch (hi)
            {
                case 0x4: A = Unary(lo, A); return 2;
                case 0x5: B = Unary(lo, B); return 2;
                default:
                {
                    int a = hi == 0x6 ? Indexed() : Extended();
                    byte r = Unary(lo, Read(a));
                    if (lo != 0xD) Write(a, r);                  // TST only reads
                    return hi == 0x6 ? 7 : 6;
                }
            }
        }
        if (hi >= 0x8)
            return Accumulator(op, hi, lo);
        throw Undefined(op);
    }

    /// <summary>$80-$FF: the A (top half $8x-$Bx) and B ($Cx-$Fx) operations, and the 16-bit loads/stores.</summary>
    private int Accumulator(byte op, int hi, int lo)
    {
        int mode = hi & 3;                                       // 0 imm, 1 dir, 2 idx, 3 ext
        bool isB = hi >= 0xC;
        int baseCycles = mode switch { 0 => 2, 1 => 3, 2 => 5, _ => 4 };

        switch (lo)
        {
            case 0xC when !isB:                                  // CPX
            {
                ushort m = mode == 0 ? Fetch16() : Read16(Address(mode));
                int r = X - m;
                Nz16((ushort)r);
                Flag(V, ((X ^ m) & (X ^ r) & 0x8000) != 0);
                return baseCycles + 1;
            }
            case 0xE:                                            // LDS / LDX
            {
                ushort m = mode == 0 ? Fetch16() : Read16(Address(mode));
                if (isB) X = m; else SP = m;
                Nz16(m); CC &= unchecked((byte)~V);
                return baseCycles + 1;
            }
            case 0xF:                                            // STS / STX
            {
                if (mode == 0) throw Undefined(op);
                ushort v = isB ? X : SP;
                Write16(Address(mode), v);
                Nz16(v); CC &= unchecked((byte)~V);
                return baseCycles + 2;
            }
            case 0x3 or 0xC or 0xD:
                throw Undefined(op);
            case 0x7:                                            // STA
            {
                if (mode == 0) throw Undefined(op);
                byte v = isB ? B : A;
                Write(Address(mode), v);
                Logic(v);
                return baseCycles + 1;
            }
        }

        byte operand = mode == 0 ? Fetch() : Read(Address(mode));
        byte acc = isB ? B : A;
        switch (lo)
        {
            case 0x0: acc = Sub(acc, operand, false); break;     // SUB
            case 0x1: Sub(acc, operand, false); break;           // CMP
            case 0x2: acc = Sub(acc, operand, true); break;      // SBC
            case 0x4: acc &= operand; Logic(acc); break;         // AND
            case 0x5: Logic((byte)(acc & operand)); break;       // BIT
            case 0x6: acc = operand; Logic(acc); break;          // LDA
            case 0x8: acc ^= operand; Logic(acc); break;         // EOR
            case 0x9: acc = Add(acc, operand, true); break;      // ADC
            case 0xA: acc |= operand; Logic(acc); break;         // ORA
            case 0xB: acc = Add(acc, operand, false); break;     // ADD
        }
        if (isB) B = acc; else A = acc;
        return baseCycles;
    }

    private void Daa()
    {
        int a = A, correction = 0;
        bool carry = (CC & C) != 0;
        if ((CC & H) != 0 || (a & 0x0F) > 9) correction |= 0x06;
        if (carry || a > 0x99 || (a > 0x8F && (a & 0x0F) > 9)) { correction |= 0x60; carry = true; }
        int r = a + correction;
        A = (byte)r;
        Nz(A);
        Flag(C, carry || r > 0xFF);
    }
}
