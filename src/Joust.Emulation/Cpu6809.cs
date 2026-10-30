namespace Joust.Emulation;

public interface IBus
{
    byte Read(ushort address);
    void Write(ushort address, byte value);
}

/// <summary>
/// Motorola 6809 core with documented-instruction cycle counts. This is the reference
/// processor that runs the original Joust program; flag behaviour follows MAME's m6809
/// where the datasheet calls a flag "undefined".
/// </summary>
public sealed class Cpu6809
{
    public const byte FlagE = 0x80, FlagF = 0x40, FlagH = 0x20, FlagI = 0x10;
    public const byte FlagN = 0x08, FlagZ = 0x04, FlagV = 0x02, FlagC = 0x01;

    private readonly IBus _bus;

    public byte A, B, DP, CC;
    public ushort X, Y, U, S, PC;

    /// <summary>Total cycles executed since reset.</summary>
    public long Cycles;

    /// <summary>Level of the IRQ input (active while true).</summary>
    public bool IrqLine;

    /// <summary>Level of the FIRQ input (active while true).</summary>
    public bool FirqLine;

    private bool _nmiPending;
    private bool _nmiArmed;
    private bool _cwaiWaiting;
    private bool _syncWaiting;
    private int _extra;

    public Cpu6809(IBus bus) => _bus = bus;

    public ushort D
    {
        get => (ushort)((A << 8) | B);
        set
        {
            A = (byte)(value >> 8);
            B = (byte)value;
        }
    }

    public void Reset()
    {
        DP = 0;
        CC = FlagI | FlagF;
        _nmiArmed = false;
        _nmiPending = false;
        _cwaiWaiting = false;
        _syncWaiting = false;
        PC = Read16(0xFFFE);
    }

    public void Nmi()
    {
        if (_nmiArmed) _nmiPending = true;
    }

    /// <summary>Adds cycles to the clock without executing (e.g. blitter bus stalls).</summary>
    public void Stall(int cycles) => Cycles += cycles;

    /// <summary>
    /// True when the next <see cref="Step"/> would execute an ordinary instruction: no
    /// interrupt is about to be taken and the CPU is not halted in CWAI/SYNC.
    /// </summary>
    public bool AtInstructionBoundary =>
        !_nmiPending && !_cwaiWaiting && !_syncWaiting &&
        !(FirqLine && (CC & FlagF) == 0) &&
        !(IrqLine && (CC & FlagI) == 0);

    public CpuState Save() => new(A, B, DP, CC, X, Y, U, S, PC, Cycles);

    public void Restore(CpuState s)
    {
        (A, B, DP, CC, X, Y, U, S, PC, Cycles) = (s.A, s.B, s.DP, s.CC, s.X, s.Y, s.U, s.S, s.PC, s.Cycles);
    }

    /// <summary>Executes one instruction (or interrupt entry) and returns the cycles used.</summary>
    public int Step()
    {
        long start = Cycles;
        if (CheckInterrupts())
            return (int)(Cycles - start);
        if (_cwaiWaiting || _syncWaiting)
        {
            Cycles += 1;
            return 1;
        }
        _extra = 0;
        byte op = Fetch();
        int c = ExecutePage0(op);
        Cycles += c + _extra;
        return (int)(Cycles - start);
    }

    // ------------------------------------------------------------------ interrupts

    private bool CheckInterrupts()
    {
        if (_nmiPending)
        {
            _nmiPending = false;
            _syncWaiting = false;
            EnterInterrupt(entire: true, 0xFFFC, FlagI | FlagF, 19);
            return true;
        }
        if (FirqLine && (CC & FlagF) == 0)
        {
            _syncWaiting = false;
            EnterInterrupt(entire: false, 0xFFF6, FlagI | FlagF, 10);
            return true;
        }
        if (IrqLine && (CC & FlagI) == 0)
        {
            _syncWaiting = false;
            EnterInterrupt(entire: true, 0xFFF8, FlagI, 19);
            return true;
        }
        if (_syncWaiting && (IrqLine || FirqLine))
            _syncWaiting = false; // masked interrupt ends SYNC without vectoring
        return false;
    }

