namespace FusionRpg.Core.World;

/// <summary>
/// A lane's *kind*. Warding and severing are lane **state** on the lane row, not types — a warded
/// corridor is still a corridor (spec-world-model.md §Data).
/// </summary>
public sealed record LaneTypeDef
{
    public string LaneTypeId { get; init; } = "";
    public string Name { get; init; } = "";

    /// <summary>Per-mille multiplier on march cost. 1000 = a plain rift lane.</summary>
    public int CostMultiplierMilli { get; init; } = 1000;

    /// <summary>Supply chains may route along it.</summary>
    public bool CarriesSupply { get; init; } = true;

    /// <summary>Incursion pressure spreads along it.</summary>
    public bool CarriesPressure { get; init; } = true;

    /// <summary>Passable in the stored direction only (temporal drift).</summary>
    public bool OneWay { get; init; }

    /// <summary>Needs a key or a cleared boss before it opens.</summary>
    public bool Gated { get; init; }

    /// <summary>Element-typed: a matching banner marches cheaper (world-movement).</summary>
    public bool Ley { get; init; }
}

public static class LaneTypeCatalog
{
    public const string RiftLaneTypeId = "rift";
    public const string CorridorLaneTypeId = "corridor";

    static IReadOnlyList<LaneTypeDef>? _all;
    static Dictionary<string, LaneTypeDef>? _byId;

    public static IReadOnlyList<LaneTypeDef> All => _all ??= Validate(Seed);

    public static bool IsKnown(string? laneTypeId) =>
        laneTypeId != null && ByIdMap().ContainsKey(laneTypeId);

    public static LaneTypeDef Get(string laneTypeId) =>
        ByIdMap().TryGetValue(laneTypeId, out var def)
            ? def
            : throw new ArgumentException($"Unknown lane type id '{laneTypeId}'.");

    /// <summary>Ids/names/structural flags stay here (schema); the per-mille cost is loaded
    /// (tunables-ssot.md T1) — see <see cref="WorldTuningHub"/>.</summary>
    static IReadOnlyList<LaneTypeDef> Seed
    {
        get
        {
            var cost = WorldTuningHub.Tuning.LaneCostMultiplierMilli;
            return new LaneTypeDef[]
            {
                new() { LaneTypeId = RiftLaneTypeId, Name = "Rift Lane", CostMultiplierMilli = cost["rift"] },
                new() { LaneTypeId = CorridorLaneTypeId, Name = "Corridor", CostMultiplierMilli = cost["corridor"] },
                new() { LaneTypeId = "ley", Name = "Ley Lane", CostMultiplierMilli = cost["ley"], Ley = true },
                new() { LaneTypeId = "deep", Name = "Deep Rift", CostMultiplierMilli = cost["deep"], CarriesSupply = false },
                new() { LaneTypeId = "one-way", Name = "Temporal Current", CostMultiplierMilli = cost["one-way"], OneWay = true, CarriesSupply = false },
                new() { LaneTypeId = "gated", Name = "Gated Rift", CostMultiplierMilli = cost["gated"], Gated = true }
            };
        }
    }

    static Dictionary<string, LaneTypeDef> ByIdMap()
    {
        if (_byId == null)
        {
            _ = All;
            _byId = All.ToDictionary(l => l.LaneTypeId, StringComparer.Ordinal);
        }

        return _byId;
    }

    /// <summary>Catalog discipline — a bad lane type is a startup error, never a runtime surprise.</summary>
    public static IReadOnlyList<LaneTypeDef> Validate(IReadOnlyList<LaneTypeDef> lanes)
    {
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var l in lanes)
        {
            WorldIds.RequireKebab(l.LaneTypeId, "Lane type id");
            if (!seenIds.Add(l.LaneTypeId))
                throw new InvalidOperationException($"Duplicate lane type id '{l.LaneTypeId}'.");
            if (string.IsNullOrWhiteSpace(l.Name))
                throw new InvalidOperationException($"Lane type '{l.LaneTypeId}' has no display name.");
            if (l.CostMultiplierMilli <= 0)
                throw new InvalidOperationException($"Lane type '{l.LaneTypeId}' must cost something to march.");

            // State, not type: a warded or severed lane keeps its own kind.
            if (l.LaneTypeId is "warded" or "severed")
                throw new InvalidOperationException(
                    $"'{l.LaneTypeId}' is lane state, not a lane type — store it on the lane row.");
        }

        var byId = lanes.ToDictionary(l => l.LaneTypeId, StringComparer.Ordinal);
        if (byId.TryGetValue(CorridorLaneTypeId, out var corridor) && byId.TryGetValue(RiftLaneTypeId, out var rift)
            && corridor.CostMultiplierMilli >= rift.CostMultiplierMilli)
            throw new InvalidOperationException("A corridor must be cheaper to march than a raw rift lane.");

        return lanes;
    }
}
