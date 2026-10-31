namespace Joust.Render;

public sealed record HdOptions
{
    /// <summary>Glow around bright pixels (0 = off).</summary>
    public float Bloom { get; init; } = 0.55f;

    /// <summary>Orange light thrown by the lava and fire (0 = off).</summary>
    public float Heat { get; init; } = 0.9f;

    /// <summary>Light-from-the-top-left edges on sprites and cliffs (0 = flat).</summary>
    public float Bevel { get; init; } = 1f;

    public bool Backdrop { get; init; } = true;
    public bool Shimmer { get; init; } = true;
    public bool Particles { get; init; } = true;
    public bool Crt { get; init; }

    /// <summary>16:9 canvas: the field keeps its 4:3 shape, with dark cavern at the sides.</summary>
    public bool Widescreen { get; init; } = true;

    /// <summary>Write pixels as A,B,G,R in memory (MonoGame's Color) instead of 0xAARRGGBB.</summary>
    public bool Abgr { get; init; }
}

/// <summary>
/// The 2026 look, drawn only from the original frame: the visible 292x240 field upscaled 4x
/// with Scale4x on palette indices (so shapes stay exact), lit edges on everything drawn,
/// a cavern backdrop where the original shows black, bloom, the lava's glow and heat shimmer,
/// and an optional CRT finish. It reads the frame and never feeds anything back.
/// </summary>
public sealed class HdRenderer
{
    public const int Scale = 4;
    public const int SourceWidth = 304, SourceHeight = 256;
    public const int VisibleX = 6, VisibleY = 7, VisibleWidth = 292, VisibleHeight = 240;
    public const int Width = VisibleWidth * Scale, Height = VisibleHeight * Scale;

    /// <summary>
    /// The 16:9 canvas width: the field is shown at 4:3, so its pixels are 1.096 wide. Rounded up
    /// to an even width so the two side panels and the field between them cover every column.
    /// </summary>
    public const int WideWidth = (Width * 4 / 3 + 1) & ~1;

    private const int LavaTop = 196;                             // rows (of the visible field) where lava can be

    private readonly byte[] _low = new byte[VisibleWidth * VisibleHeight];
    private readonly byte[] _mid = new byte[VisibleWidth * VisibleHeight * 4];
    private readonly byte[] _high = new byte[Width * Height];
    private readonly float[] _glow = new float[VisibleWidth * VisibleHeight * 3];
    private readonly float[] _heat = new float[VisibleWidth * VisibleHeight * 3];
    private readonly float[] _blurTemp = new float[VisibleWidth * VisibleHeight * 3];
    private readonly float[] _palette = new float[16 * 3];
    private readonly bool[] _hot = new bool[16];
    private readonly Backdrop _backdrop = new(WideWidth, Height);

    public uint[] Output { get; } = new uint[WideWidth * Height];

    private float _arena;                                        // 0..1: how much of the cavern backdrop shows

    /// <summary>Width of the image in <see cref="Output"/> (rows are this long): <see cref="Width"/> or <see cref="WideWidth"/>.</summary>
    public int OutputWidth { get; private set; } = WideWidth;

