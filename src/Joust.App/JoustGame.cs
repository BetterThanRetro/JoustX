using Joust.Assembler;
using Joust.Emulation;
using Microsoft.Xna.Framework;
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
        _machine.SoundCommand += cmd => _lastSound = cmd;

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

        var i = _machine.Inputs;
        i.P1Left = k.IsKeyDown(Keys.Left) || k.IsKeyDown(Keys.A);
        i.P1Right = k.IsKeyDown(Keys.Right) || k.IsKeyDown(Keys.D);
        i.P1Flap = k.IsKeyDown(Keys.Space) || k.IsKeyDown(Keys.LeftControl);
        i.P2Left = k.IsKeyDown(Keys.J);
        i.P2Right = k.IsKeyDown(Keys.L);
        i.P2Flap = k.IsKeyDown(Keys.K) || k.IsKeyDown(Keys.RightControl);
        i.Start1 = k.IsKeyDown(Keys.D1);
        i.Start2 = k.IsKeyDown(Keys.D2);
        i.CoinLeft = k.IsKeyDown(Keys.D5);
        i.CoinRight = k.IsKeyDown(Keys.D6);
        i.Advance = k.IsKeyDown(Keys.F2);
        i.AutoUp = k.IsKeyDown(Keys.F4);

        _machine.RunFrame();
        _previousKeys = k;
        if (_machine.Frame % 30 == 0)
            Window.Title = $"JoustX  |  F1 objects  F2 advance  F3 reset  |  5 coin  1/2 start  |  native calls {_machine.NativeCalls}  |  last sound ${_lastSound:X2}";
        base.Update(gameTime);
    }

    private bool Pressed(KeyboardState k, Keys key) => k.IsKeyDown(key) && !_previousKeys.IsKeyDown(key);

    protected override void Draw(GameTime gameTime)
    {
        _machine.RenderArgb(_argb);
        for (int p = 0; p < _argb.Length; p++)
        {
            uint c = _argb[p]; // AARRGGBB -> AABBGGRR (MonoGame Color is RGBA in memory)
            _rgba[p] = (c & 0xFF00FF00) | ((c >> 16) & 0xFF) | ((c & 0xFF) << 16);
        }
        _screen.SetData(_rgba);

        GraphicsDevice.Clear(Color.Black);
        var dest = FitRectangle(GraphicsDevice.Viewport.Bounds, 4f / 3f);
        _batch.Begin(samplerState: SamplerState.PointClamp);
        _batch.Draw(_screen, dest, Visible, Color.White);
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
