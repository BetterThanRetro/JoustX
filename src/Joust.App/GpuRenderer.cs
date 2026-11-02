using Joust.Render;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Joust.App;

/// <summary>
/// The HD picture drawn on the GPU (Shaders/Hd.fx), from the field's indices and palette that
/// <see cref="HdRenderer.Prepare"/> leaves. The GPU builds the bloom and heat light and blurs it
/// at the field's size, takes the indices up 4x in two Scale2x passes, lights and colours every
/// pixel in one compose pass, and adds the particles as soft dots. <see cref="HdRenderer"/>'s
/// CPU compose is the reference it follows (<see cref="RenderCompare"/>).
/// </summary>
public sealed class GpuRenderer : IDisposable
{
    private const int LowW = HdRenderer.VisibleWidth, LowH = HdRenderer.VisibleHeight;

    private readonly GraphicsDevice _gd;
    private readonly Effect _fx;
    private readonly SpriteBatch _batch;

    // The per-frame inputs, three sets used in turn, so a frame never writes a texture the GPU
    // may still be reading for the one before.
    private const int Ring = 3;
    private readonly Texture2D[] _lows = new Texture2D[Ring], _palettes = new Texture2D[Ring];
    private int _slot;
    private readonly uint[] _lowData = new uint[LowW * LowH];
    private readonly uint[] _paletteData = new uint[16];

    private readonly Texture2D _dot;
    private Texture2D? _backdrop;
    private readonly RenderTarget2D _glow, _glowTemp, _heat, _heatTemp, _mid, _high, _narrow, _wide;
    private readonly List<Particles.Dot> _dots = [];

    /// <summary>Adds the source colour as it is (the dot texture is premultiplied).</summary>
    private static readonly BlendState AddOne = new()
    {
        ColorSourceBlend = Blend.One, ColorDestinationBlend = Blend.One,
        AlphaSourceBlend = Blend.One, AlphaDestinationBlend = Blend.One,
    };

    public GpuRenderer(GraphicsDevice gd)
    {
        _gd = gd;
        using var stream = typeof(GpuRenderer).Assembly.GetManifestResourceStream("Hd.mgfxo")
            ?? throw new InvalidOperationException("the compiled shaders (Hd.mgfxo) are missing");
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        _fx = new Effect(gd, bytes.ToArray());
        _batch = new SpriteBatch(gd);
        for (int i = 0; i < Ring; i++)
        {
            _lows[i] = new Texture2D(gd, LowW, LowH);
            _palettes[i] = new Texture2D(gd, 16, 1);
        }
        _glow = new RenderTarget2D(gd, LowW, LowH);
        _glowTemp = new RenderTarget2D(gd, LowW, LowH);
        _heat = new RenderTarget2D(gd, LowW, LowH);
        _heatTemp = new RenderTarget2D(gd, LowW, LowH);
        _mid = new RenderTarget2D(gd, LowW * 2, LowH * 2);
        _high = new RenderTarget2D(gd, HdRenderer.Width, HdRenderer.Height);
        _narrow = new RenderTarget2D(gd, HdRenderer.Width, HdRenderer.Height);
        _wide = new RenderTarget2D(gd, HdRenderer.WideWidth, HdRenderer.Height);
        _dot = GaussianDot(64);
    }

    /// <summary>A soft dot: exp(-d²) across ±1.8 sizes, in all four channels (premultiplied).</summary>
    private Texture2D GaussianDot(int n)
    {
        var data = new uint[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float dx = ((x + 0.5f) / n * 2 - 1) * 1.8f, dy = ((y + 0.5f) / n * 2 - 1) * 1.8f;
                uint v = Byte(MathF.Exp(-(dx * dx + dy * dy)));
                data[y * n + x] = v << 24 | v << 16 | v << 8 | v;
            }
        var t = new Texture2D(_gd, n, n);
        t.SetData(data);
        return t;
    }

    private static uint Byte(float v) => (uint)Math.Clamp((int)(v * 255f + 0.5f), 0, 255);

    /// <summary>MonoGame's Color in memory: R in the low byte.</summary>
    private static uint Rgba(float r, float g, float b, float a = 1) => Byte(a) << 24 | Byte(b) << 16 | Byte(g) << 8 | Byte(r);

