using Joust.Emulation;
using static Joust.Emulation.Alu6809;

namespace Joust.Port.Routines;

/// <summary>
/// RANDSYS (SYSTEM.ASM): the game's random number generator. RANDOM:RANDOM+1 is a 16-bit
/// shift register; each call shifts it right one bit, feeding back bit0 XOR bit3 of the low
/// byte. The IRQ handler also stirs RANDOM every 4 ms.
/// Returns A = 0..127 and Carry = a random bit.
/// </summary>
public sealed class RandomRoutine : NativeRoutine
{
    private ushort _random;

    public override string Symbol => "RANDSYS";
    public override int MaxCycles => 33;

    public override void Bind(IReadOnlyDictionary<string, long> symbols) => _random = Sym(symbols, "RANDOM");

    public override bool Execute(NativeContext c)
    {
        var cpu = c.Cpu;
        int hiAddr = c.Direct(_random);
        int loAddr = c.Direct((ushort)(_random + 1));
        byte hi = c.Read(hiAddr);
        byte lo = c.Read(loAddr);

        int mixed = lo ^ (lo >> 3);
        bool feedback = (mixed & 1) != 0;
        byte newHi = (byte)((hi >> 1) | (feedback ? 0x80 : 0));
        byte newLo = (byte)((lo >> 1) | ((hi & 1) != 0 ? 0x80 : 0));
        c.Write(hiAddr, newHi);
        c.Write(loAddr, newLo);

        cpu.A = (byte)(mixed >> 1);
        // Last flag writer is ROR RANDOM+1 (N, Z, C); V was cleared by EORA; H untouched.
        byte cc = Logic8(cpu.CC, newLo);
        cpu.CC = Set(cc, C, (lo & 1) != 0);
        c.Return(33);
        return true;
    }
}
