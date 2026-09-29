using FusionRpg.Core.World;
using FusionRpg.Core.World.Growth;
using FusionRpg.Core.World.Loam;
using Xunit;

namespace FusionRpg.Core.Tests.World.Loam;

/// <summary>L7 acceptance (spec-loam-calc.md #1): a hand-built fixture, workable on paper.</summary>
public class LoamProductionTests
{
    static WorldSlot Rootbed(int index) => new() { SlotIndex = index, SlotTypeId = SlotTypeCatalog.RootbedSlotTypeId };
    static WorldSlot NonSource(int index) => new() { SlotIndex = index, SlotTypeId = SlotTypeCatalog.WildlandSlotTypeId };

    [Fact]
    public void One_rootbed_produces_exactly_the_seep_constant()
    {
        var sector = new WorldSector { SectorId = "s", OwnerFactionId = "f1", Slots = new[] { Rootbed(0) } };
        Assert.Equal(LoamPolicy.SeepPerTurn, LoamProduction.For(sector));
    }

    [Fact]
    public void Two_rootbeds_produce_double_and_a_non_source_slot_adds_nothing()
    {
        var sector = new WorldSector
        {
            SectorId = "s", OwnerFactionId = "f1",
            Slots = new[] { Rootbed(0), Rootbed(1), NonSource(2) }
        };
        Assert.Equal(LoamPolicy.SeepPerTurn * 2, LoamProduction.For(sector));
    }

    [Fact]
    public void Reversing_slot_order_changes_no_answer()
    {
        var forward = new WorldSector
        {
            SectorId = "s", OwnerFactionId = "f1",
            Slots = new[] { Rootbed(0), NonSource(1), Rootbed(2) }
        };
        var reversed = forward with { Slots = forward.Slots.Reverse().ToList() };

        Assert.Equal(LoamProduction.For(forward), LoamProduction.For(reversed));
    }

    [Fact]
    public void An_unowned_sector_produces_nothing_even_with_a_rootbed()
    {
        var sector = new WorldSector { SectorId = "s", OwnerFactionId = null, Slots = new[] { Rootbed(0) } };
        Assert.Equal(0, LoamProduction.For(sector));
    }

    [Fact]
    public void The_belief_overload_produces_nothing_for_an_unowned_sector_even_with_a_rootbed()
    {
        // The truth side's own G-B guard (`An_unowned_sector_produces_nothing_even_with_a_rootbed`)
        // has no belief-side twin — a mutation pass (2026-08-23, loam-texture Checkpoint 10) found
        // the belief overload's identical guard could be deleted outright with nothing noticing.
        Assert.Equal(0, LoamProduction.For(null, new[] { SlotTypeCatalog.RootbedSlotTypeId }));
    }

    [Fact]
    public void The_belief_overload_agrees_with_the_truth_overload()
    {
        var sector = new WorldSector
        {
            SectorId = "s", OwnerFactionId = "f1",
            Slots = new[] { Rootbed(0), NonSource(1) }
        };

        Assert.Equal(
            LoamProduction.For(sector),
            LoamProduction.For(sector.OwnerFactionId, sector.Slots.Select(sl => sl.SlotTypeId)));
    }

    // ---- world-map W55: the development-yield term, additive on the truth side only -------------

    [Fact]
    public void A_sector_at_DevelopmentLevel_zero_is_byte_identical_to_before_this_task()
    {
        var sector = new WorldSector { SectorId = "s", OwnerFactionId = "f1", Slots = new[] { Rootbed(0) } };
        Assert.Equal(LoamPolicy.SeepPerTurn, LoamProduction.For(sector));
    }

    [Fact]
    public void A_developed_sector_yields_more_than_an_undeveloped_one_by_exactly_the_development_term()
    {
        var undeveloped = new WorldSector { SectorId = "s", OwnerFactionId = "f1", Slots = new[] { Rootbed(0) } };
        var developed = undeveloped with { DevelopmentLevel = 5 };

        var expectedTerm = DevelopmentYield.For(5, LoamPolicy.DevelopmentYieldPerLevel);
        Assert.Equal(LoamProduction.For(undeveloped) + expectedTerm, LoamProduction.For(developed));
    }

    [Fact]
    public void A_developed_but_sourceless_sector_still_yields_the_development_term_alone()
    {
        var sector = new WorldSector { SectorId = "s", OwnerFactionId = "f1", DevelopmentLevel = 3, Slots = Array.Empty<WorldSlot>() };
        Assert.Equal(DevelopmentYield.For(3, LoamPolicy.DevelopmentYieldPerLevel), LoamProduction.For(sector));
    }

    [Fact]
    public void An_unowned_developed_sector_still_yields_nothing_G_B_governs_the_whole_sum()
    {
        var sector = new WorldSector { SectorId = "s", OwnerFactionId = null, DevelopmentLevel = 5, Slots = new[] { Rootbed(0) } };
        Assert.Equal(0, LoamProduction.For(sector));
    }

    /// <summary>
    /// A genuine, known gap this task found and deliberately did not fix: the belief-side overload
    /// (`FrontierRulesPolicy.cs`'s own AI production estimate) has no `DevelopmentLevel` parameter at
    /// all, so it cannot model this term — the AI underestimates a developed sector's own production
    /// once `DevelopmentLevel` is genuinely nonzero. Adding a parameter there is a real, further
    /// wiring gap, not this task's own stated scope (`W55`'s Files list names `LoamProduction.cs`
    /// only), so it is proven here rather than silently left for a future session to rediscover.
    /// </summary>
    [Fact]
    public void The_belief_overload_disagrees_with_the_truth_overload_once_development_is_nonzero_a_known_gap()
    {
        var sector = new WorldSector
        {
            SectorId = "s", OwnerFactionId = "f1", DevelopmentLevel = 5,
            Slots = new[] { Rootbed(0) }
        };

        var truth = LoamProduction.For(sector);
        var belief = LoamProduction.For(sector.OwnerFactionId, sector.Slots.Select(sl => sl.SlotTypeId));

        Assert.NotEqual(truth, belief);
        Assert.Equal(truth - DevelopmentYield.For(5, LoamPolicy.DevelopmentYieldPerLevel), belief);
    }
}