    private void EnterInterrupt(bool entire, ushort vector, byte mask, int cycles)
    {
        if (_cwaiWaiting)
        {
            // State was already stacked by CWAI.
            _cwaiWaiting = false;
            cycles = 7;
        }
        else if (entire)
        {
            CC |= FlagE;
            PushEntireState();
        }
        else
        {
            CC = (byte)(CC & ~FlagE);
            PushS16(PC);
            PushS8(CC);
        }
        CC |= mask;
        PC = Read16(vector);
        Cycles += cycles;
    }

    private void PushEntireState()
    {
        PushS16(PC);
        PushS16(U);
        PushS16(Y);
        PushS16(X);
        PushS8(DP);
        PushS8(B);
        PushS8(A);
        PushS8(CC);
    }

    // ------------------------------------------------------------------ memory helpers

    private byte Read(ushort a) => _bus.Read(a);
    private void Write(ushort a, byte v) => _bus.Write(a, v);
    private ushort Read16(ushort a) => (ushort)((Read(a) << 8) | Read((ushort)(a + 1)));

    private void Write16(ushort a, ushort v)
    {
        Write(a, (byte)(v >> 8));
        Write((ushort)(a + 1), (byte)v);
    }

    private byte Fetch() => Read(PC++);

    private ushort Fetch16()
    {
        ushort v = Read16(PC);
        PC += 2;
        return v;
    }

    private void PushS8(byte v) => Write(--S, v);
    private void PushS16(ushort v) { PushS8((byte)v); PushS8((byte)(v >> 8)); }
    private byte PullS8() => Read(S++);
    private ushort PullS16() => (ushort)((PullS8() << 8) | PullS8());
    private void PushU8(byte v) => Write(--U, v);
    private void PushU16(ushort v) { PushU8((byte)v); PushU8((byte)(v >> 8)); }
    private byte PullU8() => Read(U++);
    private ushort PullU16() => (ushort)((PullU8() << 8) | PullU8());

    // ------------------------------------------------------------------ addressing

    private ushort EaDirect() => (ushort)((DP << 8) | Fetch());

    private ushort EaExtended() => Fetch16();

    private ushort EaIndexed()
    {
        byte pb = Fetch();
        int reg = (pb >> 5) & 3;
        ushort r = GetIndexReg(reg);
        ushort ea;
        if ((pb & 0x80) == 0)
        {
            int off = (pb & 0x10) != 0 ? (pb & 0x0F) - 16 : pb & 0x0F;
            _extra += 1;
            return (ushort)(r + off);
        }
        switch (pb & 0x0F)
        {
            case 0x0: ea = r; SetIndexReg(reg, (ushort)(r + 1)); _extra += 2; break;
            case 0x1: ea = r; SetIndexReg(reg, (ushort)(r + 2)); _extra += 3; break;
            case 0x2: r--; SetIndexReg(reg, r); ea = r; _extra += 2; break;
            case 0x3: r -= 2; SetIndexReg(reg, r); ea = r; _extra += 3; break;
            case 0x4: ea = r; break;
            case 0x5: ea = (ushort)(r + (sbyte)B); _extra += 1; break;
            case 0x6: ea = (ushort)(r + (sbyte)A); _extra += 1; break;
            case 0x8: ea = (ushort)(r + (sbyte)Fetch()); _extra += 1; break;
            case 0x9: ea = (ushort)(r + Fetch16()); _extra += 4; break;
            case 0xB: ea = (ushort)(r + D); _extra += 4; break;
            case 0xC: { sbyte o = (sbyte)Fetch(); ea = (ushort)(PC + o); _extra += 1; break; }
            case 0xD: { ushort o = Fetch16(); ea = (ushort)(PC + o); _extra += 5; break; }
            case 0xF: ea = Fetch16(); _extra += 2; break;
            default: throw new IllegalInstructionException((ushort)(PC - 2), pb, "indexed postbyte");
        }
        if ((pb & 0x10) != 0)
        {
            ea = Read16(ea);
            _extra += 3;
        }
        return ea;
    }

    private ushort GetIndexReg(int r) => r switch { 0 => X, 1 => Y, 2 => U, _ => S };

    private void SetIndexReg(int r, ushort v)
    {
        switch (r)
        {
            case 0: X = v; break;
            case 1: Y = v; break;
            case 2: U = v; break;
            default: S = v; break;
        }
    }

