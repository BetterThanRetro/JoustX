using System.Runtime.InteropServices;
using System.Runtime.InteropServices.JavaScript;
using Joust.Emulation;
using Joust.Host;
using Joust.Render;

namespace Joust.Web;

/// <summary>
/// The game as the page (wwwroot/main.js) sees it: one <see cref="JoustSession"/>, run a frame at
/// a time, with its picture, sound and saved settings handed over as typed arrays.
/// </summary>
public static partial class WebGame
{
    private static JoustSession? _session;
    private static readonly uint[] Classic304 = new uint[JoustMachine.ScreenWidth * JoustMachine.ScreenHeight];
    private static readonly List<Particles.Dot> Dots = [];

    private static JoustSession S => _session ?? throw new InvalidOperationException("Start first");

    /// <summary>Starts the board from a saved game image and CMOS (empty for a new board). True if it got factory settings (save the CMOS).</summary>
    [JSExport]
    public static bool Start(byte[] image, byte[] cmos)
    {
        _session = new JoustSession(GameImage.Load(new MemoryStream(image)), cmos.Length > 0 ? cmos : null);
        return _session.FactoryRestored;
    }

    /// <summary>
    /// Plays one frame. <paramref name="buttons"/>: bit 0-2 player 1 left, right, flap; 3-5
    /// player 2; 6-7 start 1 and 2; 8-9 left and right coin; 10 advance; 11 auto-up;
    /// 12 high-score reset.
    /// </summary>
    [JSExport]
    public static void Frame(int buttons)
    {
        var i = S.Inputs;
        bool B(int bit) => (buttons & (1 << bit)) != 0;
        (i.P1Left, i.P1Right, i.P1Flap) = (B(0), B(1), B(2));
        (i.P2Left, i.P2Right, i.P2Flap) = (B(3), B(4), B(5));
        (i.Start1, i.Start2, i.CoinLeft, i.CoinRight) = (B(6), B(7), B(8), B(9));
        (i.Advance, i.AutoUp, i.HighScoreReset) = (B(10), B(11), B(12));
        S.RunFrame();
    }

    /// <summary>The frame's sound: interleaved stereo 48 kHz samples as little-endian float32 bytes.</summary>
    [JSExport]
    public static byte[] Audio() => MemoryMarshal.AsBytes(S.Audio).ToArray();

    [JSExport]
    public static void SetReverb(bool on) => S.Reverb = on;

    /// <summary>The original picture: the 304x256 frame buffer as RGBA bytes.</summary>
    [JSExport]
    public static byte[] Classic()
    {
        S.RenderClassic(Classic304, abgr: true);                 // ABGR words are R, G, B, A bytes
        return MemoryMarshal.AsBytes(Classic304.AsSpan()).ToArray();
    }

    /// <summary>The HD frame's field: 292x240 palette indices, one byte each (then call <see cref="HdState"/>).</summary>
    [JSExport]
    public static byte[] PrepareHd(bool particles) => S.PrepareHd(new HdOptions { Particles = particles }).Low.ToArray();

    /// <summary>
    /// The prepared frame's state: time, flicker, arena; the 16 palette entries as R, G, B and
    /// 1 for lava/fire red; the number of particles, then per particle X, Y, R, G, B, size, brightness.
    /// </summary>
    [JSExport]
    public static double[] HdState()
    {
        var r = S.Renderer;
        r.Effects.Dots(Dots);
        var state = new double[3 + 64 + 1 + 7 * Dots.Count];
        (state[0], state[1], state[2]) = (r.Time, r.Flicker, r.Arena);
        var pal = r.PaletteRgb;
        var hot = r.Hot;
        for (int c = 0; c < 16; c++)
        {
            state[3 + 4 * c] = pal[3 * c];
            state[4 + 4 * c] = pal[3 * c + 1];
            state[5 + 4 * c] = pal[3 * c + 2];
            state[6 + 4 * c] = hot[c] ? 1 : 0;
        }
        state[67] = Dots.Count;
        int o = 68;
        foreach (var d in Dots)
        {
            (state[o], state[o + 1], state[o + 2], state[o + 3]) = (d.X, d.Y, d.R, d.G);
            (state[o + 4], state[o + 5], state[o + 6]) = (d.B, d.Size, d.Intensity);
            o += 7;
        }
        return state;
    }

    /// <summary>The cavern backdrop (it never changes) as RGBA bytes, WideWidth x Height, colour x 6.</summary>
    [JSExport]
    public static byte[] Backdrop()
    {
        var b = S.Renderer.Cavern.Base;
        var bytes = new byte[b.Length / 3 * 4];
        for (int p = 0, q = 0; p < b.Length; p += 3, q += 4)
        {
            bytes[q] = Byte(b[p] * 6);
            bytes[q + 1] = Byte(b[p + 1] * 6);
            bytes[q + 2] = Byte(b[p + 2] * 6);
            bytes[q + 3] = 255;
        }
        return bytes;
    }

    private static byte Byte(float v) => (byte)Math.Clamp((int)(v * 255f + 0.5f), 0, 255);

    /// <summary>The settings to save (empty while the CMOS is blank).</summary>
    [JSExport]
    public static byte[] Cmos() => S.Cmos ?? [];

    /// <summary>Why the game stopped (a copy-protection check found the program changed), or empty.</summary>
    [JSExport]
    public static string Halted() => S.Halted ?? "";

    /// <summary>Power-cycles the board (F3).</summary>
    [JSExport]
    public static void Reset() => S.Reset();
}
