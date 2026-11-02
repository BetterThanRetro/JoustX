using Joust.Emulation;
using Joust.Host;
using Joust.Render;

namespace Joust.Tests;

/// <summary>The front-end-independent session: what any window, browser or phone host builds on.</summary>
public class HostTests
{
    private static readonly Lazy<GameImage> Image = new(() => GameImage.FromSourceNear(AppContext.BaseDirectory));

    [Fact]
    public void AGameImageSurvivesSavingAndLoading()
    {
        var stream = new MemoryStream();
        Image.Value.Save(stream);
        stream.Position = 0;
        var loaded = GameImage.Load(stream);
        Assert.Equal(Image.Value.Main, loaded.Main);
        Assert.Equal(Image.Value.Sound, loaded.Sound);
        Assert.Equal(Image.Value.Symbols.Count, loaded.Symbols.Count);
        Assert.Equal(Image.Value.Symbols["SNDPTR"], loaded.Symbols["sndptr"]);    // names ignore case, as in the source
    }

    /// <summary>Coins from 10 s in (the board ignores them while it starts up), a game at 21 s, then flapping.</summary>
    private static void Play(JoustSession s, int f)
    {
        s.Inputs.CoinLeft = f > 600 && f % 300 < 4;
        s.Inputs.Start1 = f is >= 1300 and < 1304;
        s.Inputs.P1Flap = f > 1400 && f % 20 < 3;
        s.Inputs.P1Right = f % 200 < 50;
    }

    [Fact]
    public void ANewBoardGetsFactorySettingsAndPlaysWithSound()
    {
        var session = new JoustSession(Image.Value, savedCmos: null);
        Assert.True(session.FactoryRestored);
        Assert.NotNull(session.Cmos);
        int samples = 0;
        float peak = 0;
        for (int f = 0; f < 2000; f++)
        {
            Play(session, f);
            session.RunFrame();
            samples += session.Audio.Length / 2;
            foreach (float s in session.Audio) peak = Math.Max(peak, Math.Abs(s));
        }
        Assert.Null(session.Halted);
        Assert.Equal(0, session.Machine.CpuSteps);                           // no CPU by default,
        Assert.Equal(0, session.SoundBoard.CpuSteps);                        // on either board
        Assert.InRange(samples, 2000 * 48000 / 61, 2000 * 48000 / 59);        // a frame's worth each frame
        Assert.True(session.SoundCommands > 20 && peak > 0.2f, $"{session.SoundCommands} sounds, peak {peak}");
        Assert.True(peak <= 0.951f, $"the mix reached {peak}: it would clip");     // the limiter's ceiling
        Assert.Contains(session.State.ReadObjects(), o => o.Kind == ObjectKind.Player);
    }

    [Fact]
    public void SavedSettingsCarryOverAndPictureAndSoundMatchTheOriginal()
    {
        byte[] cmos = new JoustSession(Image.Value, savedCmos: null).Cmos!;
        var native = new JoustSession(Image.Value, cmos);
        var original = new JoustSession(Image.Value, cmos, CpuMode.Original);
        Assert.False(native.FactoryRestored);
        int sounds = 0;
        for (int f = 0; f < 2400; f++)
        {
            foreach (var s in new[] { native, original })
            {
                Play(s, f);
                s.RunFrame();
            }
            Assert.Null(Lockstep.Compare(original.Machine, native.Machine));
            // The sound board hears each command at the same cycle and runs the same program (in
            // C# here, emulated there), so every sample matches.
            Assert.True(original.Audio.SequenceEqual(native.Audio), $"frame {f}: the sound differs");
            sounds = original.SoundCommands;
        }
        Assert.True(sounds > 20, $"only {sounds} sounds played");
    }

    [Fact]
    public void BothPicturesRender()
    {
        var session = new JoustSession(Image.Value, savedCmos: null);
        for (int f = 0; f < 300; f++) session.RunFrame();
        var classic = new uint[JoustMachine.ScreenWidth * JoustMachine.ScreenHeight];
        session.RenderClassic(classic);
        Assert.Contains(classic, p => (p & 0xFFFFFF) != 0);
        var hd = session.RenderHd(new HdOptions { Widescreen = true });
        Assert.Equal(HdRenderer.WideWidth, session.Renderer.OutputWidth);
        Assert.Contains(hd.Take(HdRenderer.WideWidth * HdRenderer.Height), p => (p & 0xFFFFFF) != 0);
    }

    [Fact]
    public void DisplaySettingsReadTheOldFileAndKeepDefaultsForTheRest()
    {
        var s = DisplaySettings.Parse(["hd=False", "crt=True", "nonsense", "reverb=maybe"]);
        Assert.False(s.Hd);
        Assert.True(s.Crt);
        Assert.True(s.Reverb);                                   // a bad value keeps the default
        Assert.Equal(s, DisplaySettings.Parse(s.ToLines()));
    }
}