    /// <summary>Effective address for the memory column of an opcode ($x0-$xF row 1..3).</summary>
    private ushort EaForMode(int mode) => mode switch
    {
        1 => EaDirect(),
        2 => EaIndexed(),
        _ => EaExtended(),
    };

    // ------------------------------------------------------------------ flags

    private void SetNZ8(byte r)
    {
        CC = (byte)(CC & ~(FlagN | FlagZ));
        if (r == 0) CC |= FlagZ;
        if ((r & 0x80) != 0) CC |= FlagN;
    }

    private void SetNZ16(ushort r)
    {
        CC = (byte)(CC & ~(FlagN | FlagZ));
        if (r == 0) CC |= FlagZ;
        if ((r & 0x8000) != 0) CC |= FlagN;
    }

    private void SetFlag(byte flag, bool on) => CC = on ? (byte)(CC | flag) : (byte)(CC & ~flag);

    private bool Carry => (CC & FlagC) != 0;

    // ------------------------------------------------------------------ ALU

    private byte Add8(byte a, byte b, bool withCarry)
    {
        int c = withCarry && Carry ? 1 : 0;
        int r = a + b + c;
        SetFlag(FlagH, ((a ^ b ^ r) & 0x10) != 0);
        SetFlag(FlagV, ((a ^ r) & (b ^ r) & 0x80) != 0);
        SetFlag(FlagC, (r & 0x100) != 0);
        SetNZ8((byte)r);
        return (byte)r;
    }

    private byte Sub8(byte a, byte b, bool withCarry)
    {
        int c = withCarry && Carry ? 1 : 0;
        int r = a - b - c;
        SetFlag(FlagV, ((a ^ b) & (a ^ r) & 0x80) != 0);
        SetFlag(FlagC, (r & 0x100) != 0);
        SetNZ8((byte)r);
        return (byte)r;
    }

    private ushort Add16(ushort a, ushort b)
    {
        int r = a + b;
        SetFlag(FlagV, ((a ^ r) & (b ^ r) & 0x8000) != 0);
        SetFlag(FlagC, (r & 0x10000) != 0);
        SetNZ16((ushort)r);
        return (ushort)r;
    }

    private ushort Sub16(ushort a, ushort b)
    {
        int r = a - b;
        SetFlag(FlagV, ((a ^ b) & (a ^ r) & 0x8000) != 0);
        SetFlag(FlagC, (r & 0x10000) != 0);
        SetNZ16((ushort)r);
        return (ushort)r;
    }

    private byte Logic(byte r)
    {
        SetNZ8(r);
        CC = (byte)(CC & ~FlagV);
        return r;
    }

    private byte Unary(int op, byte m)
    {
        int r;
        switch (op)
        {
            case 0x0: // NEG
                r = -m & 0xFF;
                SetFlag(FlagV, m == 0x80);
                SetFlag(FlagC, m != 0);
                SetNZ8((byte)r);
                return (byte)r;
            case 0x3: // COM
                r = ~m & 0xFF;
                CC = (byte)((CC & ~FlagV) | FlagC);
                SetNZ8((byte)r);
                return (byte)r;
            case 0x4: // LSR
                SetFlag(FlagC, (m & 1) != 0);
                r = m >> 1;
                SetNZ8((byte)r);
                return (byte)r;
            case 0x6: // ROR
                r = (m >> 1) | (Carry ? 0x80 : 0);
                SetFlag(FlagC, (m & 1) != 0);
                SetNZ8((byte)r);
                return (byte)r;
            case 0x7: // ASR
                SetFlag(FlagC, (m & 1) != 0);
                r = (m & 0x80) | (m >> 1);
                SetNZ8((byte)r);
                return (byte)r;
            case 0x8: // ASL/LSL
                r = m << 1;
                SetFlag(FlagC, (m & 0x80) != 0);
                SetFlag(FlagV, ((m ^ r) & 0x80) != 0);
                SetNZ8((byte)r);
                return (byte)r;
            case 0x9: // ROL
                r = (m << 1) | (Carry ? 1 : 0);
                SetFlag(FlagC, (m & 0x80) != 0);
                SetFlag(FlagV, ((m ^ r) & 0x80) != 0);
                SetNZ8((byte)r);
                return (byte)r;
            case 0xA: // DEC
                r = (m - 1) & 0xFF;
                SetFlag(FlagV, m == 0x80);
                SetNZ8((byte)r);
                return (byte)r;
            case 0xC: // INC
                r = (m + 1) & 0xFF;
                SetFlag(FlagV, m == 0x7F);
                SetNZ8((byte)r);
                return (byte)r;
            case 0xD: // TST
                Logic(m);
                return m;
            case 0xF: // CLR
                CC = (byte)((CC & ~(FlagN | FlagV | FlagC)) | FlagZ);
                return 0;
        }
        throw new IllegalInstructionException((ushort)(PC - 1), (byte)op, "unary");
    }

