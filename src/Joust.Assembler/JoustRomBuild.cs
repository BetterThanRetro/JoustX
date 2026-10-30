namespace Joust.Assembler;

public readonly record struct RomChecksum(int Base, int Sum, int Expected)
{
    public bool Ok => Sum == Expected;
}

/// <summary>Builds the Joust main-board program from the original source and verifies it.</summary>
public static class JoustRomBuild
{
    public const string SourceFolderName = "joust-main-asm";
    public const string MainFile = "make.ASM";

    /// <summary>The 12 ROM chips of the main board, as named in MAME's joust set.</summary>
    public static readonly (int Start, string Name)[] Roms =
    [
        (0x0000, "joust_rom_1b_3006-13.e4"),
        (0x1000, "joust_rom_2b_3006-14.c4"),
        (0x2000, "joust_rom_3b_3006-15.a4"),
        (0x3000, "joust_rom_4b_3006-16.e5"),
        (0x4000, "joust_rom_5b_3006-17.c5"),
        (0x5000, "joust_rom_6b_3006-18.a5"),
        (0x6000, "joust_rom_7b_3006-19.e6"),
        (0x7000, "joust_rom_8b_3006-20.c6"),
        (0x8000, "joust_rom_9b_3006-21.a6"),
        (0xD000, "joust_rom_10b_3006-22.a7"),
        (0xE000, "joust_rom_11b_3006-23.c7"),
        (0xF000, "joust_rom_12b_3006-24.e7"),
    ];

    /// <summary>Walks up from <paramref name="startDir"/> looking for the assembly source folder.</summary>
    public static string? FindSourceDirectory(string startDir)
    {
        for (var dir = new DirectoryInfo(startDir); dir != null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, SourceFolderName);
            if (File.Exists(Path.Combine(candidate, MainFile)))
                return candidate;
        }
        return null;
    }

    public static AssemblyResult Assemble(string sourceDir, params string[] defines)
    {
        var options = new AssemblerOptions();
        foreach (var d in defines)
            options.Defines[d] = 1;
        return new Assembler(options).AssembleFile(Path.Combine(sourceDir, MainFile));
    }

    /// <summary>The sound board's program (6800 code), its own 4K ROM at $F000.</summary>
    public const string SoundFile = "VSNDRM4.ASM";
    public const int SoundRomBase = 0xF000;

    public static AssemblyResult AssembleSound(string sourceDir) =>
        new Assembler(new AssemblerOptions()).AssembleFile(Path.Combine(sourceDir, SoundFile));

    /// <summary>
    /// The sound ROM's checksum as the source's CKSUM tool made it: an add-with-carry of
    /// $FFFF down to $F001, stored at $F000. Returns (computed, stored).
    /// </summary>
    public static (int Sum, int Stored) SoundChecksum(AssemblyResult result)
    {
        int sum = 0, carry = 0;
        for (int a = 0xFFFF; a > SoundRomBase; a--)
        {
            int s = sum + result.Memory[a] + carry;
            carry = s >> 8;
            sum = s & 0xFF;
        }
        return (sum, result.Memory[SoundRomBase]);
    }

    /// <summary>
    /// Sums each 4K ROM the way the game's own power-up ROM test does and compares against
    /// the expected values stored in its ROMTAB table.
    /// </summary>
    public static IReadOnlyList<RomChecksum> VerifyChecksums(AssemblyResult result)
    {
        if (!result.Symbols.TryGetValue("ROMTAB", out long romtab) || !result.Symbols.TryGetValue("ROMEND", out long romend))
            throw new InvalidOperationException("ROMTAB/ROMEND not found in the assembled program");
        var list = new List<RomChecksum>();
        for (int entry = (int)romtab; entry < romend; entry += 2)
        {
            int baseHi = result.Memory[entry];
            int expected = result.Memory[entry + 1];
            if (expected == 0)
                continue;
            int sum = 0;
            for (int a = baseHi << 8; a < (baseHi << 8) + 0x1000; a++)
                sum = (sum + result.Memory[a]) & 0xFF;
            list.Add(new RomChecksum(baseHi << 8, sum, expected));
        }
        return list;
    }
}
