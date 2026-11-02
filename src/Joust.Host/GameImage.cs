using System.Text;
using Joust.Assembler;

namespace Joust.Host;

/// <summary>
/// The assembled game: the main board's 64K image, the sound board's image and the symbol table.
/// Made from the original source (<see cref="FromSource"/>), or loaded from a file saved earlier,
/// for a front end that cannot assemble at start-up, such as one in a browser.
/// </summary>
public sealed class GameImage
{
    private const string Magic = "JOUSTX-IMAGE-1";

    private GameImage(byte[] main, byte[] sound, IReadOnlyDictionary<string, long> symbols)
    {
        Main = main;
        Sound = sound;
        Symbols = symbols;
    }

    public byte[] Main { get; }
    public byte[] Sound { get; }
    public IReadOnlyDictionary<string, long> Symbols { get; }

    /// <summary>
    /// Assembles the main and sound programs from <paramref name="sourceDir"/> and checks them
    /// against the ROMs' own checksums.
    /// </summary>
    public static GameImage FromSource(string sourceDir)
    {
        var program = JoustRomBuild.Assemble(sourceDir);
        if (!program.Success)
            throw new InvalidOperationException("Assembly failed: " + string.Join("; ", program.Errors.Take(5)));
        if (JoustRomBuild.VerifyChecksums(program).Any(c => !c.Ok))
            throw new InvalidOperationException("Assembled ROMs do not match the original checksums.");
        var sound = JoustRomBuild.AssembleSound(sourceDir);
        if (!sound.Success || JoustRomBuild.SoundChecksum(sound) is var (sum, stored) && sum != stored)
            throw new InvalidOperationException("The sound ROM did not assemble to its checksum.");
        return new GameImage(program.Memory, sound.Memory, program.Symbols);
    }

    /// <summary>Finds the source folder above <paramref name="startDirs"/> (in order) and assembles it.</summary>
    public static GameImage FromSourceNear(params string[] startDirs)
    {
        foreach (string dir in startDirs)
            if (JoustRomBuild.FindSourceDirectory(dir) is { } source)
                return FromSource(source);
        throw new DirectoryNotFoundException($"Could not find {JoustRomBuild.SourceFolderName}/{JoustRomBuild.MainFile}");
    }

    public void Save(Stream stream)
    {
        using var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        w.Write(Magic);
        w.Write(Main.Length);
        w.Write(Main);
        w.Write(Sound.Length);
        w.Write(Sound);
        w.Write(Symbols.Count);
        foreach (var (name, value) in Symbols.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            w.Write(name);
            w.Write(value);
        }
    }

    public static GameImage Load(Stream stream)
    {
        using var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (r.ReadString() != Magic)
            throw new InvalidDataException("not a JoustX game image");
        byte[] main = r.ReadBytes(r.ReadInt32());
        byte[] sound = r.ReadBytes(r.ReadInt32());
        int count = r.ReadInt32();
        var symbols = new Dictionary<string, long>(count, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < count; i++)
            symbols[r.ReadString()] = r.ReadInt64();
        return new GameImage(main, sound, symbols);
    }
}