    private bool Condition(int cond) => cond switch
    {
        0x0 => true,
        0x1 => false,
        0x2 => (CC & (FlagC | FlagZ)) == 0,
        0x3 => (CC & (FlagC | FlagZ)) != 0,
        0x4 => (CC & FlagC) == 0,
        0x5 => (CC & FlagC) != 0,
        0x6 => (CC & FlagZ) == 0,
        0x7 => (CC & FlagZ) != 0,
        0x8 => (CC & FlagV) == 0,
        0x9 => (CC & FlagV) != 0,
        0xA => (CC & FlagN) == 0,
        0xB => (CC & FlagN) != 0,
        0xC => ((CC & FlagN) != 0) == ((CC & FlagV) != 0),
        0xD => ((CC & FlagN) != 0) != ((CC & FlagV) != 0),
        0xE => (CC & FlagZ) == 0 && ((CC & FlagN) != 0) == ((CC & FlagV) != 0),
        _ => (CC & FlagZ) != 0 || ((CC & FlagN) != 0) != ((CC & FlagV) != 0),
    };

    // ------------------------------------------------------------------ page 0

    private int ExecutePage0(byte op)
    {
        int hi = op >> 4, lo = op & 0x0F;
        switch (hi)
        {
            case 0x0: return MemoryUnary(lo, EaDirect(), 6, 3);
            case 0x6: return MemoryUnary(lo, EaIndexed(), 6, 3);
            case 0x7: return MemoryUnary(lo, EaExtended(), 7, 4);
            case 0x4:
                if (!IsUnary(lo)) break;
                A = Unary(lo, A);
                return 2;
            case 0x5:
                if (!IsUnary(lo)) break;
                B = Unary(lo, B);
                return 2;
            case 0x2:
            {
                sbyte rel = (sbyte)Fetch();
                if (Condition(lo)) PC = (ushort)(PC + rel);
                return 3;
            }
            case 0x1: return ExecuteMisc(op);
            case 0x3: return ExecuteStackGroup(op);
            case >= 0x8:
                return ExecuteAccumulator(op);
        }
        throw new IllegalInstructionException((ushort)(PC - 1), op);
    }

    private static bool IsUnary(int lo) => lo is 0x0 or 0x3 or 0x4 or 0x6 or 0x7 or 0x8 or 0x9 or 0xA or 0xC or 0xD or 0xF;

    private int MemoryUnary(int lo, ushort ea, int cycles, int jmpCycles)
    {
        if (lo == 0xE)
        {
            PC = ea;
            return jmpCycles;
        }
        if (!IsUnary(lo))
            throw new IllegalInstructionException((ushort)(PC - 1), (byte)lo, "memory unary");
        byte m = Read(ea);
        byte r = Unary(lo, m);
        if (lo != 0xD)
            Write(ea, r); // TST reads only, but takes the same cycles on the 6809
        return cycles;
    }

    private int ExecuteMisc(byte op)
    {
        switch (op)
        {
            case 0x10: return ExecutePage1(Fetch());
            case 0x11: return ExecutePage2(Fetch());
            case 0x12: return 2; // NOP
            case 0x13: _syncWaiting = true; return 4; // SYNC
            case 0x16: { ushort rel = Fetch16(); PC = (ushort)(PC + rel); return 5; } // LBRA
            case 0x17: { ushort rel = Fetch16(); PushS16(PC); PC = (ushort)(PC + rel); return 9; } // LBSR
            case 0x19: Daa(); return 2;
            case 0x1A: CC |= Fetch(); return 3; // ORCC
            case 0x1C: CC &= Fetch(); return 3; // ANDCC
            case 0x1D: // SEX
                A = (B & 0x80) != 0 ? (byte)0xFF : (byte)0;
                SetNZ16(D);
                CC = (byte)(CC & ~FlagV);
                return 2;
            case 0x1E: Exchange(Fetch()); return 8;
            case 0x1F: Transfer(Fetch()); return 6;
        }
        throw new IllegalInstructionException((ushort)(PC - 1), op);
    }

