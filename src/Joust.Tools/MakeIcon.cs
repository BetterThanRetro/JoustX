using Joust.Assembler;
using Joust.Emulation;

namespace Joust.Tools;

/// <summary>
/// make-icon &lt;outdir&gt;: the app icon, cut from the game itself. Boots the game, starts a one-player
/// game, finds player 1's knight on its ostrich once it has materialised, and draws those exact
/// pixels large on a dark cavern with a band of lava. Writes icon-N.png previews, Icon.ico
/// (16-256 px) and Icon.bmp (64 px, the window icon MonoGame's DesktopGL loads).
/// </summary>
internal static class MakeIcon
{
    public static int Run(string[] args)
    {
        string outDir = args.Length > 0 ? args[0] : "build/icon";
        Directory.CreateDirectory(outDir);
        var (sprite, w, h) = CaptureKnight();

        var sizes = new[] { 16, 24, 32, 48, 64, 128, 256 };
        var pngs = new List<byte[]>();
        uint[] full = Compose(sprite, w, h, 256);
        foreach (int size in sizes)
        {
            uint[] img = size == 256 ? full : Shrink(full, 256, size);
            string png = Path.Combine(outDir, $"icon-{size}.png");
            PngWriter.Write(png, img, size, size);
            pngs.Add(File.ReadAllBytes(png));
            if (size == 64) WriteBmp(Path.Combine(outDir, "Icon.bmp"), img, size);
        }
        WriteIco(Path.Combine(outDir, "Icon.ico"), sizes, pngs);
        Console.WriteLine($"knight {w}x{h} px -> {outDir}");
        return 0;
    }

    /// <summary>Player 1's sprite as ARGB (transparent where the frame is black), trimmed to its pixels.</summary>
    private static (uint[] Pixels, int W, int H) CaptureKnight()
    {
        string source = JoustRomBuild.FindSourceDirectory(Environment.CurrentDirectory)
            ?? JoustRomBuild.FindSourceDirectory(AppContext.BaseDirectory)
            ?? throw new DirectoryNotFoundException(JoustRomBuild.SourceFolderName);
        var program = JoustRomBuild.Assemble(source);
        var factory = new JoustMachine(program.Memory);
        factory.Reset();
        for (int f = 0; f < 1300; f++) factory.RunFrame();
        var m = new JoustMachine(program.Memory);
        factory.Cmos.CopyTo(m.Cmos, 0);
        m.Reset();
        var reader = new GameStateReader(m, JoustLayout.FromSymbols(program.Symbols));
        var indices = new byte[JoustMachine.ScreenWidth * JoustMachine.ScreenHeight];

        for (int f = 0; f < 3000; f++)
        {
            m.Inputs.CoinLeft = f is >= 900 and < 904;
            m.Inputs.Start1 = f is >= 960 and < 964;
            m.RunFrame();
            if (f < 1250) continue;                              // let the knight materialise and stand
            var knight = reader.ReadObjects().FirstOrDefault(o => o.Kind == ObjectKind.Player);
            if (knight.Kind != ObjectKind.Player) continue;
            m.RenderIndices(indices);
            // The bird's box: 16 wide, about 20 tall above its feet (Y). Take a margin, then trim.
            int x0 = Math.Max(0, knight.X - 4), x1 = Math.Min(JoustMachine.ScreenWidth, knight.X + 22);
            int y0 = Math.Max(0, knight.Y - 24), y1 = Math.Min(JoustMachine.ScreenHeight, knight.Y + 1);
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                    if (indices[y * JoustMachine.ScreenWidth + x] != 0)
                    {
                        minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                        minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                    }
            if (maxX - minX < 10 || maxY - minY < 14) continue;  // not fully drawn yet
            int w = maxX - minX + 1, h = maxY - minY + 1;
            var px = new uint[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    byte i = indices[(minY + y) * JoustMachine.ScreenWidth + minX + x];
                    px[y * w + x] = i == 0 ? 0 : JoustMachine.PaletteToArgb(m.Palette[i]);
                }
            return (px, w, h);
        }
        throw new InvalidOperationException("player 1 never appeared");
    }

