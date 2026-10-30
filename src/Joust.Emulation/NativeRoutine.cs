namespace Joust.Emulation;

/// <summary>
/// A C# replacement for one original subroutine, installed at the routine's ROM address.
/// When the CPU reaches that address, <see cref="Execute"/> runs instead of the 6809 code and
/// must leave the machine exactly as the original would: registers, flags, memory, the
/// return (RTS) and the cycle count. <see cref="NativeVerifier"/> checks this on every call.
/// </summary>
/// <remarks>
/// Rules for implementations:
/// <list type="bullet">
/// <item>Only touch RAM, palette, CMOS and the blitter (no PIA/VSCAN access); leave I and F as the original does.</item>
/// <item>To hand a case back to the original code, return false <em>before</em> making any change.</item>
/// <item>Charge the cycles the original path takes, including the final RTS.</item>
/// </list>
/// </remarks>
public abstract class NativeRoutine
{
    /// <summary>Label of the original routine in the assembly source.</summary>
    public abstract string Symbol { get; }

    /// <summary>
    /// How many cycles from entry the original can be interrupted: its whole length, or up to
    /// the point where it masks interrupts. The native only runs if no interrupt can arrive in
    /// that window. Routines that check at commit time instead (via
    /// <see cref="NativeContext.InterruptSafe"/>) return 0.
    /// </summary>
    public abstract int MaxCycles { get; }

    /// <summary>Resolves the RAM and ROM addresses this routine uses.</summary>
    public virtual void Bind(IReadOnlyDictionary<string, long> symbols) { }

    /// <summary>Entry address to install at (by default the address of <see cref="Symbol"/>).</summary>
    public virtual ushort ResolveAddress(IReadOnlyDictionary<string, long> symbols) => Sym(symbols, Symbol);

    /// <summary>
    /// True for subroutines that finish with RTS. False for code that leaves by jumping
    /// (scheduler, process slices); the verifier then runs the original for exactly as many
    /// cycles as the native took and compares the whole machine state.
    /// </summary>
    public virtual bool EndsWithRts => true;

    /// <summary>Runs the routine. Returns false (with no side effects) to let the original run.</summary>
    public abstract bool Execute(NativeContext c);

    protected static ushort Sym(IReadOnlyDictionary<string, long> symbols, string name) =>
        symbols.TryGetValue(name, out long v) ? (ushort)v : throw new KeyNotFoundException($"symbol {name} not found");
}

/// <summary>What a native routine sees: the CPU registers and the board's memory.</summary>
public sealed class NativeContext
{
    private readonly JoustMachine _m;

    internal NativeContext(JoustMachine m) => _m = m;

    public Cpu6809 Cpu => _m.Cpu;

    public byte Read(int address) => _m.Read((ushort)address);

    /// <summary>A RAM byte (video RAM under $9000 too), whatever the ROM/RAM read latch says.</summary>
    public byte ReadRam(int address) => _m.Ram[address & 0xFFFF];

    /// <summary>A byte as code would read it with the ROM read latch selected (ROM under $9000).</summary>
    public byte ReadRom(int address) => _m.ReadRom((ushort)address);

    public ushort Read16(int address) => (ushort)((Read(address) << 8) | Read(address + 1));

    public void Write(int address, byte value) => _m.Write((ushort)address, value);

    public void Write16(int address, ushort value)
    {
        Write(address, (byte)(value >> 8));
        Write(address + 1, (byte)value);
    }

    /// <summary>
    /// Address of a base-page variable as the original reaches it with direct addressing
    /// (assembled under ASSUME DPR:$A0), i.e. relative to the live DP register.
    /// </summary>
    public int Direct(ushort symbolAddress) => (Cpu.DP << 8) | (symbolAddress & 0xFF);

    /// <summary>
    /// True if running <paramref name="cycles"/> cycles atomically from here cannot move an
    /// interrupt relative to the original code.
    /// </summary>
    public bool InterruptSafe(int cycles) => _m.InterruptSafe(cycles);

    /// <summary>Current player multiplexer of the widget PIA (true = player 1).</summary>
    public bool WidgetMux => _m.WidgetMux;

    /// <summary>Widget port A as a read would return it with the multiplexer at <paramref name="player1"/> (no side effects).</summary>
    public byte PeekWidget(bool player1) => _m.PeekWidget(player1);