    /// <param name="indices">The whole 304x256 frame, one palette index per pixel.</param>
    /// <param name="palette">The 16 palette colours as 0xAARRGGBB.</param>
    /// <param name="frame">Frame number, for the animated effects.</param>
    public void Render(ReadOnlySpan<byte> indices, ReadOnlySpan<uint> palette, long frame, HdOptions options)
    {
        for (int i = 0; i < 16; i++)
        {
            float r = ((palette[i] >> 16) & 0xFF) / 255f, g = ((palette[i] >> 8) & 0xFF) / 255f, b = (palette[i] & 0xFF) / 255f;
            _palette[i * 3] = r; _palette[i * 3 + 1] = g; _palette[i * 3 + 2] = b;
            _hot[i] = r > 0.6f && g < 0.45f * r && b < 0.3f;        // lava and fire red (not the orange rock)
        }
        for (int y = 0; y < VisibleHeight; y++)
            indices.Slice((y + VisibleY) * SourceWidth + VisibleX, VisibleWidth).CopyTo(_low.AsSpan(y * VisibleWidth));

        PixelScaler.Scale4x(_low, VisibleWidth, VisibleHeight, _mid, _high);
        BuildLight(options);

        // The cavern belongs to the arena: shown while the lava is on screen (play and the attract
        // demo), faded out on the title, high-score and self-test screens, which are black in the
        // original. Lava is solid rows of red: red text (CREDITS) or the RAM test's noise is not.
        int lavaRows = 0;
        for (int y = LavaTop; y < VisibleHeight; y++)
        {
            int hot = 0;
            for (int x = 0; x < VisibleWidth; x++)
                if (_hot[_low[y * VisibleWidth + x]]) hot++;
            if (hot * 2 > VisibleWidth) lavaRows++;
        }
        _arena += ((lavaRows >= 3 ? 1f : 0f) - _arena) * 0.08f;

        float t = frame;
        float flicker = 1f + 0.10f * MathF.Sin(t * 0.071f) + 0.05f * MathF.Sin(t * 0.23f + 1.3f);
        OutputWidth = options.Widescreen ? WideWidth : Width;
        Parallel.For(0, Height, y =>
        {
            ComposeRow(y, t, flicker, options);
            if (options.Widescreen) ComposeSides(y, options);
        });
        if (options.Particles)
        {
            Effects.Step(_low, _hot, LavaTop, embers: _arena > 0.5f);   // embers rise only off real lava
            Effects.Draw(Output, OutputWidth, (OutputWidth - Width) / 2, options.Abgr);
        }
    }

    /// <summary>Embers and spark bursts drawn over the frame (the host adds bursts for events).</summary>
    public Particles Effects { get; } = new();

    /// <summary>Bright pixels feed the bloom; lava and fire feed a much wider heat glow.</summary>
    private void BuildLight(HdOptions o)
    {
        Array.Clear(_glow);
        Array.Clear(_heat);
        for (int p = 0; p < _low.Length; p++)
        {
            int i = _low[p];
            if (i == 0) continue;
            float r = _palette[i * 3], g = _palette[i * 3 + 1], b = _palette[i * 3 + 2];
            float luma = 0.3f * r + 0.59f * g + 0.11f * b;
            float bright = Math.Max(0, luma - 0.35f) * 1.6f;
            _glow[p * 3] = r * bright; _glow[p * 3 + 1] = g * bright; _glow[p * 3 + 2] = b * bright;
            if (_hot[i])
            {
                _heat[p * 3] = 1f; _heat[p * 3 + 1] = 0.38f; _heat[p * 3 + 2] = 0.08f;
            }
        }
        if (o.Bloom > 0) { BoxBlur(_glow, 3); BoxBlur(_glow, 3); }
        if (o.Heat > 0) { BoxBlur(_heat, 14); BoxBlur(_heat, 14); BoxBlur(_heat, 14); }
    }

