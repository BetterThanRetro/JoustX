using System.Text.RegularExpressions;

namespace Joust.Tools;

/// <summary>What the 6809 and 6800 translators share: how routines are cut, named and how their branches are written.</summary>
internal static partial class TranslationText
{
    private const char GoMark = '\u0001';

    /// <summary>A transfer of control to <paramref name="target"/>, written once the routines are known (<see cref="Resolve"/>).</summary>
    public static string Go(int target) => $"{GoMark}{target & 0xFFFF:X4}";

    /// <summary>A goto inside the routine (<paramref name="members"/>), or a return to the dispatcher.</summary>
    public static string Resolve(string line, HashSet<int> members) =>
        GoRegex().Replace(line, m =>
        {
            int target = Convert.ToInt32(m.Groups[1].Value, 16);
            return members.Contains(target) ? $"goto case 0x{target:X4};" : $"return 0x{target:X4};";
        });

    /// <summary>A source line on one line with single spaces, for a comment.</summary>
    public static string Squash(string s) => WhitespaceRegex().Replace(s, " ");

    public static string Identifier(string name) => IdentifierRegex().Replace(name, "_");

    /// <summary>
    /// Cuts instructions (in address order) into routines: a new one at each label, where the code
    /// is not contiguous, and every 300 instructions. Returns each routine's name and members.
    /// </summary>
    public static List<(string Name, List<T> Insns)> Routines<T>(
        IEnumerable<T> insns, Func<T, int> pc, Func<T, int> length, IReadOnlyDictionary<int, string> labels, string firstName)
    {
        var routines = new List<(string Name, List<T> Insns)>();
        List<T>? current = null;
        int expected = -1;
        string lastLabel = firstName;
        foreach (var d in insns)
        {
            bool labelled = labels.TryGetValue(pc(d), out string? label);
            if (labelled) lastLabel = label!;
            if (current == null || labelled || pc(d) != expected || current.Count >= 300)
            {
                current = [];
                routines.Add((labelled ? label! : $"{lastLabel}_{pc(d):X4}", current));
            }
            current.Add(d);
            expected = pc(d) + length(d);
        }
        return routines;
    }

    /// <summary>The program's line labels (no local '.' labels) at instruction addresses, one name each.</summary>
    public static Dictionary<int, string> Labels(IReadOnlyDictionary<string, long> symbols, Func<int, bool> isInstruction) =>
        symbols.Where(kv => !kv.Key.Contains('.') && kv.Value is >= 0 and <= 0xFFFF && isInstruction((int)kv.Value))
            .GroupBy(kv => (int)kv.Value)
            .ToDictionary(g => g.Key, g => g.Select(kv => kv.Key).Order(StringComparer.Ordinal).First());

    [GeneratedRegex("\u0001([0-9A-F]{4})")]
    private static partial Regex GoRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"[^A-Za-z0-9_]")]
    private static partial Regex IdentifierRegex();
}
