using static Joust.Emulation.Alu6800;

namespace Joust.Emulation;

/// <summary>Memory and I/O as the 6800 sees it.</summary>
public interface IBus6800
{
    byte Read(ushort address);
    void Write(ushort address, byte value);
}

/// <summary>
/// Motorola 6800 (the sound board's 6808), with the documented cycle counts. Undefined
/// opcodes throw: the sound ROM never runs one. The flag arithmetic is <see cref="Alu6800"/>.
/// </summary>
public sealed class Cpu6800(IBus6800 bus)
{
    public byte A, B, CC = 0xC0 | I;
    public ushort X, SP, PC;
    public long Cycles;
    public bool IrqLine, NmiLine;
    private bool _nmiPrevious;

    /// <summary>Stopped in WAI with the registers stacked, until an interrupt.</summary>
    public bool Waiting;

    public void Reset()
    {
        CC = 0xC0 | I;
        Waiting = false;
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
        if (Waiting)
        {
            Cycles += 1;
            return 1;
        }
        int c = Execute(Fetch());
        Cycles += c;
        return c;
    }

    /// <summary>Enters the interrupt through <paramref name="vector"/> (also used by the translated sound program); returns its cycles.</summary>
    public int Interrupt(ushort vector)
    {
        int c = 12;
        if (Waiting) c = 4;                                      // WAI already stacked the registers
        else PushAll();
        Waiting = false;
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

    private InvalidOperationException Undefined(int op) =>
        new($"undefined 6800 opcode ${op:X2} at ${(ushort)(PC - 1):X4}");

    // ------------------------------------------------------------------ execute

    private int Execute(byte op)
    {
        switch (op)
        {
            case 0x01: return 2;                                 // NOP
            case 0x06: CC = (byte)(A | 0xC0); return 2;          // TAP
            case 0x07: A = CC; return 2;                         // TPA
            case 0x08: X++; Flag(ref CC, Z, X == 0); return 4;   // INX
            case 0x09: X--; Flag(ref CC, Z, X == 0); return 4;   // DEX
            case 0x0A: CC &= unchecked((byte)~V); return 2;
            case 0x0B: CC |= V; return 2;
            case 0x0C: CC &= unchecked((byte)~C); return 2;
            case 0x0D: CC |= C; return 2;
            case 0x0E: CC &= unchecked((byte)~I); return 2;
            case 0x0F: CC |= I; return 2;
            case 0x10: A = Sub(ref CC, A, B, false); return 2;   // SBA
            case 0x11: Sub(ref CC, A, B, false); return 2;       // CBA
            case 0x16: B = A; Logic(ref CC, B); return 2;        // TAB
            case 0x17: A = B; Logic(ref CC, A); return 2;        // TBA
            case 0x19: A = Daa(ref CC, A); return 2;
            case 0x1B: A = Add(ref CC, A, B, false); return 2;   // ABA
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
            case 0x3E: PushAll(); Waiting = true; return 9;      // WAI
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
            if (Branch(CC, lo)) PC = (ushort)(PC + d);
            return 4;
        }
        if (hi is >= 0x4 and <= 0x7)
        {
            if (lo is 0x1 or 0x2 or 0x5 or 0xB or 0xE) throw Undefined(op);
            switch (hi)
            {
                case 0x4: A = Unary(ref CC, lo, A); return 2;
                case 0x5: B = Unary(ref CC, lo, B); return 2;
                default:
                {
                    int a = hi == 0x6 ? Indexed() : Extended();
                    byte r = Unary(ref CC, lo, Read(a));
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
                Cpx(ref CC, X, m);
                return baseCycles + 1;
            }
            case 0xE:                                            // LDS / LDX
            {
                ushort m = mode == 0 ? Fetch16() : Read16(Address(mode));
                if (isB) X = m; else SP = m;
                Logic16(ref CC, m);
                return baseCycles + 1;
            }
            case 0xF:                                            // STS / STX
            {
                if (mode == 0) throw Undefined(op);
                ushort v = isB ? X : SP;
                Write16(Address(mode), v);
                Logic16(ref CC, v);
                return baseCycles + 2;
            }
            case 0x3 or 0xC or 0xD:
                throw Undefined(op);
            case 0x7:                                            // STA
            {
                if (mode == 0) throw Undefined(op);
                byte v = isB ? B : A;
                Write(Address(mode), v);
                Logic(ref CC, v);
                return baseCycles + 1;
            }
        }

        byte operand = mode == 0 ? Fetch() : Read(Address(mode));
        byte acc = isB ? B : A;
        switch (lo)
        {
            case 0x0: acc = Sub(ref CC, acc, operand, false); break;     // SUB
            case 0x1: Sub(ref CC, acc, operand, false); break;           // CMP
            case 0x2: acc = Sub(ref CC, acc, operand, true); break;      // SBC
            case 0x4: acc &= operand; Logic(ref CC, acc); break;         // AND
            case 0x5: Logic(ref CC, (byte)(acc & operand)); break;       // BIT
            case 0x6: acc = operand; Logic(ref CC, acc); break;          // LDA
            case 0x8: acc ^= operand; Logic(ref CC, acc); break;         // EOR
            case 0x9: acc = Add(ref CC, acc, operand, true); break;      // ADC
            case 0xA: acc |= operand; Logic(ref CC, acc); break;         // ORA
            case 0xB: acc = Add(ref CC, acc, operand, false); break;     // ADD
        }
        if (isB) B = acc; else A = acc;
        return baseCycles;
    }
}
