using FusionRpg.Core.Battle.Board;
using FusionRpg.Core.World.Siege;

namespace FusionRpg.Core.World;

/// <summary>
/// A category, not a building (spec-structure-substrate.md §New state). One value this wave —
/// enough to express both a Well and a Waystation, which `loam-structures` adds next.
/// </summary>
public enum StructureKind
{
    LoamSource,

    /// <summary>Raises capacity — a real third thing a structure can do (spec-loam-texture.md).</summary>
    Storage,

    /// <summary>
    /// world-map W56 (spec-sector-development.md §3): the yield kinds the reward layer needs — a
    /// soul conduit, extractors, a hatchery on a lair. This module's own structures produce **loam
    /// and recruits only**; the reward layer itself (souls, essence, materials) is unassigned and
    /// stays out (spec-sector-development.md's own fourth open question) — these rows are the
    /// mechanism the reward layer will eventually attach to, not that layer itself.
    /// </summary>
    Yield,

    /// <summary>base-defense `siege-construction` decision 28: refines `WorldSector.RubbleStock` into
    /// `IronworkStock` at a lossy, gated rate — the third thing a structure can do, following
    /// <see cref="Storage"/>'s own precedent. Gated by a WORKING BUILDING on a slot, not a cooldown, so
    /// the refine rate is something a player builds toward rather than waits out.</summary>
    Refinery,

    /// <summary>base-defense `siege-obstacles`/`siege-construction` (2026-09-06): does none of the
    /// above — no yield, no capacity, no refine. Occupies its cell and blocks (via
    /// <see cref="StructureDef.BlocksMovement"/>/<see cref="StructureDef.BlocksLineOfFire"/>) and
    /// nothing else. The fourth thing a structure can do is deliberately "nothing economic" — every
    /// obstacle row (`ObstacleKind` on <see cref="StructureDef.Obstacle"/> says WHICH obstacle; this
    /// says the row has no OTHER behavior to gate on `Kind`), the first of which is the moat.</summary>
    Obstacle,

    /// <summary>scoped-inventory `sector-storage`: raises a sector's ITEM capacity, never loam's —
    /// a distinct capacity axis, not a second field on <see cref="Storage"/> (owner decision,
    /// scoped-inventory-hierarchy-ideal.md Open question #2). The fifth thing a structure can do.</summary>
    ItemStorage
}

/// <summary>One structure type. Mirrors <see cref="SlotTypeDef"/>'s own shape exactly.</summary>
public sealed record StructureDef
{
    public string StructureId { get; init; } = "";
    public string Name { get; init; } = "";
    public StructureKind Kind { get; init; }

    /// <summary>What kind of slot this structure must sit on.</summary>
    public SlotKind RequiredSlotKind { get; init; }

    /// <summary>Upfront build cost, spent from the building legion's own <c>CarriedLoam</c>.</summary>
    public long Cost { get; init; }

    /// <summary>
    /// Per-mille, 1000 = unchanged. A Well multiplies its Rootbed's existing yield; a Waystation
    /// multiplies its Seat's zero — one field expresses both (spec-structure-substrate.md).
    /// </summary>
    public int YieldMultiplierMilli { get; init; } = 1000;

    /// <summary>
    /// How many `Production` passes construction takes, decrementing to zero, before the structure
    /// is active (spec-loam-structures.md). Zero means never under construction — instant.
    /// </summary>
    public int BuildTurns { get; init; }

    /// <summary>Read only by `Storage`-kind structures — how much a granary raises a sector's cap by (spec-loam-texture.md).</summary>
    public long CapacityBonus { get; init; }

    /// <summary>Read only by ItemStorage-kind structures — how many item-storage slots this structure
    /// grants a sector (scoped-inventory `sector-storage`). Never read by loam's EffectiveCapacity, and
    /// CapacityBonus is never read by item-storage math — two capacity axes, two fields, per the owner's
    /// own rejection of overloading one field to mean both (Open question #2).</summary>
    public long ItemStorageCapacityBonus { get; init; }

