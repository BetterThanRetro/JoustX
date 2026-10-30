namespace Joust.Render;

/// <summary>
/// A procedural cavern wall drawn behind the play field: layered rock from fractal noise,
/// stalactites along the top, and a warm band near the lava that the renderer flickers.
/// Kept dark so the game itself stays the brightest thing on screen.
/// </summary>
public sealed class Backdrop
{
    public Backdrop(int width, int height, int seed = 1982)
    {
        Width = width;
        Height = height;
        Base = new float[width * height * 3];
        LavaLight = new float[height];
        var noise = new ValueNoise(seed);
        for (int y = 0; y < height; y++)
        {
            float v = y / (float)height;
            // Cool and dark at the top, warmer towards the lava.
            float r = Lerp(0.030f, 0.075f, v), g = Lerp(0.030f, 0.040f, v), b = Lerp(0.065f, 0.030f, v);
            LavaLight[y] = MathF.Pow(Math.Clamp((v - 0.55f) / 0.45f, 0, 1), 2.2f);
            for (int x = 0; x < width; x++)
            {
                float nx = x / (float)width * 9f, ny = y / (float)height * 7.5f;
                float rock = noise.Fbm(nx, ny, 5);
                float strata = 0.5f + 0.5f * MathF.Sin(ny * 5.5f + noise.Fbm(nx * 0.6f, ny * 0.6f, 3) * 6f);
                float shade = 0.55f + 0.75f * rock + 0.25f * strata;
                // Stalactites: a ragged ceiling hanging down a varying length.
                float hang = 0.05f + 0.16f * MathF.Pow(noise.Fbm(x / (float)width * 28f, 0.5f, 3), 2.5f) * 2.4f;
                if (v < hang) shade *= 0.35f + 0.4f * (v / hang);
                int o = (y * width + x) * 3;
                Base[o] = r * shade;
                Base[o + 1] = g * shade;
                Base[o + 2] = b * shade;
            }
        }
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>Linear RGB (0-1) per pixel, three floats each.</summary>
    public float[] Base { get; }

    /// <summary>Per row, how much the lava's light reaches it (0 at the top, 1 at the bottom).</summary>
    public float[] LavaLight { get; }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}

/// <summary>Smooth 2D value noise with fractal sums; deterministic for a seed.</summary>
public sealed class ValueNoise
{
    private readonly float[] _lattice = new float[256 * 256];

    public ValueNoise(int seed)
    {
        var rng = new Random(seed);
        for (int i = 0; i < _lattice.Length; i++) _lattice[i] = (float)rng.NextDouble();
    }

    public float At(float x, float y)
    {
        int xi = (int)MathF.Floor(x), yi = (int)MathF.Floor(y);
        float fx = x - xi, fy = y - yi;
        fx = fx * fx * (3 - 2 * fx);
        fy = fy * fy * (3 - 2 * fy);
        float a = L(xi, yi), b = L(xi + 1, yi), c = L(xi, yi + 1), d = L(xi + 1, yi + 1);
        return a + (b - a) * fx + (c - a) * fy + (a - b - c + d) * fx * fy;
    }

    /// <summary>Fractal sum of <paramref name="octaves"/> octaves, normalised to 0-1.</summary>
    public float Fbm(float x, float y, int octaves)
    {
        float sum = 0, amp = 0.5f, norm = 0;
        for (int i = 0; i < octaves; i++)
        {
            sum += At(x, y) * amp;
            norm += amp;
            x *= 2.03f;
            y *= 2.03f;
            amp *= 0.5f;
        }
        return sum / norm;
    }

    private float L(int x, int y) => _lattice[(y & 255) * 256 + (x & 255)];
}
