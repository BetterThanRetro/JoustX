using Joust.Emulation;
using static Joust.Emulation.Alu6809;

namespace Joust.Port.Routines;

/// <summary>
/// DCOLOR / DCOLR2 (SYSTEM.ASM): copy 16 palette bytes from [X] into RAMCOL (DCOLOR) or into
/// [Y] (DCOLR2). The IRQ at line 0 pushes RAMCOL into the colour RAM.
/// </summary>
public sealed class LoadColorsRoutine(bool explicitDestination) : NativeRoutine
{
    private ushort _ramColors;

    public override string Symbol => explicitDestination ? "DCOLR2" : "DCOLOR";

    // DCOLOR: LDY #RAMCOL (4) + LDB #16 (2) + 16 x [LDA ,X+ / STA ,Y+ / DECB / BNE] (17) + RTS (5)
    public override int MaxCycles => (explicitDestination ? 0 : 4) + 2 + 16 * 17 + 5;

    public override void Bind(IReadOnlyDictionary<string, long> symbols) => _ramColors = Sym(symbols, "RAMCOL");

    public override bool Execute(NativeContext c)
    {
        var cpu = c.Cpu;
        ushort src = cpu.X;
        ushort dst = explicitDestination ? cpu.Y : _ramColors;
        byte last = 0;
        for (int i = 0; i < 16; i++)
        {
            last = c.Read((ushort)(src + i));
            c.Write((ushort)(dst + i), last);
        }
        cpu.X = (ushort)(src + 16);
        cpu.Y = (ushort)(dst + 16);
        cpu.A = last;
        cpu.B = 0;
        cpu.CC = Logic8(cpu.CC, 0); // final DECB reaching zero: Z set, N and V clear
        c.Return(MaxCycles);
        return true;
    }
}
