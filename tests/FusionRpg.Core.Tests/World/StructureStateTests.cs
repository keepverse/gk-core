using System.Linq;
using System.Numerics;
using FusionRpg.Core.World;
using FusionRpg.Core.World.Loam;
using FusionRpg.Core.World.Turn;
using Xunit;

namespace FusionRpg.Core.Tests.World;

/// <summary>
/// base-defense `structure-state` (spec-structure-state.md): a structure gains combat identity —
/// material tier, hit points derived from the SECTOR's DevelopmentLevel through the one power ladder,
/// repair, capacity-halt (F12) — without moving a single existing world golden. Every conditional
/// canonical row (`slot-hp`/`slot-depletion`) mirrors the shipped `faction-scope` precedent exactly.
/// </summary>
public class StructureStateTests
{
    static WorldState World(params WorldSector[] sectors) => new() { TemplateId = "t", Sectors = sectors };

    static StructureDef Def(string id, int tier, long cost = 100) => new()
    {
        StructureId = id, Name = id, Kind = StructureKind.LoamSource,
        RequiredSlotKind = SlotKind.Rootbed, Cost = cost, MaterialTier = tier
    };

    [Fact]
    public void World_goldens_are_byte_identical_at_default()
    {
        // Null StructureHp, zero SlotDepletionMilli -- both defaults -- must emit nothing.
        var withDefaults = World(new WorldSector
        {
            SectorId = "s1",
            Slots = new[] { new WorldSlot { SlotIndex = 0, SlotTypeId = "rootbed" } }
        });
        var withoutFields = World(new WorldSector
        {
            SectorId = "s1",
            Slots = new[] { new WorldSlot { SlotIndex = 0, SlotTypeId = "rootbed" } }
        });

        Assert.Equal(WorldCanonical.Write(withoutFields), WorldCanonical.Write(withDefaults));
        Assert.DoesNotContain("slot-hp", WorldCanonical.Write(withDefaults), StringComparison.Ordinal);
        Assert.DoesNotContain("slot-depletion", WorldCanonical.Write(withDefaults), StringComparison.Ordinal);
    }

    [Fact]
    public void Canonical_gains_exactly_one_row_per_damaged_slot()
    {
        var world = World(new WorldSector
        {
            SectorId = "s1",
            Slots = new[]
            {
                new WorldSlot { SlotIndex = 0, SlotTypeId = "rootbed", StructureHp = 500 },
                new WorldSlot { SlotIndex = 1, SlotTypeId = "rootbed" }, // undamaged
                new WorldSlot { SlotIndex = 2, SlotTypeId = "rootbed", SlotDepletionMilli = 250 },
            }
        });
        var text = WorldCanonical.Write(world);

        Assert.Single(text.Split('\n'), l => l.StartsWith("slot-hp\ts1\t0\t500", StringComparison.Ordinal));
        Assert.DoesNotContain("slot-hp\ts1\t1\t", text, StringComparison.Ordinal);
        Assert.Single(text.Split('\n'), l => l.StartsWith("slot-depletion\ts1\t2\t250", StringComparison.Ordinal));
    }

    [Fact]
    public void Slot_rows_are_emitted_in_slot_index_order()
    {
        var world = World(new WorldSector
        {
            SectorId = "s1",
            Slots = new[]
            {
                new WorldSlot { SlotIndex = 0, SlotTypeId = "rootbed", StructureHp = 10 },
                new WorldSlot { SlotIndex = 1, SlotTypeId = "rootbed", StructureHp = 20 },
                new WorldSlot { SlotIndex = 2, SlotTypeId = "rootbed", StructureHp = 30 },
            }
        });
        var hpLines = WorldCanonical.Write(world).Split('\n').Where(l => l.StartsWith("slot-hp", StringComparison.Ordinal)).ToList();
        Assert.Equal(new[] { "slot-hp\ts1\t0\t10", "slot-hp\ts1\t1\t20", "slot-hp\ts1\t2\t30" }, hpLines);
    }

    [Fact]
    public void Repair_cost_is_zero_at_full_health_and_at_zero_max_hp()
    {
        Assert.Equal(0, StructurePolicy.RepairCost(cost: 1000, maxHp: 500, currentHp: 500));
        Assert.Equal(0, StructurePolicy.RepairCost(cost: 1000, maxHp: 0, currentHp: 0));
    }

