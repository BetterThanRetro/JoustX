using Joust.Emulation;

namespace Joust.Tests;

public class CpuTests
{
    private sealed class FlatBus : IBus
    {
        public readonly byte[] Mem = new byte[0x10000];
        public byte Read(ushort a) => Mem[a];
        public void Write(ushort a, byte v) => Mem[a] = v;
    }

    /// <summary>Loads code at $1000, runs until it reaches the trailing infinite loop.</summary>
    private static (Cpu6809 Cpu, FlatBus Bus) Run(params byte[] code)
    {
        var bus = new FlatBus();
        code.CopyTo(bus.Mem, 0x1000);
        int end = 0x1000 + code.Length;
        bus.Mem[end] = 0x20; // BRA *
        bus.Mem[end + 1] = 0xFE;
        bus.Mem[0xFFFE] = 0x10;
        bus.Mem[0xFFFF] = 0x00;
        var cpu = new Cpu6809(bus);
        cpu.Reset();
        cpu.S = 0x8000;
        for (int i = 0; i < 10_000 && cpu.PC != end; i++)
            cpu.Step();
        Assert.Equal(end, cpu.PC);
        return (cpu, bus);
    }

    [Fact]
    public void AddSetsHalfCarryOverflowAndCarry()
    {
        var (cpu, _) = Run(0x86, 0x7F, 0x8B, 0x01); // LDA #$7F; ADDA #1
        Assert.Equal(0x80, cpu.A);
        Assert.Equal(Cpu6809.FlagN | Cpu6809.FlagV | Cpu6809.FlagH, cpu.CC & 0x2F);

        (cpu, _) = Run(0x86, 0xFF, 0x8B, 0x01);
        Assert.Equal(0x00, cpu.A);
        Assert.Equal(Cpu6809.FlagZ | Cpu6809.FlagC | Cpu6809.FlagH, cpu.CC & 0x2F);
    }

    [Fact]
    public void DaaAdjustsBcdAddition()
    {
        var (cpu, _) = Run(0x86, 0x19, 0x8B, 0x28, 0x19); // 19 + 28 = 47 BCD
        Assert.Equal(0x47, cpu.A);
        (cpu, _) = Run(0x86, 0x99, 0x8B, 0x01, 0x19);     // 99 + 01 = 00, carry
        Assert.Equal(0x00, cpu.A);
        Assert.True((cpu.CC & Cpu6809.FlagC) != 0);
    }

    [Fact]
    public void MulAndSex()
    {
        var (cpu, _) = Run(0x86, 0x0C, 0xC6, 0x0B, 0x3D); // 12*11
        Assert.Equal(132, cpu.D);
        (cpu, _) = Run(0xC6, 0x80, 0x1D);
        Assert.Equal(0xFF80, cpu.D);
        Assert.True((cpu.CC & Cpu6809.FlagN) != 0);
    }

    [Fact]
    public void PushPullOrderMatchesHardware()
    {
        // LDX #$1234; LDA #$56; PSHS X,A -> A at lowest address, then X
        var (cpu, bus) = Run(0x8E, 0x12, 0x34, 0x86, 0x56, 0x34, 0x12);
        Assert.Equal(0x7FFD, cpu.S);
        Assert.Equal(0x56, bus.Mem[0x7FFD]);
        Assert.Equal(0x12, bus.Mem[0x7FFE]);
        Assert.Equal(0x34, bus.Mem[0x7FFF]);
    }

    [Fact]
    public void SignedBranchesAndSubroutines()
    {
        // LDA #-2; CMPA #1; BLT +2; LDB #1; BSR +1; RTS?  -> covers BLT taken and BSR/RTS
        var (cpu, _) = Run(0x86, 0xFE, 0x81, 0x01, 0x2D, 0x02, 0xC6, 0x01, 0x8D, 0x02, 0x20, 0x03, 0xC6, 0x07, 0x39);
        Assert.Equal(0x07, cpu.B);
    }

    [Fact]
    public void IndexedAutoIncrementAndIndirect()
    {
        // LDX #$2000; LDD ,X++; LDY [,X]   with $2000=1234 $2002=3000 $3000=ABCD
        var bus0 = new byte[] { 0x8E, 0x20, 0x00, 0xEC, 0x81, 0x10, 0xAE, 0x94 };
        var bus = new FlatBus();
        bus0.CopyTo(bus.Mem, 0x1000);
        bus.Mem[0x1008] = 0x20; bus.Mem[0x1009] = 0xFE;
        bus.Mem[0x2000] = 0x12; bus.Mem[0x2001] = 0x34; bus.Mem[0x2002] = 0x30; bus.Mem[0x2003] = 0x00;
        bus.Mem[0x3000] = 0xAB; bus.Mem[0x3001] = 0xCD;
        bus.Mem[0xFFFE] = 0x10;
        var cpu = new Cpu6809(bus);
        cpu.Reset();
        while (cpu.PC != 0x1008) cpu.Step();
        Assert.Equal(0x1234, cpu.D);
        Assert.Equal(0x2002, cpu.X);
        Assert.Equal(0xABCD, cpu.Y);
    }

    [Fact]
    public void IrqStacksEntireStateAndRtiRestores()
    {
        var bus = new FlatBus();
        // main: ANDCC #$EF; LDA #$11; BRA *    irq at $2000: LDA #$22; RTI
        new byte[] { 0x1C, 0xEF, 0x86, 0x11, 0x20, 0xFE }.CopyTo(bus.Mem, 0x1000);
        new byte[] { 0x86, 0x22, 0x3B }.CopyTo(bus.Mem, 0x2000);
        bus.Mem[0xFFFE] = 0x10;
        bus.Mem[0xFFF8] = 0x20;
        var cpu = new Cpu6809(bus);
        cpu.Reset();
        cpu.S = 0x8000;
        for (int i = 0; i < 3; i++) cpu.Step();
        cpu.IrqLine = true;
        long before = cpu.Cycles;
        cpu.Step();
        Assert.Equal(19, cpu.Cycles - before);
        Assert.Equal(0x2000, cpu.PC);
        cpu.IrqLine = false;
        cpu.Step(); // LDA #$22
        cpu.Step(); // RTI
        Assert.Equal(0x11, cpu.A);
        Assert.Equal(0x1004, cpu.PC);
        Assert.Equal(0x8000, cpu.S);
    }
}
