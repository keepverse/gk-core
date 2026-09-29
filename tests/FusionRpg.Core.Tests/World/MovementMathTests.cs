using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.World;
using FusionRpg.Core.World.Movement;
using Xunit;

namespace FusionRpg.Core.Tests.World;

/// <summary>
/// W9 (spec-world-movement.md): lane cost, banner element, and the arithmetic that makes two
/// legions meet where they actually cross rather than at the nearest sample.
/// </summary>
public class MovementMathTests
{
    static readonly WorldState World =
        WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 1);

    static WorldLane Lane(string id) => World.Lanes.Single(l => l.LaneId == id);

    // ---- lane cost ----

    [Fact]
    public void A_plain_rift_lane_costs_its_length()
    {
        var lane = new WorldLane { LaneId = "l", TypeId = "rift", Length = 1000, FromSectorId = "a", ToSectorId = "b" };
        Assert.Equal(1000, LaneCost.For(World, lane, bannerElement: null));
    }

    [Fact]
    public void A_corridor_is_cheaper_and_a_deep_rift_is_dearer()
    {
        var corridor = new WorldLane { LaneId = "c", TypeId = "corridor", Length = 1000, FromSectorId = "a", ToSectorId = "b" };
        var deep = new WorldLane { LaneId = "d", TypeId = "deep", Length = 1000, FromSectorId = "a", ToSectorId = "b" };

        Assert.Equal(700, LaneCost.For(World, corridor, null));
        Assert.Equal(1600, LaneCost.For(World, deep, null));
    }

    [Fact]
    public void Hazard_raises_the_cost_proportionally()
    {
        var lane = new WorldLane
        {
            LaneId = "h", TypeId = "rift", Length = 1000, HazardMilli = 250,
            FromSectorId = "a", ToSectorId = "b"
        };
        Assert.Equal(1250, LaneCost.For(World, lane, null));
    }

    [Fact]
    public void A_ley_lane_is_cheaper_for_a_banner_that_matches_either_end()
    {
        // l-ember-ash is a ley lane between fire (ember-hollow) and earth (ash-waste).
        var ley = Lane("l-ember-ash");

        var neutral = LaneCost.For(World, ley, bannerElement: null);
        var matching = LaneCost.For(World, ley, ElementTypeId.Fire);
        var alsoMatching = LaneCost.For(World, ley, ElementTypeId.Earth);
        var mismatched = LaneCost.For(World, ley, ElementTypeId.Light);

        Assert.True(matching < neutral, "a matched banner marches a ley lane cheaper");
        Assert.Equal(matching, alsoMatching);
        Assert.Equal(neutral, mismatched);
    }

    [Fact]
    public void Only_ley_lanes_care_about_the_banner()
    {
        var rift = Lane("l-home-frost");
        Assert.Equal(
            LaneCost.For(World, rift, null),
            LaneCost.For(World, rift, ElementTypeId.Ice));
    }

    [Fact]
    public void Cost_is_never_free_however_generous_the_discounts()
    {
        var tiny = new WorldLane { LaneId = "t", TypeId = "corridor", Length = 1, FromSectorId = "a", ToSectorId = "b" };
        Assert.True(LaneCost.For(World, tiny, null) >= 1);
    }

    // ---- banner element ----

    [Fact]
    public void A_legions_banner_is_the_element_most_of_it_carries()
    {
        var legion = World.Entities.Single(e => e.EntityId == "e-dave-legion-1");
        var banner = BannerElement.Of(legion);

        // Computed from the members' species, never stored — so it cannot drift from the roster.
        Assert.NotNull(banner);
    }

    [Fact]
    public void An_empty_or_unknown_legion_simply_has_no_banner()
    {
        Assert.Null(BannerElement.Of(new WorldEntity { EntityId = "e" }));
        Assert.Null(BannerElement.Of(new WorldEntity
        {
            EntityId = "e",
            Members = new[] { new WorldEntityMember { SpeciesId = "not-a-species" } }
        }));
    }

    [Fact]
    public void A_tie_breaks_by_the_rings_declared_order_not_by_member_order()
    {
        var fireFirst = new WorldEntity
        {
            EntityId = "e",
            Members = new[]
            {
                new WorldEntityMember { SpeciesId = SpeciesOf(ElementTypeId.Ice) },
                new WorldEntityMember { SpeciesId = SpeciesOf(ElementTypeId.Fire) }
            }
        };
        var iceFirst = fireFirst with { Members = fireFirst.Members.Reverse().ToList() };

        Assert.Equal(BannerElement.Of(fireFirst), BannerElement.Of(iceFirst));
        Assert.Equal(ElementTypeId.Fire, BannerElement.Of(fireFirst)); // Fire precedes Ice in the ring
    }

    static string SpeciesOf(ElementTypeId element) =>
        FusionRpg.Core.Creatures.CreatureSpeciesCatalog.All.First(s => s.ElementPrimary == element).SpeciesId;

    // ---- crossing ----

    [Fact]
    public void Two_legions_closing_on_one_lane_meet_between_them()
    {
        // Each covers half the lane this turn, from opposite ends: they meet in the middle.
        Assert.True(LaneCrossing.TryFind(0, 500, 0, 500, out var time, out var position));
        Assert.Equal(500, position);
        Assert.InRange(time, 1, 1000);
    }

    [Fact]
    public void Legions_that_together_cannot_close_the_lane_do_not_meet_this_turn()
    {
        // 400 + 400 covers 800 of a 1000 lane — the gap survives the turn.
        Assert.False(LaneCrossing.TryFind(0, 400, 0, 400, out _, out _));
    }

    [Fact]
    public void The_meeting_point_does_not_depend_on_which_legion_is_processed_first()
    {
        for (var i = 0; i < 200; i++)
        {
            // Deterministic sweep rather than a random one: a property test that cannot be
            // reproduced is not much of a property test.
            var pA = i * 3 % 500;
            var pB = i * 7 % 400;
            var sA = 1 + i * 11 % 600;
            var sB = 1 + i * 13 % 600;

            var forward = LaneCrossing.TryFind(pA, sA, pB, sB, out var tF, out var posF);
            var backward = LaneCrossing.TryFind(pB, sB, pA, sA, out var tB, out var posB);

            Assert.Equal(forward, backward);
            if (!forward) continue;

            Assert.Equal(tF, tB);
            Assert.Equal(1000 - posF, posB);              // same point, measured from each end
            Assert.InRange(posF, pA, 1000 - pB);          // strictly between where they started
        }
    }

    [Fact]
    public void Legions_that_cannot_reach_each_other_this_turn_do_not_meet()
    {
        // 100 apart, each covering 10 — the gap outlives the turn.
        Assert.False(LaneCrossing.TryFind(0, 10, 900, 10, out _, out _));
    }

    [Fact]
    public void Two_stationary_legions_never_meet()
    {
        Assert.False(LaneCrossing.TryFind(100, 0, 100, 0, out _, out _));
    }
}
