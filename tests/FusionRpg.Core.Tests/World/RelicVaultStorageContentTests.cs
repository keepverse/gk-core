using FusionRpg.Core.World;
using FusionRpg.Core.World.Movement;
using FusionRpg.Core.World.Siege;
using FusionRpg.Core.World.Turn;
using Xunit;

namespace FusionRpg.Core.Tests.World;

/// <summary>
/// Task 4B.2 (empire-inventory-surfaces `storage-content` §Design 1 + §Design 3): the hand-authored
/// `relic-vault` ItemStorage row at `gk-data/packs/fusion/data/seed/structures/store/relic-vault.json` — catalog loads
/// it, `SectorItemCapacity` reads it, `BuildResolver` offers it. Reads the REAL committed corpus
/// (this row IS committed content, so no temp corpus and no `Configure` — nothing to restore).
/// </summary>
public class RelicVaultStorageContentTests
{
    static WorldSector Sector(params WorldSlot[] slots) => new()
    {
        SectorId = "s", TypeId = "stable", OwnerFactionId = "dave", Slots = slots,
    };

    // SlotTypeId "vault" is SlotKind.Vault's own catalog id (SlotTypeCatalog has no Vault const —
    // only Seat/Wildland/Rootbed get named consts — so the literal is used, same as "market" in
    // WorldTemplateCatalog.TwoHearths.cs).
    static WorldSlot Vault(int index, string? structureId = null, int? construction = null) => new()
    {
        SlotIndex = index, SlotTypeId = "vault",
        StructureId = structureId, ConstructionTurnsRemaining = construction,
    };

    static WorldSlot Wildland(int index, string? structureId = null) => new()
    {
        SlotIndex = index, SlotTypeId = SlotTypeCatalog.WildlandSlotTypeId, StructureId = structureId,
    };

    static WorldEntity Founder(long carriedLoam) => new()
    {
        EntityId = "legion", Kind = WorldEntityKind.Legion, OwnerFactionId = "dave",
        AtSectorId = "s", CarriedLoam = carriedLoam,
        Members = new[] { new WorldEntityMember { SpeciesId = "grunt" } },
    };

    static WorldState WorldWith(WorldSector sector, WorldEntity entity) => new()
    {
        Factions = new[] { new WorldFaction { FactionId = "dave", Kind = WorldFactionKind.Player, Name = "Dave" } },
        Sectors = new[] { sector },
        Entities = new[] { entity },
    };

    static WorldCommand Build(string structureId, int slotIndex = 0) => new()
    {
        CommanderId = "dave", CommandId = "b1", Kind = WorldCommandKinds.Build,
        EntityId = "legion", SectorId = "s", SlotIndex = slotIndex, StructureId = structureId,
    };

    [Fact]
    public void The_catalog_loads_relic_vault_as_ItemStorage_with_bonus_20_on_Vault()
    {
        Assert.True(StructureCatalog.IsKnown("relic-vault"));
        var def = StructureCatalog.Get("relic-vault");
        Assert.Equal(StructureKind.ItemStorage, def.Kind);
        Assert.Equal(20, def.ItemStorageCapacityBonus);
        Assert.Equal(SlotKind.Vault, def.RequiredSlotKind);
        Assert.Equal("Relic Vault", def.Name);
        // Planner-assigned magnitudes (§Design 1) + the two-axes-never-bleed zero.
        Assert.Equal(200, def.Cost);
        Assert.Equal(2, def.BuildTurns);
        Assert.Equal(0, def.CapacityBonus);
        Assert.Equal(0, def.RelicCost);
        Assert.Null(def.WonderScope);
        Assert.Contains(AcquisitionPath.Built, def.AcquisitionPaths);
    }

    [Fact]
    public void One_built_relic_vault_reports_EffectiveCapacity_20()
    {
        Assert.Equal(20, SectorItemCapacity.EffectiveCapacity(Sector(Vault(0, "relic-vault"))));
    }

    [Fact]
    public void An_under_construction_unknown_or_empty_slot_contributes_0()
    {
        var sector = Sector(
            Vault(0, "relic-vault", construction: 2),
            Vault(1, "not-a-real-structure"),
            Vault(2, null));
        Assert.Equal(0, SectorItemCapacity.EffectiveCapacity(sector));
    }

    [Fact]
    public void A_loam_Storage_granary_contributes_0_to_the_item_axis()
    {
        // The two-axes-never-bleed rule (§Design 1: CapacityBonus 0 on the vault, Storage kind on
        // the granary): a granary moves loam only, a vault moves items only.
        Assert.Equal(0, SectorItemCapacity.EffectiveCapacity(Sector(Wildland(0, "granary"))));
        Assert.Equal(20, SectorItemCapacity.EffectiveCapacity(Sector(
            Wildland(0, "granary"),
            Vault(1, "relic-vault"))));
    }

    [Fact]
    public void A_build_order_for_relic_vault_on_an_empty_Vault_slot_starts()
    {
        var world = WorldWith(Sector(Vault(0)), Founder(carriedLoam: 200));
        var report = new TurnReport();

        var result = BuildResolver.Run(world, new[] { Build("relic-vault") }, report, "snapshot");

        Assert.Equal("relic-vault", result.Sectors[0].Slots[0].StructureId);
        Assert.Equal(2, result.Sectors[0].Slots[0].ConstructionTurnsRemaining);
        Assert.Equal(0, result.Entities[0].CarriedLoam);
        Assert.Contains(report.Entries, e => e.Detail == "build.started:relic-vault");
    }

    [Fact]
    public void A_build_order_for_relic_vault_on_a_Wildland_slot_is_refused()
    {
        var world = WorldWith(Sector(Wildland(0)), Founder(carriedLoam: 200));
        var report = new TurnReport();

        var result = BuildResolver.Run(world, new[] { Build("relic-vault") }, report, "snapshot");

        Assert.Null(result.Sectors[0].Slots[0].StructureId);
        Assert.Contains(report.Entries, e =>
            e.Kind == TurnReportKinds.CommandDropped && e.Detail.StartsWith("build.wrong-slot-kind"));
    }
}
