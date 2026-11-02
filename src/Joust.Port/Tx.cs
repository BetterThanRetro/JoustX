using Joust.Emulation;

namespace Joust.Port;

/// <summary>
/// A transaction over the machine for one native routine: a copy of the registers plus
/// pending memory writes. Nothing reaches the machine until <see cref="Commit"/>, so a routine
/// can still hand the call back to the original code (by not committing) at any point,
/// for example after discovering the bird fell into the lava.
/// </summary>
public sealed class Tx
{
    private readonly NativeContext _c;
    private readonly Dictionary<int, byte> _pending = [];
    private readonly List<(int Address, byte Value, bool Write, int Cycle)> _io = [];

    public byte A, B, DP, CC;
    public ushort X, Y, U, S;

    /// <summary>Cycles used so far, excluding the routine's final RTS.</summary>
    public int Cycles;

    /// <summary>
    /// Cycles the blitter will steal when the logged blits run at commit. They count toward the
    /// interrupt window but are charged by the blits themselves, not by <see cref="Cycles"/>.
    /// </summary>
    public int StallCycles;

    /// <summary>The player multiplexer as the I/O so far would leave it.</summary>
    public bool Mux;

    public Tx(NativeContext c)
    {
        _c = c;
        var p = c.Cpu;
        (A, B, DP, CC, X, Y, U, S) = (p.A, p.B, p.DP, p.CC, p.X, p.Y, p.U, p.S);
        Mux = c.WidgetMux;
    }

    public NativeContext Context => _c;

    // ------------------------------------------------------------------ marks

    private List<(int Address, bool Had, byte Old)>? _undo;

    /// <summary>A restore point: the registers, cycles and multiplexer, and how far the write logs had got.</summary>
    public readonly record struct TxMark(
        byte A, byte B, byte DP, byte CC, ushort X, ushort Y, ushort U, ushort S, int Cycles, int StallCycles, bool Mux,
        int Undo, int Io);

    /// <summary>
    /// Remembers the current state so a later step can be undone. It copies nothing: from the
    /// first mark on, each write logs what it replaced, so translated code can take one per instruction.
    /// </summary>
    public TxMark Mark()
    {
        _undo ??= [];
        return new(A, B, DP, CC, X, Y, U, S, Cycles, StallCycles, Mux, _undo.Count, _io.Count);
    }

    /// <summary>Undoes everything since <paramref name="m"/> (the latest mark, or an earlier one).</summary>
    public void Rollback(TxMark m)
    {
        (A, B, DP, CC, X, Y, U, S, Cycles, StallCycles, Mux) = (m.A, m.B, m.DP, m.CC, m.X, m.Y, m.U, m.S, m.Cycles, m.StallCycles, m.Mux);
        if (m.Undo > _undo!.Count) throw new InvalidOperationException("rolling back to a mark that was already undone");
        for (int i = _undo.Count - 1; i >= m.Undo; i--)
        {
            var (a, had, old) = _undo[i];
            if (had) _pending[a] = old;
            else _pending.Remove(a);
        }
        _undo.RemoveRange(m.Undo, _undo.Count - m.Undo);
        _io.RemoveRange(m.Io, _io.Count - m.Io);
    }

    // ------------------------------------------------------------------ I/O

    /// <summary>
    /// A write to an I/O register, performed (in order) at commit, and timed at
    /// <paramref name="cycle"/> cycles into the routine (by default, the cycles charged so far:
    /// the start of the instruction making it, as each instruction charges its cycles after it).
    /// </summary>
    public void IoWrite(int address, byte value, int? cycle = null) => _io.Add((address, value, true, cycle ?? Cycles));

    /// <summary>
    /// A read of an I/O register whose value the caller already knows (e.g. from
    /// <see cref="NativeContext.PeekWidget"/>); it is repeated at commit for its side effects.
    /// </summary>
    public void IoRead(int address) => _io.Add((address, 0, false, Cycles));

    public ushort D
    {
        get => (ushort)((A << 8) | B);
        set
        {
            A = (byte)(value >> 8);
            B = (byte)value;
        }
    }

    public byte Read(int a)
    {
        a &= 0xFFFF;
        return _pending.TryGetValue(a, out byte v) ? v : _c.Read(a);
    }

    /// <summary>A RAM byte as code would read it with the RAM read latch selected (video RAM under $9000).</summary>
    public byte ReadRam(int a)
    {
        a &= 0xFFFF;
        return _pending.TryGetValue(a, out byte v) ? v : _c.ReadRam(a);
    }

    /// <summary>A byte as code would read it with the ROM read latch selected (pending writes only above $9000).</summary>
    public byte ReadRom(int a)
    {
        a &= 0xFFFF;
        return a >= 0x9000 && _pending.TryGetValue(a, out byte v) ? v : _c.ReadRom(a);
    }

    public ushort Read16(int a) => (ushort)((Read(a) << 8) | Read(a + 1));

    public void Write(int a, byte v)
    {
        a &= 0xFFFF;
        if (_undo != null)
            _undo.Add((a, _pending.TryGetValue(a, out byte old), old));
        _pending[a] = v;
    }

    public void Write16(int a, ushort v)
    {
        Write(a, (byte)(v >> 8));
        Write(a + 1, (byte)v);
    }

    /// <summary>Address of a variable as the original reaches it (DP-relative if direct page).</summary>
    public int Var(ushort address) => Symbols.AddressCost(address) == 0 ? (DP << 8) | (address & 0xFF) : address;

    /// <summary>Pushes a word on the system stack (as JSR or PSHS would).</summary>
    public void Push16(ushort value)
    {
        S -= 2;
        Write16(S, value);
    }

    /// <summary>True if <paramref name="extra"/> more cycles could still run without moving an interrupt.</summary>
    public bool CanRun(int extra) => _c.InterruptSafe(Cycles + StallCycles + extra);

    /// <summary>
    /// Applies the writes and registers and performs the final RTS, or returns false (changing
    /// nothing) if running atomically could move an interrupt.
    /// </summary>
    public bool Commit()
    {
        const int rts = 5;
        if (!_c.InterruptSafe(Cycles + StallCycles + rts))
            return false;
        Apply();
        _c.Return(Cycles + rts);
        return true;
    }

    /// <summary>Like <see cref="Commit"/>, but the code leaves by jumping to <paramref name="pc"/>.</summary>
    public bool CommitJump(ushort pc)
    {
        if (!_c.InterruptSafe(Cycles + StallCycles))
            return false;
        Apply();
        _c.Jump(pc, Cycles);
        return true;
    }

    /// <summary>
    /// Like <see cref="CommitJump"/> without the interrupt-window check, for exactly one
    /// instruction: an interrupt that arrives during it is taken after it, as the CPU does.
    /// </summary>
    public void CommitJumpNow(ushort pc)
    {
        Apply();
        _c.Jump(pc, Cycles);
    }

    private void Apply()
    {
        foreach (var (a, v) in _pending)
            _c.Write(a, v);
        foreach (var (a, v, write, cycle) in _io)
        {
            if (write) _c.WriteAt(a, v, cycle);
            else _c.Read(a);
        }
        var p = _c.Cpu;
        (p.A, p.B, p.DP, p.CC, p.X, p.Y, p.U, p.S) = (A, B, DP, CC, X, Y, U, S);
    }
}
