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

/// <summary>A scripted player for long test runs: sets the inputs for each frame.</summary>
public interface ITestPlayer
{
    void Apply(JoustInputs i, long frame);
}

/// <summary>
/// Deterministic pseudo-random player: keeps feeding coins and starts, and flaps and steers
/// with a seeded generator, so long runs cover many waves, deaths and game-overs.
/// </summary>
public sealed class RandomPlayer(int seed, bool twoPlayers = false, int flapPercent = 18) : ITestPlayer
{
    private uint _state = (uint)seed * 2654435761u + 1;
    private int _flapHold, _dir, _dirHold;
    private int _flapHold2, _dir2, _dirHold2;

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
        // Two coins (on separate presses) for a two-player game.
        i.CoinLeft = cycle is >= 0 and < 4 || (twoPlayers && cycle is >= 20 and < 24);
        i.Start1 = !twoPlayers && cycle is >= 100 and < 104;
        i.Start2 = twoPlayers && cycle is >= 100 and < 104;

        (i.P1Flap, i.P1Left, i.P1Right) = Steer(ref _flapHold, ref _dir, ref _dirHold);
        if (twoPlayers)
            (i.P2Flap, i.P2Left, i.P2Right) = Steer(ref _flapHold2, ref _dir2, ref _dirHold2);
    }

    private (bool Flap, bool Left, bool Right) Steer(ref int flapHold, ref int dir, ref int dirHold)
    {
        if (flapHold > 0) flapHold--;
        else if (Next() % 100 < flapPercent) flapHold = 3;

        if (dirHold > 0) dirHold--;
        else
        {
            dir = (int)(Next() % 3) - 1;
            dirHold = 10 + (int)(Next() % 50);
        }
        return (flapHold > 0, dir < 0, dir > 0);
    }
}