    private void Daa()
    {
        int cf = 0;
        int msn = A & 0xF0, lsn = A & 0x0F;
        if (lsn > 0x09 || (CC & FlagH) != 0) cf |= 0x06;
        if (msn > 0x80 && lsn > 0x09) cf |= 0x60;
        if (msn > 0x90 || (CC & FlagC) != 0) cf |= 0x60;
        int t = cf + A;
        CC = (byte)(CC & ~FlagV);
        SetNZ8((byte)t);
        if ((t & 0x100) != 0) CC |= FlagC;
        A = (byte)t;
    }

    private int ExecuteStackGroup(byte op)
    {
        switch (op)
        {
            case 0x30: X = EaIndexed(); SetFlag(FlagZ, X == 0); return 4;
            case 0x31: Y = EaIndexed(); SetFlag(FlagZ, Y == 0); return 4;
            case 0x32: S = EaIndexed(); _nmiArmed = true; return 4;
            case 0x33: U = EaIndexed(); return 4;
            case 0x34: return 5 + PushRegs(Fetch(), system: true);
            case 0x35: return 5 + PullRegs(Fetch(), system: true);
            case 0x36: return 5 + PushRegs(Fetch(), system: false);
            case 0x37: return 5 + PullRegs(Fetch(), system: false);
            case 0x39: PC = PullS16(); return 5; // RTS
            case 0x3A: X = (ushort)(X + B); return 3; // ABX
            case 0x3B: // RTI
                CC = PullS8();
                if ((CC & FlagE) != 0)
                {
                    A = PullS8();
                    B = PullS8();
                    DP = PullS8();
                    X = PullS16();
                    Y = PullS16();
                    U = PullS16();
                    PC = PullS16();
                    return 15;
                }
                PC = PullS16();
                return 6;
            case 0x3C: // CWAI
                CC &= Fetch();
                CC |= FlagE;
                PushEntireState();
                _cwaiWaiting = true;
                return 20;
            case 0x3D: // MUL
                D = (ushort)(A * B);
                SetFlag(FlagZ, D == 0);
                SetFlag(FlagC, (B & 0x80) != 0);
                return 11;
            case 0x3F: // SWI
                CC |= FlagE;
                PushEntireState();
                CC |= FlagI | FlagF;
                PC = Read16(0xFFFA);
                return 19;
        }
        throw new IllegalInstructionException((ushort)(PC - 1), op);
    }

    private int PushRegs(byte mask, bool system)
    {
        int n = 0;
        void P8(byte v) { if (system) PushS8(v); else PushU8(v); n++; }
        void P16(ushort v) { if (system) PushS16(v); else PushU16(v); n += 2; }
        if ((mask & 0x80) != 0) P16(PC);
        if ((mask & 0x40) != 0) P16(system ? U : S);
        if ((mask & 0x20) != 0) P16(Y);
        if ((mask & 0x10) != 0) P16(X);
        if ((mask & 0x08) != 0) P8(DP);
        if ((mask & 0x04) != 0) P8(B);
        if ((mask & 0x02) != 0) P8(A);
        if ((mask & 0x01) != 0) P8(CC);
        return n;
    }

    private int PullRegs(byte mask, bool system)
    {
        int n = 0;
        byte G8() { n++; return system ? PullS8() : PullU8(); }
        ushort G16() { n += 2; return system ? PullS16() : PullU16(); }
        if ((mask & 0x01) != 0) CC = G8();
        if ((mask & 0x02) != 0) A = G8();
        if ((mask & 0x04) != 0) B = G8();
        if ((mask & 0x08) != 0) DP = G8();
        if ((mask & 0x10) != 0) X = G16();
        if ((mask & 0x20) != 0) Y = G16();
        if ((mask & 0x40) != 0)
        {
            if (system) U = G16();
            else S = G16();
        }
        if ((mask & 0x80) != 0) PC = G16();
        return n;
    }