    /// <summary>
    /// world-map W56 (spec-sector-development.md §3): a flat per-turn loam add, structure-only —
    /// the field `spec-structure-substrate.md` explicitly deferred to "a new field added when there
    /// is a real row to test it against." Read by <c>LoamProduction.For</c> for *every* active
    /// structure regardless of the slot kind it sits on (unlike <see cref="YieldMultiplierMilli"/>,
    /// which only ever multiplies a Rootbed's own seep) — additive to the existing sum, never a
    /// replacement. Defaults to 0, so every structure minted before this task is untouched.
    /// </summary>
    public long FlatYieldPerTurn { get; init; }

    /// <summary>
    /// base-defense `siege-construction` §2 (decision 27's `Built` path): rubble spent to place this
    /// structure during an active siege. Distinct from <see cref="Cost"/> — peacetime `Build` spends
    /// the founding legion's own `CarriedLoam`; siege-time `Built` spends the SECTOR's world-scoped
    /// `RubbleStock`/`IronworkStock` instead (decision 18: construction-only, never loam-denominated).
    /// Defaults to 0, so every structure minted before this task can still be sieged-constructed for
    /// free until a real cost is authored — a structurally honest default, not a guessed number.
    /// </summary>
    public long ConstructRubbleCost { get; init; }

    /// <summary>See <see cref="ConstructRubbleCost"/> — the worked-material half of the same cost.</summary>
    public long ConstructIronworkCost { get; init; }

    /// <summary>
    /// base-defense `structure-state` (decision 32): the MATERIAL TIER ordinal, not a hit-point count.
    ///
    /// <para><b>An ordinal, because a model picks it.</b> "we will use llm to generate variant like
    /// stone wall, iron wall that iron wall have more defense than stone wall." Seedsmith Law 2
    /// exactly: the model writes IDENTITY (stone, iron, …) and deterministic code writes MAGNITUDE. A
    /// model has no calibrated sense of scale, so a number it picks is a plausible-looking guess that
    /// survives review because nothing looks wrong with it.</para>
    ///
    /// <para>Zero means <b>indestructible by damage</b> — a real content state, not an oversight. The
    /// seven shipped loam rows above predate any notion of a siege and stay at 0, unaffected.</para>
    /// </summary>
    public int MaterialTier { get; init; }

    /// <summary>
    /// Whether this structure blocks movement through its cell. A wall does; a granary you can walk
    /// around does not. Default false — every shipped row keeps today's behaviour.
    /// </summary>
    public bool BlocksMovement { get; init; }

    /// <summary>
    /// Whether this structure blocks line of fire through its cell (base-defense decision 25: an
    /// unoccupied building "occupies its cell, blocks movement AND FIRE, and has HP. It simply does
    /// not act."). Separate from <see cref="BlocksMovement"/> on purpose — a moat blocks movement and
    /// not fire; a smoke-filled ruin could block fire and not movement — this is the field that
    /// finally gives `ActionRow.RequiresLineOfSight` a reader (`siege-obstacles`/`siege-cover`).
    /// </summary>
    public bool BlocksLineOfFire { get; init; }

    /// <summary>
    /// Effective hit points. Decision 32: <c>P(Θ_development) × tierMultiplier</c>, where Θ is the
    /// SECTOR'S <see cref="WorldSector.DevelopmentLevel"/> — a structure has no level of its own, and
    /// a developed city has stronger walls.
    ///
    /// <para><b>long, from <c>P(Θ)</c></b> — the one power ladder; there is no private <c>f(level)</c>
    /// here. Widen before multiplying; divide by 1000 last, exactly once; <c>checked</c>.</para>
    /// </summary>
    public static long MaxHpOf(StructureDef def, int developmentLevel)
    {
        if (def.MaterialTier <= 0) return 0;
        var pTheta = new FusionRpg.Core.Power.PowerLadder(FusionRpg.Core.Power.PowerTuningHub.Tuning)
            .Value(Math.Max(0, developmentLevel));
        return checked(pTheta * StructurePolicy.TierMultiplierMilli(def.MaterialTier) / 1000);
    }

    /// <summary>
    /// base-defense `siege-obstacles` §1: which of §5.18's five rows this structure IS, if any. Not a
    /// parallel system — an obstacle is a `StructureDef` facet, so it inherits HP, destructibility and
    /// everything else `structure-state` already built. Default `None`, so every shipped loam row
    /// (predating any notion of a siege) is unaffected and no golden moves.
    /// </summary>
    public ObstacleKind Obstacle { get; init; } = ObstacleKind.None;

