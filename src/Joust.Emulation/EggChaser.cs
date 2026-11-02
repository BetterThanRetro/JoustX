namespace Joust.Emulation;

/// <summary>
/// A test player that plays for eggs, so long runs cover collecting them (the random player
/// rarely does). It reads where everything is through <see cref="GameStateReader"/> and steers
/// player 1: to the nearest egg if there is one, approaching at its level so it lands on it or
/// catches it in the air; otherwise above the nearest enemy, to come down on it and win the
/// joust (which drops a new egg). It stays out of the lava, takes the short way round the
/// screen's wrap, and flaps in presses (each press is one flap). It keeps feeding coins and
/// starts like <see cref="RandomPlayer"/>. Deterministic: it decides only from the machine's
/// state, the frame number and <paramref name="seed"/> (which varies how far above enemies it
/// comes in and its flapping rhythm), so two machines in lockstep get the same inputs.
/// </summary>
public sealed class EggChaser(GameStateReader reader, int seed = 1) : ITestPlayer
{
    private readonly int _above = 10 + (int)((uint)seed * 7 % 9);                   // 10-18 pixels
    private readonly int _flapPeriod = 5 + (int)((uint)seed % 3);                   // a press every 5-7 frames

    private const int ScreenWidth = 300;
    private const int LavaLine = 205;                            // feet lower than this are in danger
    private readonly List<GameObject> _objects = [];

    /// <summary>Frames in which player 1 was going for an egg (for tests to check it plays as meant).</summary>
    public int EggFrames { get; private set; }

    public void Apply(JoustInputs i, long frame)
    {
        long cycle = frame % 2400;
        i.CoinLeft = cycle is >= 0 and < 4;
        i.Start1 = cycle is >= 100 and < 104;
        i.P1Left = i.P1Right = i.P1Flap = false;

        reader.ReadObjects(_objects);
        GameObject? me = null;
        foreach (var o in _objects)
            if (o.Kind == ObjectKind.Player) { me = o; break; }
        if (me is not { } p) return;

        // Target: the nearest egg; else the nearest enemy, approached from above.
        GameObject? target = Nearest(p, ObjectKind.Egg);
        int aboveBy = 0;
        if (target != null) EggFrames++;
        else
        {
            target = Nearest(p, ObjectKind.Enemy);
            aboveBy = _above;
        }

        int wantY = target is { } t ? t.Y - aboveBy : 120;
        if (target is { } goal)
        {
            int dx = WrapDx(p.X, goal.X);
            if (Math.Abs(dx) > 3)
            {
                i.P1Right = dx > 0;
                i.P1Left = dx < 0;
            }
        }

        // Height: flap in presses while below where we want to be, or near the lava.
        bool climb = p.Y > wantY + 2 || p.Y > LavaLine;
        i.P1Flap = climb && frame % _flapPeriod < 2;
    }

    private GameObject? Nearest(GameObject me, ObjectKind kind)
    {
        GameObject? best = null;
        int bestDistance = int.MaxValue;
        foreach (var o in _objects)
        {
            if (o.Kind != kind) continue;
            int dx = WrapDx(me.X, o.X), dy = o.Y - me.Y;
            int d = dx * dx + dy * dy;
            if (d < bestDistance) { best = o; bestDistance = d; }
        }
        return best;
    }

    /// <summary>The horizontal step from <paramref name="from"/> to <paramref name="to"/>, the short way round the wrap.</summary>
    private static int WrapDx(int from, int to)
    {
        int dx = to - from;
        if (dx > ScreenWidth / 2) dx -= ScreenWidth;
        if (dx < -ScreenWidth / 2) dx += ScreenWidth;
        return dx;
    }
}
