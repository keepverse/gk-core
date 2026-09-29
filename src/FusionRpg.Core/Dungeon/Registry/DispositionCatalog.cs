namespace FusionRpg.Core.Dungeon.Registry;

/// <summary>The four wild-room disposition ordinals (ideal §11.6): <c>eager · open · wary · hostile</c>.</summary>
public static class DispositionCatalog
{
    const string File = "disposition.v1.json";
    static IReadOnlyList<string>? _all;
    static HashSet<string>? _set;

    public static IReadOnlyList<string> All => _all
        ?? throw new InvalidOperationException("DispositionCatalog.Configure(...) has not run.");

    public static bool IsKnown(string? id) => id != null && SetOf().Contains(id);

    public static string Get(string id) =>
        SetOf().Contains(id) ? id : throw new ArgumentException($"Unknown disposition id '{id}'.");

    public static void Configure(IReadOnlyList<string> rows)
    {
        _all = Validate(rows);
        _set = new HashSet<string>(_all, StringComparer.Ordinal);
    }

    static HashSet<string> SetOf() =>
        _set ?? throw new InvalidOperationException("DispositionCatalog.Configure(...) has not run.");

    public static IReadOnlyList<string> Validate(IReadOnlyList<string> rows)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in rows)
        {
            DungeonRegistryIds.RequireKebab(d, "Disposition id", File);
            if (!seen.Add(d))
                throw new DungeonRegistryRejection($"{File}: duplicate id '{d}'.");
        }
        return rows;
    }

    public static IReadOnlyList<string> Parse(string json)
    {
        var root = DungeonRegistryJson.Root(json, File);
        return DungeonRegistryJson.StringArray(root, "disposition", "$", File);
    }
}