    /// <summary>
    /// base-defense `siege-obstacles` §7, decision 27, §5.24: which of the four acquisition paths can
    /// produce this structure. A structure no path can produce is a catalog row that can never appear
    /// on a board — validated non-empty at load, like every other catalog rule (a bad row is a startup
    /// error, never a runtime surprise).
    /// </summary>
    public IReadOnlyList<AcquisitionPath> AcquisitionPaths { get; init; } = Array.Empty<AcquisitionPath>();

    /// <summary>base-defense `siege-obstacles` §2: a Trench's cover value — flat contest points, never
    /// `P(Θ)` (§5.17: a contest is linear, difference-based, not a magnitude). Zero for every non-cover
    /// structure. Consumed by `siege-cover` as DATA, never a call into that module — this is the field
    /// that used to look like a dependency and turned out to be one line apart.</summary>
    public int CoverPowerMilli { get; init; }

    /// <summary>base-defense `siege-obstacles` §2: how far this structure's cover reaches, in cells.
    /// Zero for every non-cover structure. See <see cref="CoverPowerMilli"/>'s own note on why this is
    /// data, not a dependency on `siege-cover`.</summary>
    public int CoverRadius { get; init; }

    /// <summary>
    /// base-defense `siege-obstacles` §4: per-mille multiplier on the STAMINA cost of entering this
    /// structure's cell. 1000 = unchanged. Stamina, never movement cost (§5.18's own correction to the
    /// original `siege-board` draft) — doubling a movement cost makes the cell a longer walk the
    /// pathfinder simply routes around; taxing stamina makes the SHORT route expensive, which is the
    /// decision Wire exists to create. Bounded ratio CONCEPTUALLY but not capped above — a 5000‰ wire
    /// is legal, per AGENTS.md's no-hard-ceilings rule for a magnitude a balance pass raises.
    /// </summary>
    public int EntryStaminaMultiplierMilli { get; init; } = 1000;

    /// <summary>
    /// base-defense `siege-fog` (spec-siege-fog.md §2, module 30, 2026-09-06): how far this structure
    /// lets its own side see, in tiles. Defaults to `fog.defaultVisionRangeTiles` so no structure ships
    /// with an unauthored, unreadable range by omission. A `See`-role structure (`structure-schema`'s
    /// own reserved-but-unmapped role) authors a larger value than a non-`See` structure — this is the
    /// mechanical payoff the idea doc named for that role's first real purpose.
    /// </summary>
    public int VisionRangeTiles { get; init; } = SiegeTuningPolicy.Fog.DefaultVisionRangeTiles;

    /// <summary>
    /// base-defense `structure-instantiate` (spec-structure-instantiate.md, module 26): which
    /// `ContainerRow` (if any) this structure rolls traits/actions from at placement time, via
    /// <see cref="FusionRpg.Core.Battle.Siege.StructureInstantiate.TryInstantiateStructure"/>. Null
    /// for every structure today — a real, honest state (spec-structure-instantiate.md §1: "the roll
    /// is narrow" — cost/HP/footprint never roll, only traits/actions do), not a placeholder: giving
    /// a structure a container is `structure-planner`/`structure-pipeline`'s (27/28) own content
    /// decision, not this catalog's. A null container means "nothing to roll," a real, correct
    /// outcome the instantiate call handles directly rather than treating as an error.
    /// </summary>
    public string? ContainerId { get; init; }

    /// <summary>Null for every ordinary structure. Set only on a Wonder-tier row (loam-relics-and-wonders
    /// `wonder-structure`) — an orthogonal facet, mirroring <see cref="Obstacle"/>'s own "a StructureDef
    /// facet, not a parallel system" shape. Kind is unchanged by this — a Wonder stays
    /// Kind = LoamSource/Yield, reusing YieldMultiplierMilli/FlatYieldPerTurn exactly as an ordinary row
    /// does. World/Multiverse are named but refused by Validate this wave.</summary>
    public WonderScope? WonderScope { get; init; }

    /// <summary>Only meaningful when WonderScope is set — Validate enforces the pairing.</summary>
    public WonderRarity? WonderRarity { get; init; }

