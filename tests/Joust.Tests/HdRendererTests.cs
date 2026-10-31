using Joust.Render;

namespace Joust.Tests;

public class HdRendererTests
{
    [Fact]
    public void Scale2xOnlyUsesOriginalIndicesAndRoundsDiagonals()
    {
        // A one-pixel diagonal staircase of index 5 on 0.
        const int n = 6;
        var src = new byte[n * n];
        for (int i = 0; i < n; i++) src[i * n + i] = 5;
        var dst = new byte[4 * n * n];
        PixelScaler.Scale2x(src, n, n, dst);
        Assert.All(dst, v => Assert.Contains(v, new byte[] { 0, 5 }));
        // Inside the staircase each step gains a filled corner, so it has more than 2 pixels per step.
        Assert.True(dst.Count(v => v == 5) > 4 * n, "diagonal was not smoothed");
    }

    [Fact]
    public void ReverbStaysBoundedAndDecays()
    {
        var reverb = new CavernReverb(48000);
        var input = new float[48000 * 3];
        for (int i = 0; i < 4800; i++) input[i] = (i / 40 % 2 == 0) ? 0.8f : -0.8f;   // 0.1 s square burst
        var output = new float[input.Length * 2];
        reverb.Process(input, output);
        Assert.All(output, v => Assert.InRange(v, -2f, 2f));
        float tail = output.Skip(output.Length - 9600).Max(Math.Abs);
        Assert.True(tail < 0.01f, $"reverb still at {tail} after 3 s");
    }

    [Fact]
    public void FlatAreasStayFlat()
    {
        var src = Enumerable.Repeat((byte)7, 16).ToArray();
        var scratch = new byte[64];
        var dst = new byte[256];
        PixelScaler.Scale4x(src, 4, 4, scratch, dst);
        Assert.All(dst, v => Assert.Equal(7, v));
    }

    [Fact]
    public void DrawnPixelsKeepTheirColourWithEffectsOff()
    {
        var indices = new byte[HdRenderer.SourceWidth * HdRenderer.SourceHeight];
        var palette = new uint[16];
        palette[3] = 0xFF4080C0;
        for (int y = 100; y < 140; y++)
            for (int x = 100; x < 160; x++)
                indices[y * HdRenderer.SourceWidth + x] = 3;
        var r = new HdRenderer();
        r.Render(indices, palette, 0, new HdOptions { Bloom = 0, Heat = 0, Bevel = 0, Backdrop = false, Shimmer = false, Widescreen = false });
        int hx = (130 - HdRenderer.VisibleX) * HdRenderer.Scale, hy = (120 - HdRenderer.VisibleY) * HdRenderer.Scale;
        Assert.Equal(0xFF4080C0u, r.Output[hy * HdRenderer.Width + hx]);
        Assert.Equal(0xFF000000u, r.Output[0]);
    }
}
