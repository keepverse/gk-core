using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.World;
using FusionRpg.Core.World.Ai;
using FusionRpg.Core.World.Intel;
using FusionRpg.Core.World.Movement;
using FusionRpg.Core.World.Topology;
using FusionRpg.Core.Tests.World.Topology;
using Xunit;

namespace FusionRpg.Core.Tests.World.Ai;

/// <summary>
/// W29 (spec-ai-commander.md §Two graphs, and not confusing them).
///
/// The supply lens asks "could this lane hold an empire together"; the march lens asks "can an army
/// put its feet on it". They differ by one rule and the difference matters: a deep rift carries no
/// grain and carries legions perfectly well.
///
/// **Every one of `first-light`'s six lanes carries supply**, so the two lenses are identical there
/// and a policy built on the wrong one would pass every test in this repo. That is why these run
/// against shapes chosen to expose it — the same reason `GraphShapes` exists at all.
/// </summary>
public class MarchGraphTests
{
    /// <summary>Two sectors joined by a rift no supply can cross. The shape `first-light` lacks.</summary>
    static WorldState AcrossARift() => Retype(GraphShapes.From("a-b"), "l-a-b", "deep");

    static WorldState Retype(WorldState world, string laneId, string typeId) => world with
    {
        Lanes = world.Lanes.Select(l => l.LaneId == laneId ? l with { TypeId = typeId } : l).ToList()
    };

    static IWorldView Blind(WorldState world) => new BelievedWorldView(world, "nobody");

    // ---- the two lenses disagree, and that is the point -----------------------------------

    [Fact]
    public void A_deep_rift_carries_no_supply_and_carries_an_army_fine()
    {
        var world = AcrossARift();

        Assert.Empty(LaneGraph.Build(world).Edges);                       // supply: no road at all
        Assert.Equal(2, MarchGraph.Of(Blind(world)).Edges.Count);         // march: there and back
    }

    [Fact]
    public void A_temporal_current_is_marchable_one_way_only()
    {
        var world = Retype(GraphShapes.From("a-b"), "l-a-b", "one-way");
        var march = MarchGraph.Of(Blind(world));

        Assert.Contains(march.Edges, e => e.FromSectorId == "a" && e.ToSectorId == "b");
        Assert.DoesNotContain(march.Edges, e => e.FromSectorId == "b" && e.ToSectorId == "a");
    }

    [Fact]
    public void Everything_the_supply_lens_keeps_the_march_lens_keeps_too()
    {
        // The march lens is strictly more permissive. If that ever stops being true, one of the two
        // has grown a rule the other should have had.
        var world = GraphShapes.Barbell();
        var supply = LaneGraph.Build(world).Edges.ToHashSet();

        Assert.Subset(MarchGraph.Of(Blind(world)).Edges.ToHashSet(), supply);
    }

    // ---- what stops an army, stops it in both -----------------------------------------------

    // Asserted on the lens itself rather than through a view, and the distinction is real: these are
    // facts about the *ground*, and a faction that cannot see the lane does not know them. Testing
    // them through belief would contradict the fog test below — which is exactly what happened, and
    // is why they moved here.

    [Fact]
    public void A_severed_lane_is_no_road_and_no_march()
    {
        var world = GraphShapes.Sever(AcrossARift(), "l-a-b");
        Assert.Empty(LaneGraph.Build(world, null, LaneLens.March).Edges);
    }

    [Fact]
    public void A_shut_gate_bars_the_march_too()
    {
        // These are the refusals MarchResolver makes, so a route planned through one is a route the
        // engine would drop — a policy that planned it would file an order and watch it vanish.
        var world = GraphShapes.From("a-b");
        var shut = world with
        {
            Lanes = world.Lanes.Select(l => l with { TypeId = "gated", GateKeyId = "key-of-ash" }).ToList()
        };

        Assert.Empty(LaneGraph.Build(shut, null, LaneLens.March).Edges);

        // The same lane with nothing barring it is just a lane an army can walk.
        var open = world with
        {
            Lanes = world.Lanes.Select(l => l with { TypeId = "gated" }).ToList()
        };
        Assert.NotEmpty(LaneGraph.Build(open, null, LaneLens.March).Edges);
    }

    // ---- fog reaches into route planning -----------------------------------------------------

    [Fact]
    public void A_faction_that_has_not_scouted_a_ley_lane_does_not_expect_its_discount()
    {
        // The consequence of correction 4: LaneCost's ley check reads a sector's *climate*, which is
        // something you learn by looking. Unscouted, the discount is invisible and the march is
        // over-priced — so an army plans with what it knows, which is the whole idea.
        var lane = new WorldLane
        {
            LaneId = "l-a-b", FromSectorId = "a", ToSectorId = "b",
            TypeId = "ley", Length = 1000, Width = 1000
        };

        var scouted = LaneCost.For(lane, ElementTypeId.Fire, _ => ElementTypeId.Fire);
        var unseen = LaneCost.For(lane, ElementTypeId.Fire, _ => null);

        Assert.True(scouted < unseen, "a known ley lane should be the cheaper one");
        Assert.Equal(unseen * LaneCost.LeyDiscountMilli / 1000, scouted);
    }

    [Fact]
    public void A_lane_the_faction_cannot_see_is_treated_as_open_and_therefore_marchable()
    {
        // Optimism, on purpose. IWorldView masks an unseen lane's state, so a faction routes over a
        // bridge that is down and learns by arriving. A pessimistic AI would never explore.
        var world = GraphShapes.Sever(GraphShapes.From("a-b"), "l-a-b");

        Assert.Empty(LaneGraph.Build(world).Edges);                   // the truth: no road
        Assert.NotEmpty(MarchGraph.Of(Blind(world)).Edges);           // the belief: looks fine
    }

    [Fact]
    public void The_climate_lookup_answers_from_belief_and_not_from_the_world()
    {
        // The wiring the ley test above skips: it proves LaneCost honours a lookup, this proves
        // MarchGraph hands it the *believed* one. A mutant that returned null for every sector
        // survived the whole suite until this existed.
        var world = WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 1);
        var seen = world with { Intel = IntelRecorder.Observe(world, world, turn: 0) };

        var dave = MarchGraph.ClimateOf(new BelievedWorldView(seen, "dave"));

        // Not `dave` before observing: the template seeds the *player's* opening belief at world
        // build time, so he is never truly blind. Somebody with no belief at all is.
        var blind = MarchGraph.ClimateOf(Blind(seen));

        // Dave's legion stands at the homeworld, so ember-hollow next door is glimpsed — and a
        // glimpse from next door is enough to tell you what a place is made of.
        Assert.Equal(ElementTypeId.Fire, dave("ember-hollow"));

        // Two lanes out he has no idea, and a stranger has none anywhere.
        Assert.Null(dave("black-gate"));
        Assert.Null(blind("ember-hollow"));
    }

    // ---- ordering ------------------------------------------------------------------------------

    [Fact]
    public void Reversing_the_input_changes_no_answer()
    {
        var world = AcrossARift();

        Assert.Equal(
            MarchGraph.Of(Blind(world)).Edges,
            MarchGraph.Of(Blind(GraphShapes.Reversed(world))).Edges);
    }
}
