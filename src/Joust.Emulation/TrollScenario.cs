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
