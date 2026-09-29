namespace FusionRpg.Core.World;

/// <summary>
/// Who can command on the map. Every kind submits the same commands through the same interface —
/// the human is not a special case (spec-turn-engine.md §Commanders).
/// </summary>
public enum WorldFactionKind
{
    /// <summary>Dave.</summary>
    Player,
    Zomboss,
    /// <summary>A neutral clan: defends its ground, never expands.</summary>
    Clan,
    /// <summary>A rival summoner — the mirror, running the same rules.</summary>
    Rival,
    /// <summary>Unaligned wildlife and slot guards.</summary>
    Wild
}

/// <summary>A fixed set, not content — but still id-addressed so the wire and the store agree.</summary>
public static class FactionKindCatalog
{
    static readonly IReadOnlyList<(WorldFactionKind Kind, string Id)> Seed = new[]
    {
        (WorldFactionKind.Player, "player"),
        (WorldFactionKind.Zomboss, "zomboss"),
        (WorldFactionKind.Clan, "clan"),
        (WorldFactionKind.Rival, "rival"),
        (WorldFactionKind.Wild, "wild")
    };

    static IReadOnlyList<string>? _all;

    public static IReadOnlyList<string> All => _all ??= Validate();

    public static string IdOf(WorldFactionKind kind) =>
        Seed.First(s => s.Kind == kind).Id;

    public static bool IsKnown(string? id) =>
        id != null && Seed.Any(s => string.Equals(s.Id, id, StringComparison.Ordinal));

    public static WorldFactionKind Parse(string id)
    {
        foreach (var (kind, candidate) in Seed)
            if (string.Equals(candidate, id, StringComparison.Ordinal))
                return kind;
        throw new ArgumentException($"Unknown faction kind id '{id}'.");
    }

    static IReadOnlyList<string> Validate()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, id) in Seed)
        {
            WorldIds.RequireKebab(id, "Faction kind id");
            if (!seen.Add(id))
                throw new InvalidOperationException($"Duplicate faction kind id '{id}'.");
        }

        foreach (var kind in Enum.GetValues<WorldFactionKind>())
            if (Seed.All(s => s.Kind != kind))
                throw new InvalidOperationException($"Faction kind {kind} has no id.");

        return Seed.Select(s => s.Id).ToList();
    }
}
