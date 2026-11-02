using Joust.Host;
using Joust.Render;
using Microsoft.Xna.Framework.Graphics;

namespace Joust.App;

/// <summary>
/// --compare-render &lt;dir&gt;: plays into a game and, at a few frames, draws the HD picture both
/// ways from the same prepared frame: the CPU compose (the reference) and the GPU's. Writes both
/// and an amplified difference as PNGs, and prints how far apart they are per pixel.
/// </summary>
internal static class RenderCompare
{
    public static void Run(JoustSession session, GpuRenderer gpu, GraphicsDevice gd, string dir)
    {
        Directory.CreateDirectory(dir);
        int[] shots = [200, 1500, 2200, 3000];
        var report = new List<string>();
        foreach (var options in new[] { new HdOptions { Abgr = true }, new HdOptions { Abgr = true, Crt = true, Widescreen = false } })
        {
            for (int f = 0, shot = 0; shot < shots.Length; f++)
            {
                var i = session.Inputs;
                i.CoinLeft = f is >= 900 and < 904;
                i.Start1 = f is >= 960 and < 964;
                i.P1Flap = f > 1000 && f % 7 == 0;
                i.P1Right = f > 1000 && (f / 90) % 3 == 0;
                i.P1Left = f > 1000 && (f / 90) % 3 == 1;
                session.RunFrame();
                var prepared = session.PrepareHd(options, forCpu: true);
                if (f != shots[shot]) continue;
                shot++;

                var target = gpu.Render(prepared, options);
                var gpuPixels = new uint[target.Width * target.Height];
                target.GetData(gpuPixels);
                prepared.Compose(options);
                var cpuPixels = prepared.Output.AsSpan(0, gpuPixels.Length).ToArray();

                long sum = 0;
                int max = 0, off = 0;
                var diff = new uint[gpuPixels.Length];
                for (int p = 0; p < gpuPixels.Length; p++)
                {
                    int worst = 0;
                    for (int shift = 0; shift < 24; shift += 8)
                    {
                        int d = Math.Abs((int)((gpuPixels[p] >> shift) & 0xFF) - (int)((cpuPixels[p] >> shift) & 0xFF));
                        sum += d;
                        worst = Math.Max(worst, d);
                    }
                    max = Math.Max(max, worst);
                    if (worst > 8) off++;
                    uint v = (uint)Math.Min(255, worst * 16);
                    diff[p] = 0xFF000000u | v << 16 | v << 8 | v;
                }
                string name = $"{(options.Crt ? "crt" : "wide")}_{f:D4}";
                Save(gd, cpuPixels, target.Width, target.Height, Path.Combine(dir, $"{name}_cpu.png"));
                Save(gd, gpuPixels, target.Width, target.Height, Path.Combine(dir, $"{name}_gpu.png"));
                Save(gd, diff, target.Width, target.Height, Path.Combine(dir, $"{name}_diff.png"));
                report.Add($"{name}: mean difference {sum / (3.0 * gpuPixels.Length):F2} of 255, largest {max}, " +
                           $"{100.0 * off / gpuPixels.Length:F2}% of pixels differ by more than 8");
            }
        }
        File.WriteAllLines(Path.Combine(dir, "report.txt"), report);
    }

    private static void Save(GraphicsDevice gd, uint[] pixels, int w, int h, string path)
    {
        using var t = new Texture2D(gd, w, h);
        t.SetData(pixels);
        using var file = File.Create(path);
        t.SaveAsPng(file, w, h);
    }
}
