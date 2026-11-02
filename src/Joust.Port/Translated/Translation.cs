using Joust.Emulation;

namespace Joust.Port.Translated;

/// <summary>One routine of the translated program: its name, its code and every address it can be entered at.</summary>
internal sealed record Unit(string Name, Func<Gen, ushort, ushort> Run, ushort[] At);

/// <summary>
/// The whole original program translated to C# by <c>joust-tools translate</c> (the files in
/// this folder ending .g.cs). Installed under the hand-written routines: those keep their
/// addresses, and the translation runs every other instruction, and also the calls a
/// hand-written routine hands back.
/// </summary>
public static class Translation
{
    /// <summary>
    /// Installs the translated code on <paramref name="machine"/>, where nothing hand-written is
    /// installed already. False (installing nothing) if the machine's ROM is not the program
    /// the translation was made from.
    /// </summary>
    public static bool Install(JoustMachine machine)
    {
        if (ImageHash(machine) != Code.ImageHash)
            return false;
        var handWritten = new bool[0x10000];
        for (int a = 0; a < 0x10000; a++)
            handWritten[a] = machine.NativeAt((ushort)a) != null;
        foreach (var unit in Code.Units)
        {
            var routine = new TranslatedRoutine("gen:" + unit.Name, unit.Run, handWritten);
            foreach (ushort a in unit.At)
            {
                var existing = machine.NativeAt(a);
                machine.InstallNative(existing == null ? routine : new FallbackRoutine(existing, routine), a);
            }
        }
        return true;
    }

    /// <summary>Instructions in the translation (one per address it can be entered at).</summary>
    public static int Instructions => Code.Units.Sum(u => u.At.Length);

    /// <summary>FNV-1a over the program ROM ($0000-$8FFF and $D000-$FFFF).</summary>
    public static ulong ImageHash(JoustMachine machine) => ImageHash(a => machine.ReadRom((ushort)a));

    public static ulong ImageHash(Func<int, byte> rom)
    {
        ulong h = 0xCBF29CE484222325;
        for (int a = 0; a < 0x10000; a++)
        {
            if (a is >= 0x9000 and < 0xD000) continue;
            h = (h ^ rom(a)) * 0x100000001B3;
        }
        return h;
    }
}

/// <summary>Installs one translated routine at each address it can be entered at.</summary>
public sealed class TranslatedRoutine(string symbol, Func<Gen, ushort, ushort> run, bool[] handWritten) : NativeRoutine
{
    public override string Symbol => symbol;
    public override int MaxCycles => 0;                          // each instruction checks the window
    public override bool EndsWithRts => false;
    public override bool Execute(NativeContext c) => Gen.Run(c, handWritten, run);

    /// <summary>Runs exactly one instruction at the PC, across an interrupt edge if need be (see <see cref="Gen.Step"/>).</summary>
    public bool Step(NativeContext c) => Gen.Step(c, run);
}

/// <summary>
/// A hand-written routine with the translation behind it: when the hand-written one hands a call
/// back, the translated code runs it instead of the CPU.
/// </summary>
public sealed class FallbackRoutine(NativeRoutine primary, TranslatedRoutine fallback) : NativeRoutine
{
    private bool _fellBack;

    public NativeRoutine Primary => primary;
    public TranslatedRoutine Fallback => fallback;
    public override string Symbol => primary.Symbol;
    public override int MaxCycles => 0;

    /// <summary>As the routine that ran the last call (the verifier asks after the call).</summary>
    public override bool EndsWithRts => !_fellBack && primary.EndsWithRts;

    public override bool Execute(NativeContext c)
    {
        _fellBack = false;
        if (c.InterruptSafe(primary.MaxCycles) && primary.Execute(c))
            return true;
        _fellBack = true;
        return fallback.Execute(c);
    }
}