    [Fact]
    public void Repair_cost_is_proportional()
    {
        // Half-damaged costs half of a full rebuild * ratio (600‰ shipped default).
        var half = StructurePolicy.RepairCost(cost: 1000, maxHp: 1000, currentHp: 500);
        var full = StructurePolicy.RepairCost(cost: 1000, maxHp: 1000, currentHp: 0);
        Assert.Equal(full / 2, half);
        Assert.Equal(600, full); // 1000 * 1000 * 600 / 1000 / 1000
    }

    [Fact]
    public void Repair_cost_overflows_loudly()
    {
        Assert.Throws<OverflowException>(() =>
            StructurePolicy.RepairCost(cost: long.MaxValue / 2, maxHp: long.MaxValue / 2, currentHp: 0));
    }

    [Fact]
    public void Repair_cost_divides_by_1000_last()
    {
        // Large enough that an early divide (e.g. folding /maxHp and /1000 into one combined divisor)
        // would lose precision or overflow that combined divisor differently than the real two-step
        // order does -- not so large that cost*missing*ratio itself overflows long (~9.2e18).
        const long cost = 1_000_003L;
        const long maxHp = 3_000_001;
        const long currentHp = 1_000_000;
        var missing = maxHp - currentHp;

        var actual = StructurePolicy.RepairCost(cost, maxHp, currentHp);

        BigInteger reference = (BigInteger)cost * missing * 600 / maxHp / 1000;
        Assert.Equal((long)reference, actual);
    }

    [Fact]
    public void Destroyed_structure_leaves_a_ruined_slot()
    {
        var world = World(new WorldSector
        {
            SectorId = "s1",
            Slots = new[]
            {
                new WorldSlot
                {
                    SlotIndex = 0, SlotTypeId = "rootbed", State = SlotState.Intact,
                    StructureId = "well", StructureHp = 10, ConstructionTurnsRemaining = null
                }
            }
        });

        var result = BattleApplication.ApplySlotResults(world, "s1", new[]
        {
            new SlotOutcome { SlotIndex = 0, StructureHp = 0, StructureDestroyed = true, HeldByFactionId = "dave" }
        });

        var slot = result.Sectors.Single(s => s.SectorId == "s1").Slots.Single(sl => sl.SlotIndex == 0);
        Assert.Equal(SlotState.Ruined, slot.State);
        Assert.Null(slot.StructureId);
        Assert.Null(slot.StructureHp);
        Assert.Null(slot.ConstructionTurnsRemaining);
        Assert.Equal("dave", slot.OwnerFactionId);
    }

    [Fact]
    public void Surviving_structure_persists_its_remaining_hp()
    {
        var world = World(new WorldSector
        {
            SectorId = "s1",
            Slots = new[] { new WorldSlot { SlotIndex = 0, SlotTypeId = "rootbed", StructureId = "well" } }
        });

        var result = BattleApplication.ApplySlotResults(world, "s1", new[]
        {
            new SlotOutcome { SlotIndex = 0, StructureHp = 42, StructureDestroyed = false, HeldByFactionId = "dave" }
        });

        var slot = result.Sectors.Single(s => s.SectorId == "s1").Slots.Single(sl => sl.SlotIndex == 0);
        Assert.Equal(SlotState.Intact, slot.State);
        Assert.Equal("well", slot.StructureId);
        Assert.Equal(42, slot.StructureHp);
    }

