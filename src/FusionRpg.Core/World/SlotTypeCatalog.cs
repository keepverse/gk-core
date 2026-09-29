namespace FusionRpg.Core.World;

/// <summary>
/// What a slot inside a sector is. One kind, one catalog entry — the kind is the mechanic, the
/// catalog row is its content (spec-world-model.md §Catalogs).
/// </summary>
public enum SlotKind
{
    /// <summary>Empty ground: the only thing you may build an outpost or stronghold on.</summary>
    Wildland,
    EssenceDeposit,
    ShardVein,
    MaterialSeam,
    Lair,
    Tear,
    Vault,
    Shrine,
    Market,
    Spire,
    Anomaly,
    /// <summary>Unusable ground — cleared by a project into wildland before anything is built.</summary>
    Hazard,
    /// <summary>The one slot in a sector that may host a stronghold.</summary>
    Seat,
    /// <summary>Ground where the old world still shows through — the loam source. Appended, never
    /// inserted: <c>WorldCanonical</c> writes the slot type id string, not the enum ordinal, so
    /// appending is the only safe way to grow this list.</summary>
    Rootbed
}

/// <summary>One slot type. Core is net6 / C# 10 — no <c>required</c>; Validate() rejects bad rows.</summary>
public sealed record SlotTypeDef
{
    public string SlotTypeId { get; init; } = "";
    public string Name { get; init; } = "";
    public SlotKind Kind { get; init; }

    /// <summary>May carry a player-built structure.</summary>
    public bool Buildable { get; init; }

    /// <summary>Produces something over time once developed (economy is a later module).</summary>
    public bool Yields { get; init; }
}

public static class SlotTypeCatalog
{
    public const string SeatSlotTypeId = "seat";
    public const string WildlandSlotTypeId = "wildland";
    public const string RootbedSlotTypeId = "rootbed";

    static IReadOnlyList<SlotTypeDef>? _all;
    static Dictionary<string, SlotTypeDef>? _byId;

    public static IReadOnlyList<SlotTypeDef> All => _all ??= Validate(Seed);

    public static bool IsKnown(string? slotTypeId) =>
        slotTypeId != null && ByIdMap().ContainsKey(slotTypeId);

    public static SlotTypeDef Get(string slotTypeId) =>
        ByIdMap().TryGetValue(slotTypeId, out var def)
            ? def
            : throw new ArgumentException($"Unknown slot type id '{slotTypeId}'.");

    static readonly IReadOnlyList<SlotTypeDef> Seed = new SlotTypeDef[]
    {
        new() { SlotTypeId = WildlandSlotTypeId, Name = "Wildland", Kind = SlotKind.Wildland, Buildable = true },
        new() { SlotTypeId = "essence-deposit", Name = "Essence Deposit", Kind = SlotKind.EssenceDeposit, Buildable = true, Yields = true },
        new() { SlotTypeId = "shard-vein", Name = "Shard Vein", Kind = SlotKind.ShardVein, Buildable = true, Yields = true },
        new() { SlotTypeId = "material-seam", Name = "Material Seam", Kind = SlotKind.MaterialSeam, Buildable = true, Yields = true },
        new() { SlotTypeId = "lair", Name = "Lair", Kind = SlotKind.Lair, Buildable = true, Yields = true },
        new() { SlotTypeId = "tear", Name = "Rift Tear", Kind = SlotKind.Tear, Buildable = true, Yields = true },
        new() { SlotTypeId = "vault", Name = "Vault", Kind = SlotKind.Vault },
        new() { SlotTypeId = "shrine", Name = "Shrine", Kind = SlotKind.Shrine },
        new() { SlotTypeId = "market", Name = "Market", Kind = SlotKind.Market },
        new() { SlotTypeId = "spire", Name = "Spire", Kind = SlotKind.Spire, Buildable = true },
        new() { SlotTypeId = "anomaly", Name = "Anomaly", Kind = SlotKind.Anomaly },
        new() { SlotTypeId = "hazard", Name = "Hazard", Kind = SlotKind.Hazard },
        new() { SlotTypeId = SeatSlotTypeId, Name = "Seat", Kind = SlotKind.Seat, Buildable = true },
        new() { SlotTypeId = RootbedSlotTypeId, Name = "Rootbed", Kind = SlotKind.Rootbed, Buildable = true, Yields = true }
    };

    static Dictionary<string, SlotTypeDef> ByIdMap()
    {
        if (_byId == null)
        {
            _ = All;
            _byId = All.ToDictionary(s => s.SlotTypeId, StringComparer.Ordinal);
        }

        return _byId;
    }

    /// <summary>Catalog discipline — a bad slot type is a startup error, never a runtime surprise.</summary>
    public static IReadOnlyList<SlotTypeDef> Validate(IReadOnlyList<SlotTypeDef> slots)
    {
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var seenKinds = new HashSet<SlotKind>();
        foreach (var s in slots)
        {
            WorldIds.RequireKebab(s.SlotTypeId, "Slot type id");
            if (!seenIds.Add(s.SlotTypeId))
                throw new InvalidOperationException($"Duplicate slot type id '{s.SlotTypeId}'.");
            if (string.IsNullOrWhiteSpace(s.Name))
                throw new InvalidOperationException($"Slot type '{s.SlotTypeId}' has no display name.");
            if (!seenKinds.Add(s.Kind))
                throw new InvalidOperationException($"Slot kind {s.Kind} already has a catalog entry ('{s.SlotTypeId}').");

            // Structural rules the rest of the module leans on.
            if (s.Kind == SlotKind.Seat && (!s.Buildable || s.Yields))
                throw new InvalidOperationException($"Seat slot '{s.SlotTypeId}' must be buildable and must not yield.");
            if (s.Kind == SlotKind.Hazard && s.Buildable)
                throw new InvalidOperationException($"Hazard slot '{s.SlotTypeId}' must be cleared before it is buildable.");
        }

        return slots;
    }
}