    /// <summary>Empty for every ordinary structure; one or more rows on a Wonder. Validate refuses any
    /// unregistered WonderEffectKind and any Scope mismatch.</summary>
    public IReadOnlyList<WonderEffectDef> WonderEffects { get; init; } = Array.Empty<WonderEffectDef>();

    /// <summary>How many DISTINCT relic instances a Wonder-tier row (WonderScope set) requires to
    /// construct — a count of items to select and consume, never a magnitude contentScale touches
    /// (loam-relics-and-wonders `wonder-build-flow` §Design 2). Zero for every non-Wonder structure.
    /// Validate (below) enforces the pairing: WonderScope set implies RelicCost &gt;= 1 (a Wonder must
    /// cost at least one relic — the whole point of the mechanic); WonderScope null implies
    /// RelicCost == 0.</summary>
    public long RelicCost { get; init; }
}

/// <summary>
/// What can be built onto a slot (spec-structure-substrate.md). Deliberately content-light this
/// wave: one placeholder row proves the mechanism before `loam-structures` adds Well and Waystation.
/// </summary>
public static class StructureCatalog
{
    static IReadOnlyList<StructureDef>? _all;
    static Dictionary<string, StructureDef>? _byId;
    static FusionRpg.Core.World.StructureSeed.StructureCorpus? _corpus;

    /// <summary>
    /// base-defense `structure-catalog-import` (module 25, spec-structure-catalog-import.md §1).
    /// Called by the composition root, never by game code — resets the cached rows so a
    /// reconfigure is honoured, the same contract `BattleModeProfileCatalog.Configure` already
    /// states for profiles.
    ///
    /// <para><b>Task 25.4: the C# literal fallback is gone.</b> Byte-identity was proven first
    /// (`StructureCatalogImportTests.The_eight_shipped_rows_are_byte_identical_through_the_corpus`),
    /// per the spec's own "order matters" instruction — prove identity, then delete, never the
    /// reverse. The real server wires this in `Program.cs` from the committed corpus on disk
    /// (`gk-data/packs/fusion/data/seed/structures/`, with a matching `.csproj` copy rule so a published build finds it
    /// next to the exe); every test project that reaches this catalog — directly or transitively —
    /// wires the identical call in its own `StructureCatalogTestBootstrap.cs`. `Configure(null)` (or
    /// never calling this at all) is a genuine startup error now, not a silent fallback — loud over
    /// silent, matching every other catalog rule in this program.</para>
    /// </summary>
    public static void Configure(FusionRpg.Core.World.StructureSeed.StructureCorpus? corpus)
    {
        _corpus = corpus;
        _all = null;
        _byId = null;
    }

    public static IReadOnlyList<StructureDef> All => _all ??= Validate(BuildRows());

    static IReadOnlyList<StructureDef> BuildRows()
    {
        if (_corpus is null)
            throw new InvalidOperationException(
                "StructureCatalog.Configure was never called — call it from the composition root " +
                "(Program.cs) or a StructureCatalogTestBootstrap module initializer before reading " +
                "StructureCatalog.All/Get/IsKnown.");

        var rows = new List<StructureDef>();
        foreach (var row in _corpus.Rows)
        {
            if (!row.IsCatalogLoadable) continue; // identity-registered only — structure-planner's job
            rows.Add(ToStructureDef(row));
        }

        return rows;
    }