    private ushort GetTransferReg(int code) => code switch
    {
        0x0 => D,
        0x1 => X,
        0x2 => Y,
        0x3 => U,
        0x4 => S,
        0x5 => PC,
        0x8 => (ushort)(0xFF00 | A),
        0x9 => (ushort)(0xFF00 | B),
        0xA => (ushort)(0xFF00 | CC),
        0xB => (ushort)(0xFF00 | DP),
        _ => 0xFFFF,
    };

    private void SetTransferReg(int code, ushort v)
    {
        switch (code)
        {
            case 0x0: D = v; break;
            case 0x1: X = v; break;
            case 0x2: Y = v; break;
            case 0x3: U = v; break;
            case 0x4: S = v; _nmiArmed = true; break;
            case 0x5: PC = v; break;
            case 0x8: A = (byte)v; break;
            case 0x9: B = (byte)v; break;
            case 0xA: CC = (byte)v; break;
            case 0xB: DP = (byte)v; break;
        }
    }

    private void Transfer(byte pb) => SetTransferReg(pb & 0x0F, GetTransferReg(pb >> 4));

    private void Exchange(byte pb)
    {
        ushort a = GetTransferReg(pb >> 4), b = GetTransferReg(pb & 0x0F);
        SetTransferReg(pb >> 4, b);
        SetTransferReg(pb & 0x0F, a);
    }

    private int ExecuteAccumulator(byte op)
    {
        int mode = (op >> 4) & 3; // 0=imm 1=dir 2=idx 3=ext
        bool isB = op >= 0xC0;
        int lo = op & 0x0F;
        int dirExtra = mode switch { 0 => 0, 1 => 2, 2 => 2, _ => 3 };

        // 16-bit and special columns
        switch (lo)
        {
            case 0x3: // SUBD / ADDD
            {
                ushort m = mode == 0 ? Fetch16() : Read16(EaForMode(mode));
                D = isB ? Add16(D, m) : Sub16(D, m);
                return 4 + dirExtra;
            }
            case 0xC: // CMPX / LDD
            {
                ushort m = mode == 0 ? Fetch16() : Read16(EaForMode(mode));
                if (isB)
                {
                    D = m;
                    SetNZ16(m);
                    CC = (byte)(CC & ~FlagV);
                    return 3 + dirExtra;
                }
                Sub16(X, m);
                return 4 + dirExtra;
            }
            case 0xD: // BSR/JSR  or  STD
                if (!isB)
                {
                    if (mode == 0)
                    {
                        sbyte rel = (sbyte)Fetch();
                        PushS16(PC);
                        PC = (ushort)(PC + rel);
                        return 7;
                    }
                    ushort target = EaForMode(mode);
                    PushS16(PC);
                    PC = target;
                    return mode == 1 ? 7 : mode == 2 ? 7 : 8;
                }
                if (mode == 0) break;
                {
                    ushort ea = EaForMode(mode);
                    Write16(ea, D);
                    SetNZ16(D);
                    CC = (byte)(CC & ~FlagV);
                    return 3 + dirExtra;
                }
            case 0xE: // LDX / LDU
            {
                ushort m = mode == 0 ? Fetch16() : Read16(EaForMode(mode));
                if (isB) U = m;
                else X = m;
                SetNZ16(m);
                CC = (byte)(CC & ~FlagV);
                return 3 + dirExtra;
            }
            case 0xF: // STX / STU
            {
                if (mode == 0) break;
                ushort ea = EaForMode(mode);
                ushort v = isB ? U : X;
                Write16(ea, v);
                SetNZ16(v);
                CC = (byte)(CC & ~FlagV);
                return 3 + dirExtra;
            }
            case 0x7: // STA / STB
            {
                if (mode == 0) break;
                ushort ea = EaForMode(mode);
                byte v = isB ? B : A;
                Write(ea, v);
                Logic(v);
                return 2 + dirExtra;
            }
            default:
            {
                byte m = mode == 0 ? Fetch() : Read(EaForMode(mode));
                ref byte acc = ref (isB ? ref B : ref A);
                switch (lo)
                {
                    case 0x0: acc = Sub8(acc, m, false); break;
                    case 0x1: Sub8(acc, m, false); break;
                    case 0x2: acc = Sub8(acc, m, true); break;
                    case 0x4: acc = Logic((byte)(acc & m)); break;
                    case 0x5: Logic((byte)(acc & m)); break;
                    case 0x6: acc = Logic(m); break;
                    case 0x8: acc = Logic((byte)(acc ^ m)); break;
                    case 0x9: acc = Add8(acc, m, true); break;
                    case 0xA: acc = Logic((byte)(acc | m)); break;
                    case 0xB: acc = Add8(acc, m, false); break;
                }
                return 2 + dirExtra;
            }
        }
        throw new IllegalInstructionException((ushort)(PC - 1), op);
    }

