namespace Joust.Emulation;

/// <summary>
/// A test scenario for the lava: the troll is awake and the bridge burns from the first wave
/// (normally waves 4 and 3, which the random player rarely reaches), and every 500 frames a
/// flying player 1 is dropped just above the middle of the lava pit, falling, so the lava death
/// runs too. Apply it to every machine of a comparison before each frame: the change depends
/// only on the machine's own state, so identical machines stay identical.
/// Use it with a <see cref="RandomPlayer"/> that rarely flaps (<see cref="FlapPercent"/>).
/// </summary>
public sealed class TrollScenario(IReadOnlyDictionary<string, long> s)
{

    /// <summary>Flap rate for the random player: low, so birds sink to the lava.</summary>
    public const int FlapPercent = 1;

    private readonly ushort _trollWaves = (ushort)s["TTROLL"], _bridgeWaves = (ushort)s["TBRIDGE"];
    private readonly ushort _target = (ushort)s["TARPLY"];
    private readonly int _id = (int)s["PID"], _state = (int)s["PSTATE"], _posX = (int)s["PPOSX"];
    private readonly int _posY = (int)s["PPOSY"], _velY = (int)s["PVELY"];
    private readonly byte _floor = (byte)s["FLOOR"];

    public void Apply(JoustMachine m)
    {
        byte[] ram = m.Ram;
        ram[_trollWaves] = 0;                                    // the troll is out
        if (ram[_bridgeWaves] > 1) ram[_bridgeWaves] = 1;        // the bridge burns at the next wave
        if (m.Frame % 500 != 499) return;
        int bird = (ram[_target] << 8) | ram[_target + 1];
        if (bird == 0 || (ram[bird + _id] & 0x80) == 0) return;
        if (ram[bird + _state] != 0 || ram[bird + _state + 1] != 0) return;   // only in the air
        ram[bird + _posX] = 0;
        ram[bird + _posX + 1] = 150;                             // over the middle of the pit
        ram[bird + _posY + 1] = (byte)(_floor + 6);              // just above the lava
        ram[bird + _velY] = 0x01;
        ram[bird + _velY + 1] = 0x00;                            // falling
    }
}

/// <summary>
/// A test scenario that starts every game in the wave table's repeating section (WTBRST, wave
/// 81 on), where cliffs vanish and reappear between waves: when the wave controller (CIA) has
/// just initialized its table pointer, it is moved to the first entry there whose next wave
/// brings a cliff back, so the next wave creates a cliff and later ones destroy them again.
/// </summary>
public sealed class CliffScenario
{
    private readonly ushort _head;
    private readonly int _link, _id, _pWave;
    private readonly ushort _initial, _start;

    /// <param name="creating">Start before a cliff comes back (else at WTBRST, where one vanishes first).</param>
    public CliffScenario(byte[] image, IReadOnlyDictionary<string, long> s, bool creating = true)
    {
        _head = (ushort)s["PDUMMY"];
        _link = (int)s["PLINK"]; _id = (int)s["PID"]; _pWave = (int)s["PWAVE"];
        int status = (int)s["WSTATUS"], length = (int)s["WLEN"];
        _initial = (ushort)(s["WAVTBL"] - length);
        long entry = s["WTBRST"];
        if (!creating) entry -= length;
        while (creating && entry + length < s["WTBEND"] && (image[entry + status] & ~image[entry + length + status] & 0xF0) == 0)
            entry += length;                                     // a cliff bit set here and clear next
        _start = (ushort)entry;
    }

    public void Apply(JoustMachine m)
    {
        byte[] ram = m.Ram;
        int p = (ram[_head + _link] << 8) | ram[_head + _link + 1];
        for (int guard = 0; p != 0 && guard < 64; guard++)
        {
            if (p + 0x40 > ram.Length) return;
            if (ram[p + _id] == 0x11)                            // the CIA process
            {
                if (((ram[p + _pWave] << 8) | ram[p + _pWave + 1]) == _initial)
                {
                    ram[p + _pWave] = (byte)(_start >> 8);
                    ram[p + _pWave + 1] = (byte)_start;
                }
                return;
            }
            p = (ram[p + _link] << 8) | ram[p + _link + 1];
        }
    }
}

/// <summary>
/// A test scenario where every wave is an egg wave (normally wave 5 and later): before each frame
/// the wave controller's (CIA's) wave table pointer is set just before the table's first egg
/// wave, unless that wave is running. The ROM is untouched (its checksums are tested at boot).
/// </summary>
public sealed class EggScenario
{
    private readonly ushort _head;
    private readonly int _link, _id, _pWave;
    private readonly ushort _eggWave, _before;
    private const byte CiaId = 0x11;

    public EggScenario(byte[] image, IReadOnlyDictionary<string, long> s)
    {
        _head = (ushort)s["PDUMMY"];
        _link = (int)s["PLINK"]; _id = (int)s["PID"]; _pWave = (int)s["PWAVE"];
        int status = (int)s["WSTATUS"], length = (int)s["WLEN"];
        long entry = s["WAVTBL"];
        while ((image[entry + status] & 0x0E) != 0x08) entry += length;   // WJSRTB entry 4: WAVEGG
        _eggWave = (ushort)entry;
        _before = (ushort)(entry - length);
    }

    public void Apply(JoustMachine m)
    {
        byte[] ram = m.Ram;
        int p = (ram[_head + _link] << 8) | ram[_head + _link + 1];
        for (int guard = 0; p != 0 && guard < 64; guard++)
        {
            if (p + 0x40 > ram.Length) return;                   // not a process list yet (booting)
            if (ram[p + _id] == CiaId)
            {
                int wave = (ram[p + _pWave] << 8) | ram[p + _pWave + 1];
                if (wave != _eggWave && wave != _before)
                {
                    ram[p + _pWave] = (byte)(_before >> 8);
                    ram[p + _pWave + 1] = (byte)_before;
                }
                return;
            }
            p = (ram[p + _link] << 8) | ram[p + _link + 1];
        }
    }
}
