using Joust.Emulation;
using Joust.Port;
using Joust.Port.Routines;
using Joust.Render;

namespace Joust.Host;

/// <summary>How the game's code runs.</summary>
public enum CpuMode
{
    /// <summary>All in C#, with no 6809 emulation (the normal way).</summary>
    None,

    /// <summary>The C# code with the emulated 6809 behind it.</summary>
    Behind,

    /// <summary>Only the emulated original, as the reference.</summary>
    Original,
}

/// <summary>
/// One running game, independent of any window, audio device or file system. Each
/// <see cref="RunFrame"/> plays a 60 Hz frame from <see cref="Inputs"/>, then leaves the frame's
/// sound in <see cref="Audio"/>. The picture comes from <see cref="RenderHd"/> or <see cref="RenderClassic"/>.
/// The front end stores the saved settings (<see cref="Cmos"/>).
/// </summary>
public sealed class JoustSession
{
    /// <summary>The board's video frame rate (about 60.1 Hz).</summary>
    public const double FrameRate = 1_000_000.0 / (JoustMachine.CyclesPerScanline * JoustMachine.ScanlinesPerFrame);

    private readonly SoundBridge _sound;
    private readonly CavernReverb _reverb;
    private readonly Limiter _limiter;
    private readonly EventEffects _events;
    private readonly float[] _samples;
    private readonly float[] _stereo;
    private int _audioFrames;
    private float _dcIn, _dcOut;
    private readonly byte[] _indices = new byte[JoustMachine.ScreenWidth * JoustMachine.ScreenHeight];
    private readonly uint[] _palette = new uint[16];

    /// <param name="savedCmos">The CMOS saved last time, or null (or blank) for a new board.</param>
    public JoustSession(GameImage image, byte[]? savedCmos, CpuMode mode = CpuMode.None, int sampleRate = 48000)
    {
        Machine = new JoustMachine(image.Main);
        if (mode != CpuMode.Original)
        {
            NativeRoutines.InstallAll(Machine, image.Symbols);
            if (mode == CpuMode.None)
                NoCpuRuntime.Attach(Machine);
        }
        State = new GameStateReader(Machine, JoustLayout.FromSymbols(image.Symbols));
        _events = new EventEffects(State, Renderer.Effects);
        Machine.SoundCommand += cmd => { LastSound = cmd; SoundCommands++; };

        SampleRate = sampleRate;
        _reverb = new CavernReverb(sampleRate);
        _limiter = new Limiter(sampleRate);
        _samples = new float[sampleRate / 10];
        _stereo = new float[sampleRate / 10 * 2];
        var board = new SoundBoard(image.Sound);
        board.Reset();
        _sound = new SoundBridge(Machine, board, sampleRate);
        // SND stores the accepted sound in SNDPTR while the process that asked for it is still
        // running (the board gets it next frame): pan toward that object, or centre for the rest.
        Machine.WatchAddress = (int)image.Symbols["SNDPTR"];
        Machine.Watched = _ => _reverb.Pan = State.Executing() is { } source ? (source.X + 8 - 152) / 146f : 0f;

        if (savedCmos != null && savedCmos.Length >= Machine.Cmos.Length && !IsBlank(savedCmos))
            savedCmos.AsSpan(0, Machine.Cmos.Length).CopyTo(Machine.Cmos);
        else
        {
            // A new board restores factory settings and then halts on that message, coins
            // silent, until the operator power-cycles. Do that first boot out of sight.
            var factory = new JoustMachine(image.Main);
            factory.Reset();
            for (int f = 0; f < 1300; f++) factory.RunFrame();
            factory.Cmos.CopyTo(Machine.Cmos, 0);
            FactoryRestored = true;
        }
        Machine.Reset();
    }

    public JoustMachine Machine { get; }
    public JoustInputs Inputs => Machine.Inputs;

    /// <summary>Where the game's objects are, for overlays and effects.</summary>
    public GameStateReader State { get; }

    /// <summary>The HD renderer, including its particle effects.</summary>
    public HdRenderer Renderer { get; } = new();

    public int SampleRate { get; }

    /// <summary>True if this board started blank and got factory settings (save <see cref="Cmos"/> now).</summary>
    public bool FactoryRestored { get; }

    /// <summary>The cavern reverb with panning; off gives the board's mono output on both channels.</summary>
    public bool Reverb { get; set; } = true;

    /// <summary>Why the game stopped (a copy-protection check found the program changed), or null.</summary>
    public string? Halted { get; private set; }

    public int SoundCommands { get; private set; }
    public byte LastSound { get; private set; }

    /// <summary>Plays one frame from the current <see cref="Inputs"/>.</summary>
    public void RunFrame()
    {
        if (Halted != null) return;                              // frozen on the last picture
        try
        {
            Machine.RunFrame();
        }
        catch (RomTamperedException e)
        {
            Halted = e.Message;
            _audioFrames = 0;
            return;
        }
        _events.Update();
        MixAudio();
    }

    /// <summary>Power-cycles the board (the saved settings stay).</summary>
    public void Reset() => Machine.Reset();

    /// <summary>
    /// The last frame's sound: interleaved stereo samples at output level, always within -1..1
    /// (a limiter squeezes loud moments instead of letting them clip).
    /// </summary>
    public ReadOnlySpan<float> Audio => _stereo.AsSpan(0, 2 * _audioFrames);

    private void MixAudio()
    {
        int n = _audioFrames = _sound.EndFrame(_samples);
        for (int s = 0; s < n; s++)
        {
            // DC blocker: the DAC rests at whatever level a sound left it, which would click.
            float x = _samples[s];
            _dcOut = x - _dcIn + 0.9985f * _dcOut;
            _dcIn = x;
            _samples[s] = _dcOut;
        }
        if (Reverb)
            _reverb.Process(_samples.AsSpan(0, n), _stereo);
        else
            for (int s = 0; s < n; s++) _stereo[2 * s] = _stereo[2 * s + 1] = _samples[s];
        _limiter.Process(_stereo.AsSpan(0, 2 * n));
    }

    /// <summary>
    /// The HD picture of the current frame: <see cref="HdRenderer.Output"/>,
    /// <see cref="HdRenderer.OutputWidth"/> pixels wide and <see cref="HdRenderer.Height"/> high.
    /// </summary>
    public uint[] RenderHd(HdOptions options)
    {
        Machine.RenderIndices(_indices);
        for (int c = 0; c < 16; c++) _palette[c] = JoustMachine.PaletteToArgb(Machine.Palette[c]);
        Renderer.Render(_indices, _palette, Machine.Frame, options);
        return Renderer.Output;
    }

    /// <summary>The original picture: the whole 304x256 frame buffer, ARGB (or ABGR) per pixel.</summary>
    public void RenderClassic(uint[] destination, bool abgr = false)
    {
        Machine.RenderArgb(destination);
        if (!abgr) return;
        for (int p = 0; p < destination.Length; p++)
        {
            uint c = destination[p];
            destination[p] = (c & 0xFF00FF00) | ((c >> 16) & 0xFF) | ((c & 0xFF) << 16);
        }
    }

    /// <summary>The settings to save, or null while the CMOS is blank (saving that would lose nothing worth keeping).</summary>
    public byte[]? Cmos => IsBlank(Machine.Cmos) ? null : (byte[])Machine.Cmos.Clone();

    /// <summary>The CMOS keeps 4-bit values; if every one is the same, nothing was ever stored.</summary>
    public static bool IsBlank(byte[] cmos) => cmos.All(b => (b & 0x0F) == (cmos[0] & 0x0F));
}
