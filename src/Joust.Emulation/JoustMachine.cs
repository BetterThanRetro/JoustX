namespace Joust.Emulation;

public readonly record struct IoState(PiaState Widget, PiaState Board, bool Mux, bool RomBank);

/// <summary>Player and cabinet switches, all active-high like the original wiring.</summary>
public sealed class JoustInputs
{
    public bool P1Left, P1Right, P1Flap;
    public bool P2Left, P2Right, P2Flap;
    public bool Start1, Start2;
    public bool CoinLeft, CoinCenter, CoinRight;
    public bool AutoUp, Advance, HighScoreReset, Tilt;
}

/// <summary>
/// The Williams Joust main board: 6809 at 1 MHz, 304x256 4-bit video RAM written by the SC1
/// blitter, 16-entry palette, two 6821 PIAs and battery-backed CMOS. One frame is 260 scanlines
/// of 64 CPU cycles (60.1 Hz); the PIA1 CB1 interrupt fires every 64 lines, as on the board.
/// </summary>
public sealed class JoustMachine : IBus
{
    public const int CyclesPerScanline = 64;
    public const int ScanlinesPerFrame = 260;
    public const int ScreenWidth = 304;
    public const int ScreenHeight = 256;

    private readonly byte[] _rom = new byte[0x10000];
    private readonly byte[] _ram = new byte[0xC000];
    private readonly byte[] _blitterRegs = new byte[8];
    private bool _romBank;
    private int _scanline;
    private bool _mux;
    private long _frameTarget;
    private readonly NativeRoutine?[] _natives = new NativeRoutine?[0x10000];
    private readonly NativeContext _nativeContext;

    public Cpu6809 Cpu { get; }
    public Pia6821 Pia0 { get; } = new(); // widget: player controls, start buttons
    public Pia6821 Pia1 { get; } = new(); // coin door, interrupts, sound
    public byte[] Palette { get; } = new byte[16];
    public byte[] Cmos { get; } = new byte[0x400];
    public JoustInputs Inputs { get; } = new();
    public long Frame { get; private set; }

    /// <summary>Raised with the 6-bit command whenever the game writes the sound PIA.</summary>
    public event Action<byte>? SoundCommand;

    /// <summary>Called for every CPU/blitter write (debug tracing); null in normal play.</summary>
    public Action<ushort, byte>? WriteHook { get; set; }

    /// <summary>When set, records (address, previous value) for every memory write so it can be undone.</summary>
    public List<(ushort Address, byte Old)>? WriteJournal { get; set; }

    /// <summary>Set when a write reached I/O (PIA, blitter, bank) while journaling; such writes cannot be undone.</summary>
    public bool JournalSawIo { get; set; }

    /// <summary>
    /// Replaces how an installed native routine is run (the verifier uses this). Returns true if
    /// the call was handled. Null runs the native routine directly.
    /// </summary>
    public Func<NativeRoutine, bool>? NativeDispatch { get; set; }

    /// <summary>Number of calls handled by native routines.</summary>
    public long NativeCalls { get; private set; }

    /// <summary>CPU cycles accounted for by native routines (including blitter stalls they caused).</summary>
    public long NativeCycles { get; private set; }

    public NativeContext NativeContext => _nativeContext;

    public JoustMachine(byte[] image64k)
    {
        if (image64k.Length != 0x10000)
            throw new ArgumentException("expected a 64K memory image", nameof(image64k));
        Array.Copy(image64k, _rom, 0x10000);
        Array.Fill(Cmos, (byte)0xF0);
        Cpu = new Cpu6809(this);
        _nativeContext = new NativeContext(this);

        Pia0.ReadPortA = ReadWidget;
        Pia0.Cb2Changed = level => _mux = level;
        Pia1.ReadPortA = ReadCoinDoor;
        // The sound board latches the low 6 bits; $3F is the idle value written between commands.
        Pia1.WritePortB = v =>
        {
            if ((v & 0x3F) != 0x3F) SoundCommand?.Invoke((byte)(v & 0x3F));
        };
    }

    /// <summary>All 48K of RAM; $0000-$97FF is the column-major frame buffer.</summary>
    public byte[] Ram => _ram;

    /// <summary>Pulses the reset line: CPU, PIAs and ROM bank reset; RAM and CMOS keep their contents.</summary>
    public void Reset()
    {
        _romBank = false;
        _mux = false;
        Pia0.Reset();
        Pia1.Reset();
        Cpu.Reset();
        _frameTarget = Cpu.Cycles;
    }

