using Joust.Assembler;
using Joust.Tools;

if (args.Length == 0)
{
    Console.WriteLine("usage:");
    Console.WriteLine("  joust-tools build-roms <make.asm> <outdir> [-D SYMBOL ...]");
    Console.WriteLine("  joust-tools run <joust_main_64k.bin> <frames> <outdir> [--shots f1,f2] [--script \"f:action ...\"] [--cmos file]");
    Console.WriteLine("  joust-tools verify-native [--frames N] [--seeds 1,2,3]");
    Console.WriteLine("  joust-tools census [--frames N] [--out file]");
    Console.WriteLine("  joust-tools translate [--out dir]");
    Console.WriteLine("  joust-tools bench [--frames N]");
    Console.WriteLine("  joust-tools build-image [file]");
    Console.WriteLine("  joust-tools hd-shots <outdir> [--frames f1,f2] [--crt]");
    Console.WriteLine("  joust-tools sound-dump <outdir> [--seconds N]");
    return 1;
}

switch (args[0].ToLowerInvariant())
{
    case "build-roms":
        return RomBuilder.Run(args[1..]);
    case "run":
        return HeadlessRunner.Run(args[1..]);
    case "sound-dump":
        return SoundDump.Run(args[1..]);
    case "hd-shots":
        return HdShots.Run(args[1..]);
    case "verify-native":
        return NativeVerifyCommand.Run(args[1..]);
    case "make-icon":
        return MakeIcon.Run(args[1..]);
    case "census":
        return CensusCommand.Run(args[1..]);
    case "translate":
        return TranslateCommand.Run(args[1..]);
    case "bench":
        return BenchCommand.Run(args[1..]);
    case "build-image":
    {
        // The assembled game as one file (Joust.Host.GameImage), for the browser build.
        string path = args.Length > 1 ? args[1] : "build/joust.img";
        var image = Joust.Host.GameImage.FromSourceNear(Environment.CurrentDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using (var file = File.Create(path)) image.Save(file);
        Console.WriteLine($"{path}: {new FileInfo(path).Length} bytes");
        return 0;
    }
    default:
        Console.Error.WriteLine($"unknown command {args[0]}");
        return 1;
}