    /// <summary>True if <paramref name="cycles"/> from now still ends in the current video frame.</summary>
    public bool FitsInFrame(int cycles) => _m.FitsInFrame(cycles);

    /// <summary>True if no PIA interrupt edge arrives in the next <paramref name="cycles"/> cycles (even with IRQs masked).</summary>
    public bool EdgeFree(int cycles) => _m.EdgeFree(cycles);

    /// <summary>What reading VSCAN returns <paramref name="cyclesFromNow"/> cycles from now.</summary>
    public byte VideoCounter(int cyclesFromNow) => _m.VideoCounter(cyclesFromNow);

    /// <summary>The coin door switches (board PIA port A) as a read would return them (no side effects).</summary>
    public byte PeekCoinDoor() => _m.PeekCoinDoor();

    /// <summary>The board PIA's interrupt outputs (IRQA, IRQB).</summary>
    public (bool A, bool B) BoardIrq => (_m.Pia1.IrqA, _m.Pia1.IrqB);

    /// <summary>Leaves by jumping to <paramref name="pc"/> after <paramref name="cycles"/> cycles.</summary>
    public void Jump(ushort pc, int cycles)
    {
        Cpu.PC = pc;
        Cpu.Cycles += cycles;
    }

    /// <summary>Performs the routine's RTS and charges its total cycle count.</summary>
    public void Return(int cycles)
    {
        Cpu.PC = Read16(Cpu.S);
        Cpu.S += 2;
        Cpu.Cycles += cycles;
    }
}

/// <summary>6809 flag arithmetic shared by native routines, matching <see cref="Cpu6809"/> exactly.</summary>
public static class Alu6809
{
    public const byte H = Cpu6809.FlagH, N = Cpu6809.FlagN, Z = Cpu6809.FlagZ, V = Cpu6809.FlagV, C = Cpu6809.FlagC;

    public static byte Set(byte cc, byte flag, bool on) => on ? (byte)(cc | flag) : (byte)(cc & ~flag);

    public static byte NZ8(byte cc, byte r) => Set(Set(cc, N, (r & 0x80) != 0), Z, r == 0);

    public static byte NZ16(byte cc, ushort r) => Set(Set(cc, N, (r & 0x8000) != 0), Z, r == 0);

    /// <summary>Flags of a load/store/logic op: N, Z from the value; V cleared.</summary>
    public static byte Logic8(byte cc, byte r) => (byte)(NZ8(cc, r) & ~V);

    public static byte Logic16(byte cc, ushort r) => (byte)(NZ16(cc, r) & ~V);

    /// <summary>ADDA/ADCA: returns the sum and updates H, N, Z, V, C.</summary>
    public static byte Add8(ref byte cc, byte a, byte b, bool withCarry)
    {
        int r = a + b + (withCarry && (cc & C) != 0 ? 1 : 0);
        cc = Set(cc, H, ((a ^ b ^ r) & 0x10) != 0);
        cc = Set(cc, V, ((a ^ r) & (b ^ r) & 0x80) != 0);
        cc = Set(cc, C, (r & 0x100) != 0);
        cc = NZ8(cc, (byte)r);
        return (byte)r;
    }

    /// <summary>SUB/CMP 8-bit flags (N, Z, V, C; H unchanged).</summary>
    public static byte Sub8(ref byte cc, byte a, byte b)
    {
        int r = a - b;
        cc = Set(cc, V, ((a ^ b) & (a ^ r) & 0x80) != 0);
        cc = Set(cc, C, (r & 0x100) != 0);
        cc = NZ8(cc, (byte)r);
        return (byte)r;
    }

    /// <summary>CMPD/SUBD 16-bit flags.</summary>
    public static ushort Sub16(ref byte cc, ushort a, ushort b)
    {
        int r = a - b;
        cc = Set(cc, V, ((a ^ b) & (a ^ r) & 0x8000) != 0);
        cc = Set(cc, C, (r & 0x10000) != 0);
        cc = NZ16(cc, (ushort)r);
        return (ushort)r;
    }