    private void ComposeRow(int y, float t, float flicker, HdOptions o)
    {
        int lowY = y / Scale;
        bool lavaRow = o.Shimmer && lowY >= LavaTop;
        int shift = lavaRow ? (int)MathF.Round(MathF.Sin(y * 0.11f + t * 0.16f) * 2.2f) : 0;
        float lavaLight = _backdrop.LavaLight[y] * flicker;
        float vy = (y - Height / 2f) / (Height / 2f);
        int stride = o.Widescreen ? WideWidth : Width, ox = (stride - Width) / 2, backdropX = (WideWidth - Width) / 2;
        for (int x = 0; x < Width; x++)
        {
            int idx = _high[y * Width + x];
            if (lavaRow && _hot[idx])
            {
                int sx = Math.Clamp(x + shift, 0, Width - 1);    // heat haze: lava pixels wobble sideways
                if (_hot[_high[y * Width + sx]]) idx = _high[y * Width + sx];
            }

            float r, g, b;
            if (idx == 0)
            {
                if (o.Backdrop)
                {
                    int bo = (y * WideWidth + backdropX + x) * 3;
                    float warm = 1f + lavaLight * 2.2f;
                    r = (_backdrop.Base[bo] * warm + lavaLight * 0.05f) * _arena;
                    g = (_backdrop.Base[bo + 1] * (1f + lavaLight * 0.9f) + lavaLight * 0.012f) * _arena;
                    b = _backdrop.Base[bo + 2] * _arena;
                }
                else r = g = b = 0;
            }
            else
            {
                r = _palette[idx * 3]; g = _palette[idx * 3 + 1]; b = _palette[idx * 3 + 2];
                if (o.Bevel > 0)
                {
                    float light = 1f;
                    if (At(x - 2, y - 2) == 0) light += 0.28f * o.Bevel;       // lit from the top left
                    else if (At(x + 2, y + 2) == 0) light -= 0.32f * o.Bevel;  // shadowed bottom right
                    if (At(x + 1, y) == 0 || At(x, y + 1) == 0) light -= 0.08f * o.Bevel;
                    r *= light; g *= light; b *= light;
                }
            }

            if (o.Bloom > 0 || o.Heat > 0)
            {
                Sample(x, y, out float gr, out float gg, out float gb, out float hr, out float hg, out float hb);
                float heat = o.Heat * flicker * (idx == 0 ? 1f : 0.35f);   // light the air more than the objects
                r += gr * o.Bloom + hr * heat;
                g += gg * o.Bloom + hg * heat;
                b += gb * o.Bloom + hb * heat;
            }

            if (o.Crt)
            {
                float scan = (y & 3) == 3 ? 0.62f : (y & 3) == 0 ? 0.9f : 1f;
                float vx = (x - Width / 2f) / (Width / 2f);
                float vignette = 1f - 0.22f * (vx * vx + vy * vy);
                r *= scan * vignette; g *= scan * vignette; b *= scan * vignette;
            }

            Output[y * stride + ox + x] = Pack(r, g, b, o.Abgr);
        }
    }

    /// <summary>
    /// The 16:9 side panels: plain cavern, darkening towards the screen edges. The lava's glow
    /// stays in the field (lit sides read as light bleeding out of it).
    /// </summary>
    private void ComposeSides(int y, HdOptions o)
    {
        int side = (WideWidth - Width) / 2;
        for (int s = 0; s < side; s++)
        {
            float fade = (0.35f + 0.65f * (s / (float)side)) * _arena;   // darker towards the screen edge
            for (int x = s; x < WideWidth; x += WideWidth - 1 - 2 * s)   // s, then its mirror
            {
                int bo = (y * WideWidth + x) * 3;
                float r = _backdrop.Base[bo] * fade, g = _backdrop.Base[bo + 1] * fade, b = _backdrop.Base[bo + 2] * fade;
                if (o.Crt)
                {
                    float scan = (y & 3) == 3 ? 0.62f : (y & 3) == 0 ? 0.9f : 1f;
                    r *= scan; g *= scan; b *= scan;
                }
                Output[y * WideWidth + x] = Pack(r, g, b, o.Abgr);
            }
        }
    }

    private byte At(int x, int y) =>
        x < 0 || y < 0 || x >= Width || y >= Height ? (byte)0 : _high[y * Width + x];

