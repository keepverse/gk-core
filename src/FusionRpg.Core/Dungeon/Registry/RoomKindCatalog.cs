using System.Text.Json;
using FusionRpg.Core.Dungeon.Tuning;

namespace FusionRpg.Core.Dungeon.Registry;

/// <summary>
/// One room kind. Schema (id, flags, cross-references) lives here; the numbers a row needs — a
/// weight, a row window — are joined from <see cref="DungeonTuningHub"/> at first read, exactly as
/// <c>LaneTypeCatalog.Seed</c> reads <c>WorldTuningHub.Tuning</c> (spec-dungeon-registries.md
/// "C# catalogs"). A registry row never carries a magnitude.
/// </summary>
public sealed record RoomKindDef
{
    public string RoomKindId { get; init; } = "";
    public bool ClimateNeutral { get; init; }
    public bool SecretEligible { get; init; }
    public bool BossRowAllowed { get; init; }
    public IReadOnlyList<string> NeverAdjacentTo { get; init; } = Array.Empty<string>();

    /// <summary>Non-empty only on the <c>unknown</c> kind.</summary>
    public IReadOnlyList<string> UnknownResolvesTo { get; init; } = Array.Empty<string>();

    public long WeightMilli => DungeonTuningHub.Tuning.Nodes[RoomKindId].WeightMilli;
    public long EarliestRowMilli => DungeonTuningHub.Tuning.Nodes[RoomKindId].EarliestRowMilli;
    public long LatestRowMilli => DungeonTuningHub.Tuning.Nodes[RoomKindId].LatestRowMilli;
}

public static class RoomKindCatalog
{
    const string File = "room-kinds.v1.json";
    static IReadOnlyList<RoomKindDef>? _all;
    static Dictionary<string, RoomKindDef>? _byId;

    public static IReadOnlyList<RoomKindDef> All => _all
        ?? throw new InvalidOperationException("RoomKindCatalog.Configure(...) has not run.");

    public static bool IsKnown(string? id) => id != null && ByIdMap().ContainsKey(id);

    public static RoomKindDef Get(string id) =>
        ByIdMap().TryGetValue(id, out var def) ? def : throw new ArgumentException($"Unknown room kind id '{id}'.");

    public static void Configure(IReadOnlyList<RoomKindDef> rows)
    {
        _all = Validate(rows);
        _byId = _all.ToDictionary(r => r.RoomKindId, StringComparer.Ordinal);
    }

    static Dictionary<string, RoomKindDef> ByIdMap() =>
        _byId ?? throw new InvalidOperationException("RoomKindCatalog.Configure(...) has not run.");

    /// <summary>Catalog discipline — a bad room kind is a startup error, never a runtime surprise.</summary>
    public static IReadOnlyList<RoomKindDef> Validate(IReadOnlyList<RoomKindDef> rows)
    {
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var allIds = rows.Select(r => r.RoomKindId).ToHashSet(StringComparer.Ordinal);
        var bossRowCount = 0;

        foreach (var r in rows)
        {
            DungeonRegistryIds.RequireKebab(r.RoomKindId, "Room kind id", File);
            if (!seenIds.Add(r.RoomKindId))
                throw new DungeonRegistryRejection($"{File}: duplicate id '{r.RoomKindId}'.");

            foreach (var other in r.NeverAdjacentTo)
                if (!allIds.Contains(other))
                    throw new DungeonRegistryRejection($"{File}: '{r.RoomKindId}'.neverAdjacentTo names unknown id '{other}'.");

            foreach (var other in r.UnknownResolvesTo)
                if (!allIds.Contains(other))
                    throw new DungeonRegistryRejection($"{File}: '{r.RoomKindId}'.unknownResolvesTo names unknown id '{other}'.");

            if (r.UnknownResolvesTo.Count > 0 && r.RoomKindId != "unknown")
                throw new DungeonRegistryRejection(
                    $"{File}: only 'unknown' may carry unknownResolvesTo (found on '{r.RoomKindId}').");

            if (r.BossRowAllowed)
            {
                bossRowCount++;
                if (r.SecretEligible)
                    throw new DungeonRegistryRejection(
                        $"{File}: '{r.RoomKindId}' cannot be both bossRowAllowed and secretEligible.");
            }
        }

        if (bossRowCount != 1)
            throw new DungeonRegistryRejection($"{File}: exactly one kind must be bossRowAllowed (found {bossRowCount}).");

        return rows;
    }

    public static IReadOnlyList<RoomKindDef> Parse(string json)
    {
        var root = DungeonRegistryJson.Root(json, File);
        var members = DungeonRegistryJson.Obj(root, "roomKinds", "$", File);
        var rows = new List<RoomKindDef>();
        foreach (var prop in members.EnumerateObject())
        {
            var id = prop.Name;
            var el = prop.Value;
            rows.Add(new RoomKindDef
            {
                RoomKindId = id,
                ClimateNeutral = DungeonRegistryJson.Bool(el, "climateNeutral", $"roomKinds.{id}", File),
                SecretEligible = DungeonRegistryJson.Bool(el, "secretEligible", $"roomKinds.{id}", File),
                BossRowAllowed = DungeonRegistryJson.Bool(el, "bossRowAllowed", $"roomKinds.{id}", File),
                NeverAdjacentTo = DungeonRegistryJson.StringArray(el, "neverAdjacentTo", $"roomKinds.{id}", File),
                UnknownResolvesTo = DungeonRegistryJson.StringArray(el, "unknownResolvesTo", $"roomKinds.{id}", File)
            });
        }
        return rows;
    }
}
