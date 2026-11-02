using Joust.Emulation;
using Joust.Host;
using Joust.Render;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace Joust.App;

/// <summary>
/// The desktop front end. The game itself is a <see cref="JoustSession"/> (in C#, with no 6809
/// emulation, unless --cpu or --original); this window only feeds it the keyboard and pads,
/// plays its sound, shows its picture and keeps the saved settings on disk.
/// </summary>
public sealed class JoustGame : Game
{
    // The monitor shows lines 7-246 and pixels 6-297 of the 304x256 frame buffer.
    private static readonly Rectangle Visible = new(6, 7, 292, 240);

    private readonly GraphicsDeviceManager _graphics;
    private JoustSession _session = null!;
    private SpriteBatch _batch = null!;
    private Texture2D _screen = null!;
    private Texture2D _white = null!;
    private readonly uint[] _rgba = new uint[JoustMachine.ScreenWidth * JoustMachine.ScreenHeight];
    private readonly List<GameObject> _objects = [];
    private KeyboardState _previousKeys;
    private bool _showObjects;
    private string _cmosPath = "", _settingsPath = "";

    // HD presentation (F5 toggles classic, F6 CRT, F8 widescreen). It only reads the frame buffer.
    private HdOptions _hdOptions = new() { Abgr = true };
    private Texture2D _hdScreen = null!, _hdWide = null!;
    private bool _hd = true;

    private const int SampleRate = 48000;
    private const float StickDeadZone = 0.35f;
    private DynamicSoundEffectInstance _audio = null!;
    private readonly byte[] _pcm = new byte[SampleRate / 10 * 4];

    // Timing shown in the title bar, to see whether a slow machine is dropping frames.
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private double _updateMs, _drawMs, _lastTitle;
    private int _draws, _updates, _fps, _ups;
    private float _peak;                                         // loudest sample sent this second
    private readonly bool _autoCoin = Environment.GetCommandLineArgs().Contains("--autocoin");   // for checking sound unattended
    private int _dropped;                                        // frames of audio dropped because the device lagged

