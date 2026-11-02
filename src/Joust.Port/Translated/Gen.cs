using Joust.Emulation;

namespace Joust.Port.Translated;

/// <summary>
/// What translated code runs on: one transaction over the machine, and the bus as the original
/// program sees it (the ROM bank over video RAM, the 16-colour palette, 4-bit CMOS, the I/O page).
/// Each translated instruction starts with <see cref="I"/>, which takes an undo point and checks
/// the interrupt window, so a call can always stop at an exact instruction boundary:
/// <list type="bullet">
/// <item>before an instruction that would run past an interrupt edge or the frame's end (the CPU
/// runs that one, and takes the interrupt where the original would);</item>
/// <item>after an instruction that wrote an I/O register or may have unmasked interrupts, so the
/// machine sees the new state before anything else runs;</item>
/// <item>before an instruction that reads an I/O register (its value depends on the exact cycle,
/// and reading has side effects), which the CPU then runs;</item>
/// <item>at an address with a hand-written routine, which then takes over.</item>
/// </list>
/// </summary>
public sealed class Gen
{
    public readonly Tx T;
    private readonly NativeContext _c;
    private readonly bool[] _handWritten;
    private readonly bool _romBank;
    private Tx.TxMark _start;
    private byte[]? _blitter;
    private bool _stopAfter;

    /// <summary>Single-step mode: one instruction, even across an interrupt edge, reading I/O as it runs.</summary>
    private readonly bool _live;

    private Gen(NativeContext c, bool[] handWritten, bool live = false)
    {
        _c = c;
        _handWritten = handWritten;
        _romBank = c.RomBankSelected;
        _live = live;
        T = new Tx(c);
    }

    /// <summary>Instructions run so far.</summary>
    public int Count { get; private set; }

    /// <summary>Address of the instruction begun last.</summary>
    public ushort Pc { get; private set; }

    /// <summary>
    /// Begins the instruction at <paramref name="pc"/> taking <paramref name="cycles"/> cycles,
    /// or returns false if the call has to stop before it.
    /// </summary>
    public bool I(ushort pc, int cycles)
    {
        if (_live ? Count > 0 : _stopAfter || (Count > 0 && _handWritten[pc]) || !T.CanRun(cycles))
            return false;
        _start = T.Mark();
        Pc = pc;
        Count++;
        T.Cycles += cycles;
        return true;
    }

    /// <summary>
    /// Like <see cref="I"/> for an instruction that reads an I/O register: only in single-step
    /// mode, where the read happens on the machine at the instruction's own cycle.
    /// </summary>
    public bool IoI(ushort pc, int cycles) => _live && I(pc, cycles);

    private static readonly bool[] NoHandWritten = new bool[0x10000];

    /// <summary>
    /// Runs exactly the one instruction at the CPU's PC, whatever the interrupt window (the
    /// interrupt is then taken after it, at the boundary where the CPU takes it). False if
    /// <paramref name="code"/> has no instruction there.
    /// </summary>
    public static bool Step(NativeContext c, Func<Gen, ushort, ushort> code)
    {
        if (c.Cpu.PC < 0x9000 && !c.RomBankSelected)
            return false;
        var g = new Gen(c, NoHandWritten, live: true);
        ushort next = code(g, c.Cpu.PC);
        if (g.Count == 0)
            return false;
        g.T.CommitJumpNow(next);
        return true;
    }

    /// <summary>Ends the call after the current instruction (it changed the interrupt mask).</summary>
    public void StopAfter() => _stopAfter = true;

    /// <summary>Takes back the instruction begun last.</summary>
    private void Undo()
    {
        T.Rollback(_start);
        Count--;
    }

    /// <summary>Runs translated code from the CPU's PC; false (changing nothing) to let the CPU run.</summary>
    public static bool Run(NativeContext c, bool[] handWritten, Func<Gen, ushort, ushort> code)
    {
        ushort entry = c.Cpu.PC;
        if (entry < 0x9000 && !c.RomBankSelected)
            return false;                                        // executing video RAM: not program code
        var g = new Gen(c, handWritten);
        ushort next;
        bool undone = false;
        try
        {
            next = code(g, entry);
        }
        catch (IoReadStop)
        {
            g.Undo();                                            // the CPU runs the instruction that reads I/O
            undone = true;
            next = g.Pc;
        }
        if (g.Count == 0)
            return false;
        if (g.T.CommitJump(next))
            return true;
        // The last instruction ran past the interrupt window (a blit's stall, a taken long
        // branch, a full RTI): leave just before it. (Every instruction's start was checked.)
        if (undone) throw new InvalidOperationException($"translated code could not stop at ${g.Pc:X4}");
        g.Undo();
        if (g.Count == 0)
            return false;
        return g.T.CommitJump(g.Pc) ? true : throw new InvalidOperationException($"translated code could not stop at ${g.Pc:X4}");
    }