    // ------------------------------------------------------------------ pages 1 & 2

    private int ExecutePage1(byte op)
    {
        if (op >= 0x21 && op <= 0x2F)
        {
            ushort rel = Fetch16();
            if (Condition(op & 0x0F))
            {
                PC = (ushort)(PC + rel);
                return 6;
            }
            return 5;
        }
        if (op == 0x3F)
        {
            CC |= FlagE;
            PushEntireState();
            PC = Read16(0xFFF4);
            return 20;
        }
        int mode = (op >> 4) & 3;
        int extra = mode switch { 0 => 0, 1 => 2, 2 => 2, _ => 3 };
        switch (op & 0xCF)
        {
            case 0x83: // CMPD
                Sub16(D, mode == 0 ? Fetch16() : Read16(EaForMode(mode)));
                return 5 + extra;
            case 0x8C: // CMPY
                Sub16(Y, mode == 0 ? Fetch16() : Read16(EaForMode(mode)));
                return 5 + extra;
            case 0x8E: // LDY
                Y = mode == 0 ? Fetch16() : Read16(EaForMode(mode));
                SetNZ16(Y);
                CC = (byte)(CC & ~FlagV);
                return 4 + extra;
            case 0x8F when mode != 0: // STY
            {
                ushort ea = EaForMode(mode);
                Write16(ea, Y);
                SetNZ16(Y);
                CC = (byte)(CC & ~FlagV);
                return 4 + extra;
            }
            case 0xCE when op >= 0xC0: // LDS
                S = mode == 0 ? Fetch16() : Read16(EaForMode(mode));
                SetNZ16(S);
                CC = (byte)(CC & ~FlagV);
                _nmiArmed = true;
                return 4 + extra;
            case 0xCF when op >= 0xC0 && mode != 0: // STS
            {
                ushort ea = EaForMode(mode);
                Write16(ea, S);
                SetNZ16(S);
                CC = (byte)(CC & ~FlagV);
                return 4 + extra;
            }
        }
        throw new IllegalInstructionException((ushort)(PC - 2), op, "page 1");
    }

    private int ExecutePage2(byte op)
    {
        if (op == 0x3F)
        {
            CC |= FlagE;
            PushEntireState();
            PC = Read16(0xFFF2);
            return 20;
        }
        int mode = (op >> 4) & 3;
        int extra = mode switch { 0 => 0, 1 => 2, 2 => 2, _ => 3 };
        if (op < 0xC0)
        {
            switch (op & 0x0F)
            {
                case 0x3: Sub16(U, mode == 0 ? Fetch16() : Read16(EaForMode(mode))); return 5 + extra;
                case 0xC: Sub16(S, mode == 0 ? Fetch16() : Read16(EaForMode(mode))); return 5 + extra;
            }
        }
        throw new IllegalInstructionException((ushort)(PC - 2), op, "page 2");
    }
}

public readonly record struct CpuState(
    byte A, byte B, byte DP, byte CC, ushort X, ushort Y, ushort U, ushort S, ushort PC, long Cycles)
{
    public override string ToString() =>
        $"A={A:X2} B={B:X2} X={X:X4} Y={Y:X4} U={U:X4} S={S:X4} DP={DP:X2} CC={CC:X2} PC={PC:X4} cyc={Cycles}";
}

public sealed class IllegalInstructionException(ushort pc, byte op, string? context = null)
    : Exception($"illegal {(context ?? "opcode")} ${op:X2} at ${pc:X4}")
{
    public ushort Address { get; } = pc;
}
