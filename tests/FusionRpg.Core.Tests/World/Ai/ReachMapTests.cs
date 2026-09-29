using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Tests.World.Topology;
using FusionRpg.Core.World;
using FusionRpg.Core.World.Ai;
using FusionRpg.Core.World.Intel;
using FusionRpg.Core.World.Movement;
using Xunit;

namespace FusionRpg.Core.Tests.World.Ai;

/// <summary>
/// W32 (spec-ai-commander.md §ReachMap and the believed frontier): how many turns away, for *this*
/// legion.
///
/// Per entity rather than per faction, because two armies do not measure the same map — a matching
/// banner walks a ley lane cheaper — and because that is where fog reaches route planning.
/// </summary>
public class ReachMapTests
{
    static WorldState Line(int laneLength = 600) => GraphShapes.From(laneLength, "a-b", "b-c", "c-d") with
    {
        Factions = new[]
        {
            new WorldFaction { FactionId = "dave", Kind = WorldFactionKind.Player, Name = "Dave" }
        }
    };

    static WorldEntity Legion(string at, string stance = "march") => new()
    {
        EntityId = "e-dave-1",
        Kind = WorldEntityKind.Legion,
        OwnerFactionId = "dave",
        AtSectorId = at,
        Stance = stance,
        MovementRemaining = MovementPolicy.BudgetFor(stance),
        Members = new[] { new WorldEntityMember { SpeciesId = "normalzombie", Level = 1, Hp = 100 } }
    };

    static WorldState With(WorldState world, params WorldEntity[] entities) => world with
    {
        Entities = entities.OrderBy(e => e.EntityId, StringComparer.Ordinal).ToList()
    };

    static IWorldView View(WorldState world) =>
        new BelievedWorldView(world with { Intel = IntelRecorder.Observe(world, world, turn: 0) }, "dave");

    static IReadOnlyDictionary<string, int> Reach(WorldState world, WorldEntity entity) =>
        ReachMap.For(View(With(world, entity)), entity);

    // ---- turns ----------------------------------------------------------------------------

    [Fact]
    public void Where_you_stand_is_no_turns_away()
    {
        Assert.Equal(0, Reach(Line(), Legion("a"))["a"]);
    }

    [Fact]
    public void A_march_that_does_not_fill_a_turn_still_costs_one()
    {
        // 600 of a 1000-point budget: you arrive part-way through, and the turn is still spent.
        // Rounding this down would let a legion cross the map for free in fractions.
        Assert.Equal(1, Reach(Line(600), Legion("a"))["b"]);
    }

    [Fact]
    public void Two_lanes_that_together_overrun_a_turn_cost_two()
    {
        var reach = Reach(Line(600), Legion("a"));

        Assert.Equal(2, reach["c"]);   // 1200 over a 1000 budget
        Assert.Equal(2, reach["d"]);   // 1800 — still inside the second turn
    }

    [Fact]
    public void Scouting_at_half_speed_puts_everything_twice_as_far_away()
    {
        var marching = Reach(Line(600), Legion("a"));
        var scouting = Reach(Line(600), Legion("a", MovementPolicy.Scout));

        Assert.Equal(1, marching["b"]);
        Assert.Equal(2, scouting["b"]);   // the same 600 against a 500 budget
    }

    [Fact]
    public void A_legion_dug_in_reaches_nowhere_and_does_not_divide_by_zero()
    {
        // `hold` gives up movement entirely. Asking for its reach must answer "nothing", not throw:
        // a garrison is an ordinary thing for a policy to ask about.
        // Hold-allowance (spec-hold-allowance.md §Design 2): the refill is nonzero now, so this
        // asserts Empty for a holder WITH act money — the regression the stance gate exists to
        // prevent. Without the first line this test would pass on a 0 budget either way.
        Assert.True(MovementPolicy.BudgetFor(MovementPolicy.Hold) > 0);
        Assert.Empty(Reach(Line(), Legion("a", MovementPolicy.Hold)));
    }

    // ---- routes ----------------------------------------------------------------------------