    /// <summary>The one place a corpus row's ordinals/magnitudes become a real `StructureDef` —
    /// see spec-structure-catalog-import.md's own Correction 1 for why `Magnitudes` (never
    /// `StrengthBand` alone) is authoritative for `MaterialTier`, and Correction 2 for why
    /// `Magnitudes.StructureKind` (never a `role`-keyed derivation) is authoritative for `Kind` —
    /// `anchor/schema.py`'s own `ROLE_TO_STRUCTURE_KIND` dict is provably wrong against real
    /// shipped rows (`hatchery`/`soul-conduit`/`extractor` are all `Yield`, not what it predicts).
    /// </summary>
    static StructureDef ToStructureDef(FusionRpg.Core.World.StructureSeed.StructureCorpusRow row)
    {
        var m = row.Magnitudes!;
        return new StructureDef
        {
            StructureId = row.StructureId,
            Name = row.Name,
            Kind = Enum.Parse<StructureKind>(m.StructureKind),
            RequiredSlotKind = Enum.Parse<SlotKind>(row.RequiredSlotKind),
            Cost = m.Cost,
            YieldMultiplierMilli = m.YieldMultiplierMilli,
            BuildTurns = m.BuildTurns,
            CapacityBonus = m.CapacityBonus,
            ItemStorageCapacityBonus = m.ItemStorageCapacityBonus,
            FlatYieldPerTurn = m.FlatYieldPerTurn,
            ConstructRubbleCost = m.ConstructRubbleCost,
            ConstructIronworkCost = m.ConstructIronworkCost,
            MaterialTier = m.MaterialTier,
            BlocksMovement = m.BlocksMovement,
            BlocksLineOfFire = m.BlocksLineOfFire,
            Obstacle = Enum.Parse<ObstacleKind>(m.ObstacleKind),
            AcquisitionPaths = row.AcquisitionPaths.Select(p => Enum.Parse<AcquisitionPath>(p, ignoreCase: true)).ToList(),
            CoverPowerMilli = m.CoverPowerMilli,
            CoverRadius = m.CoverRadius,
            EntryStaminaMultiplierMilli = m.EntryStaminaMultiplierMilli,
            VisionRangeTiles = m.VisionRangeTiles ?? SiegeTuningPolicy.Fog.DefaultVisionRangeTiles,
            ContainerId = m.ContainerId,
            WonderScope = m.WonderScope is { } wsStr ? Enum.Parse<WonderScope>(wsStr) : null,
            WonderRarity = m.WonderRarity is { } wrStr ? Enum.Parse<WonderRarity>(wrStr) : null,
            WonderEffects = m.WonderEffects?
                .Select(e => new WonderEffectDef
                {
                    Kind = Enum.Parse<WonderEffectKind>(e.Kind),
                    Scope = Enum.Parse<WonderScope>(e.Scope),
                    ValueMilli = e.ValueMilli
                })
                .ToArray() ?? Array.Empty<WonderEffectDef>(),
            RelicCost = m.RelicCost ?? 0,
        };
    }

    public static bool IsKnown(string? structureId) =>
        structureId != null && ByIdMap().ContainsKey(structureId);

    public static StructureDef Get(string structureId) =>
        ByIdMap().TryGetValue(structureId, out var def)
            ? def
            : throw new ArgumentException($"Unknown structure id '{structureId}'.");

    static Dictionary<string, StructureDef> ByIdMap()
    {
        if (_byId == null)
        {
            _ = All;
            _byId = All.ToDictionary(s => s.StructureId, StringComparer.Ordinal);
        }

        return _byId;
    }

