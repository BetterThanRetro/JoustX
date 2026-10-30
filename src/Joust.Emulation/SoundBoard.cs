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
        double cyclesPerSample = ClockHz / sampleRate;
        _target += cycles;                                       // absolute, so instruction overshoot never drifts
        int n = 0;
        while (Cpu.Cycles < _target)
        {
            Cpu.IrqLine = Pia.IrqA || Pia.IrqB;
            byte level = _dac;
            int c = Cpu.Step();
            // The DAC held its old level for this instruction (writes land at its end).
            double left = c;
            while (left > 0)
            {
                double take = Math.Min(left, cyclesPerSample - _sampleClock);
                _sampleSum += take * (level - 128) / 128.0;
                _sampleWeight += take;
                _sampleClock += take;
                left -= take;
                if (_sampleClock >= cyclesPerSample - 1e-9)
                {
                    if (n < samples.Length) samples[n++] = (float)(_sampleSum / _sampleWeight);
                    _sampleClock = _sampleSum = _sampleWeight = 0;
                }
            }
        }
        return n;
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
