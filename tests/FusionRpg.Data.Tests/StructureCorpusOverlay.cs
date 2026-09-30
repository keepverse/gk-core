using FusionRpg.Core.World.StructureSeed;
using FusionRpg.TestSupport;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Data.Tests;

/// <summary>
/// The shipped structure corpus plus extra rows held **in memory** — the seam for a suite that needs
/// one synthetic depot or wonder row. The corpus is data, not a file-system contract: a suite that
/// needs one extra row must not materialise a directory of the developer's SSD to get it. The write is
/// **removed**, never written-and-deleted (owner ruling, and the reason
/// <c>docs/contributing/testing-standard.md</c> exists).
///
/// <para><b>What this replaced.</b> Six fixtures in this project copied all 28 files of
/// <c>gk-data/packs/fusion/data/seed/structures</c> into <c>%TEMP%/&lt;suite&gt;-test-{guid}</c>, wrote one hand-authored
/// row beside the copy, and pointed <see cref="StructureCorpus.Load"/> at the copy — because the
/// loader took a directory and the shipped 25 rows carry no <c>ItemStorage</c> capacity. The copy is
/// gone: <see cref="LoadWithRows"/> is <see cref="StructureCorpus.Load"/> plus
/// <see cref="StructureCorpus.WithRows"/>, the in-memory entrance added for exactly this
/// (<c>data-test-substrate</c> BU8).</para>
///
/// <para><b>The superset property is preserved exactly.</b> Every shipped row is still present, so a
/// suite running concurrently against the static <c>StructureCatalog</c> reads the same answer for a
/// real structure id as it would with the real corpus configured — the only additions are inert test
/// ids nothing else references. That is load-bearing, not incidental: the catalog is static and the
/// <c>StructureCatalogSwap</c> collection serializes only the fixtures that replace it, never the
/// suites that merely read it.</para>
/// </summary>
internal static class StructureCorpusOverlay
{
    /// <summary>The committed corpus root — read, never written. The one place this project names it.</summary>
    public static string RealCorpusRoot() => Path.Combine(KeepverseRoots.Content(), "data", "seed", "structures");

    /// <summary>
    /// A freshly loaded real corpus with <paramref name="extraRows"/> appended, ready for
    /// <c>StructureCatalog.Configure</c>. Fresh per call on purpose: these fixtures are constructed
    /// once per test method, and a cached corpus would accumulate duplicate ids and fail
    /// <c>StructureCatalog.Validate</c>.
    /// </summary>
    public static StructureCorpus LoadWithRows(params StructureCorpusRow[] extraRows) =>
        StructureCorpus.Load(RealCorpusRoot()).WithRows(extraRows);

    /// <summary>
    /// An inert <c>ItemStorage</c> depot granting <paramref name="itemStorageCapacityBonus"/> item
    /// slots. Sector item storage has no base allowance (`SectorItemCapacity.EffectiveCapacity` sums
    /// structure rows only), so a storage-sourced test needs one live row.
    /// </summary>
    public static StructureCorpusRow Depot(string id, long itemStorageCapacityBonus) =>
        Row(id, structureKind: "ItemStorage", requiredSlotKind: "Wildland",
            itemStorageCapacityBonus: itemStorageCapacityBonus);

    /// <summary>
    /// An inert Wonder row: a <c>Storage</c>-kind structure carrying <c>relicCost</c>, a wonder
    /// scope/rarity pair, and one <c>LoamGenerationRate</c> effect. Pinned to the slot kind the
    /// caller passes, because a build is only legal on the slot kind it names.
    /// </summary>
    public static StructureCorpusRow Wonder(
        string id,
        string requiredSlotKind,
        string wonderScope,
        string wonderRarity,
        long relicCost,
        int buildTurns = 0,
        long effectValueMilli = 0) =>
        Row(id, structureKind: "Storage", requiredSlotKind: requiredSlotKind, buildTurns: buildTurns,
            wonderScope: wonderScope, wonderRarity: wonderRarity, relicCost: relicCost,
            wonderEffectValueMilli: effectValueMilli);

    /// <summary>
    /// The one shape every row here shares: identity only (the anchor is ordinals, never numbers),
    /// with every magnitude at its structural default except the two fields the caller names. This
    /// mirrors what the hand-written corpus JSON said, field for field.
    /// </summary>
    static StructureCorpusRow Row(
        string id,
        string structureKind,
        string requiredSlotKind,
        long capacityBonus = 0,
        long itemStorageCapacityBonus = 0,
        int buildTurns = 0,
        string? wonderScope = null,
        string? wonderRarity = null,
        long? relicCost = null,
        long wonderEffectValueMilli = 0) =>
        new(
            StructureId: id,
            Name: id,
            Role: "Store",
            RequiredSlotKind: requiredSlotKind,
            StrengthBand: "rubble",
            AcquisitionPaths: new[] { "built" },
            ControlPoint: true,
            Magnitudes: new StructureMagnitudes(
                StructureKind: structureKind,
                Cost: 0,
                YieldMultiplierMilli: 1000,
                BuildTurns: buildTurns,
                CapacityBonus: capacityBonus,
                ItemStorageCapacityBonus: itemStorageCapacityBonus,
                FlatYieldPerTurn: 0,
                ConstructRubbleCost: 0,
                ConstructIronworkCost: 0,
                MaterialTier: 0,
                BlocksMovement: false,
                BlocksLineOfFire: false,
                ObstacleKind: "None",
                CoverPowerMilli: 0,
                CoverRadius: 0,
                EntryStaminaMultiplierMilli: 1000,
                VisionRangeTiles: null,
                ContainerId: null,
                WonderScope: wonderScope,
                WonderRarity: wonderRarity,
                WonderEffects: wonderScope is null
                    ? null
                    : new[] { new WonderEffectMagnitude("LoamGenerationRate", wonderScope, wonderEffectValueMilli) },
                RelicCost: relicCost));
}
