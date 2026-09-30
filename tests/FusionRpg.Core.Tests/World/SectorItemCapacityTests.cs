using FusionRpg.Core.World;
using FusionRpg.Core.World.Loam;
using FusionRpg.Core.World.Siege;
using FusionRpg.Core.World.StructureSeed;
using Xunit;
using FusionRpg.TestSupport;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.World;

/// <summary>
/// Task 1.2a (`sector-storage`, spec-sector-storage.md §Design 1-3): the 6th
/// <see cref="StructureKind"/> value, <see cref="StructureDef.ItemStorageCapacityBonus"/>, and
/// <see cref="SectorItemCapacity.EffectiveCapacity"/>.
/// </summary>
public class SectorItemCapacityTests
{
    static string RealCorpusRoot() => Path.Combine(KeepverseRoots.Content(), "data", "seed", "structures");

    static void RestoreRealCorpus() => StructureCatalog.Configure(StructureCorpus.Load(RealCorpusRoot()));

    static WorldSector SectorWith(params WorldSlot[] slots) =>
        new() { SectorId = "s", TypeId = "stable", OwnerFactionId = "f1", Slots = slots };

    static WorldSlot Slot(int index, string? structureId, int? construction = null) =>
        new() { SlotIndex = index, StructureId = structureId, ConstructionTurnsRemaining = construction };

    static string RowJson(string id, string kind, long capacityBonus, long? itemBonus) =>
        $$"""
        {
          "id": "{{id}}",
          "name": "{{id}}",
          "anchor": {
            "structureId": "{{id}}", "family": "test", "role": "Store", "roleSecondary": "none",
            "requiredSlotKind": "Wildland", "elementPrimary": "none", "elementSecondary": "none",
            "tempo": "none", "reach": "melee", "strengthBand": "rubble", "rarity": "sprout",
            "traits": [], "costProfile": "cheap", "targetPreference": "none", "variants": [],
            "acquisitionPaths": ["built"], "footprint": "one-cell", "coverTier": "none",
            "controlPoint": true, "obstacleVerbs": [], "reason": "test"
          },
          "_provenance": {"source": "AUTHORED", "citation": "test"},
          "magnitudes": {
            "structureKind": "{{kind}}", "cost": 0, "yieldMultiplierMilli": 1000,
            "buildTurns": 0, "capacityBonus": {{capacityBonus}},
            {{(itemBonus is null ? "" : $"\"itemStorageCapacityBonus\": {itemBonus},")}}
            "flatYieldPerTurn": 0,
            "constructRubbleCost": 0, "constructIronworkCost": 0, "materialTier": 0,
            "blocksMovement": false, "blocksLineOfFire": false, "obstacleKind": "None",
            "coverPowerMilli": 0, "coverRadius": 0, "entryStaminaMultiplierMilli": 1000,
            "visionRangeTiles": null
          }
        }
        """;

    /// <summary>The rows the JSON document below always held, as an **in-memory** corpus: the corpus is
    /// data, so a fixture that needs its own rows does not write a corpus file to `%TEMP%`
    /// (testing-standard R1 — the `temp-corpus-write` rule refuses the write). `StructureCorpus.FromJson`
    /// is the file format's own in-memory entrance: one parser, so a literal and a committed file cannot
    /// drift apart.</summary>
    static StructureCorpus Corpus(params string[] rows) =>
        StructureCorpus.FromJson("""
        {
          "kind": "structure-anchor",
          "_meta": {"partition": "Store"},
          "entries": [
        """ + string.Join(",\n", rows) + """
          ]
        }
        """);

    [Fact]
    public void StructureKind_has_exactly_six_members_with_ItemStorage_newest()
    {
        // CLOSED vocabulary pin (validation-ssot): StructureKind is an enum the code owns and a
        // human changes — pinning 6 is correct here, unlike a population count over seed content.
        var members = Enum.GetValues<StructureKind>();
        Assert.Equal(6, members.Length);
        Assert.Equal(
            new[] { StructureKind.LoamSource, StructureKind.Storage, StructureKind.Yield, StructureKind.Refinery, StructureKind.Obstacle, StructureKind.ItemStorage },
            members);
    }

    [Fact]
    public void EffectiveCapacity_sums_additively_across_N_active_ItemStorage_structures()
    {
        var corpus = Corpus(
            RowJson("test-depot-a", "ItemStorage", 0, 20),
            RowJson("test-depot-b", "ItemStorage", 0, 30));
        try
        {
            StructureCatalog.Configure(corpus);
            var sector = SectorWith(Slot(0, "test-depot-a"), Slot(1, "test-depot-b"));
            Assert.Equal(50, SectorItemCapacity.EffectiveCapacity(sector));
        }
        finally
        {
            RestoreRealCorpus();
        }
    }