    // ------------------------------------------------------------------ bus

    private sealed class IoReadStop : Exception;

    private static readonly IoReadStop Stop = new();

    public byte Rd(int a)
    {
        a &= 0xFFFF;
        if (a < 0x9000) return _romBank ? _c.ReadRom(a) : T.ReadRam(a);
        if (a < 0xC000) return T.Read(a);
        if (a >= 0xD000) return _c.ReadRom(a);
        if (a < 0xC400) return T.Read(0xC000 | (a & 0x0F));     // palette
        if (a >= 0xCC00) return T.Read(0xCC00 | (a & 0x3FF));   // CMOS
        if (_live) return _c.Read(a);                            // the read's side effects happen now
        throw Stop;                                              // PIAs, VSCAN, blitter
    }

    public ushort Rd16(int a) => (ushort)((Rd(a) << 8) | Rd(a + 1));

    public void Wr(int a, byte v)
    {
        a &= 0xFFFF;
        if (a < 0xC000) T.Write(a, v);                           // RAM (under the ROM bank too)
        else if (a >= 0xD000) { }                                // ROM
        else if (a < 0xC400) T.Write(0xC000 | (a & 0x0F), v);
        else if (a >= 0xCC00) T.Write(0xCC00 | (a & 0x3FF), (byte)(v | 0xF0));
        else IoWrite(a, v);
    }

    public void Wr16(int a, ushort v)
    {
        Wr(a, (byte)(v >> 8));
        Wr(a + 1, (byte)v);
    }

    /// <summary>PIA, bank latch, blitter, watchdog: logged in order, and the call ends after this instruction.</summary>
    private void IoWrite(int a, byte v)
    {
        T.IoWrite(a, v, _start.Cycles);                          // as the CPU times it: the instruction's start
        _stopAfter = true;
        if ((a & 0xFF00) != 0xCA00) return;
        if (_blitter == null)
        {
            _blitter = new byte[8];
            for (int r = 0; r < 8; r++) _blitter[r] = _c.BlitterRegister(r);
        }
        _blitter[a & 7] = v;
        if ((a & 7) == 0)                                        // the control byte starts the blit
            T.StallCycles += JoustMachine.BlitStallCycles(_blitter[6] ^ 4, _blitter[7] ^ 4, v);
    }

    // ------------------------------------------------------------------ stacks

    public void PushS(byte v) { T.S--; Wr(T.S, v); }
    public void PushS16(ushort v) { PushS((byte)v); PushS((byte)(v >> 8)); }
    public byte PullS() { byte v = Rd(T.S); T.S++; return v; }
    public ushort PullS16() { int hi = PullS(); return (ushort)((hi << 8) | PullS()); }

    public void PushU(byte v) { T.U--; Wr(T.U, v); }
    public void PushU16(ushort v) { PushU((byte)v); PushU((byte)(v >> 8)); }
    public byte PullU() { byte v = Rd(T.U); T.U++; return v; }
    public ushort PullU16() { int hi = PullU(); return (ushort)((hi << 8) | PullU()); }

    // ------------------------------------------------------------------ TFR / EXG registers

    /// <summary>A TFR/EXG register by its 4-bit code, as the 6809 reads it (8-bit ones with $FF above).</summary>
    public ushort Reg(int code, ushort pc) => code switch
    {
        0x0 => T.D,
        0x1 => T.X,
        0x2 => T.Y,
        0x3 => T.U,
        0x4 => T.S,
        0x5 => pc,
        0x8 => (ushort)(0xFF00 | T.A),
        0x9 => (ushort)(0xFF00 | T.B),
        0xA => (ushort)(0xFF00 | T.CC),
        0xB => (ushort)(0xFF00 | T.DP),
        _ => 0xFFFF,
    };

    /// <summary>Sets a TFR/EXG register (not PC, which the translated code turns into a jump).</summary>
    public void SetReg(int code, ushort v)
    {
        switch (code)
        {
            case 0x0: T.D = v; break;
            case 0x1: T.X = v; break;
            case 0x2: T.Y = v; break;
            case 0x3: T.U = v; break;
            case 0x4: T.S = v; break;
            case 0x8: T.A = (byte)v; break;
            case 0x9: T.B = (byte)v; break;
            case 0xA: T.CC = (byte)v; break;
            case 0xB: T.DP = (byte)v; break;
        }
    }
}
