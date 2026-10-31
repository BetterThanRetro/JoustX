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
    public void ObjectsDoNotFlickerOnTheScannedPicture()
    {
        var program = Joust.Assembler.JoustRomBuild.Assemble(Joust.Assembler.JoustRomBuild.FindSourceDirectory(AppContext.BaseDirectory)!);
        var factory = new Joust.Emulation.JoustMachine(program.Memory);
        factory.Reset();
        for (int f = 0; f < 1300; f++) factory.RunFrame();
        var m = new Joust.Emulation.JoustMachine(program.Memory);
        factory.Cmos.CopyTo(m.Cmos, 0);
        m.Reset();
        var reader = new Joust.Emulation.GameStateReader(m, Joust.Emulation.JoustLayout.FromSymbols(program.Symbols));
        var pixels = new byte[Joust.Emulation.JoustMachine.ScreenWidth * Joust.Emulation.JoustMachine.ScreenHeight];
        int seen = 0, missing = 0;
        for (int f = 0; f < 3000; f++)
        {
            m.Inputs.CoinLeft = f is >= 900 and < 904;
            m.Inputs.Start1 = f is >= 960 and < 964;
            m.Inputs.P1Flap = f > 1100 && f % 12 < 3;
            m.RunFrame();
            if (f < 1200) continue;
            m.RenderIndices(pixels);
            foreach (var o in reader.ReadObjects())
            {
                if (o.Kind is not (Joust.Emulation.ObjectKind.Player or Joust.Emulation.ObjectKind.Enemy)) continue;
                if (o.X < 4 || o.X > 280 || o.Y < 20 || o.Y > 220) continue;
                seen++;
                bool drawn = false;
                for (int y = o.Y - 20; y < o.Y && !drawn; y++)          // Y is the feet
                    for (int x = o.X; x < o.X + 16 && !drawn; x++)
                        drawn = pixels[y * Joust.Emulation.JoustMachine.ScreenWidth + x] != 0;
                if (!drawn) missing++;
            }
        }
        Assert.True(seen > 1000, $"only {seen} object sightings");
        // Reading video RAM once at the end of the frame missed 23%; scanned line by line, about 0.6%.
        Assert.True(missing * 50 < seen, $"{missing} of {seen} object sightings had nothing on screen");
    }

    [Fact]
    public void WidescreenDrawsEveryColumn()
    {
        var r = new HdRenderer();
        Array.Fill(r.Output, 0xDEADBEEFu);                       // stale pixels from an earlier frame
        r.Render(new byte[HdRenderer.SourceWidth * HdRenderer.SourceHeight], new uint[16], 0, new HdOptions());
        for (int y = 0; y < HdRenderer.Height; y += 97)
            for (int x = 0; x < HdRenderer.WideWidth; x++)
                Assert.True(r.Output[y * HdRenderer.WideWidth + x] != 0xDEADBEEFu, $"column {x} (row {y}) was not drawn");
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
