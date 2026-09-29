namespace FusionRpg.Core.Dungeon.Registry;

/// <summary>One of the ten named difficulty rungs. "No number of any other kind — every column
/// lives in dungeon.v1.json" (spec-dungeon-registries.md) — this row carries only the id and its
/// ordinal; every reward/penalty column is read from <c>DungeonTuningHub.Tuning.Rungs[id]</c> by
/// the rung's own consumer (<c>difficulty-ladder</c>), not joined here.</summary>
public sealed record DifficultyRungDef
{
    public string RungId { get; init; } = "";

    /// <summary>1-based, contiguous across the ten shipped rungs.</summary>
    public int Ordinal { get; init; }
}

public static class DifficultyRungCatalog
{
    const string File = "difficulty-rungs.v1.json";
    static IReadOnlyList<DifficultyRungDef>? _all;
    static Dictionary<string, DifficultyRungDef>? _byId;

    public static IReadOnlyList<DifficultyRungDef> All => _all
        ?? throw new InvalidOperationException("DifficultyRungCatalog.Configure(...) has not run.");

    public static bool IsKnown(string? id) => id != null && ByIdMap().ContainsKey(id);

    public static DifficultyRungDef Get(string id) =>
        ByIdMap().TryGetValue(id, out var def) ? def : throw new ArgumentException($"Unknown difficulty rung id '{id}'.");

    public static void Configure(IReadOnlyList<DifficultyRungDef> rows)
    {
        _all = Validate(rows);
        _byId = _all.ToDictionary(r => r.RungId, StringComparer.Ordinal);
    }

    static Dictionary<string, DifficultyRungDef> ByIdMap() =>
        _byId ?? throw new InvalidOperationException("DifficultyRungCatalog.Configure(...) has not run.");

    /// <summary>Ordinals must be 1..n contiguous with no gap and no duplicate — the ladder's
    /// neighbour comparisons (R8) depend on it.</summary>
    public static IReadOnlyList<DifficultyRungDef> Validate(IReadOnlyList<DifficultyRungDef> rows)
    {
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var seenOrdinals = new HashSet<int>();
        foreach (var r in rows)
        {
            DungeonRegistryIds.RequireKebab(r.RungId, "Difficulty rung id", File);
            if (!seenIds.Add(r.RungId))
                throw new DungeonRegistryRejection($"{File}: duplicate id '{r.RungId}'.");
            if (!seenOrdinals.Add(r.Ordinal))
                throw new DungeonRegistryRejection($"{File}: duplicate ordinal {r.Ordinal} (on '{r.RungId}').");
        }

        var sorted = seenOrdinals.OrderBy(o => o).ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            var expected = i + 1;
            if (sorted[i] != expected)
                throw new DungeonRegistryRejection(
                    $"{File}: rung ordinals must be 1..{sorted.Count} contiguous — expected {expected}, found {sorted[i]}.");
        }

        return rows.OrderBy(r => r.Ordinal).ToList();
    }

    public static IReadOnlyList<DifficultyRungDef> Parse(string json)
    {
        var root = DungeonRegistryJson.Root(json, File);
        var members = DungeonRegistryJson.Obj(root, "rungs", "$", File);
        var rows = new List<DifficultyRungDef>();
        foreach (var prop in members.EnumerateObject())
        {
            var id = prop.Name;
            var el = prop.Value;
            rows.Add(new DifficultyRungDef
            {
                RungId = id,
                Ordinal = DungeonRegistryJson.Int(el, "ordinal", $"rungs.{id}", File)
            });
        }
        return rows;
    }
}