    /// <summary>Runs one 60 Hz video frame.</summary>
    public void RunFrame()
    {
        for (_scanline = 0; _scanline < ScanlinesPerFrame; _scanline++)
        {
            // VA11 (scanline bit 5) drives PIA1 CB1: the 4 ms interrupt. The counter only
            // covers lines 0-255, so CB1 stays high through 256-259 and its falling edges
            // land on lines 0, 64, 128 and 192 (the IRQ at line 0 refreshes the palette).
            Pia1.SetCb1(_scanline >= 256 || (_scanline & 0x20) != 0);
            // Line 240 drives PIA1 CA1 (the game leaves this interrupt disabled).
            Pia1.SetCa1(_scanline >= 240);
            _frameTarget += CyclesPerScanline;
            while (Cpu.Cycles < _frameTarget)
            {
                Cpu.IrqLine = Pia1.IrqA || Pia1.IrqB;
                var native = _natives[Cpu.PC];
                if (native != null && Cpu.AtInstructionBoundary && InterruptSafe(native.MaxCycles))
                {
                    long before = Cpu.Cycles;
                    bool handled = NativeDispatch?.Invoke(native) ?? native.Execute(_nativeContext);
                    if (handled)
                    {
                        NativeCalls++;
                        NativeCycles += Cpu.Cycles - before;
                        continue;
                    }
                }
                if (Cpu.PC >= IdleStart && Cpu.PC < IdleEnd)
                    IdleCycles += Cpu.Step();
                else
                    Cpu.Step();
            }
        }
        Frame++;
    }

    /// <summary>Address range of a busy-wait loop whose cycles are counted as <see cref="IdleCycles"/>.</summary>
    public ushort IdleStart { get; set; }

    public ushort IdleEnd { get; set; }

    /// <summary>Cycles the original code spent spinning in the idle range.</summary>
    public long IdleCycles { get; private set; }

    /// <summary>
    /// True if <paramref name="cycles"/> from now still ends in this video frame. Native calls
    /// never straddle a frame boundary, so frame-end snapshots of native and original runs
    /// are always taken at the same point.
    /// </summary>
    public bool FitsInFrame(int cycles)
    {
        long frameEnd = _frameTarget + (long)(ScanlinesPerFrame - 1 - _scanline) * CyclesPerScanline;
        return Cpu.Cycles + cycles <= frameEnd;
    }

    /// <summary>CPU cycles the SC1 blitter steals for a blit (w x h bytes after the XOR-4 decode).</summary>
    public static int BlitStallCycles(int width, int height, byte control)
    {
        if (width == 0) width = 1;
        if (height == 0) height = 1;
        int accesses = 2 * width * height;
        int clocksAt4MHz = (control & 0x04) != 0 ? 4 + 4 * (accesses + 2) : 4 + 2 * (accesses + 3);
        return (clocksAt4MHz + 3) / 4;
    }

    /// <summary>Installs a native routine at its original entry address.</summary>
    public void InstallNative(NativeRoutine routine, ushort address) => _natives[address] = routine;

    public void RemoveNative(ushort address) => _natives[address] = null;

    /// <summary>
    /// A native routine runs atomically, so it may only run when the original could not have
    /// been interrupted part-way: IRQs are masked, or no interrupt-raising scanline (the CB1
    /// edges at lines 0/64/128/192 and CA1 at 240) starts within its cycle window.
    /// </summary>
    public bool InterruptSafe(int maxCycles)
    {
        if (!FitsInFrame(maxCycles))
            return false;
        if ((Cpu.CC & Cpu6809.FlagI) != 0)
            return true;
        long end = Cpu.Cycles + maxCycles;
        long boundary = _frameTarget;
        for (int line = _scanline + 1; end > boundary; line++, boundary += CyclesPerScanline)
        {
            int l = line % ScanlinesPerFrame;
            if ((l < 256 && l % 64 == 0) || l == 240)
                return false;
        }
        return true;
    }

    private byte ReadWidget() => WidgetInputs(_mux);

    /// <summary>The switches on widget port A with the player multiplexer at <paramref name="player1"/>.</summary>
    private byte WidgetInputs(bool player1)
    {
        var i = Inputs;
        int v = 0;
        if (player1)
        {
            if (i.P1Left) v |= 0x01;
            if (i.P1Right) v |= 0x02;
            if (i.P1Flap) v |= 0x04;
        }
        else
        {
            if (i.P2Left) v |= 0x01;
            if (i.P2Right) v |= 0x02;
            if (i.P2Flap) v |= 0x04;
        }
        if (i.Start2) v |= 0x10;
        if (i.Start1) v |= 0x20;
        return (byte)v;
    }

