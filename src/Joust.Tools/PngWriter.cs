using System.IO.Compression;

namespace Joust.Tools;

/// <summary>Minimal truecolor PNG encoder for screenshots (no external dependencies).</summary>
internal static class PngWriter
{
    public static void Write(string path, ReadOnlySpan<uint> argb, int width, int height, int scale = 1)
    {
        int w = width * scale, h = height * scale;
        var raw = new byte[h * (w * 3 + 1)];
        int p = 0;
        for (int y = 0; y < h; y++)
        {
            raw[p++] = 0; // filter: none
            int sy = y / scale;
            for (int x = 0; x < w; x++)
            {
                uint c = argb[sy * width + x / scale];
                raw[p++] = (byte)(c >> 16);
                raw[p++] = (byte)(c >> 8);
                raw[p++] = (byte)c;
            }
        }

        using var fs = File.Create(path);
        fs.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        WriteBE(ihdr, 0, (uint)w);
        WriteBE(ihdr, 4, (uint)h);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 2;  // truecolor
        Chunk(fs, "IHDR", ihdr);
        using (var ms = new MemoryStream())
        {
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                z.Write(raw);
            Chunk(fs, "IDAT", ms.ToArray());
        }
        Chunk(fs, "IEND", []);
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        WriteBE(len, 0, (uint)data.Length);
        s.Write(len);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        uint crc = Crc32(typeBytes, 0xFFFFFFFF);
        crc = Crc32(data, crc) ^ 0xFFFFFFFF;
        var c = new byte[4];
        WriteBE(c, 0, crc);
        s.Write(c);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    public static uint Crc32(ReadOnlySpan<byte> data, uint crc)
    {
        foreach (byte b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static void WriteBE(byte[] b, int o, uint v)
    {
        b[o] = (byte)(v >> 24);
        b[o + 1] = (byte)(v >> 16);
        b[o + 2] = (byte)(v >> 8);
        b[o + 3] = (byte)v;
    }
}
