using Joust.Assembler;
using Joust.Emulation;
using Joust.Render;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace Joust.App;

/// <summary>
/// Host window. The original program runs on the reference machine every frame; this class
/// only feeds it switches and presents its output. Rendering and audio upgrades plug in here
/// through <see cref="GameStateReader"/> and <see cref="JoustMachine.SoundCommand"/>.
/// </summary>
public sealed class JoustGame : Game
{
    // The monitor shows lines 7-246 and pixels 6-297 of the 304x256 frame buffer.
    private static readonly Rectangle Visible = new(6, 7, 292, 240);
    private const double FrameRate = 1_000_000.0 / (JoustMachine.CyclesPerScanline * JoustMachine.ScanlinesPerFrame);

    private readonly GraphicsDeviceManager _graphics;
    private JoustMachine _machine = null!;
    private GameStateReader _state = null!;
    private SpriteBatch _batch = null!;
    private Texture2D _screen = null!;
    private Texture2D _white = null!;
    private readonly uint[] _argb = new uint[JoustMachine.ScreenWidth * JoustMachine.ScreenHeight];
    private readonly uint[] _rgba = new uint[JoustMachine.ScreenWidth * JoustMachine.ScreenHeight];
    private readonly List<GameObject> _objects = [];
    private KeyboardState _previousKeys;
    private bool _showObjects;
    private byte _lastSound;
    private string _cmosPath = "";

    // HD presentation (F5 toggles classic, F6 CRT). It only reads the frame buffer.
    private readonly HdRenderer _renderer = new();
    private HdOptions _hdOptions = new() { Abgr = true };
    private readonly byte[] _indices = new byte[JoustMachine.ScreenWidth * JoustMachine.ScreenHeight];
    private readonly uint[] _palette = new uint[16];
    private Texture2D _hdScreen = null!;
    private EventEffects _events = null!;
    private bool _hd = !Environment.GetCommandLineArgs().Contains("--classic");

    private const int SampleRate = 48000;
    private const float StickDeadZone = 0.35f;
    private SoundBridge _sound = null!;
    private DynamicSoundEffectInstance _audio = null!;
    private readonly float[] _samples = new float[SampleRate / 10];
    private readonly float[] _stereo = new float[SampleRate / 10 * 2];
    private readonly byte[] _pcm = new byte[SampleRate / 10 * 4];
    private readonly CavernReverb _reverb = new(SampleRate);
    private bool _reverbOn = true;                               // F7: original sound only
    private float _dcIn, _dcOut;

