using Joust.Emulation;
using Joust.Port.Translated;

namespace Joust.Port;

/// <summary>
/// Runs the game with no 6809 emulation. The hand-written routines and the translated program
/// run every call as before. Where neither may run as a whole, because an interrupt edge falls
/// inside or an instruction reads an I/O register, this runs that one translated instruction by
/// itself. It also enters interrupts in C#, at the instruction boundary where the 6809 enters
/// them, so the result is identical to the original.
/// The <see cref="Cpu6809"/> object stays only as the register file and the cycle clock: its
/// instruction stepping is never called (<see cref="JoustMachine.CpuSteps"/> stays 0).
/// </summary>
public sealed class NoCpuRuntime
{
    private readonly JoustMachine _m;
    private readonly NativeContext _c;

    private NoCpuRuntime(JoustMachine m)
    {
        _m = m;
        _c = m.NativeContext;
    }

    /// <summary>Single-stepped instructions so far.</summary>
    public long Steps { get; private set; }

    /// <summary>Interrupts entered so far.</summary>
    public long Interrupts { get; private set; }

    /// <summary>Takes the 6809's place on <paramref name="machine"/> (natives and translation must be installed).</summary>
    public static NoCpuRuntime Attach(JoustMachine machine)
    {
        var runtime = new NoCpuRuntime(machine);
        machine.StepOverride = runtime.Step;
        return runtime;
    }

    private int Step()
    {
        var cpu = _m.Cpu;
        long before = cpu.Cycles;
        if (cpu.FirqLine && (cpu.CC & Cpu6809.FlagF) == 0)
            Interrupt(entire: false, 0xFFF6, Cpu6809.FlagI | Cpu6809.FlagF, 10);
        else if (cpu.IrqLine && (cpu.CC & Cpu6809.FlagI) == 0)
            Interrupt(entire: true, 0xFFF8, Cpu6809.FlagI, 19);
        else
        {
            var native = _m.NativeAt(cpu.PC);
            var translated = native as TranslatedRoutine ?? (native as FallbackRoutine)?.Fallback;
            if (translated == null || !translated.Step(_c))
                throw new UntranslatedCodeException(cpu.PC);
            Steps++;
        }
        return (int)(cpu.Cycles - before);
    }

    /// <summary>Interrupt entry as the 6809 does it: stack the registers, mask, and fetch the vector.</summary>
    private void Interrupt(bool entire, ushort vector, int mask, int cycles)
    {
        var cpu = _m.Cpu;
        if (entire)
        {
            cpu.CC |= Cpu6809.FlagE;
            Push16(cpu.PC);
            Push16(cpu.U);
            Push16(cpu.Y);
            Push16(cpu.X);
            Push(cpu.DP);
            Push(cpu.B);
            Push(cpu.A);
            Push(cpu.CC);
        }
        else
        {
            cpu.CC = (byte)(cpu.CC & ~Cpu6809.FlagE);
            Push16(cpu.PC);
            Push(cpu.CC);
        }
        cpu.CC |= (byte)mask;
        cpu.PC = _c.Read16(vector);
        cpu.Cycles += cycles;
        Interrupts++;
    }

    private void Push(byte v)
    {
        _m.Cpu.S--;
        _c.Write(_m.Cpu.S, v);
    }

    private void Push16(ushort v)
    {
        Push((byte)v);
        Push((byte)(v >> 8));
    }
}

/// <summary>The program reached an address the C# code does not cover.</summary>
public sealed class UntranslatedCodeException(ushort pc) : Exception($"no C# code for the instruction at ${pc:X4}")
{
    public ushort Address { get; } = pc;
}
