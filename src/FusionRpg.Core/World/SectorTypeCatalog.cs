namespace FusionRpg.Core.World;

/// <summary>Structural flags a sector type carries (spec-world-model.md §Catalogs).</summary>
[Flags]
public enum SectorTypeFlags
{
    None = 0,
    /// <summary>Dave's own timeline — unique, and the one sector the fracture has not swallowed.</summary>
    Home = 1,
    /// <summary>No Seat slot, ever: traversable and exploitable, never a capital.</summary>
    NoBase = 2,
    Boss = 4,
    /// <summary>A chokepoint joining clusters.</summary>
    Nexus = 8,
    Fortress = 1 << 4
}

/// <summary>One sector type. Core is net6 / C# 10 — no <c>required</c>; Validate() rejects bad rows.</summary>
public sealed record SectorTypeDef
{
    public string TypeId { get; init; } = "";
    public string Name { get; init; } = "";

    /// <summary>Relative danger, and therefore relative reward. 0 = safe ground.</summary>
    public int BaseDangerBand { get; init; }

    /// <summary>May contain the one Seat slot that hosts a stronghold.</summary>
    public bool CanHostSeat { get; init; }

    public SectorTypeFlags Flags { get; init; }

    /// <summary>Slot type ids this sector may generate — validated against <see cref="SlotTypeCatalog"/>.</summary>
    public IReadOnlyList<string> AllowedSlotTypes { get; init; } = Array.Empty<string>();
}

public static class SectorTypeCatalog
{
    static IReadOnlyList<SectorTypeDef>? _all;
    static Dictionary<string, SectorTypeDef>? _byId;

    public static IReadOnlyList<SectorTypeDef> All => _all ??= Validate(Seed);

    public static bool IsKnown(string? typeId) =>
        typeId != null && ByIdMap().ContainsKey(typeId);

    public static SectorTypeDef Get(string typeId) =>
        ByIdMap().TryGetValue(typeId, out var def)
            ? def
            : throw new ArgumentException($"Unknown sector type id '{typeId}'.");

    const string Wildland = SlotTypeCatalog.WildlandSlotTypeId;
    const string Seat = SlotTypeCatalog.SeatSlotTypeId;
    const string Rootbed = SlotTypeCatalog.RootbedSlotTypeId;

    static readonly IReadOnlyList<SectorTypeDef> Seed = new SectorTypeDef[]
    {
        new()
        {
            TypeId = "homeworld", Name = "Homeworld", BaseDangerBand = 0, CanHostSeat = true,
            Flags = SectorTypeFlags.Home,
            AllowedSlotTypes = new[] { Seat, Wildland, "market", "shrine", Rootbed }
        },
        new()
        {
            TypeId = "stable", Name = "Stable Ground", BaseDangerBand = 1, CanHostSeat = true,
            AllowedSlotTypes = new[] { Seat, Wildland, "essence-deposit", "material-seam", "lair", "vault", Rootbed }
        },
        new()
        {
            TypeId = "rich", Name = "Rich Ground", BaseDangerBand = 3, CanHostSeat = true,
            AllowedSlotTypes = new[] { Seat, Wildland, "essence-deposit", "shard-vein", "material-seam", "lair", Rootbed }
        },
        new()
        {
            TypeId = "barren", Name = "Barren Reach", BaseDangerBand = 2, CanHostSeat = false,
            Flags = SectorTypeFlags.NoBase,
            // `anomaly` (npc-story-events NR2.31, spec-world-anomaly-sites.md §3, reviewed with
            // world-map-program): ground the Fracture stripped, and — being NoBase — a site to raid,
            // never to settle. It is the only fitting type present in BOTH shipped templates.
            AllowedSlotTypes = new[] { Wildland, "hazard", "material-seam", "anomaly" }
        },
        new()
        {
            TypeId = "storm", Name = "Rift Storm", BaseDangerBand = 4, CanHostSeat = false,
            Flags = SectorTypeFlags.NoBase,
            // `anomaly`: the Rift Storm is the Fracture at its most active — the type a study site
            // belongs on, beside the `tear` it already allows. No shipped template places a storm
            // sector, so this is the allowance a later template's placement needs.
            AllowedSlotTypes = new[] { Wildland, "hazard", "shard-vein", "tear", "anomaly" }
        },
        new()
        {
            TypeId = "warcamp", Name = "Warcamp", BaseDangerBand = 4, CanHostSeat = true,
            AllowedSlotTypes = new[] { Seat, Wildland, "tear", "lair", Rootbed }
        },
        new()
        {
            TypeId = "nexus", Name = "Nexus", BaseDangerBand = 3, CanHostSeat = true,
            Flags = SectorTypeFlags.Nexus,
            // `anomaly`: where clusters join, the rift folds, and the chokepoint flag makes a
            // study-site raid a contested choice — the one type that can also host a Seat.
            AllowedSlotTypes = new[] { Seat, Wildland, "spire", "market", Rootbed, "anomaly" }
        },
        new()
        {
            TypeId = "boss-lair", Name = "Boss Lair", BaseDangerBand = 6, CanHostSeat = true,
            Flags = SectorTypeFlags.Boss,
            AllowedSlotTypes = new[] { Seat, Wildland, "lair", "vault", "shard-vein", Rootbed }
        }
    };

    static Dictionary<string, SectorTypeDef> ByIdMap()
    {
        if (_byId == null)
        {
            _ = All;
            _byId = All.ToDictionary(s => s.TypeId, StringComparer.Ordinal);
        }

        return _byId;
    }

    /// <summary>Catalog discipline — a bad sector type is a startup error, never a runtime surprise.</summary>
    public static IReadOnlyList<SectorTypeDef> Validate(IReadOnlyList<SectorTypeDef> sectors)
    {
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in sectors)
        {
            WorldIds.RequireKebab(s.TypeId, "Sector type id");
            if (!seenIds.Add(s.TypeId))
                throw new InvalidOperationException($"Duplicate sector type id '{s.TypeId}'.");
            if (string.IsNullOrWhiteSpace(s.Name))
                throw new InvalidOperationException($"Sector type '{s.TypeId}' has no display name.");
            if (s.BaseDangerBand < 0)
                throw new InvalidOperationException($"Sector type '{s.TypeId}' has a negative danger band.");

            // The structural rule the whole territory model rests on: no-base means no base, ever.
            if (s.Flags.HasFlag(SectorTypeFlags.NoBase) && s.CanHostSeat)
                throw new InvalidOperationException(
                    $"Sector type '{s.TypeId}' is flagged no-base yet claims it can host a Seat.");

            foreach (var slotTypeId in s.AllowedSlotTypes)
                if (!SlotTypeCatalog.IsKnown(slotTypeId))
                    throw new InvalidOperationException(
                        $"Sector type '{s.TypeId}' allows unknown slot type '{slotTypeId}'.");

            if (s.CanHostSeat && !s.AllowedSlotTypes.Contains(SlotTypeCatalog.SeatSlotTypeId, StringComparer.Ordinal))
                throw new InvalidOperationException(
                    $"Sector type '{s.TypeId}' can host a Seat but does not allow the '{SlotTypeCatalog.SeatSlotTypeId}' slot.");
            if (!s.CanHostSeat && s.AllowedSlotTypes.Contains(SlotTypeCatalog.SeatSlotTypeId, StringComparer.Ordinal))
                throw new InvalidOperationException(
                    $"Sector type '{s.TypeId}' allows a Seat slot but cannot host one.");
        }

        return sectors;
    }
}