    /// <summary>The knight scaled by a whole number (pixels stay crisp) on a cavern with lava below.</summary>
    private static uint[] Compose(uint[] sprite, int w, int h, int size)
    {
        var img = new uint[size * size];
        int lavaTop = size * 13 / 16;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float v = y / (float)size;
                float glow = MathF.Max(0, (v - 0.45f) / 0.55f);   // warm light rising from the lava
                int r = (int)(18 + 120 * glow * glow), g = (int)(14 + 40 * glow * glow), b = (int)(30 - 12 * glow);
                if (y >= lavaTop) { r = 250; g = 70 + (int)(30 * MathF.Sin(x * 0.9f)); b = 10; }
                img[y * size + x] = 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
            }

        // Ledge for the knight to stand on, just above the lava.
        int scale = Math.Max(1, Math.Min((size * 7 / 8) / w, (lavaTop - size / 16) / h));
        int sw = w * scale, sh = h * scale;
        int ox = (size - sw) / 2, oy = lavaTop - size / 16 - sh;
        int ledgeTop = oy + sh, ledgeBottom = Math.Min(lavaTop, ledgeTop + Math.Max(1, size / 16));
        for (int y = ledgeTop; y < ledgeBottom; y++)
            for (int x = Math.Max(0, ox - scale * 2); x < Math.Min(size, ox + sw + scale * 2); x++)
                img[y * size + x] = 0xFFB4641Eu;                 // the cliffs' orange rock

        for (int y = 0; y < sh; y++)
            for (int x = 0; x < sw; x++)
            {
                uint c = sprite[(y / scale) * w + x / scale];
                if (c != 0) img[(oy + y) * size + ox + x] = c;
            }
        return img;
    }

    /// <summary>Area-average <paramref name="from"/> pixels square down to <paramref name="to"/>.</summary>
    private static uint[] Shrink(uint[] img, int from, int to)
    {
        var o = new uint[to * to];
        for (int y = 0; y < to; y++)
            for (int x = 0; x < to; x++)
            {
                int sx0 = x * from / to, sx1 = (x + 1) * from / to, sy0 = y * from / to, sy1 = (y + 1) * from / to;
                long r = 0, g = 0, b = 0, n = 0;
                for (int sy = sy0; sy < sy1; sy++)
                    for (int sx = sx0; sx < sx1; sx++)
                    {
                        uint c = img[sy * from + sx];
                        r += (c >> 16) & 0xFF; g += (c >> 8) & 0xFF; b += c & 0xFF; n++;
                    }
                o[y * to + x] = 0xFF000000u | (uint)(r / n) << 16 | (uint)(g / n) << 8 | (uint)(b / n);
            }
        return o;
    }

    /// <summary>A 32-bit bottom-up BMP (opaque), as SDL_LoadBMP reads it.</summary>
    private static void WriteBmp(string path, uint[] argb, int size)
    {
        using var bw = new BinaryWriter(File.Create(path));
        int data = size * size * 4;
        bw.Write((byte)'B'); bw.Write((byte)'M');
        bw.Write(14 + 40 + data); bw.Write(0); bw.Write(14 + 40);
        bw.Write(40); bw.Write(size); bw.Write(size); bw.Write((short)1); bw.Write((short)32);
        bw.Write(0); bw.Write(data); bw.Write(2835); bw.Write(2835); bw.Write(0); bw.Write(0);
        for (int y = size - 1; y >= 0; y--)
            for (int x = 0; x < size; x++)
                bw.Write(argb[y * size + x]);                    // B,G,R,A in memory
    }

    /// <summary>An .ico holding each size as an embedded PNG.</summary>
    private static void WriteIco(string path, int[] sizes, List<byte[]> pngs)
    {
        using var bw = new BinaryWriter(File.Create(path));
        bw.Write((short)0); bw.Write((short)1); bw.Write((short)sizes.Length);
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            bw.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            bw.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            bw.Write((byte)0); bw.Write((byte)0);
            bw.Write((short)1); bw.Write((short)32);
            bw.Write(pngs[i].Length); bw.Write(offset);
            offset += pngs[i].Length;
        }
        foreach (var p in pngs) bw.Write(p);
    }
}
