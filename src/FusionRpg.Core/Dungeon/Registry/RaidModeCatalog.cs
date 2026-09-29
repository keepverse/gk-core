namespace FusionRpg.Core.Dungeon.Registry;

/// <summary>The three raid modes: <c>solo · pair · quad</c>. Party counts, squad slots and every
/// other magnitude live in <c>dungeon.v1.json</c>'s <c>raid.modes.*</c> block, joined by the reader,
/// never on this row.</summary>
public static class RaidModeCatalog
{
    const string File = "raid-modes.v1.json";
    static IReadOnlyList<string>? _all;
    static HashSet<string>? _set;

    public static IReadOnlyList<string> All => _all
        ?? throw new InvalidOperationException("RaidModeCatalog.Configure(...) has not run.");

    public static bool IsKnown(string? id) => id != null && SetOf().Contains(id);

    public static string Get(string id) =>
        SetOf().Contains(id) ? id : throw new ArgumentException($"Unknown raid mode id '{id}'.");

    public static void Configure(IReadOnlyList<string> rows)
    {
        _all = Validate(rows);
        _set = new HashSet<string>(_all, StringComparer.Ordinal);
    }

    static HashSet<string> SetOf() =>
        _set ?? throw new InvalidOperationException("RaidModeCatalog.Configure(...) has not run.");

    public static IReadOnlyList<string> Validate(IReadOnlyList<string> rows)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in rows)
        {
            DungeonRegistryIds.RequireKebab(m, "Raid mode id", File);
            if (!seen.Add(m))
                throw new DungeonRegistryRejection($"{File}: duplicate id '{m}'.");
        }
        return rows;
    }

    public static IReadOnlyList<string> Parse(string json)
    {
        var root = DungeonRegistryJson.Root(json, File);
        return DungeonRegistryJson.StringArray(root, "raidModes", "$", File);
    }
}