    /// <summary>DAA: decimal-adjusts A after a BCD add. Carry is kept or set, V cleared.</summary>
    public static byte Daa(ref byte cc, byte a)
    {
        int cf = 0;
        int msn = a & 0xF0, lsn = a & 0x0F;
        if (lsn > 0x09 || (cc & H) != 0) cf |= 0x06;
        if (msn > 0x80 && lsn > 0x09) cf |= 0x60;
        if (msn > 0x90 || (cc & C) != 0) cf |= 0x60;
        int t = cf + a;
        cc = (byte)(cc & ~V);
        cc = NZ8(cc, (byte)t);
        if ((t & 0x100) != 0) cc |= C;
        return (byte)t;
    }

    /// <summary>ADDD: 16-bit add, N, Z, V, C (H unchanged).</summary>
    public static ushort Add16(ref byte cc, ushort a, ushort b)
    {
        int r = a + b;
        cc = Set(cc, V, ((a ^ r) & (b ^ r) & 0x8000) != 0);
        cc = Set(cc, C, (r & 0x10000) != 0);
        cc = NZ16(cc, (ushort)r);
        return (ushort)r;
    }

    /// <summary>SBCA/SBCB: subtract with borrow (N, Z, V, C).</summary>
    public static byte Sbc8(ref byte cc, byte a, byte b)
    {
        int r = a - b - ((cc & C) != 0 ? 1 : 0);
        cc = Set(cc, V, ((a ^ b) & (a ^ r) & 0x80) != 0);
        cc = Set(cc, C, (r & 0x100) != 0);
        cc = NZ8(cc, (byte)r);
        return (byte)r;
    }

    public static byte Neg8(ref byte cc, byte m)
    {
        byte r = (byte)-m;
        cc = Set(cc, V, m == 0x80);
        cc = Set(cc, C, m != 0);
        cc = NZ8(cc, r);
        return r;
    }

    public static byte Com8(ref byte cc, byte m)
    {
        byte r = (byte)~m;
        cc = (byte)((cc & ~V) | C);
        cc = NZ8(cc, r);
        return r;
    }

    public static byte Asl8(ref byte cc, byte m)
    {
        byte r = (byte)(m << 1);
        cc = Set(cc, C, (m & 0x80) != 0);
        cc = Set(cc, V, ((m ^ r) & 0x80) != 0);
        cc = NZ8(cc, r);
        return r;
    }

    public static byte Rol8(ref byte cc, byte m)
    {
        byte r = (byte)((m << 1) | ((cc & C) != 0 ? 1 : 0));
        cc = Set(cc, C, (m & 0x80) != 0);
        cc = Set(cc, V, ((m ^ r) & 0x80) != 0);
        cc = NZ8(cc, r);
        return r;
    }

    public static byte Asr8(ref byte cc, byte m)
    {
        cc = Set(cc, C, (m & 1) != 0);
        byte r = (byte)((m & 0x80) | (m >> 1));
        cc = NZ8(cc, r);
        return r;
    }

    public static byte Ror8(ref byte cc, byte m)
    {
        byte r = (byte)((m >> 1) | ((cc & C) != 0 ? 0x80 : 0));
        cc = Set(cc, C, (m & 1) != 0);
        cc = NZ8(cc, r);
        return r;
    }

    /// <summary>CLR: N, V, C cleared, Z set.</summary>
    public static byte Clr(byte cc) => (byte)((cc & ~(N | V | C)) | Z);

    // Branch conditions, evaluated on the flags exactly as the 6809 does.
    public static bool Mi(byte cc) => (cc & N) != 0;
    public static bool Eq(byte cc) => (cc & Z) != 0;
    public static bool Cs(byte cc) => (cc & C) != 0;
    public static bool Vs(byte cc) => (cc & V) != 0;
    public static bool Lt(byte cc) => ((cc & N) != 0) != ((cc & V) != 0);
    public static bool Le(byte cc) => Eq(cc) || Lt(cc);
    public static bool Hi(byte cc) => (cc & (C | Z)) == 0;

    /// <summary>INC: N, Z, V (C unchanged).</summary>
    public static byte Inc(ref byte cc, byte m)
    {
        byte r = (byte)(m + 1);
        cc = Set(cc, V, m == 0x7F);
        cc = NZ8(cc, r);
        return r;
    }

    /// <summary>DEC: N, Z, V (C unchanged).</summary>
    public static byte Dec(ref byte cc, byte m)
    {
        byte r = (byte)(m - 1);
        cc = Set(cc, V, m == 0x80);
        cc = NZ8(cc, r);
        return r;
    }
}
