using Joust.Emulation;

namespace Joust.Port.Translated;

/// <summary>One routine of the translated sound program and every address it can be entered at.</summary>
internal sealed record SoundUnit(string Name, Func<SoundBoard, ushort, ushort> Run, ushort[] At);

/// <summary>
/// The sound board's program (the 6808's 4K ROM) translated to C# by <c>joust-tools translate</c>
/// (Sound.g.cs). Each translated instruction checks <see cref="SoundBoard.Due"/> and charges its
/// cycles through <see cref="SoundBoard.Spend"/> before it runs, exactly as the emulated core is
/// stepped, so the samples come out bit for bit the same.
/// </summary>
public static class SoundTranslation
{
    private static readonly Func<SoundBoard, ushort, ushort>?[] Code = Table();

    private static Func<SoundBoard, ushort, ushort>?[] Table()
    {
        var table = new Func<SoundBoard, ushort, ushort>?[0x10000];
        foreach (var unit in SoundCode.Units)
            foreach (ushort a in unit.At)
                table[a] = unit.Run;
        return table;
    }

    /// <summary>Runs <paramref name="board"/> on the translated program instead of the emulated 6808. False if its ROM is not the one translated.</summary>
    public static bool Attach(SoundBoard board, byte[] soundImage)
    {
        if (ImageHash(soundImage) != SoundCode.ImageHash)
            return false;
        board.Runner = Run;
        return true;
    }

    /// <summary>Instructions in the translation.</summary>
    public static int Instructions => SoundCode.Units.Sum(u => u.At.Length);

    /// <summary>FNV-1a over the sound ROM ($F000-$FFFF).</summary>
    public static ulong ImageHash(byte[] image)
    {
        ulong h = 0xCBF29CE484222325;
        for (int a = 0xF000; a < 0x10000; a++)
            h = (h ^ image[a]) * 0x100000001B3;
        return h;
    }

    private static void Run(SoundBoard b)
    {
        var cpu = b.Cpu;
        if ((b.Pia.IrqA || b.Pia.IrqB) && (cpu.CC & Alu6800.I) == 0)
            b.EnterInterrupt();
        else if (cpu.Waiting)
        {
            // In WAI nothing changes until the next command interrupts, which arrives between
            // time slices: wait out this one, a cycle at a time as the 6808 does.
            while (!b.Due) b.Spend(1);
        }
        else if (Code[cpu.PC] is { } code)
            cpu.PC = code(b, cpu.PC);
        else if (cpu.PC < 0x0080 && b.Read(cpu.PC) == 0x7E)
        {
            // One sound (command $18) stores "JMP address" in RAM and jumps through it, a
            // vector it sets up as it runs. Follow it as the 6808 does: 3 cycles, PC only.
            ushort target = b.Read16(cpu.PC + 1);
            b.Spend(3);
            cpu.PC = target;
        }
        else
            throw new UntranslatedCodeException(cpu.PC);
    }
}

/// <summary>The 6808's stack and 16-bit accesses, for the translated sound program.</summary>
public static class SoundBus
{
    public static ushort Read16(this SoundBoard b, int a) => (ushort)((b.Read((ushort)a) << 8) | b.Read((ushort)(a + 1)));

    public static void Write16(this SoundBoard b, int a, ushort v)
    {
        b.Write((ushort)a, (byte)(v >> 8));
        b.Write((ushort)(a + 1), (byte)v);
    }

    public static void Push(this SoundBoard b, byte v) { b.Write(b.Cpu.SP, v); b.Cpu.SP--; }
    public static byte Pull(this SoundBoard b) { b.Cpu.SP++; return b.Read(b.Cpu.SP); }
    public static void Push16(this SoundBoard b, ushort v) { b.Push((byte)v); b.Push((byte)(v >> 8)); }
    public static ushort Pull16(this SoundBoard b) { int hi = b.Pull(); return (ushort)((hi << 8) | b.Pull()); }

    /// <summary>SWI and WAI: PC (the next instruction), X, A, B and CC onto the stack.</summary>
    public static void PushAll(this SoundBoard b, ushort pc)
    {
        var c = b.Cpu;
        b.Push16(pc);
        b.Push16(c.X);
        b.Push(c.A);
        b.Push(c.B);
        b.Push(c.CC);
    }
}
