namespace FusionRpg.Core.Dungeon.Registry;

/// <summary>The five supply/curio override tags (ideal §11.5): <c>herbs · key · holy · bait · watch</c>.
/// A flat id list — no row fields beyond the id itself.</summary>
public static class OverrideTagCatalog
{
    const string File = "override-tags.v1.json";
    static IReadOnlyList<string>? _all;
    static HashSet<string>? _set;

    public static IReadOnlyList<string> All => _all
        ?? throw new InvalidOperationException("OverrideTagCatalog.Configure(...) has not run.");

    public static bool IsKnown(string? id) => id != null && SetOf().Contains(id);

    public static string Get(string id) =>
        SetOf().Contains(id) ? id : throw new ArgumentException($"Unknown override tag id '{id}'.");

    public static void Configure(IReadOnlyList<string> tags)
    {
        _all = Validate(tags);
        _set = new HashSet<string>(_all, StringComparer.Ordinal);
    }

    static HashSet<string> SetOf() =>
        _set ?? throw new InvalidOperationException("OverrideTagCatalog.Configure(...) has not run.");

    public static IReadOnlyList<string> Validate(IReadOnlyList<string> tags)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in tags)
        {
            DungeonRegistryIds.RequireKebab(t, "Override tag id", File);
            if (!seen.Add(t))
                throw new DungeonRegistryRejection($"{File}: duplicate id '{t}'.");
        }
        return tags;
    }

    public static IReadOnlyList<string> Parse(string json)
    {
        var root = DungeonRegistryJson.Root(json, File);
        return DungeonRegistryJson.StringArray(root, "overrideTags", "$", File);
    }
}
