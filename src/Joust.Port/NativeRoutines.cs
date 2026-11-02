using Joust.Emulation;
using Joust.Port.Routines;

namespace Joust.Port;

/// <summary>The routines ported to native C# so far.</summary>
public static class NativeRoutines
{
    public static IReadOnlyList<NativeRoutine> Create() =>
    [
        new RandomRoutine(),
        new SoundRoutine(),
        new LoadColorsRoutine(explicitDestination: false),
        new LoadColorsRoutine(explicitDestination: true),
        new AddScoreRoutine(tens: false),
        new AddScoreRoutine(tens: true),
        new TextRoutine("CHROUT", phrase: false, smallFont: false),
        new TextRoutine("CHR35", phrase: false, smallFont: true),
        new TextRoutine("PHROUT", phrase: true, smallFont: false),
        new TextRoutine("PHR35", phrase: true, smallFont: true),
        .. TxRoutine.All(),
        .. TxJumpRoutine.All(),
    ];

    /// <summary>
    /// Binds every native routine to the assembled program and installs it at its entry address,
    /// with the translated program (<see cref="Translated.Translation"/>) under them unless
    /// <paramref name="translated"/> is false.
    /// </summary>
    public static IReadOnlyList<NativeRoutine> InstallAll(JoustMachine machine, IReadOnlyDictionary<string, long> symbols, bool translated = true)
    {
        var routines = Create();
        foreach (var r in routines)
        {
            r.Bind(symbols);
            machine.InstallNative(r, r.ResolveAddress(symbols));
        }
        if (translated && !Translated.Translation.Install(machine))
            throw new InvalidOperationException("the translated program does not match this ROM; run joust-tools translate");
        return routines;
    }
}
