namespace Joust.Emulation;

public enum ObjectKind
{
    Other,
    Player,
    Enemy,
    Egg,
    Pterodactyl,
    LavaTroll,
}

/// <summary>
/// One live game object, read from its process block. Positions are in original screen
/// pixels (0-303 x 0-255) so a renderer at any resolution can scale them.
/// </summary>
public readonly record struct GameObject(
    ushort Block,
    byte Id,
    ObjectKind Kind,
    int X,
    int Y,
    bool FacingLeft,
    ushort HorseImage,
    ushort RiderImage,
    byte Frame);

/// <summary>Process-block field offsets and RAM addresses, taken from the assembled symbol table.</summary>
public sealed class JoustLayout
{
    public required ushort ProcessListHead { get; init; } // PDUMMY
    public required int Link { get; init; }
    public required int Id { get; init; }
    public required int PosX { get; init; }
    public required int PosY { get; init; }
    public required int Face { get; init; }
    public required int Image { get; init; }
    public required int HorsePic { get; init; }
    public required int RiderPic { get; init; }
    public ushort Executing { get; init; }                // PEXEC: the process running now (0 if unknown)

    public static JoustLayout FromSymbols(IReadOnlyDictionary<string, long> s) => new()
    {
        ProcessListHead = (ushort)s["PDUMMY"],
        Link = (int)s["PLINK"],
        Id = (int)s["PID"],
        PosX = (int)s["PPOSX"],
        PosY = (int)s["PPOSY"],
        Face = (int)s["PFACE"],
        Image = (int)s["PIMAGE"],
        HorsePic = (int)s["PPICH"],
        RiderPic = (int)s["PPICR"],
        Executing = s.TryGetValue("PEXEC", out long exec) ? (ushort)exec : (ushort)0,
    };
}

/// <summary>
/// Reads the game's live objects out of RAM. This is the seam an enhanced renderer uses:
/// the original code still decides everything, and the renderer only looks.
/// </summary>
public sealed class GameStateReader(JoustMachine machine, JoustLayout layout)
{
    private const int PlayerId = 0x09, EnemyId = 0x0F, EggId = 0x02, PteroId = 0x17, LavaId = 0x15;

    public List<GameObject> ReadObjects(List<GameObject>? into = null)
    {
        into ??= [];
        into.Clear();
        var ram = machine.Ram;
        int block = Word(ram, layout.ProcessListHead + layout.Link);
        for (int guard = 0; block != 0 && guard < 64; guard++)
        {
            if (block + 64 > ram.Length) break;                  // not a process block (e.g. RAM not yet set up at boot)
            byte id = ram[block + layout.Id];
            var kind = (id & 0x7F) switch
            {
                PlayerId => ObjectKind.Player,
                EnemyId => ObjectKind.Enemy,
                EggId => ObjectKind.Egg,
                PteroId => ObjectKind.Pterodactyl,
                LavaId => ObjectKind.LavaTroll,
                _ => ObjectKind.Other,
            };
            if (kind != ObjectKind.Other)
            {
                into.Add(new GameObject(
                    (ushort)block,
                    id,
                    kind,
                    Word(ram, block + layout.PosX),
                    ram[block + layout.PosY + 1],
                    ram[block + layout.Face] != 0,
                    (ushort)Word(ram, block + layout.HorsePic),
                    (ushort)Word(ram, block + layout.RiderPic),
                    ram[block + layout.Image]));
            }
            block = Word(ram, block + layout.Link);
        }
        return into;
    }

    /// <summary>
    /// The game object whose process is running right now (PEXEC), or null when the running
    /// process is not one (wave control, the score display, coin handling...).
    /// </summary>
    public GameObject? Executing()
    {
        if (layout.Executing == 0) return null;
        int block = Word(machine.Ram, layout.Executing);
        if (block == 0 || block + 64 > machine.Ram.Length) return null;
        foreach (var o in ReadObjects(_scratch))
            if (o.Block == block) return o;
        return null;
    }

    private readonly List<GameObject> _scratch = [];

    private static int Word(byte[] ram, int a) => (ram[a] << 8) | ram[a + 1];
}
