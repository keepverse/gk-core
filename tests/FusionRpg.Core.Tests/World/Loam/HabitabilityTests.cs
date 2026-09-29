using FusionRpg.Core.World;
using FusionRpg.Core.World.Loam;
using Xunit;

namespace FusionRpg.Core.Tests.World.Loam;

/// <summary>L8 acceptance (spec-loam-calc.md #6): the owner's settlement rule.</summary>
public class HabitabilityTests
{
    [Fact]
    public void A_sector_with_a_rootbed_is_habitable()
    {
        var sector = new WorldSector
        {
            SectorId = "s", TypeId = "stable",
            Slots = new[] { new WorldSlot { SlotIndex = 0, SlotTypeId = SlotTypeCatalog.RootbedSlotTypeId } }
        };
        Assert.True(Habitability.For(sector));
    }

    [Fact]
    public void The_same_sector_shape_with_no_rootbed_is_not()
    {
        var sector = new WorldSector
        {
            SectorId = "s", TypeId = "stable",
            Slots = new[] { new WorldSlot { SlotIndex = 0, SlotTypeId = SlotTypeCatalog.WildlandSlotTypeId } }
        };
        Assert.False(Habitability.For(sector));
    }

    [Fact]
    public void A_sector_with_no_slots_at_all_is_never_habitable()
    {
        var sector = new WorldSector { SectorId = "s", TypeId = "stable" };
        Assert.False(Habitability.For(sector));
    }

    [Fact]
    public void A_seat_with_an_active_waystation_is_habitable_even_with_no_rootbed_anywhere()
    {
        // Matches spec-loam-texture.md's Prospecting wording exactly: "rootbed/well/waystation-bearing
        // slots" — a waystation is StructureKind.LoamSource though it sits on a Seat, not a Rootbed.
        var sector = new WorldSector
        {
            SectorId = "s", TypeId = "stable",
            Slots = new[] { new WorldSlot { SlotIndex = 0, SlotTypeId = "seat", StructureId = "waystation" } }
        };
        Assert.True(Habitability.For(sector));
    }

    [Fact]
    public void An_unknown_structure_id_never_makes_ground_habitable()
    {
        // Coverage found this branch of IsSource's short-circuit (StructureCatalog.IsKnown returning
        // false) had never actually run — every prior fixture used a real, known structure id.
        var sector = new WorldSector
        {
            SectorId = "s", TypeId = "stable",
            Slots = new[] { new WorldSlot { SlotIndex = 0, SlotTypeId = SlotTypeCatalog.WildlandSlotTypeId, StructureId = "not-a-real-structure" } }
        };
        Assert.False(Habitability.For(sector));
    }

    [Fact]
    public void A_granary_never_makes_ground_habitable_on_its_own()
    {
        // A mutation pass (2026-08-23, loam-texture Checkpoint 10) found that "any known structure
        // counts as a source" survived undetected — a Granary is StructureKind.Storage, not
        // LoamSource, and must never substitute for a real one.
        var sector = new WorldSector
        {
            SectorId = "s", TypeId = "stable",
            Slots = new[] { new WorldSlot { SlotIndex = 0, SlotTypeId = SlotTypeCatalog.WildlandSlotTypeId, StructureId = "granary" } }
        };
        Assert.False(Habitability.For(sector));
    }

    [Fact]
    public void The_belief_overload_agrees_with_the_truth_overload()
    {
        var sector = new WorldSector
        {
            SectorId = "s", TypeId = "stable",
            Slots = new[] { new WorldSlot { SlotIndex = 0, SlotTypeId = SlotTypeCatalog.RootbedSlotTypeId } }
        };
        Assert.Equal(
            Habitability.For(sector),
            Habitability.For(sector.Slots.Select(sl => (sl.SlotTypeId, sl.StructureId, sl.ConstructionTurnsRemaining))));
    }
}
