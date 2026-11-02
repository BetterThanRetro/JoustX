namespace Joust.Host;

/// <summary>The player's presentation choices, kept between runs as "name=true|false" lines.</summary>
public sealed record DisplaySettings
{
    public bool Hd { get; init; } = true;
    public bool Crt { get; init; }
    public bool Widescreen { get; init; }
    public bool Reverb { get; init; } = true;
    public bool Fullscreen { get; init; }

    /// <summary>Reads saved lines; names it does not know, and bad values, keep the defaults.</summary>
    public static DisplaySettings Parse(IEnumerable<string> lines)
    {
        var s = lines.Select(l => l.Split('=', 2))
            .Where(p => p.Length == 2 && bool.TryParse(p[1], out _))
            .ToDictionary(p => p[0].Trim(), p => bool.Parse(p[1]));
        var d = new DisplaySettings();
        return d with
        {
            Hd = s.GetValueOrDefault("hd", d.Hd),
            Crt = s.GetValueOrDefault("crt", d.Crt),
            Widescreen = s.GetValueOrDefault("widescreen", d.Widescreen),
            Reverb = s.GetValueOrDefault("reverb", d.Reverb),
            Fullscreen = s.GetValueOrDefault("fullscreen", d.Fullscreen),
        };
    }

    public IEnumerable<string> ToLines() =>
    [
        $"hd={Hd}", $"crt={Crt}", $"widescreen={Widescreen}", $"reverb={Reverb}", $"fullscreen={Fullscreen}",
    ];
}
