namespace FusionRpg.Core.World;

/// <summary>
/// The five-tier size ladder (spec-loam-maps.md). Ids are plain, display names are content — the
/// same discipline `resource-hub-ssot.md` §3 sets, and it dodges two real collisions: `reach`
/// already names 31 source files (`ReachMap`, `SupplyReach`) and `hollow` is already a sector id in
/// `first-light`.
///
/// Hand-authoring stops at `medium`: `WorldTemplateCatalog` runs about 33 lines per sector, so a
/// `large` map is ~1000 lines and a `giant` ~4000 — nobody should write that, and nobody should
/// review it. `large` and above are declared here and marked unavailable, gated on `world-generator`,
/// the module that was always meant to produce maps at scale.
/// </summary>
public sealed record WorldSizeDef
{
    public string SizeId { get; init; } = "";
    public string DisplayName { get; init; } = "";

    /// <summary>A range, not a number — the authored map lands where its teaching properties want it.</summary>
    public int MinNodes { get; init; }
    public int MaxNodes { get; init; }

    public bool Available { get; init; }
}

public static class WorldSizeCatalog
{
    public const string SmallId = "small";
    public const string MediumId = "medium";
    public const string LargeId = "large";
    public const string HugeId = "huge";
    public const string GiantId = "giant";

    static IReadOnlyList<WorldSizeDef>? _all;
    static Dictionary<string, WorldSizeDef>? _byId;

    public static IReadOnlyList<WorldSizeDef> All => _all ??= Validate(Seed);

    /// <summary>Ids/names/availability stay here (schema); the node range per tier is loaded
    /// (tunables-ssot.md T1) — see <see cref="WorldTuningHub"/>.</summary>
    static IReadOnlyList<WorldSizeDef> Seed
    {
        get
        {
            var nodes = WorldTuningHub.Tuning.WorldSizeNodes;
            return new WorldSizeDef[]
            {
                new() { SizeId = SmallId, DisplayName = "Pocket", MinNodes = nodes["small"].Min, MaxNodes = nodes["small"].Max, Available = true },
                new() { SizeId = MediumId, DisplayName = "Fragment", MinNodes = nodes["medium"].Min, MaxNodes = nodes["medium"].Max, Available = true },

                // Declared and unavailable — see the class doc for why hand-authoring stops here.
                new() { SizeId = LargeId, DisplayName = "Expanse", MinNodes = nodes["large"].Min, MaxNodes = nodes["large"].Max, Available = false },
                // A5 (loam-map.md finding): measured 2026-08-23 at 64 nodes, ~52-80ms — comfortably shippable
                // once `world-generator` exists to actually produce a map this size.
                new() { SizeId = HugeId, DisplayName = "Abyss", MinNodes = nodes["huge"].Min, MaxNodes = nodes["huge"].Max, Available = false },
                // A5: measured at 128 nodes, ~0.6-0.7s — needs the Tarjan-first optimisation
                // (spec-world-topology.md) before it can be offered regardless of who generates the map.
                new() { SizeId = GiantId, DisplayName = "Maelstrom", MinNodes = nodes["giant"].Min, MaxNodes = nodes["giant"].Max, Available = false }
            };
        }
    }

    public static bool IsKnown(string? sizeId) =>
        sizeId != null && ByIdMap().ContainsKey(sizeId);

    public static WorldSizeDef Get(string sizeId) =>
        ByIdMap().TryGetValue(sizeId, out var def)
            ? def
            : throw new ArgumentException($"Unknown world size id '{sizeId}'.");

    /// <summary>
    /// The gate a creation flow must pass before it builds a map nobody can afford to compute — a
    /// clear reason naming the tier, not a map that silently never finishes.
    /// </summary>
    public static void RequireAvailable(string sizeId)
    {
        var def = Get(sizeId);
        if (!def.Available)
            throw new InvalidOperationException(
                $"World size '{sizeId}' ({def.DisplayName}) is not available yet — gated on world-generator.");
    }

    static Dictionary<string, WorldSizeDef> ByIdMap()
    {
        if (_byId == null)
        {
            _ = All;
            _byId = All.ToDictionary(s => s.SizeId, StringComparer.Ordinal);
        }

        return _byId;
    }

    /// <summary>Catalog discipline — a bad size row is a startup error, never a runtime surprise.</summary>
    public static IReadOnlyList<WorldSizeDef> Validate(IReadOnlyList<WorldSizeDef> sizes)
    {
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in sizes)
        {
            WorldIds.RequireKebab(s.SizeId, "World size id");
            if (!seenIds.Add(s.SizeId))
                throw new InvalidOperationException($"Duplicate world size id '{s.SizeId}'.");
            if (string.IsNullOrWhiteSpace(s.DisplayName))
                throw new InvalidOperationException($"World size '{s.SizeId}' has no display name.");
            if (s.MinNodes <= 0 || s.MaxNodes < s.MinNodes)
                throw new InvalidOperationException(
                    $"World size '{s.SizeId}' has an invalid node range {s.MinNodes}..{s.MaxNodes}.");
        }

        return sizes;
    }
}