    /// <summary>Bilinear sample of the low-resolution glow and heat buffers at a high-resolution pixel.</summary>
    private void Sample(int x, int y, out float gr, out float gg, out float gb, out float hr, out float hg, out float hb)
    {
        float fx = (x + 0.5f) / Scale - 0.5f, fy = (y + 0.5f) / Scale - 0.5f;
        int x0 = Math.Clamp((int)MathF.Floor(fx), 0, VisibleWidth - 1), y0 = Math.Clamp((int)MathF.Floor(fy), 0, VisibleHeight - 1);
        int x1 = Math.Min(x0 + 1, VisibleWidth - 1), y1 = Math.Min(y0 + 1, VisibleHeight - 1);
        float ax = Math.Clamp(fx - x0, 0, 1), ay = Math.Clamp(fy - y0, 0, 1);
        int p00 = (y0 * VisibleWidth + x0) * 3, p10 = (y0 * VisibleWidth + x1) * 3;
        int p01 = (y1 * VisibleWidth + x0) * 3, p11 = (y1 * VisibleWidth + x1) * 3;
        float w00 = (1 - ax) * (1 - ay), w10 = ax * (1 - ay), w01 = (1 - ax) * ay, w11 = ax * ay;
        gr = _glow[p00] * w00 + _glow[p10] * w10 + _glow[p01] * w01 + _glow[p11] * w11;
        gg = _glow[p00 + 1] * w00 + _glow[p10 + 1] * w10 + _glow[p01 + 1] * w01 + _glow[p11 + 1] * w11;
        gb = _glow[p00 + 2] * w00 + _glow[p10 + 2] * w10 + _glow[p01 + 2] * w01 + _glow[p11 + 2] * w11;
        hr = _heat[p00] * w00 + _heat[p10] * w10 + _heat[p01] * w01 + _heat[p11] * w11;
        hg = _heat[p00 + 1] * w00 + _heat[p10 + 1] * w10 + _heat[p01 + 1] * w01 + _heat[p11 + 1] * w11;
        hb = _heat[p00 + 2] * w00 + _heat[p10 + 2] * w10 + _heat[p01 + 2] * w01 + _heat[p11 + 2] * w11;
    }

    /// <summary>Separable box blur of an RGB float image at the visible field's size.</summary>
    private void BoxBlur(float[] img, int radius)
    {
        int w = VisibleWidth, h = VisibleHeight;
        float norm = 1f / (2 * radius + 1);
        for (int y = 0; y < h; y++)
            for (int c = 0; c < 3; c++)
            {
                float sum = 0;
                for (int k = -radius; k <= radius; k++) sum += img[(y * w + Math.Clamp(k, 0, w - 1)) * 3 + c];
                for (int x = 0; x < w; x++)
                {
                    _blurTemp[(y * w + x) * 3 + c] = sum * norm;
                    sum += img[(y * w + Math.Min(x + radius + 1, w - 1)) * 3 + c] - img[(y * w + Math.Max(x - radius, 0)) * 3 + c];
                }
            }
        for (int x = 0; x < w; x++)
            for (int c = 0; c < 3; c++)
            {
                float sum = 0;
                for (int k = -radius; k <= radius; k++) sum += _blurTemp[(Math.Clamp(k, 0, h - 1) * w + x) * 3 + c];
                for (int y = 0; y < h; y++)
                {
                    img[(y * w + x) * 3 + c] = sum * norm;
                    sum += _blurTemp[(Math.Min(y + radius + 1, h - 1) * w + x) * 3 + c] - _blurTemp[(Math.Max(y - radius, 0) * w + x) * 3 + c];
                }
            }
    }

    private static uint Pack(float r, float g, float b, bool abgr)
    {
        // A soft shoulder instead of a hard clip, so bloom on bright pixels doesn't band.
        uint R = Channel(r), G = Channel(g), B = Channel(b);
        return abgr ? 0xFF000000u | (B << 16) | (G << 8) | R : 0xFF000000u | (R << 16) | (G << 8) | B;
    }

    private static uint Channel(float v)
    {
        if (v > 0.8f) v = 0.8f + 0.2f * (1f - MathF.Exp(-(v - 0.8f) * 5f));
        return (uint)Math.Clamp((int)(v * 255f + 0.5f), 0, 255);
    }
}
