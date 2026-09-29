using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Creatures;

/// <summary>
/// Fusion material stubs (spec-expeditions.md §Resolver): element essences + rarity shards.
/// creature-fusion consumes these later; until then they are inventory rows with validated ids.
/// </summary>
public static class CreatureMaterialCatalog
{
    /// <summary>Currently ISSUABLE materials — essences plus all ten shard rungs
    /// (spec-rarity-migration.md §4 point 1: "Ten shard materials exist after the migration, one
    /// per rung"). Never a legacy id — those are resolvable but not issuable (see <see cref="Known"/>).</summary>
    // Lazy, not `static readonly ... = Build()` (T4.7, catalog-runtime §3a — applied here for the
    // same uniform guard, even though this Build() does not itself read CreatureSpeciesCatalog).
    static IReadOnlyList<string>? _all;
    public static IReadOnlyList<string> All => _all ??= Build();

    static IReadOnlyList<string> Build()
    {
        var ids = new List<string>();
        foreach (var element in ElementRoster.Concrete)
            ids.Add($"essence.{element.ToElementId()}");
        foreach (var rarity in CreatureRarityLadder.All)
            ids.Add($"shard.{rarity.ToId()}");
        return ids;
    }

    /// <summary>The four legacy shard ids (spec §4 point 4) — resolvable for one release so a
    /// stale client or a saved reference does not hard-fail, but never minted going forward:
    /// `IsKnown` accepts them, `All` does not list them.</summary>
    static readonly IReadOnlyList<string> LegacyIds = LegacyCreatureRarityIds.ForwardMap.Keys
        .Select(id => $"shard.{id}")
        .ToList();

    static HashSet<string>? _known;
    static HashSet<string> Known => _known ??= new HashSet<string>(All.Concat(LegacyIds), StringComparer.Ordinal);

    public static bool IsKnown(string? materialId) => materialId != null && Known.Contains(materialId);
}
