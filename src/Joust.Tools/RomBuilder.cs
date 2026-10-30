using Joust.Assembler;

namespace Joust.Tools;

/// <summary>Assembles the main-board program and splits it into the 12 ROM images.</summary>
internal static class RomBuilder
{
    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("build-roms <make.asm> <outdir> [-D SYMBOL ...]");
            return 1;
        }
        var options = new AssemblerOptions();
        for (int i = 2; i < args.Length; i++)
            if (args[i] == "-D" && i + 1 < args.Length)
                options.Defines[args[++i]] = 1;

        var result = new Assembler.Assembler(options).AssembleFile(args[0]);
        foreach (var w in result.Warnings.Take(20))
            Console.WriteLine($"warning: {w}");
        foreach (var e in result.Errors.Take(50))
            Console.WriteLine($"error: {e}");
        Console.WriteLine($"{result.Passes} passes, {result.Symbols.Count} symbols, {result.Errors.Count} errors, {result.Warnings.Count} warnings");
        if (!result.Success)
            return 2;

        string outDir = args[1];
        Directory.CreateDirectory(outDir);
        foreach (var (start, name) in JoustRomBuild.Roms)
            File.WriteAllBytes(Path.Combine(outDir, name), result.Slice(start, 0x1000));
        File.WriteAllBytes(Path.Combine(outDir, "joust_main_64k.bin"), result.Memory);
        File.WriteAllLines(Path.Combine(outDir, "joust.sym"),
            result.Symbols.OrderBy(s => s.Value).Select(s => $"{s.Value:X4} {s.Key}"));

        bool allOk = true;
        foreach (var c in JoustRomBuild.VerifyChecksums(result))
        {
            allOk &= c.Ok;
            Console.WriteLine($"  ROM ${c.Base:X4}  sum ${c.Sum:X2}  expected ${c.Expected:X2}  {(c.Ok ? "OK" : "MISMATCH")}");
        }
        Console.WriteLine(allOk ? "All ROM checksums match the original ROM test table." : "Checksum mismatch.");
        return allOk ? 0 : 3;
    }
}