    private byte ReadCoinDoor()
    {
        var i = Inputs;
        int v = 0;
        if (i.AutoUp) v |= 0x01;
        if (i.Advance) v |= 0x02;
        if (i.CoinRight) v |= 0x04;
        if (i.HighScoreReset) v |= 0x08;
        if (i.CoinLeft) v |= 0x10;
        if (i.CoinCenter) v |= 0x20;
        if (i.Tilt) v |= 0x40;
        return (byte)v;
    }

    // ------------------------------------------------------------------ bus

    public byte Read(ushort a)
    {
        if (a < 0x9000)
            return _romBank ? _rom[a] : _ram[a];
        if (a < 0xC000)
            return _ram[a];
        if (a >= 0xD000)
            return _rom[a];
        if (a < 0xC400)
            return Palette[a & 0x0F];
        if (a >= 0xCC00)
            return Cmos[a & 0x3FF];
        if ((a & 0xFF00) == 0xC800)
        {
            int reg = a & 0x0F;
            if (reg is >= 4 and <= 7) return Pia0.Read(reg - 4);
            if (reg >= 12) return Pia1.Read(reg - 12);
            return 0;
        }
        if ((a & 0xFF00) == 0xCB00)
            return (byte)(_scanline < 0x100 ? _scanline & 0xFC : 0xFC);
        return 0;
    }

    public void Write(ushort a, byte v)
    {
        WriteHook?.Invoke(a, v);
        if (a < 0xC000)
        {
            WriteJournal?.Add((a, _ram[a]));
            _ram[a] = v;
            return;
        }
        if (a >= 0xD000)
            return; // ROM
        if (a < 0xC400)
        {
            WriteJournal?.Add((a, Palette[a & 0x0F]));
            Palette[a & 0x0F] = v;
            return;
        }
        if (a >= 0xCC00)
        {
            WriteJournal?.Add((a, Cmos[a & 0x3FF]));
            Cmos[a & 0x3FF] = (byte)(v | 0xF0); // 4-bit CMOS
            return;
        }
        if (WriteJournal != null && (a & 0xFF00) != 0xCA00)
            JournalSawIo = true; // PIA or bank register: cannot be undone
        switch (a & 0xFF00)
        {
            case 0xC800:
            {
                int reg = a & 0x0F;
                if (reg is >= 4 and <= 7) Pia0.Write(reg - 4, v);
                else if (reg >= 12) Pia1.Write(reg - 12, v);
                break;
            }
            case 0xC900:
                _romBank = (v & 0x01) != 0;
                break;
            case 0xCA00:
                WriteBlitter(a & 7, v);
                break;
        }
    }

    // ------------------------------------------------------------------ SC1 blitter

    /// <summary>Byte of the program image (ROM), regardless of the bank switch.</summary>
    public byte ReadRom(ushort a) => _rom[a];

    /// <summary>State of the PIAs and bank/mux latches, so a native call's I/O can be undone.</summary>
    public IoState SaveIo() => new(Pia0.Save(), Pia1.Save(), _mux, _romBank);

    public void RestoreIo(IoState s)
    {
        Pia0.Restore(s.Widget);
        Pia1.Restore(s.Board);
        _mux = s.Mux;
        _romBank = s.RomBank;
    }

    /// <summary>Current player multiplexer (CB2 of the widget PIA): true = player 1.</summary>
    public bool WidgetMux => _mux;

    /// <summary>What reading widget port A would return with the multiplexer at <paramref name="player1"/>, without side effects.</summary>
    public byte PeekWidget(bool player1) => Pia0.PeekA(WidgetInputs(player1));

    /// <summary>Reads what a journaled address currently holds (RAM, palette, CMOS or blitter register).</summary>
    public byte Peek(ushort a) => a switch
    {
        < 0xC000 => _ram[a],
        < 0xC400 => Palette[a & 0x0F],
        >= 0xCA00 and < 0xCB00 => _blitterRegs[a & 7],
        _ => Cmos[a & 0x3FF],
    };

    /// <summary>Puts back a journaled value without side effects (no blit is triggered).</summary>
    public void Restore(ushort a, byte old)
    {
        if (a < 0xC000) _ram[a] = old;
        else if (a < 0xC400) Palette[a & 0x0F] = old;
        else if ((a & 0xFF00) == 0xCA00) _blitterRegs[a & 7] = old;
        else Cmos[a & 0x3FF] = old;
    }

