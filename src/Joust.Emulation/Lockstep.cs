namespace Joust.Emulation;

/// <summary>Frame-level comparison of two machines (e.g. pure original vs. native routines).</summary>
public static class Lockstep
{
    /// <summary>
    /// Returns null when the machines are in the same state, otherwise a description of the
    /// first difference. Stack bytes below S (dead temporaries) are not compared.
    /// </summary>
    public static string? Compare(JoustMachine a, JoustMachine b)
    {
        var ca = a.Cpu.Save();
        var cb = b.Cpu.Save();
        if (ca != cb)
            return $"CPU: original {ca} vs native {cb}";
        if (a.SaveIo() != b.SaveIo())
            return $"I/O: original {a.SaveIo()} vs native {b.SaveIo()}";
        int deadLo = ca.S - 0x100, deadHi = ca.S;
        var ra = a.Ram;
        var rb = b.Ram;
        for (int i = 0; i < ra.Length; i++)
        {
            if (ra[i] != rb[i] && !(i >= deadLo && i < deadHi))
                return $"RAM[{i:X4}]: original {ra[i]:X2} vs native {rb[i]:X2}";
        }
        for (int i = 0; i < 16; i++)
            if (a.Palette[i] != b.Palette[i])
                return $"palette[{i}]: original {a.Palette[i]:X2} vs native {b.Palette[i]:X2}";
        for (int i = 0; i < a.Cmos.Length; i++)
            if (a.Cmos[i] != b.Cmos[i])
                return $"CMOS[{i:X3}]: original {a.Cmos[i]:X2} vs native {b.Cmos[i]:X2}";
        return null;
    }
}

/// <summary>
/// Deterministic pseudo-random player: keeps feeding coins and starts, and flaps and steers
/// with a seeded generator, so long runs cover many waves, deaths and game-overs.
/// </summary>
public sealed class RandomPlayer(int seed)
{
    private uint _state = (uint)seed * 2654435761u + 1;
    private int _flapHold, _dir, _dirHold;

    private uint Next()
    {
        _state ^= _state << 13;
        _state ^= _state >> 17;
        _state ^= _state << 5;
        return _state;
    }

    public void Apply(JoustInputs i, long frame)
    {
        long cycle = frame % 2400;
        i.CoinLeft = cycle is >= 0 and < 4;
        i.Start1 = cycle is >= 100 and < 104;
        i.Start2 = false;

        if (_flapHold > 0) _flapHold--;
        else if (Next() % 100 < 18) _flapHold = 3;
        i.P1Flap = _flapHold > 0;

        if (_dirHold > 0) _dirHold--;
        else
        {
            _dir = (int)(Next() % 3) - 1;
            _dirHold = 10 + (int)(Next() % 50);
        }
        i.P1Left = _dir < 0;
        i.P1Right = _dir > 0;
    }
}
