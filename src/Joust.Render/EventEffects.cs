using Joust.Emulation;

namespace Joust.Render;

/// <summary>
/// Watches the live objects frame to frame (through <see cref="GameStateReader"/>, read-only)
/// and turns what happens into effects: a rider, enemy or pterodactyl leaving the play field
/// (unseated, killed, burned) gets a burst of sparks in its colour.
/// </summary>
public sealed class EventEffects(GameStateReader reader, Particles particles)
{
    private readonly Dictionary<ushort, GameObject> _previous = [];
    private readonly HashSet<ushort> _seen = [];
    private readonly List<GameObject> _objects = [];

    /// <summary>Call once per game frame, after it ran.</summary>
    public void Update()
    {
        _seen.Clear();
        foreach (var o in reader.ReadObjects(_objects))
        {
            _seen.Add(o.Block);
            if (_previous.TryGetValue(o.Block, out var before) && before.Kind != o.Kind)
                Gone(before);                                    // the block was reused at once
            _previous[o.Block] = o;
        }
        foreach (var (block, o) in _previous.ToArray())
        {
            if (_seen.Contains(block)) continue;
            Gone(o);
            _previous.Remove(block);
        }
    }

    private void Gone(GameObject o)
    {
        (float r, float g, float b) = o.Kind switch
        {
            ObjectKind.Player => (1f, 0.9f, 0.3f),
            ObjectKind.Enemy => (1f, 0.55f, 0.35f),
            ObjectKind.Pterodactyl => (0.7f, 0.9f, 1f),
            _ => (0f, 0f, 0f),
        };
        if (r == 0) return;
        // Leaving through the side of the screen (the demo's riders fly off) is not a death.
        if (o.X < HdRenderer.VisibleX + 8 || o.X + 16 > HdRenderer.VisibleX + HdRenderer.VisibleWidth - 8) return;
        // Positions are the bottom-left of a 16x20 box in original pixels.
        particles.Burst(o.X + 8, o.Y - 10, r, g, b, o.Kind == ObjectKind.Pterodactyl ? 90 : 45);
    }
}