    private void WriteBlitter(int reg, byte v)
    {
        WriteJournal?.Add(((ushort)(0xCA00 | reg), _blitterRegs[reg]));
        _blitterRegs[reg] = v;
        if (reg != 0) return;

        int control = v;
        int src = (_blitterRegs[2] << 8) | _blitterRegs[3];
        int dst = (_blitterRegs[4] << 8) | _blitterRegs[5];
        // The SC1 chip has the "bit 2" bug: width and height arrive XORed with 4.
        int w = _blitterRegs[6] ^ 4;
        int h = _blitterRegs[7] ^ 4;
        if (w == 0) w = 1;
        if (h == 0) h = 1;

        Blit(src, dst, w, h, control);
        Cpu.Stall(BlitStallCycles(w, h, (byte)control));
    }

    private int Blit(int sstart, int dstart, int w, int h, int control)
    {
        int sxadv = (control & 0x01) != 0 ? 0x100 : 1;
        int syadv = (control & 0x01) != 0 ? 1 : w;
        int dxadv = (control & 0x02) != 0 ? 0x100 : 1;
        int dyadv = (control & 0x02) != 0 ? 1 : w;
        int pixdata = 0;
        int accesses = 0;

        for (int y = 0; y < h; y++)
        {
            int source = sstart & 0xFFFF;
            int dest = dstart & 0xFFFF;
            for (int x = 0; x < w; x++)
            {
                int data = Read((ushort)source);
                if ((control & 0x20) != 0)
                {
                    pixdata = (pixdata << 8) | data;
                    BlitPixel(dest, (pixdata >> 4) & 0xFF, control);
                }
                else
                {
                    BlitPixel(dest, data, control);
                }
                accesses += 2;
                source = (source + sxadv) & 0xFFFF;
                dest = (dest + dxadv) & 0xFFFF;
            }
            dstart = (control & 0x02) != 0 ? (dstart & 0xFF00) | ((dstart + dyadv) & 0xFF) : dstart + dyadv;
            sstart = (control & 0x01) != 0 ? (sstart & 0xFF00) | ((sstart + syadv) & 0xFF) : sstart + syadv;
        }
        return accesses;
    }

    private void BlitPixel(int dst, int srcData, int control)
    {
        int cur = dst < 0xC000 ? _ram[dst] : Read((ushort)dst);
        int keep = 0xFF;
        bool fgOnly = (control & 0x08) != 0;

        if (fgOnly && (srcData & 0xF0) == 0)
        {
            if ((control & 0x40) != 0) keep &= 0x0F;
        }
        else if ((control & 0x40) == 0)
        {
            keep &= 0x0F;
        }

        if (fgOnly && (srcData & 0x0F) == 0)
        {
            if ((control & 0x80) != 0) keep &= 0xF0;
        }
        else if ((control & 0x80) == 0)
        {
            keep &= 0xF0;
        }

        cur &= keep;
        cur |= ((control & 0x10) != 0 ? _blitterRegs[1] : srcData) & ~keep;
        if (dst < 0xC000)
        {
            WriteJournal?.Add(((ushort)dst, _ram[dst]));
            _ram[dst] = (byte)cur;
        }
        else
        {
            Write((ushort)dst, (byte)cur);
        }
    }

    // ------------------------------------------------------------------ video

    /// <summary>
    /// Renders the 4-bit frame buffer to 32-bit ARGB using the Williams palette byte format
    /// (BBGGGRRR, resistor-weighted). Video RAM is column-major: address = x/2 * 256 + y.
    /// </summary>
    public void RenderArgb(Span<uint> target)
    {
        Span<uint> lut = stackalloc uint[16];
        for (int i = 0; i < 16; i++)
            lut[i] = PaletteToArgb(Palette[i]);
        for (int y = 0; y < ScreenHeight; y++)
        {
            int row = y * ScreenWidth;
            for (int col = 0; col < ScreenWidth / 2; col++)
            {
                byte b = _ram[(col << 8) | y];
                target[row + col * 2] = lut[b >> 4];
                target[row + col * 2 + 1] = lut[b & 0x0F];
            }
        }
    }

    public static uint PaletteToArgb(byte entry)
    {
        // Weighted resistor DAC values from MAME's williams palette (rounded).
        static int R3(int bits) => bits switch { 0 => 0, 1 => 38, 2 => 81, 3 => 118, 4 => 137, 5 => 174, 6 => 218, _ => 255 };
        static int B2(int bits) => bits switch { 0 => 0, 1 => 95, 2 => 160, _ => 255 };
        int r = R3(entry & 7);
        int g = R3((entry >> 3) & 7);
        int b = B2((entry >> 6) & 3);
        return 0xFF000000u | (uint)(r << 16) | (uint)(g << 8) | (uint)b;
    }
}