    [Fact]
    public void The_cheapest_way_round_is_the_one_reported()
    {
        // The lane ids decide which route is *found* first — `l-a-b` sorts before `l-a-d` — so the
        // direct road has to be deliberately expensive for this to mean anything. A version where
        // both routes cost the same passes whether or not Dijkstra relaxes at all, which is what the
        // first draft of this test did.
        var ring = GraphShapes.From(400, "a-b", "b-c", "c-d") with { Factions = Line().Factions };
        ring = ring with
        {
            Lanes = ring.Lanes
                .Append(new WorldLane
                {
                    LaneId = "l-a-d", FromSectorId = "a", ToSectorId = "d",
                    TypeId = "rift", Length = 5000, Width = 1000
                })
                .OrderBy(l => l.LaneId, StringComparer.Ordinal)
                .ToList()
        };

        // Round the houses is 1200 (two turns); the direct lane is 5000 (five). Taking the first
        // route discovered would report five.
        Assert.Equal(2, Reach(ring, Legion("a"))["d"]);
    }

    [Fact]
    public void Somewhere_with_no_route_is_absent_rather_than_far_away()
    {
        var islands = GraphShapes.From(600, "a-b", "y-z") with { Factions = Line().Factions };
        var reach = Reach(islands, Legion("a"));

        Assert.False(reach.ContainsKey("y"));
        Assert.False(reach.ContainsKey("z"));
    }

    [Fact]
    public void A_legion_mid_stride_measures_from_where_it_is_going()
    {
        // Routing from the sector it set off from would offer it a road it has already left.
        var world = Line(600);
        var walking = Legion("a") with
        {
            AtSectorId = null,
            OnLaneId = "l-a-b",
            OnLaneTowardSectorId = "b",
            LaneProgressMilli = 300
        };

        Assert.Equal(0, Reach(world, walking)["b"]);
    }

    // ---- the banner, and what a faction knows about it ----------------------------------------

    [Fact]
    public void A_matching_banner_walks_a_ley_lane_cheaper_when_it_knows_the_ground()
    {
        // The ley discount is per legion, which is the whole reason reach is per entity — and it
        // only applies where the faction has *seen* the climate that grants it.
        // 1200 is chosen so the discount straddles a turn boundary and the saving is *visible*:
        //   plain:      1200 × 900‰ (ley type) = 1080  -> 2 turns against a 1000 budget
        //   discounted: 1080 × 800‰ (banner)   =  864  -> 1 turn
        // A length where both round to the same number would make this assertion pass whether or not
        // the discount applied at all, which is worth nothing.
        var world = Line(1200) with
        {
            Sectors = Line().Sectors
                .Select(s => s.SectorId == "b" ? s with { Climate = ElementTypeId.Fire } : s)
                .ToList(),
            Lanes = Line(1200).Lanes
                .Select(l => l.LaneId == "l-a-b" ? l with { TypeId = "ley" } : l)
                .ToList()
        };

        // A legion whose members are fire-aligned, standing next door so it can see b's climate.
        var fiery = Legion("a") with
        {
            Members = new[] { new WorldEntityMember { SpeciesId = FireSpecies, Level = 1, Hp = 100 } }
        };

        Assert.Equal(1, ReachMap.For(View(With(world, fiery)), fiery)["b"]);
        Assert.Equal(2, ReachMap.For(View(With(world, Legion("a"))), Legion("a"))["b"]);
    }

    /// <summary>A species whose primary element is Fire, so a banner can match a Fire climate.</summary>
    static readonly string FireSpecies = FusionRpg.Core.Creatures.CreatureSpeciesCatalog.All
        .First(s => s.ElementPrimary == ElementTypeId.Fire).SpeciesId;

    // ---- fog ------------------------------------------------------------------------------------

    [Fact]
    public void A_lane_the_faction_cannot_see_is_walked_optimistically()
    {
        // Severed two lanes out, beyond sight. The legion plans straight through it and finds out on
        // arrival — a planner that assumed the worst about everything it could not see would sit at
        // home forever.
        var world = GraphShapes.Sever(Line(600), "l-c-d") with { Factions = Line().Factions };

        Assert.True(Reach(world, Legion("a")).ContainsKey("d"));
    }

    // ---- ordering --------------------------------------------------------------------------------

    [Fact]
    public void Reversing_the_world_changes_no_distance()
    {
        var world = Line(600);
        var reversed = world with
        {
            Sectors = world.Sectors.Reverse().ToList(),
            Lanes = world.Lanes.Reverse().ToList()
        };

        Assert.Equal(
            Reach(world, Legion("a")).OrderBy(kv => kv.Key, StringComparer.Ordinal),
            Reach(reversed, Legion("a")).OrderBy(kv => kv.Key, StringComparer.Ordinal));
    }
}