    /// <summary>Draws the HD picture of the frame <paramref name="r"/> was prepared for; returns it (WideWidth or Width x Height).</summary>
    public RenderTarget2D Render(HdRenderer r, HdOptions o)
    {
        var (low, palette) = Upload(r);
        var p = _fx.Parameters;

        // The light, at the field's size: bright pixels for the bloom, lava for the heat glow,
        // each box-blurred as the CPU does (radius 3 twice, radius 14 three times).
        p["LightPaletteTex"].SetValue(palette);
        p["SrcSize"].SetValue(new Vector2(LowW, LowH));
        Pass("GlowSourcePass", low, _glow);
        for (int i = 0; i < 2; i++)
        {
            Pass("BoxH3Pass", _glow, _glowTemp);
            Pass("BoxV3Pass", _glowTemp, _glow);
        }
        Pass("HeatSourcePass", low, _heat);
        for (int i = 0; i < 3; i++)
        {
            Pass("BoxH14Pass", _heat, _heatTemp);
            Pass("BoxV14Pass", _heatTemp, _heat);
        }

        // Scale4x: two Scale2x passes on the indices.
        Pass("Scale2xPass", low, _mid);
        p["SrcSize"].SetValue(new Vector2(LowW * 2, LowH * 2));
        Pass("Scale2xPass", _mid, _high);

        // Compose every pixel of the canvas.
        var target = o.Widescreen ? _wide : _narrow;
        p["FieldSize"].SetValue(new Vector2(HdRenderer.Width, HdRenderer.Height));
        p["LowSize"].SetValue(new Vector2(LowW, LowH));
        p["BackdropWidth"].SetValue((float)HdRenderer.WideWidth);
        p["OutputWidth"].SetValue((float)target.Width);
        p["Time"].SetValue(r.Time);
        p["Flicker"].SetValue(r.Flicker);
        p["Arena"].SetValue(r.Arena);
        p["BloomAmount"].SetValue(o.Bloom);
        p["HeatAmount"].SetValue(o.Heat);
        p["BevelAmount"].SetValue(o.Bevel);
        p["UseBackdrop"].SetValue(o.Backdrop ? 1f : 0f);
        p["UseShimmer"].SetValue(o.Shimmer ? 1f : 0f);
        p["UseCrt"].SetValue(o.Crt ? 1f : 0f);
        p["LavaTop"].SetValue((float)HdRenderer.LavaRow);
        p["HighTex"].SetValue(_high);
        p["PaletteTex"].SetValue(palette);
        p["GlowTex"].SetValue(_glow);
        p["HeatTex"].SetValue(_heat);
        p["BackdropTex"].SetValue(_backdrop);
        Pass("ComposePass", _high, target);

        // Embers and sparks, added on top like the CPU's soft dots.
        if (o.Particles)
        {
            _gd.SetRenderTarget(target);
            r.Effects.Dots(_dots);
            float ox = (target.Width - HdRenderer.Width) / 2;
            _batch.Begin(SpriteSortMode.Deferred, AddOne, SamplerState.LinearClamp);
            var origin = new Vector2(_dot.Width / 2f);
            foreach (var d in _dots)
            {
                var at = new Vector2((int)d.X + ox + 0.5f, (int)d.Y + 0.5f);
                float scale = 3.6f * d.Size / _dot.Width;
                for (float left = d.Intensity; left > 0; left -= 1)  // a colour can't pass 1: add in steps
                {
                    float i = MathF.Min(left, 1);
                    _batch.Draw(_dot, at, null, new Color(d.R * i, d.G * i, d.B * i, 1f), 0, origin, scale, SpriteEffects.None, 0);
                }
            }
            _batch.End();
        }
        _gd.SetRenderTarget(null);
        return target;
    }

    /// <summary>One full-target pass of <paramref name="technique"/> reading <paramref name="source"/>.</summary>
    private void Pass(string technique, Texture2D source, RenderTarget2D target)
    {
        _gd.SetRenderTarget(target);
        _fx.CurrentTechnique = _fx.Techniques[technique];
        _batch.Begin(SpriteSortMode.Immediate, BlendState.Opaque, SamplerState.PointClamp, effect: _fx);
        _batch.Draw(source, new Rectangle(0, 0, target.Width, target.Height), Color.White);
        _batch.End();
    }

    /// <summary>The frame's indices and palette (and the cavern, once), into the next textures of the ring.</summary>
    private (Texture2D Low, Texture2D Palette) Upload(HdRenderer r)
    {
        _slot = (_slot + 1) % Ring;
        var low = r.Low;
        for (int i = 0; i < low.Length; i++) _lowData[i] = 0xFF000000u | low[i];
        _lows[_slot].SetData(_lowData);

        var pal = r.PaletteRgb;
        var hot = r.Hot;
        for (int i = 0; i < 16; i++) _paletteData[i] = Rgba(pal[i * 3], pal[i * 3 + 1], pal[i * 3 + 2], hot[i] ? 1 : 0);
        _palettes[_slot].SetData(_paletteData);

        if (_backdrop == null)                                   // the cavern never changes
        {
            var b = r.Cavern.Base;
            var data = new uint[HdRenderer.WideWidth * HdRenderer.Height];
            for (int i = 0; i < data.Length; i++) data[i] = Rgba(b[i * 3] * 6, b[i * 3 + 1] * 6, b[i * 3 + 2] * 6);
            _backdrop = new Texture2D(_gd, HdRenderer.WideWidth, HdRenderer.Height);
            _backdrop.SetData(data);
        }
        return (_lows[_slot], _palettes[_slot]);
    }

    public void Dispose()
    {
        foreach (var d in new IDisposable?[] { _fx, _batch, _dot, _backdrop, _glow, _glowTemp, _heat, _heatTemp, _mid, _high, _narrow, _wide }
                     .Concat(_lows).Concat(_palettes))
            d?.Dispose();
    }
}
