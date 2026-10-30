namespace Joust.Emulation;

public readonly record struct PiaState(
    byte DdrA, byte OutA, byte CrA, byte DdrB, byte OutB, byte CrB, bool Ca1, bool Cb1, bool Ca2, bool Cb2);

/// <summary>Motorola 6821 PIA: the subset of behaviour Williams boards depend on.</summary>
public sealed class Pia6821
{
    private byte _ddrA, _outA, _crA;
    private byte _ddrB, _outB, _crB;
    private bool _ca1, _cb1;

    public Func<byte> ReadPortA { get; set; } = () => 0;
    public Func<byte> ReadPortB { get; set; } = () => 0;
    public Action<byte>? WritePortA { get; set; }
    public Action<byte>? WritePortB { get; set; }
    public Action<bool>? Ca2Changed { get; set; }
    public Action<bool>? Cb2Changed { get; set; }

    public bool Ca2Output { get; private set; }
    public bool Cb2Output { get; private set; }

    public bool IrqA => ((_crA & 0x80) != 0 && (_crA & 0x01) != 0) ||
                        ((_crA & 0x40) != 0 && (_crA & 0x28) == 0x08);

    public bool IrqB => ((_crB & 0x80) != 0 && (_crB & 0x01) != 0) ||
                        ((_crB & 0x40) != 0 && (_crB & 0x28) == 0x08);

    public byte OutputB => _outB;

    public PiaState Save() => new(_ddrA, _outA, _crA, _ddrB, _outB, _crB, _ca1, _cb1, Ca2Output, Cb2Output);

    public void Restore(PiaState s)
    {
        (_ddrA, _outA, _crA, _ddrB, _outB, _crB, _ca1, _cb1) = (s.DdrA, s.OutA, s.CrA, s.DdrB, s.OutB, s.CrB, s.Ca1, s.Cb1);
        Ca2Output = s.Ca2;
        Cb2Output = s.Cb2;
    }

    /// <summary>What reading port A would return for <paramref name="input"/>, without side effects.</summary>
    public byte PeekA(byte input) =>
        (_crA & 0x04) == 0 ? _ddrA : (byte)((input & ~_ddrA) | (_outA & _ddrA));

    public void Reset()
    {
        _ddrA = _outA = _crA = 0;
        _ddrB = _outB = _crB = 0;
        Ca2Output = Cb2Output = false;
    }

    public byte Read(int offset)
    {
        switch (offset & 3)
        {
            case 0:
                if ((_crA & 0x04) == 0) return _ddrA;
                _crA &= 0x3F; // reading the data register clears the IRQ flags
                return (byte)((ReadPortA() & ~_ddrA) | (_outA & _ddrA));
            case 1:
                return _crA;
            case 2:
                if ((_crB & 0x04) == 0) return _ddrB;
                _crB &= 0x3F;
                return (byte)((ReadPortB() & ~_ddrB) | (_outB & _ddrB));
            default:
                return _crB;
        }
    }

    public void Write(int offset, byte value)
    {
        switch (offset & 3)
        {
            case 0:
                if ((_crA & 0x04) == 0) _ddrA = value;
                else
                {
                    _outA = value;
                    WritePortA?.Invoke((byte)(_outA & _ddrA));
                }
                break;
            case 1:
                _crA = (byte)((_crA & 0xC0) | (value & 0x3F));
                UpdateControlLine(_crA, isA: true);
                break;
            case 2:
                if ((_crB & 0x04) == 0) _ddrB = value;
                else
                {
                    _outB = value;
                    WritePortB?.Invoke((byte)(_outB & _ddrB));
                }
                break;
            default:
                _crB = (byte)((_crB & 0xC0) | (value & 0x3F));
                UpdateControlLine(_crB, isA: false);
                break;
        }
    }

    private void UpdateControlLine(byte cr, bool isA)
    {
        // Manual output mode (bits 5,4 = 1,1): CA2/CB2 follows bit 3.
        if ((cr & 0x30) != 0x30) return;
        bool level = (cr & 0x08) != 0;
        if (isA)
        {
            if (Ca2Output == level) return;
            Ca2Output = level;
            Ca2Changed?.Invoke(level);
        }
        else
        {
            if (Cb2Output == level) return;
            Cb2Output = level;
            Cb2Changed?.Invoke(level);
        }
    }

    public void SetCa1(bool level)
    {
        if (level == _ca1) return;
        bool rising = level && !_ca1;
        _ca1 = level;
        if (rising == ((_crA & 0x02) != 0))
            _crA |= 0x80;
    }

    public void SetCb1(bool level)
    {
        if (level == _cb1) return;
        bool rising = level && !_cb1;
        _cb1 = level;
        if (rising == ((_crB & 0x02) != 0))
            _crB |= 0x80;
    }
}
