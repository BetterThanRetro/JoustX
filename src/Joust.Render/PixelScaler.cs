namespace Joust.Render;

/// <summary>
/// Scale2x / Scale4x (EPX) on palette indices: doubles a pixel-art image and rounds its
/// diagonal steps without inventing colours, so every output pixel is still an original index.
/// </summary>
public static class PixelScaler
{
    public static void Scale2x(ReadOnlySpan<byte> src, int width, int height, Span<byte> dst)
    {
        int dw = width * 2;
        for (int y = 0; y < height; y++)
        {
            int up = Math.Max(y - 1, 0), down = Math.Min(y + 1, height - 1);
            for (int x = 0; x < width; x++)
            {
                int left = Math.Max(x - 1, 0), right = Math.Min(x + 1, width - 1);
                byte p = src[y * width + x];
                byte a = src[up * width + x], b = src[y * width + right];
                byte c = src[y * width + left], d = src[down * width + x];
                int o = 2 * y * dw + 2 * x;
                dst[o] = c == a && c != d && a != b ? a : p;
                dst[o + 1] = a == b && a != c && b != d ? b : p;
                dst[o + dw] = d == c && d != b && c != a ? c : p;
                dst[o + dw + 1] = b == d && b != a && d != c ? d : p;
            }
        }
    }

    /// <summary>Scale2x twice. <paramref name="scratch"/> must hold (2w x 2h) bytes.</summary>
    public static void Scale4x(ReadOnlySpan<byte> src, int width, int height, Span<byte> scratch, Span<byte> dst)
    {
        Scale2x(src, width, height, scratch);
        Scale2x(scratch, width * 2, height * 2, dst);
    }
}
