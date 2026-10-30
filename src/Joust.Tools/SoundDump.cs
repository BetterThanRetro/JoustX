using Joust.Assembler;
using Joust.Emulation;

namespace Joust.Tools;

/// <summary>
/// sound-dump &lt;outdir&gt; [--seconds N]: assemble the sound ROM and write each command's sound
/// (00-3E) as a 48 kHz mono WAV, from a freshly reset board, so it can be listened to alone.
/// </summary>
internal static class SoundDump
{
    public static int Run(string[] args)
    {
        string outDir = args.Length > 0 ? args[0] : "build/sounds";
        double seconds = 2.5;
        for (int i = 1; i < args.Length; i++)
            if (args[i] == "--seconds") seconds = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
        Directory.CreateDirectory(outDir);

        string source = JoustRomBuild.FindSourceDirectory(Environment.CurrentDirectory)
            ?? throw new DirectoryNotFoundException(JoustRomBuild.SourceFolderName);
        var rom = JoustRomBuild.AssembleSound(source);
        const int rate = 48000;
        var samples = new float[(int)(rate * (seconds + 1))];
        for (byte cmd = 0; cmd < 0x3F; cmd++)
        {
            var board = new SoundBoard(rom.Memory);
            board.Reset();
            board.Run(20000, rate, samples);                     // boot to the idle loop
            board.Command(cmd);
            int n = board.Run(200, rate, samples);
            board.Command(0x3F);
            n += board.Run((long)(SoundBoard.ClockHz * seconds), rate, samples.AsSpan(n));
            // Trim the trailing silence (the DAC parks at a constant level).
            int end = n;
            while (end > 1 && Math.Abs(samples[end - 1] - samples[end - 2]) < 1e-6f) end--;
            if (end < rate / 100) continue;                      // silent command
            string path = Path.Combine(outDir, $"sound_{cmd:X2}.wav");
            WriteWav(path, samples.AsSpan(0, Math.Min(n, end + rate / 20)), rate);
            Console.WriteLine($"{path}  {end / (double)rate:F2} s");
        }
        return 0;
    }

    private static void WriteWav(string path, ReadOnlySpan<float> samples, int rate)
    {
        using var w = new BinaryWriter(File.Create(path));
        int bytes = samples.Length * 2;
        w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(bytes);
        // Remove the DAC's DC offset so the files start and end at zero.
        float mean = 0;
        foreach (float s in samples) mean += s;
        mean /= Math.Max(1, samples.Length);
        foreach (float s in samples)
            w.Write((short)Math.Clamp((s - mean) * 0.9f * short.MaxValue, short.MinValue, short.MaxValue));
    }
}