    [Fact]
    public void Negative_material_tier_throws_at_catalog_load()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            StructureCatalog.Validate(new[] { Def("bad", tier: -1) }));
        Assert.Contains("material tier", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_tier_with_no_multiplier_row_throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            StructureCatalog.Validate(new[] { Def("unauthored-tier", tier: 99) }));
    }

    [Fact]
    public void Tier_zero_is_indestructible_and_every_pre_siege_loam_row_is_tier_zero()
    {
        // 2026-09-06: the "moat" row (siege-construction §5) is the FIRST shipped row with a real
        // material tier (1) — every OTHER shipped row still predates any notion of a siege and stays
        // indestructible, which is what this test's own name always meant to guarantee.
        Assert.Equal(0, StructureDef.MaxHpOf(Def("stone", tier: 0), developmentLevel: 50));
        Assert.All(StructureCatalog.All.Where(s => s.StructureId != "moat"), s => Assert.Equal(0, s.MaterialTier));
        Assert.Equal(1, StructureCatalog.Get("moat").MaterialTier);
    }

    [Fact]
    public void Iron_wall_has_more_hp_than_stone_wall_at_the_same_development()
    {
        var stone = StructureDef.MaxHpOf(Def("stone", tier: 1), developmentLevel: 10);
        var iron = StructureDef.MaxHpOf(Def("iron", tier: 2), developmentLevel: 10);
        Assert.True(stone > 0);
        Assert.True(iron > stone, $"iron ({iron}) must exceed stone ({stone}) at the same development level");
    }

    [Fact]
    public void Hp_scales_with_sector_development_level()
    {
        var low = StructureDef.MaxHpOf(Def("stone", tier: 1), developmentLevel: 1);
        var high = StructureDef.MaxHpOf(Def("stone", tier: 1), developmentLevel: 20);
        Assert.True(high > low, $"HP at development 20 ({high}) must exceed development 1 ({low})");
    }

    [Fact]
    public void There_is_no_hard_ceiling_on_investment()
    {
        // Run development to a large index; structure HP keeps rising -- the escape valve stage 3
        // that makes a fixed board legal under the no-hard-ceilings rule.
        var atFifty = StructureDef.MaxHpOf(Def("stone", tier: 1), developmentLevel: 50);
        var atFiveHundred = StructureDef.MaxHpOf(Def("stone", tier: 1), developmentLevel: 500);
        Assert.True(atFiveHundred > atFifty);
    }

    [Fact]
    public void Capacity_grows_enough_that_a_new_slot_produces()
    {
        var baseSector = new WorldSector { SectorId = "s1", DevelopmentLevel = 0 };
        var developed = baseSector with { DevelopmentLevel = 4 };

        var baseCap = LoamPhases.EffectiveCapacity(baseSector);
        var developedCap = LoamPhases.EffectiveCapacity(developed);

        // F12: growth over 4 levels must cover at least one rootbed's own SeepPerTurn (the smallest
        // unit of production a new slot could add) -- an asserted invariant, not a hope.
        Assert.True(developedCap - baseCap >= LoamPolicy.SeepPerTurn,
            $"capacity grew by {developedCap - baseCap}, less than one rootbed's SeepPerTurn ({LoamPolicy.SeepPerTurn})");
    }

    [Fact]
    public void Capacity_halt_is_reversible_unlike_depletion()
    {
        Assert.True(StructurePolicy.IsHaltedByCapacity(stock: 300, effectiveCapacity: 300));
        Assert.False(StructurePolicy.IsHaltedByCapacity(stock: 299, effectiveCapacity: 300));
        // A capacity halt says nothing about the deposit -- building storage (raising
        // effectiveCapacity) un-halts it, which IsExhausted's own irreversible predicate never does.
        Assert.False(StructurePolicy.IsHaltedByCapacity(stock: 299, effectiveCapacity: 301));
    }

    [Fact]
    public void Exhaustion_is_irreversible_unlike_a_capacity_halt()
    {
        Assert.True(StructurePolicy.IsExhausted(1000));
        Assert.False(StructurePolicy.IsExhausted(999));
        Assert.False(StructurePolicy.IsExhausted(0));
    }

    [Fact]
    public void Blocks_line_of_fire_is_independent_of_blocks_movement()
    {
        var moat = new StructureDef { StructureId = "moat", Name = "Moat", BlocksMovement = true, BlocksLineOfFire = false };
        var smokeRuin = new StructureDef { StructureId = "ruin", Name = "Ruin", BlocksMovement = false, BlocksLineOfFire = true };
        Assert.True(moat.BlocksMovement);
        Assert.False(moat.BlocksLineOfFire);
        Assert.False(smokeRuin.BlocksMovement);
        Assert.True(smokeRuin.BlocksLineOfFire);
    }
}