    public JoustGame()
    {
        _graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = 960,
            PreferredBackBufferHeight = 720,
            SynchronizeWithVerticalRetrace = true,
        };
        IsFixedTimeStep = true;
        TargetElapsedTime = TimeSpan.FromSeconds(1.0 / FrameRate);
        Window.AllowUserResizing = true;
        Window.Title = "JoustX";
    }

    protected override void Initialize()
    {
        string source = JoustRomBuild.FindSourceDirectory(AppContext.BaseDirectory)
            ?? JoustRomBuild.FindSourceDirectory(Environment.CurrentDirectory)
            ?? throw new DirectoryNotFoundException($"Could not find {JoustRomBuild.SourceFolderName}/{JoustRomBuild.MainFile}");
        var program = JoustRomBuild.Assemble(source);
        if (!program.Success)
            throw new InvalidOperationException("Assembly failed: " + string.Join("; ", program.Errors.Take(5)));
        if (JoustRomBuild.VerifyChecksums(program).Any(c => !c.Ok))
            throw new InvalidOperationException("Assembled ROMs do not match the original checksums.");

        _machine = new JoustMachine(program.Memory);
        if (!Environment.GetCommandLineArgs().Contains("--original"))
            Port.NativeRoutines.InstallAll(_machine, program.Symbols);
        _state = new GameStateReader(_machine, JoustLayout.FromSymbols(program.Symbols));
        _events = new EventEffects(_state, _renderer.Effects);
        _machine.SoundCommand += cmd => _lastSound = cmd;
        // SND stores the accepted sound in SNDPTR while the process that asked for it is still
        // running (the board gets it next frame): pan toward that object, or centre for the rest.
        _machine.WatchAddress = (int)program.Symbols["SNDPTR"];
        _machine.Watched = _ => _reverb.Pan = _state.Executing() is { } source ? (source.X + 8 - 152) / 146f : 0f;

        var sound = JoustRomBuild.AssembleSound(source);
        if (!sound.Success || JoustRomBuild.SoundChecksum(sound) is var (sum, stored) && sum != stored)
            throw new InvalidOperationException("The sound ROM did not assemble to its checksum.");
        var board = new SoundBoard(sound.Memory);
        board.Reset();
        _sound = new SoundBridge(_machine, board, SampleRate);
        _audio = new DynamicSoundEffectInstance(SampleRate, AudioChannels.Stereo);
        _audio.Play();

        string dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JoustX");
        Directory.CreateDirectory(dataDir);
        _cmosPath = Path.Combine(dataDir, "cmos.bin");
        if (File.Exists(_cmosPath))
            File.ReadAllBytes(_cmosPath).AsSpan(0, _machine.Cmos.Length).CopyTo(_machine.Cmos);

        _machine.Reset();
        base.Initialize();
    }

    protected override void LoadContent()
    {
        _batch = new SpriteBatch(GraphicsDevice);
        _screen = new Texture2D(GraphicsDevice, JoustMachine.ScreenWidth, JoustMachine.ScreenHeight);
        _hdScreen = new Texture2D(GraphicsDevice, HdRenderer.Width, HdRenderer.Height);
        _white = new Texture2D(GraphicsDevice, 1, 1);
        _white.SetData([Color.White]);
    }

    protected override void Update(GameTime gameTime)
    {
        var k = Keyboard.GetState();
        if (k.IsKeyDown(Keys.Escape))
            Exit();
        if (Pressed(k, Keys.F1))
            _showObjects = !_showObjects;
        if (Pressed(k, Keys.F3))
            _machine.Reset();
        if (Pressed(k, Keys.F5))
            _hd = !_hd;
        if (Pressed(k, Keys.F7))
            _reverbOn = !_reverbOn;
        if (Pressed(k, Keys.F6))
            _hdOptions = _hdOptions with { Crt = !_hdOptions.Crt };

        // Xbox (XInput) controllers: pad 1 and pad 2 are players 1 and 2. D-pad or left stick
        // steers, A/B/X flaps, Start starts that player's game, Back drops a coin.
        var pad1 = GamePad.GetState(PlayerIndex.One);
        var pad2 = GamePad.GetState(PlayerIndex.Two);

        var i = _machine.Inputs;
        i.P1Left = k.IsKeyDown(Keys.Left) || k.IsKeyDown(Keys.A) || PadLeft(pad1);
        i.P1Right = k.IsKeyDown(Keys.Right) || k.IsKeyDown(Keys.D) || PadRight(pad1);
        i.P1Flap = k.IsKeyDown(Keys.Space) || k.IsKeyDown(Keys.LeftControl) || PadFlap(pad1);
        i.P2Left = k.IsKeyDown(Keys.J) || PadLeft(pad2);
        i.P2Right = k.IsKeyDown(Keys.L) || PadRight(pad2);
        i.P2Flap = k.IsKeyDown(Keys.K) || k.IsKeyDown(Keys.RightControl) || PadFlap(pad2);
        i.Start1 = k.IsKeyDown(Keys.D1) || pad1.IsButtonDown(Buttons.Start);
        i.Start2 = k.IsKeyDown(Keys.D2) || pad2.IsButtonDown(Buttons.Start);
        i.CoinLeft = k.IsKeyDown(Keys.D5) || pad1.IsButtonDown(Buttons.Back);
        i.CoinRight = k.IsKeyDown(Keys.D6) || pad2.IsButtonDown(Buttons.Back);
        i.Advance = k.IsKeyDown(Keys.F2);
        i.AutoUp = k.IsKeyDown(Keys.F4);

        _machine.RunFrame();
        _events.Update();
        SubmitAudio();
        _previousKeys = k;
        if (_machine.Frame % 30 == 0)
            Window.Title = $"JoustX  |  F1 objects  F2 advance  F3 reset  F5 HD/classic  F6 CRT  F7 reverb  |  5 coin  1/2 start  |  native calls {_machine.NativeCalls}  |  last sound ${_lastSound:X2}";
        base.Update(gameTime);
    }

    private bool Pressed(KeyboardState k, Keys key) => k.IsKeyDown(key) && !_previousKeys.IsKeyDown(key);

    private static bool PadLeft(GamePadState p) =>
        p.IsConnected && (p.IsButtonDown(Buttons.DPadLeft) || p.ThumbSticks.Left.X < -StickDeadZone);

    private static bool PadRight(GamePadState p) =>
        p.IsConnected && (p.IsButtonDown(Buttons.DPadRight) || p.ThumbSticks.Left.X > StickDeadZone);

    private static bool PadFlap(GamePadState p) =>
        p.IsConnected && (p.IsButtonDown(Buttons.A) || p.IsButtonDown(Buttons.B) || p.IsButtonDown(Buttons.X));

    /// <summary>
    /// Hands this frame's sound-board output to the audio device. If the device has fallen
    /// behind by more than a few frames, the frame is dropped rather than letting latency grow.
    /// </summary>
    private void SubmitAudio()
    {
        int n = _sound.EndFrame(_samples);
        if (n == 0 || _audio.PendingBufferCount > 4) return;
        for (int s = 0; s < n; s++)
        {
            // DC blocker: the DAC rests at whatever level a sound left it, which would click.
            float x = _samples[s];
            _dcOut = x - _dcIn + 0.9985f * _dcOut;
            _dcIn = x;
            _samples[s] = _dcOut;
        }
        if (_reverbOn)
        {
            _reverb.Process(_samples.AsSpan(0, n), _stereo);
        }
        else
        {
            for (int s = 0; s < n; s++) _stereo[2 * s] = _stereo[2 * s + 1] = _samples[s];
        }
        for (int s = 0; s < 2 * n; s++)
        {
            short v = (short)Math.Clamp(_stereo[s] * 0.8f * short.MaxValue, short.MinValue, short.MaxValue);
            _pcm[2 * s] = (byte)v;
            _pcm[2 * s + 1] = (byte)(v >> 8);
        }
        _audio.SubmitBuffer(_pcm, 0, n * 4);
    }

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(Color.Black);
        var dest = FitRectangle(GraphicsDevice.Viewport.Bounds, 4f / 3f);
        if (_hd)
        {
            _machine.RenderIndices(_indices);
            for (int c = 0; c < 16; c++) _palette[c] = JoustMachine.PaletteToArgb(_machine.Palette[c]);
            _renderer.Render(_indices, _palette, _machine.Frame, _hdOptions);
            _hdScreen.SetData(_renderer.Output);
            _batch.Begin(samplerState: SamplerState.LinearClamp);
            _batch.Draw(_hdScreen, dest, Color.White);
        }
        else
        {
            _machine.RenderArgb(_argb);
            for (int p = 0; p < _argb.Length; p++)
            {
                uint c = _argb[p]; // AARRGGBB -> AABBGGRR (MonoGame Color is RGBA in memory)
                _rgba[p] = (c & 0xFF00FF00) | ((c >> 16) & 0xFF) | ((c & 0xFF) << 16);
            }
            _screen.SetData(_rgba);
            _batch.Begin(samplerState: SamplerState.PointClamp);
            _batch.Draw(_screen, dest, Visible, Color.White);
        }
        if (_showObjects)
            DrawObjectBoxes(dest);
        _batch.End();
        base.Draw(gameTime);
    }

    private void DrawObjectBoxes(Rectangle dest)
    {
        float sx = dest.Width / (float)Visible.Width, sy = dest.Height / (float)Visible.Height;
        foreach (var o in _state.ReadObjects(_objects))
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
        if (_machine != null && _cmosPath.Length > 0)
            File.WriteAllBytes(_cmosPath, _machine.Cmos);
        base.OnExiting(sender, args);
    }
}