    /// <summary>Catalog discipline — a bad structure row is a startup error, never a runtime surprise.</summary>
    public static IReadOnlyList<StructureDef> Validate(IReadOnlyList<StructureDef> structures)
    {
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in structures)
        {
            WorldIds.RequireKebab(s.StructureId, "Structure id");
            if (!seenIds.Add(s.StructureId))
                throw new InvalidOperationException($"Duplicate structure id '{s.StructureId}'.");
            if (string.IsNullOrWhiteSpace(s.Name))
                throw new InvalidOperationException($"Structure '{s.StructureId}' has no display name.");
            if (s.Cost < 0)
                throw new InvalidOperationException($"Structure '{s.StructureId}' has negative cost.");
            if (s.ConstructRubbleCost < 0)
                throw new InvalidOperationException($"Structure '{s.StructureId}' has negative construct rubble cost.");
            if (s.ConstructIronworkCost < 0)
                throw new InvalidOperationException($"Structure '{s.StructureId}' has negative construct ironwork cost.");
            if (s.YieldMultiplierMilli < 0)
                throw new InvalidOperationException($"Structure '{s.StructureId}' has a negative yield multiplier.");
            if (s.FlatYieldPerTurn < 0)
                throw new InvalidOperationException($"Structure '{s.StructureId}' has a negative flat yield.");
            if (s.ItemStorageCapacityBonus < 0)
                throw new InvalidOperationException($"Structure '{s.StructureId}' has a negative item storage capacity bonus.");
            if (s.MaterialTier < 0)
                throw new InvalidOperationException($"Structure '{s.StructureId}' has a negative material tier.");
            // Tier 0 (indestructible) never reaches TierMultiplierMilli — MaxHpOf short-circuits it.
            // Every tier above 0 must have an authored multiplier, or decision 32's whole ladder is
            // useless the moment content actually uses a tier the tuning file forgot.
            if (s.MaterialTier > 0)
                _ = StructurePolicy.TierMultiplierMilli(s.MaterialTier);
            if (s.AcquisitionPaths.Count == 0)
                throw new InvalidOperationException(
                    $"Structure '{s.StructureId}' names no acquisition path — a structure no path can produce can never appear on a board.");
            if (s.CoverPowerMilli < 0)
                throw new InvalidOperationException($"Structure '{s.StructureId}' has negative cover power.");
            if (s.CoverRadius < 0)
                throw new InvalidOperationException($"Structure '{s.StructureId}' has negative cover radius.");
            if (s.EntryStaminaMultiplierMilli < 0)
                throw new InvalidOperationException($"Structure '{s.StructureId}' has a negative entry stamina multiplier.");
            if (s.VisionRangeTiles < 0)
                throw new InvalidOperationException($"Structure '{s.StructureId}' has a negative vision range.");
            // loam-relics-and-wonders `wonder-structure` §Design 5: the enforcement that keeps the
            // World/Multiverse + DefensePower/AuraGrant/EmpireBuff reservation real — a bad row is a
            // startup error, never a silently-accepted inert value.
            if ((s.WonderScope is null) != (s.WonderRarity is null))
                throw new InvalidOperationException(
                    $"Structure '{s.StructureId}' must set both WonderScope and WonderRarity, or neither.");
            if (s.WonderScope is not (null or WonderScope.Sector or WonderScope.Empire))
                throw new InvalidOperationException(
                    $"Structure '{s.StructureId}' authors WonderScope={s.WonderScope}, which is reserved and " +
                    "not yet registered (loam-relics-and-wonders `wonder-structure` §Design 5).");
            if (s.WonderScope is not null && s.WonderEffects.Count == 0)
                throw new InvalidOperationException(
                    $"Structure '{s.StructureId}' is a Wonder (WonderScope set) but names no WonderEffectDef.");
            if (s.WonderScope is null && s.WonderEffects.Count > 0)
                throw new InvalidOperationException(
                    $"Structure '{s.StructureId}' names WonderEffects but has no WonderScope — not a Wonder.");
            foreach (var effect in s.WonderEffects)
            {
                if (effect.Kind != WonderEffectKind.LoamGenerationRate)
                    throw new InvalidOperationException(
                        $"Structure '{s.StructureId}' authors WonderEffectKind={effect.Kind}, which is reserved " +
                        "and not yet registered (loam-relics-and-wonders `wonder-structure` §Design 5).");
                if (effect.Scope != s.WonderScope)
                    throw new InvalidOperationException(
                        $"Structure '{s.StructureId}' has a WonderEffectDef whose Scope ({effect.Scope}) does not " +
                        $"match its own WonderScope ({s.WonderScope}).");
                if (effect.ValueMilli < 0)
                    throw new InvalidOperationException($"Structure '{s.StructureId}' has a negative WonderEffectDef.ValueMilli.");
            }
            if (s.WonderEffects.Select(e => (e.Kind, e.Scope)).Distinct().Count() != s.WonderEffects.Count)
                throw new InvalidOperationException($"Structure '{s.StructureId}' has a duplicate (Kind, Scope) WonderEffectDef pair.");
            // loam-relics-and-wonders `wonder-build-flow` §Design 2: the RelicCost/WonderScope
            // pairing — a Wonder must cost at least one relic; a non-Wonder must cost none.
            if (s.WonderScope is not null && s.RelicCost < 1)
                throw new InvalidOperationException($"Structure '{s.StructureId}' is a Wonder but names no relic cost.");
            if (s.WonderScope is null && s.RelicCost != 0)
                throw new InvalidOperationException($"Structure '{s.StructureId}' names a relic cost but has no WonderScope.");
        }

        return structures;
    }
}
