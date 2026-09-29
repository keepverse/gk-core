using FusionRpg.Core.World;
using FusionRpg.Core.World.Loam;
using FusionRpg.Core.World.LegionCargo;
using Xunit;

namespace FusionRpg.Core.Tests.World.LegionCargo;

/// <summary>
/// Task 1.1 (spec-legion-cargo.md §Design 1, §Tunables): capacity is <c>memberCount × tuning</c> —
/// pure Core math over a live count, plus the tuning-file loader. The all-members-vs-Bearer
/// divergence is proven here at the formula level and again at the SQL level by
/// <c>FusionRpg.Data.Tests.LegionCargo</c> (which counts real <c>rpg_world_entity_members</c> rows
/// with no role filter).
/// </summary>
public class LegionCargoCapacityTests
{
    static ScopedInventoryTuning TestTuning => new(
        SchemaVersion: 1, Version: 1, CargoWeightPerUnit: 100, CargoSlotsPerUnit: 2);

    [Fact]
    public void Loader_parses_the_shipped_tuning_shape()
    {
        var tuning = ScopedInventoryTuningLoader.Parse("""
            {
              "schemaVersion": 1,
              "version": 1,
              "cargoWeightPerUnit": 50,
              "cargoSlotsPerUnit": 2
            }
            """);

        Assert.Equal(1, tuning.SchemaVersion);
        Assert.Equal(1, tuning.Version);
        Assert.Equal(50, tuning.CargoWeightPerUnit);
        Assert.Equal(2, tuning.CargoSlotsPerUnit);
    }

    [Fact]
    public void Loader_rejects_a_missing_key_instead_of_defaulting()
    {
        Assert.Throws<ScopedInventoryTuningRejection>(() =>
            ScopedInventoryTuningLoader.Parse("""
                { "schemaVersion": 1, "version": 1, "cargoWeightPerUnit": 50 }
                """));
    }

    [Fact]
    public void Capacity_is_member_count_times_tuning()
    {
        ScopedInventoryPolicy.Configure(TestTuning);

        Assert.Equal(500L, ScopedInventoryPolicy.WeightCapacityFor(5));
        Assert.Equal(10, ScopedInventoryPolicy.SlotCapacityFor(5));
    }

    [Fact]
    public void An_empty_legion_carries_nothing()
    {
        ScopedInventoryPolicy.Configure(TestTuning);

        Assert.Equal(0L, ScopedInventoryPolicy.WeightCapacityFor(0));
        Assert.Equal(0, ScopedInventoryPolicy.SlotCapacityFor(0));
    }

    /// <summary>
    /// Capacity follows the configured tuning file, never a constant: reconfiguring changes the
    /// returned capacity with no code change. (The pre-Configure throw cannot be proven here —
    /// Configure is process-wide and sibling fixtures configure it — so this asserts the live
    /// binding instead, which is the property dependents actually rely on.)
    /// </summary>
    [Fact]
    public void Capacity_follows_the_configured_tuning_not_a_constant()
    {
        ScopedInventoryPolicy.Configure(TestTuning);
        Assert.Equal(500L, ScopedInventoryPolicy.WeightCapacityFor(5));

        ScopedInventoryPolicy.Configure(TestTuning with { CargoWeightPerUnit = 7 });
        try
        {
            Assert.Equal(35L, ScopedInventoryPolicy.WeightCapacityFor(5));
        }
        finally
        {
            ScopedInventoryPolicy.Configure(TestTuning);
        }
    }

    [Fact]
    public void Weight_capacity_overflow_throws_never_wraps()
    {
        ScopedInventoryPolicy.Configure(new ScopedInventoryTuning(1, 1, long.MaxValue / 2, 2));
        try
        {
            Assert.Throws<OverflowException>(() => ScopedInventoryPolicy.WeightCapacityFor(3));
        }
        finally
        {
            ScopedInventoryPolicy.Configure(TestTuning);
        }
    }

    /// <summary>
    /// The Locked-anchors divergence, proven at the formula level: a legion of 4 Fighters + 1
    /// Bearer has cargo capacity for all 5 members, while <c>LegionSupply</c>'s loam-carry rule
    /// sees only the 1 Bearer. The Data suite proves the same divergence through real SQL
    /// (<c>COUNT(*)</c> with no role filter); this test proves the two rules are deliberately
    /// independent numbers that can move without affecting each other — and touches nothing in
    /// <c>LegionSupply.cs</c> itself (spec Success criterion #5).
    /// </summary>
    [Fact]
    public void Capacity_counts_every_member_regardless_of_role_unlike_bearer_only_loam_carry()
    {
        ScopedInventoryPolicy.Configure(TestTuning);

        var entity = new WorldEntity
        {
            EntityId = "e-test",
            Kind = WorldEntityKind.Legion,
            OwnerFactionId = "dave",
            AtSectorId = "homeworld",
            Members = new WorldEntityMember[]
            {
                new() { SpeciesId = "s1", Role = WorldEntityMemberRole.Fighter },
                new() { SpeciesId = "s2", Role = WorldEntityMemberRole.Fighter },
                new() { SpeciesId = "s3", Role = WorldEntityMemberRole.Fighter },
                new() { SpeciesId = "s4", Role = WorldEntityMemberRole.Fighter },
                new() { SpeciesId = "s5", Role = WorldEntityMemberRole.Bearer },
            },
        };

        Assert.Equal(1, LegionSupply.BearerCount(entity));
        Assert.Equal(5, entity.Members.Count);
        Assert.Equal(500L, ScopedInventoryPolicy.WeightCapacityFor(entity.Members.Count));
        Assert.Equal(10, ScopedInventoryPolicy.SlotCapacityFor(entity.Members.Count));
    }
}
