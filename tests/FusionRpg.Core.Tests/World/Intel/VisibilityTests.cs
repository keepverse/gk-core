using FusionRpg.Core.World;
using FusionRpg.Core.World.Intel;
using Xunit;

namespace FusionRpg.Core.Tests.World.Intel;

/// <summary>
/// W18 (spec-world-intel.md §Seeing, now): what a faction can see this turn.
///
/// Presence plus one lane, doubled by the `scout` stance. Adjacency is a *glimpse* — you know who
/// is there and roughly how big, never what is buried in the ground. You have to stand on a sector
/// to survey it, which is what keeps claiming a rich sector a gamble rather than a lookup.
/// </summary>
public class VisibilityTests
{
    static WorldState World() => WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 1);

    static IReadOnlyDictionary<string, SectorSight> Dave(WorldState w) => Visibility.SeenBy(w, "dave");
    static IReadOnlyDictionary<string, SectorSight> Wild(WorldState w) => Visibility.SeenBy(w, "wild");

    static WorldState Place(WorldState w, string entityId, string sectorId) => w with
    {
        Entities = w.Entities
            .Select(e => e.EntityId == entityId
                ? e with { AtSectorId = sectorId, OnLaneId = null, OnLaneTowardSectorId = null, LaneProgressMilli = 0 }
                : e)
            .ToList()
    };

    static WorldState OnLane(WorldState w, string entityId, string laneId, string toward) => w with
    {
        Entities = w.Entities
            .Select(e => e.EntityId == entityId
                ? e with { AtSectorId = null, OnLaneId = laneId, OnLaneTowardSectorId = toward, LaneProgressMilli = 400 }
                : e)
            .ToList()
    };

    static WorldState Stance(WorldState w, string entityId, string stance) => w with
    {
        Entities = w.Entities.Select(e => e.EntityId == entityId ? e with { Stance = stance } : e).ToList()
    };

    [Fact]
    public void You_survey_what_you_stand_in()
    {
        Assert.Equal(SectorSight.Full, Dave(World())["homeworld"]);
    }

    [Fact]
    public void You_survey_what_you_own_even_with_nobody_in_it()
    {
        // The legion marches off; the homeworld is still administered and still fully known.
        var empty = Place(World(), "e-dave-legion-1", "ember-hollow");

        Assert.Equal(SectorSight.Full, Dave(empty)["homeworld"]);
    }

    [Fact]
    public void One_lane_out_is_a_glimpse_and_two_is_nothing()
    {
        var sight = Dave(World());

        Assert.Equal(SectorSight.Glimpse, sight["ember-hollow"]);
        Assert.Equal(SectorSight.Glimpse, sight["frost-mire"]);
        Assert.Equal(SectorSight.None, sight["ash-waste"]);
        Assert.Equal(SectorSight.None, sight["black-gate"]);
    }

    [Fact]
    public void Scouting_doubles_how_far_you_see()
    {
        var scouting = Stance(World(), "e-dave-legion-1", "scout");
        var sight = Dave(scouting);

        Assert.Equal(SectorSight.Glimpse, sight["ash-waste"]);      // two lanes out, now visible
        Assert.Equal(SectorSight.None, sight["black-gate"]);        // three is still too far
        Assert.Equal(SectorSight.Full, sight["homeworld"]);         // and a glimpse never demotes a survey
    }

    [Fact]
    public void A_severed_lane_carries_no_sight()
    {
        var world = World();
        var cut = world with
        {
            Lanes = world.Lanes
                .Select(l => l.LaneId == "l-home-ember" ? l with { State = LaneState.Severed } : l)
                .ToList()
        };

        var sight = Dave(cut);
        Assert.Equal(SectorSight.None, sight["ember-hollow"]);
        Assert.Equal(SectorSight.Glimpse, sight["frost-mire"]);   // the other way round is still open
    }

    [Fact]
    public void A_force_on_a_lane_sees_both_ends_of_it()
    {
        var marching = OnLane(World(), "e-wild-pack-1", "l-black-verdant", "verdant-shelf");
        var sight = Wild(marching);

        Assert.Equal(SectorSight.Glimpse, sight["ash-waste"]);
        Assert.Equal(SectorSight.Glimpse, sight["verdant-shelf"]);
    }

    /// <summary>
    /// Guards defend a slot, not the ground — the same rule that makes marching past one free. The
    /// homeworld is the only sector in `first-light` the wild pack cannot already glimpse from
    /// ash-waste, so it is the only place this can be shown in isolation.
    /// </summary>
    [Fact]
    public void A_slot_guard_watches_its_own_vein_and_nothing_else()
    {
        var world = World();

        WorldState WithWildEntityAtHome(WorldEntityKind kind) => WorldValidation.Validate(world with
        {
            Entities = world.Entities
                .Append(world.Entities.Single(e => e.EntityId == "e-wild-pack-1") with
                {
                    EntityId = "e-wild-x-1", Kind = kind, AtSectorId = "homeworld"
                })
                .OrderBy(e => e.EntityId, StringComparer.Ordinal)
                .ToList()
        });

        Assert.Equal(SectorSight.None, Wild(WithWildEntityAtHome(WorldEntityKind.Guard))["homeworld"]);

        // The same entity that is not a guard does see the ground it is standing on.
        Assert.Equal(SectorSight.Full, Wild(WithWildEntityAtHome(WorldEntityKind.Warband))["homeworld"]);
    }

    [Fact]
    public void A_faction_that_holds_nothing_and_stands_nowhere_sees_nothing()
    {
        // "nobody" is a faction id nobody plays, rather than Zomboss: since 2026-08-22 the template gives him a
        // warband and a Seat, so he is no longer anybody's example of knowing nothing.
        Assert.All(Visibility.SeenBy(World(), "nobody").Values, v => Assert.Equal(SectorSight.None, v));
    }

    /// <summary>
    /// Visibility is the union of turn start and turn end. Two consequences fall out with no special
    /// case: a legion that marches *through* a sector reports on it, and a faction driven out of one
    /// remembers it as of this turn rather than as of whenever it last happened to look.
    /// </summary>
    [Fact]
    public void You_see_everywhere_you_were_this_turn_not_only_where_you_ended_up()
    {
        var start = World();                                             // legion at homeworld
        var end = Place(start, "e-dave-legion-1", "ash-waste");          // and it ends up out east

        var sight = Visibility.SeenBy(start, end, "dave");

        Assert.Equal(SectorSight.Full, sight["homeworld"]);
        Assert.Equal(SectorSight.Full, sight["ash-waste"]);
        Assert.Equal(SectorSight.Glimpse, sight["black-gate"]);          // next door to where it arrived
    }

    [Fact]
    public void Losing_a_sector_still_counts_as_having_seen_it_this_turn()
    {
        var start = World();
        var lost = start with
        {
            Sectors = start.Sectors
                .Select(s => s.SectorId == "homeworld" ? s with { OwnerFactionId = "zomboss" } : s)
                .ToList(),
            Entities = start.Entities
                .Select(e => e.EntityId == "e-dave-legion-1" ? e with { AtSectorId = "ember-hollow" } : e)
                .ToList()
        };

        // Driven out and dispossessed — but you were there when it happened.
        Assert.Equal(SectorSight.Full, Visibility.SeenBy(start, lost, "dave")["homeworld"]);
    }

    [Fact]
    public void Every_sector_gets_an_answer_in_stable_order()
    {
        var sight = Dave(World());

        Assert.Equal(World().Sectors.Count, sight.Count);
        Assert.Equal(
            World().Sectors.Select(s => s.SectorId).OrderBy(s => s, StringComparer.Ordinal),
            sight.Keys);
    }

    [Fact]
    public void The_answer_does_not_depend_on_the_order_entities_were_written_in()
    {
        var world = World();
        var reversed = world with { Entities = world.Entities.Reverse().ToList() };

        Assert.Equal(Dave(world), Dave(reversed));
    }
}
