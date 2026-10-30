namespace Joust.Render;

/// <summary>
/// Glowing particles drawn over the HD frame: embers that drift up off the lava, and bursts
/// of sparks the host asks for (a rider unseated, a pterodactyl destroyed). Purely visual and
/// driven by a fixed seed, so a replay looks the same every time.
/// </summary>
public sealed class Particles
{
    private struct Particle
    {
        public float X, Y, Vx, Vy, Life, MaxLife, R, G, B, Size, Gravity;
    }

    private readonly List<Particle> _live = [];
    private readonly Random _rng = new(1982);

    public int Count => _live.Count;

    /// <summary>A burst at original screen coordinates (304x256 space) in an RGB colour (0-1).</summary>
    public void Burst(float x, float y, float r, float g, float b, int count = 40)
    {
        var (hx, hy) = ToHd(x, y);
        for (int i = 0; i < count; i++)
        {
            float angle = (float)(_rng.NextDouble() * Math.PI * 2);
            float speed = 1.5f + (float)_rng.NextDouble() * 6f;
            float life = 25 + _rng.Next(35);
            _live.Add(new Particle
            {
                X = hx, Y = hy, Vx = MathF.Cos(angle) * speed, Vy = MathF.Sin(angle) * speed - 2f,
                Life = life, MaxLife = life, R = r, G = g, B = b, Size = 1.5f + (float)_rng.NextDouble() * 2f, Gravity = 0.18f,
            });
        }
    }

    /// <summary>Spawn embers above lava pixels and age everything by one frame.</summary>
    internal void Step(ReadOnlySpan<byte> low, bool[] hot, int lavaTop)
    {
        for (int i = 0; i < 3; i++)
        {
            int x = _rng.Next(HdRenderer.VisibleWidth), y = lavaTop + _rng.Next(HdRenderer.VisibleHeight - lavaTop);
            if (!hot[low[y * HdRenderer.VisibleWidth + x]]) continue;
            float life = 60 + _rng.Next(80);
            _live.Add(new Particle
            {
                X = (x + 0.5f) * HdRenderer.Scale, Y = y * HdRenderer.Scale,
                Vx = ((float)_rng.NextDouble() - 0.5f) * 0.6f, Vy = -0.6f - (float)_rng.NextDouble() * 1.2f,
                Life = life, MaxLife = life, R = 1f, G = 0.45f + (float)_rng.NextDouble() * 0.25f, B = 0.1f,
                Size = 1f + (float)_rng.NextDouble() * 1.5f, Gravity = -0.004f,
            });
        }

        for (int i = _live.Count - 1; i >= 0; i--)
        {
            var p = _live[i];
            p.X += p.Vx;
            p.Y += p.Vy;
            p.Vy += p.Gravity;
            p.Vx *= 0.985f;
            p.Vx += ((float)_rng.NextDouble() - 0.5f) * 0.08f;  // a little turbulence
            p.Life--;
            if (p.Life <= 0 || p.Y < -10 || p.Y > HdRenderer.Height + 10) _live.RemoveAt(i);
            else _live[i] = p;
        }
    }

    /// <summary>Additively splat each particle as a soft dot into the packed output.</summary>
    internal void Draw(uint[] output, bool abgr)
    {
        foreach (var p in _live)
        {
            float fade = p.Life / p.MaxLife;
            float intensity = fade * fade * 1.4f;
            int radius = (int)MathF.Ceiling(p.Size * 1.8f);
            int cx = (int)p.X, cy = (int)p.Y;
            for (int dy = -radius; dy <= radius; dy++)
            {
                int y = cy + dy;
                if (y < 0 || y >= HdRenderer.Height) continue;
                for (int dx = -radius; dx <= radius; dx++)
                {
                    int x = cx + dx;
                    if (x < 0 || x >= HdRenderer.Width) continue;
                    float d2 = (dx * dx + dy * dy) / (p.Size * p.Size);
                    float w = MathF.Exp(-d2) * intensity;
                    if (w < 0.01f) continue;
                    output[y * HdRenderer.Width + x] = Add(output[y * HdRenderer.Width + x], p.R * w, p.G * w, p.B * w, abgr);
                }
            }
        }
    }

    private static uint Add(uint c, float r, float g, float b, bool abgr)
    {
        int c0 = (int)((c >> 16) & 0xFF), c1 = (int)((c >> 8) & 0xFF), c2 = (int)(c & 0xFF);
        // In ABGR order the low byte is red; in ARGB it is blue.
        float lo = abgr ? r : b, hi = abgr ? b : r;
        c2 = Math.Min(255, c2 + (int)(lo * 255));
        c1 = Math.Min(255, c1 + (int)(g * 255));
        c0 = Math.Min(255, c0 + (int)(hi * 255));
        return 0xFF000000u | ((uint)c0 << 16) | ((uint)c1 << 8) | (uint)c2;
    }

    private static (float X, float Y) ToHd(float x, float y) =>
        ((x - HdRenderer.VisibleX) * HdRenderer.Scale, (y - HdRenderer.VisibleY) * HdRenderer.Scale);
}