    public JoustGame()
    {
        _graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = 960,
            PreferredBackBufferHeight = 720,
            SynchronizeWithVerticalRetrace = true,
        };
        IsFixedTimeStep = true;
        TargetElapsedTime = TimeSpan.FromSeconds(1.0 / JoustSession.FrameRate);
        Window.AllowUserResizing = true;
        Window.Title = "JoustX";
    }

    protected override void Initialize()
    {
        var args = Environment.GetCommandLineArgs();
        var image = GameImage.FromSourceNear(AppContext.BaseDirectory, Environment.CurrentDirectory);
        var mode = args.Contains("--original") ? CpuMode.Original : args.Contains("--cpu") ? CpuMode.Behind : CpuMode.None;

        string dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JoustX");
        Directory.CreateDirectory(dataDir);
        _cmosPath = Path.Combine(dataDir, "cmos.bin");
        _session = new JoustSession(image, File.Exists(_cmosPath) ? File.ReadAllBytes(_cmosPath) : null, mode, SampleRate);
        if (_session.FactoryRestored)
            SaveCmos();

        _settingsPath = Path.Combine(dataDir, "display.txt");
        var settings = File.Exists(_settingsPath) ? DisplaySettings.Parse(File.ReadAllLines(_settingsPath)) : new DisplaySettings();
        _hd = settings.Hd && !args.Contains("--classic");
        _hdOptions = _hdOptions with { Crt = settings.Crt, Widescreen = settings.Widescreen };
        _session.Reverb = settings.Reverb;
        if (settings.Fullscreen) ToggleFullScreen();

        _audio = new DynamicSoundEffectInstance(SampleRate, AudioChannels.Stereo);
        _audio.Play();
        base.Initialize();
    }

    protected override void LoadContent()
    {
        _batch = new SpriteBatch(GraphicsDevice);
        _screen = new Texture2D(GraphicsDevice, JoustMachine.ScreenWidth, JoustMachine.ScreenHeight);
        _hdScreen = new Texture2D(GraphicsDevice, HdRenderer.Width, HdRenderer.Height);
        _hdWide = new Texture2D(GraphicsDevice, HdRenderer.WideWidth, HdRenderer.Height);
        _white = new Texture2D(GraphicsDevice, 1, 1);
        _white.SetData([Color.White]);
    }

    protected override void Update(GameTime gameTime)
    {
        var k = Keyboard.GetState();
        // Alt+1..Alt+8 stand in for F1..F8, for keyboards whose top row sends media keys.
        bool alt = k.IsKeyDown(Keys.LeftAlt) || k.IsKeyDown(Keys.RightAlt);
        if (k.IsKeyDown(Keys.Escape))
            Exit();
        if (Pressed(k, Keys.F11) || (alt && Pressed(k, Keys.Enter)))
            ToggleFullScreen();
        if (FnPressed(k, alt, 1))
            _showObjects = !_showObjects;
        if (FnPressed(k, alt, 3))
            _session.Reset();
        if (FnPressed(k, alt, 5))
            _hd = !_hd;
        if (FnPressed(k, alt, 8))
            _hdOptions = _hdOptions with { Widescreen = !_hdOptions.Widescreen };
        if (FnPressed(k, alt, 7))
            _session.Reverb = !_session.Reverb;
        if (FnPressed(k, alt, 6))
            _hdOptions = _hdOptions with { Crt = !_hdOptions.Crt };

        // Xbox (XInput) controllers: pad 1 and pad 2 are players 1 and 2. D-pad or left stick
        // steers, A/B/X flaps, Start starts that player's game, Back drops a coin.
        var pad1 = GamePad.GetState(PlayerIndex.One);
        var pad2 = GamePad.GetState(PlayerIndex.Two);

        var i = _session.Inputs;
        i.P1Left = k.IsKeyDown(Keys.Left) || k.IsKeyDown(Keys.A) || PadLeft(pad1);
        i.P1Right = k.IsKeyDown(Keys.Right) || k.IsKeyDown(Keys.D) || PadRight(pad1);
        i.P1Flap = k.IsKeyDown(Keys.Space) || k.IsKeyDown(Keys.LeftControl) || PadFlap(pad1);
        i.P2Left = k.IsKeyDown(Keys.J) || PadLeft(pad2);
        i.P2Right = k.IsKeyDown(Keys.L) || PadRight(pad2);
        i.P2Flap = k.IsKeyDown(Keys.K) || k.IsKeyDown(Keys.RightControl) || PadFlap(pad2);
        bool Digit(Keys d) => !alt && k.IsKeyDown(d);                // with Alt, digits are the F-keys
        i.Start1 = Digit(Keys.D1) || pad1.IsButtonDown(Buttons.Start);
        i.Start2 = Digit(Keys.D2) || pad2.IsButtonDown(Buttons.Start);
        long frame = _session.Machine.Frame;
        i.CoinLeft = Digit(Keys.D5) || pad1.IsButtonDown(Buttons.Back)
            || (_autoCoin && frame % 300 is >= 0 and < 4 && frame > 600);   // --autocoin: a coin every 5 s
        i.CoinRight = Digit(Keys.D6) || pad2.IsButtonDown(Buttons.Back);
        i.Advance = FnDown(k, alt, 2);
        i.AutoUp = FnDown(k, alt, 4);
        _previousKeys = k;

        double start = _clock.Elapsed.TotalMilliseconds;
        if (_session.Halted != null)
        {
            Window.Title = "JoustX stopped: " + _session.Halted;
            base.Update(gameTime);
            return;
        }
        _session.RunFrame();
        SubmitAudio();
        _updateMs = 0.9 * _updateMs + 0.1 * (_clock.Elapsed.TotalMilliseconds - start);
        _updates++;
        if (start - _lastTitle >= 1000)
        {
            _fps = _draws; _ups = _updates; _draws = _updates = 0; _lastTitle = start;
            Window.Title = $"JoustX  |  F1 objects  F2 advance  F3 reset  F5 HD/classic  F6 CRT  F7 reverb  F8 widescreen  F11 fullscreen (Alt+1..8 = F1..F8)  |  5 coin  1/2 start  |  " +
                $"{_ups} game / {_fps} drawn per s, game {_updateMs:F1} ms, draw {_drawMs:F1} ms{(gameTime.IsRunningSlowly ? ", SLOW" : "")}  |  " +
                $"audio {_audio.State} {_audio.PendingBufferCount} queued, peak {_peak:F2}, dropped {_dropped}, {_session.SoundCommands} sounds (last ${_session.LastSound:X2})";
            _peak = 0;
        }
        base.Update(gameTime);
    }

    private bool Pressed(KeyboardState k, Keys key) => k.IsKeyDown(key) && !_previousKeys.IsKeyDown(key);

    /// <summary>F<paramref name="n"/>, or Alt+<paramref name="n"/>, just pressed.</summary>
    private bool FnPressed(KeyboardState k, bool alt, int n) =>
        Pressed(k, Keys.F1 + (n - 1)) || (alt && Pressed(k, Keys.D1 + (n - 1)));

    /// <summary>F<paramref name="n"/>, or Alt+<paramref name="n"/>, held down.</summary>
    private static bool FnDown(KeyboardState k, bool alt, int n) =>
        k.IsKeyDown(Keys.F1 + (n - 1)) || (alt && k.IsKeyDown(Keys.D1 + (n - 1)));

    /// <summary>F11 / Alt+Enter: borderless fullscreen at the desktop's resolution, or back to the window.</summary>
    private void ToggleFullScreen()
    {
        _graphics.HardwareModeSwitch = false;
        _graphics.IsFullScreen = !_graphics.IsFullScreen;
        _graphics.ApplyChanges();
    }

    private static bool PadLeft(GamePadState p) =>
        p.IsConnected && (p.IsButtonDown(Buttons.DPadLeft) || p.ThumbSticks.Left.X < -StickDeadZone);

    private static bool PadRight(GamePadState p) =>
        p.IsConnected && (p.IsButtonDown(Buttons.DPadRight) || p.ThumbSticks.Left.X > StickDeadZone);

    private static bool PadFlap(GamePadState p) =>
        p.IsConnected && (p.IsButtonDown(Buttons.A) || p.IsButtonDown(Buttons.B) || p.IsButtonDown(Buttons.X));

    /// <summary>
    /// Hands this frame's sound to the audio device. If the device has fallen behind by more than
    /// a few frames, the frame is dropped rather than letting latency grow.
    /// </summary>
    private void SubmitAudio()
    {
        var stereo = _session.Audio;
        if (stereo.Length == 0) return;
        if (_audio.PendingBufferCount > 4) { _dropped++; return; }
        for (int s = 0; s < stereo.Length; s++)
        {
            _peak = Math.Max(_peak, Math.Abs(stereo[s]));
            short v = (short)Math.Clamp(stereo[s] * 0.8f * short.MaxValue, short.MinValue, short.MaxValue);
            _pcm[2 * s] = (byte)v;
            _pcm[2 * s + 1] = (byte)(v >> 8);
        }
        _audio.SubmitBuffer(_pcm, 0, stereo.Length * 2);
    }

    protected override void Draw(GameTime gameTime)
    {
        double start = _clock.Elapsed.TotalMilliseconds;
        GraphicsDevice.Clear(Color.Black);
        bool wide = _hd && _hdOptions.Widescreen;
        var dest = FitRectangle(GraphicsDevice.Viewport.Bounds, wide ? 16f / 9f : 4f / 3f);
        if (_hd)
        {
            var picture = _session.RenderHd(_hdOptions);
            var texture = wide ? _hdWide : _hdScreen;
            texture.SetData(picture, 0, texture.Width * texture.Height);
            _batch.Begin(samplerState: SamplerState.LinearClamp);
            _batch.Draw(texture, dest, Color.White);
            if (wide)                                            // the object boxes (F1) are relative to the field
                dest = new Rectangle(dest.X + dest.Width / 8, dest.Y, dest.Width * 3 / 4, dest.Height);
        }
        else
        {
            _session.RenderClassic(_rgba, abgr: true);           // MonoGame's Color is RGBA in memory
            _screen.SetData(_rgba);
            _batch.Begin(samplerState: SamplerState.PointClamp);
            _batch.Draw(_screen, dest, Visible, Color.White);
        }
        if (_showObjects)
            DrawObjectBoxes(dest);
        _batch.End();
        _drawMs = 0.9 * _drawMs + 0.1 * (_clock.Elapsed.TotalMilliseconds - start);
        _draws++;
        base.Draw(gameTime);
    }

    private void DrawObjectBoxes(Rectangle dest)
    {
        float sx = dest.Width / (float)Visible.Width, sy = dest.Height / (float)Visible.Height;
        foreach (var o in _session.State.ReadObjects(_objects))
        {
            var color = o.Kind switch
            {
                ObjectKind.Player => Color.Yellow,
                ObjectKind.Enemy => Color.Red,
                ObjectKind.Egg => Color.LightGreen,
                ObjectKind.Pterodactyl => Color.Magenta,
                _ => Color.Cyan,
            };
            var r = new Rectangle(
                dest.X + (int)((o.X - Visible.X) * sx),
                dest.Y + (int)((o.Y - 20 - Visible.Y) * sy),
                (int)(16 * sx),
                (int)(20 * sy));
            DrawOutline(r, color * 0.8f);
        }
    }

    private void DrawOutline(Rectangle r, Color c)
    {
        _batch.Draw(_white, new Rectangle(r.X, r.Y, r.Width, 2), c);
        _batch.Draw(_white, new Rectangle(r.X, r.Bottom - 2, r.Width, 2), c);
        _batch.Draw(_white, new Rectangle(r.X, r.Y, 2, r.Height), c);
        _batch.Draw(_white, new Rectangle(r.Right - 2, r.Y, 2, r.Height), c);
    }

    private static Rectangle FitRectangle(Rectangle bounds, float aspect)
    {
        int w = bounds.Width, h = (int)(bounds.Width / aspect);
        if (h > bounds.Height)
        {
            h = bounds.Height;
            w = (int)(h * aspect);
        }
        return new Rectangle(bounds.X + (bounds.Width - w) / 2, bounds.Y + (bounds.Height - h) / 2, w, h);
    }

    protected override void OnExiting(object sender, ExitingEventArgs args)
    {
        if (_session != null)
        {
            SaveCmos();
            if (_settingsPath.Length > 0)
                File.WriteAllLines(_settingsPath, new DisplaySettings
                {
                    Hd = _hd, Crt = _hdOptions.Crt, Widescreen = _hdOptions.Widescreen,
                    Reverb = _session.Reverb, Fullscreen = _graphics.IsFullScreen,
                }.ToLines());
        }
        base.OnExiting(sender, args);
    }

    private void SaveCmos()
    {
        if (_session.Cmos is { } cmos)
            File.WriteAllBytes(_cmosPath, cmos);
    }
}
