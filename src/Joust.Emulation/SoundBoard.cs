namespace Joust.Emulation;

/// <summary>
/// The Williams sound board: a 6808 at 894.886 kHz with 128 bytes of RAM, a 6821 PIA at $0400
/// and the 4K sound ROM at $F000. The main board's sound command arrives on PIA port B
/// (bits 0-5) with CB1 raised for any value but the idle $3F, which interrupts the 6808; port A
/// drives the 8-bit DAC. It never talks back, so it runs beside the game without affecting it.
/// </summary>
public sealed class SoundBoard : IBus6800
{
    public const double ClockHz = 3_579_545.0 / 4;

    private readonly byte[] _rom = new byte[0x1000];
    private readonly byte[] _ram = new byte[0x80];
    private byte _command = 0x3F, _dac = 0x80;
    private double _sampleClock, _sampleSum, _sampleWeight;
    private long _target;

    public SoundBoard(byte[] image64k)
    {
        Array.Copy(image64k, 0xF000, _rom, 0, 0x1000);
        Cpu = new Cpu6800(this);
        Pia.ReadPortB = () => (byte)(_command | 0xC0);
        Pia.WritePortA = v => _dac = v;
    }

    public Cpu6800 Cpu { get; }
    public Pia6821 Pia { get; } = new();

    /// <summary>The DAC's current level (0-255, $80 is silence).</summary>
    public byte Dac => _dac;

    public void Reset()
    {
        Pia.Reset();
        Cpu.Reset();
        _command = 0x3F;
        _target = Cpu.Cycles;
    }

    /// <summary>The main board writes its sound PIA port (bits 0-5 are the command).</summary>
    public void Command(byte value)
    {
        _command = (byte)(value & 0x3F);
        Pia.SetCb1(_command != 0x3F);
    }

    /// <summary>
    /// Runs the board for <paramref name="cycles"/> of its own clock, writing the DAC output as
    /// samples in -1..1 at <paramref name="sampleRate"/> (box-filtered per sample). Returns the
    /// number of samples written.
    /// </summary>
    public int Run(long cycles, int sampleRate, Span<float> samples)
    {
        _cyclesPerSample = ClockHz / sampleRate;
        _target += cycles;                                       // absolute, so instruction overshoot never drifts
        _out = samples.Length <= _buffer.Length ? _buffer : _buffer = new float[samples.Length];
        _outLimit = samples.Length;
        _n = 0;
        while (Cpu.Cycles < _target)
        {
            if (Runner != null)
            {
                Runner(this);
                continue;
            }
            Cpu.IrqLine = Pia.IrqA || Pia.IrqB;
            byte level = _dac;
            int c = Cpu.Step();
            CpuSteps++;
            Account(level, c);
        }
        _out.AsSpan(0, _n).CopyTo(samples);
        return _n;
    }

    /// <summary>
    /// Replaces the emulated 6808: runs instructions (each through <see cref="Spend"/>) until
    /// <see cref="Due"/>, or enters a pending interrupt. Set by the translated sound program.
    /// </summary>
    public Action<SoundBoard>? Runner { get; set; }

    /// <summary>Instructions (and interrupt entries) the emulated 6808 core has run.</summary>
    public long CpuSteps { get; private set; }

    /// <summary>
    /// True when the next instruction must not run as part of a translated stretch: the time
    /// slice is used up, or an interrupt is pending (the 6808 checks before every instruction).
    /// </summary>
    public bool Due => Cpu.Cycles >= _target || ((Pia.IrqA || Pia.IrqB) && (Cpu.CC & Alu6800.I) == 0);

    /// <summary>
    /// Charges the next instruction's (or interrupt entry's) <paramref name="cycles"/>, with the
    /// DAC at the level it had before the instruction: its writes land at its end.
    /// </summary>
    public void Spend(int cycles)
    {
        byte level = _dac;
        Cpu.Cycles += cycles;
        Account(level, cycles);
    }

    /// <summary>Enters the pending IRQ (the command interrupt) as the 6808 does, and charges its cycles.</summary>
    public void EnterInterrupt()
    {
        byte level = _dac;
        int c = Cpu.Interrupt(0xFFF8);
        Account(level, c);
    }

    private double _cyclesPerSample;
    private float[] _buffer = new float[4096];
    private float[] _out = [];
    private int _n, _outLimit;

    private void Account(byte level, int c)
    {
        double left = c;
        while (left > 0)
        {
            double take = Math.Min(left, _cyclesPerSample - _sampleClock);
            _sampleSum += take * (level - 128) / 128.0;
            _sampleWeight += take;
            _sampleClock += take;
            left -= take;
            if (_sampleClock >= _cyclesPerSample - 1e-9)
            {
                if (_n < _outLimit) _out[_n++] = (float)(_sampleSum / _sampleWeight);
                _sampleClock = _sampleSum = _sampleWeight = 0;
            }
        }
    }

    public byte Read(ushort a)
    {
        if (a >= 0x8000) return _rom[a & 0x0FFF];
        if (a < 0x0400) return _ram[a & 0x7F];
        if (a < 0x0800) return Pia.Read(a & 3);
        return 0xFF;
    }

    public void Write(ushort a, byte v)
    {
        if (a < 0x0400) _ram[a & 0x7F] = v;
        else if (a < 0x0800) Pia.Write(a & 3, v);
    }
}