    [Fact]
    public void EffectiveCapacity_ignores_under_construction_unknown_and_empty_slots()
    {
        var corpus = Corpus(RowJson("test-depot-a", "ItemStorage", 0, 20));
        try
        {
            StructureCatalog.Configure(corpus);
            var sector = SectorWith(
                Slot(0, "test-depot-a"),
                Slot(1, "test-depot-a", construction: 2),
                Slot(2, "not-a-real-structure"),
                Slot(3, null));
            Assert.Equal(20, SectorItemCapacity.EffectiveCapacity(sector));
        }
        finally
        {
            RestoreRealCorpus();
        }
    }

    [Fact]
    public void Item_and_loam_capacity_axes_move_independently()
    {
        // The owner's reason for a distinct kind/field (Open question #2): neither axis may bleed
        // into the other. A granary moves loam only; a depot moves items only.
        var corpus = Corpus(
            RowJson("test-granary", "Storage", 300, 0),
            RowJson("test-depot", "ItemStorage", 0, 25));
        try
        {
            StructureCatalog.Configure(corpus);
            var sector = SectorWith(Slot(0, "test-granary"), Slot(1, "test-depot"));

            Assert.Equal(25, SectorItemCapacity.EffectiveCapacity(sector));
            Assert.Equal(
                checked(LoamPolicy.LoamCapacity + 300 + StructurePolicy.CapacityGrowthFor(0)),
                LoamPhases.EffectiveCapacity(sector));

            // Each axis alone: granary-only sector has zero item capacity; depot-only sector adds
            // nothing to loam's cap.
            Assert.Equal(0, SectorItemCapacity.EffectiveCapacity(SectorWith(Slot(0, "test-granary"))));
            Assert.Equal(
                checked(LoamPolicy.LoamCapacity + StructurePolicy.CapacityGrowthFor(0)),
                LoamPhases.EffectiveCapacity(SectorWith(Slot(0, "test-depot"))));
        }
        finally
        {
            RestoreRealCorpus();
        }
    }

    [Fact]
    public void A_row_without_itemStorageCapacityBonus_parses_to_zero()
    {
        // Optional at parse time: a magnitudes block that predates the field loads as 0.
        var corpus = Corpus(RowJson("test-legacy", "Storage", 10, null));
        try
        {
            StructureCatalog.Configure(corpus);
            Assert.Equal(0, StructureCatalog.Get("test-legacy").ItemStorageCapacityBonus);
        }
        finally
        {
            RestoreRealCorpus();
        }
    }

    [Fact]
    public void Every_non_ItemStorage_loadable_row_defaults_to_zero_item_bonus()
    {
        // Byte-identity proof without pinning a population count: whatever the corpus holds today,
        // no row outside the ItemStorage axis opts into it, so each loads exactly as before plus a
        // default 0. Gated on Kind (not a universal zero): `relic-vault` (task 4B.2,
        // empire-inventory-surfaces `storage-content`) is the first ItemStorage row and carries 20 —
        // its value is pinned by RelicVaultStorageContentTests, not here. A second future
        // ItemStorage row passes this test untouched.
        try
        {
            var corpus = StructureCorpus.Load(RealCorpusRoot());
            foreach (var row in corpus.Rows.Where(r => r.IsCatalogLoadable && r.Magnitudes!.StructureKind != nameof(StructureKind.ItemStorage)))
                Assert.Equal(0, row.Magnitudes!.ItemStorageCapacityBonus);

            StructureCatalog.Configure(corpus);
            foreach (var def in StructureCatalog.All.Where(d => d.Kind != StructureKind.ItemStorage))
                Assert.Equal(0, def.ItemStorageCapacityBonus);
        }
        finally
        {
            RestoreRealCorpus();
        }
    }

    [Fact]
    public void Validate_refuses_a_negative_ItemStorageCapacityBonus()
    {
        var bad = new[]
        {
            new StructureDef
            {
                StructureId = "bad-item-bonus", Name = "A", RequiredSlotKind = SlotKind.Wildland,
                Kind = StructureKind.ItemStorage, ItemStorageCapacityBonus = -1,
                AcquisitionPaths = new[] { AcquisitionPath.Built },
            }
        };
        Assert.Contains("item storage capacity", Assert.Throws<InvalidOperationException>(
            () => StructureCatalog.Validate(bad)).Message, StringComparison.OrdinalIgnoreCase);
    }
}
