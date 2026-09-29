using FusionRpg.Core.World;
using FusionRpg.Core.World.Intel;
using FusionRpg.Core.World.Turn;
using Xunit;

namespace FusionRpg.Core.Tests.World.Intel;

/// <summary>
/// The intel spec promises that "a legion that marches *through* a sector and out the far side
/// reports on it — anything else is absurd, you were standing in it."
///
/// Visibility spans the turn's start and end, which covers where a force set off from and where it
/// ended up. It does not, on its own, cover the ground in between.
/// </summary>
public class MarchedThroughTests
{
    /// <summary>
    /// `first-light` with a short ley lane, so one turn's budget carries a legion clean across
    /// ember-hollow and on to ash-waste. The stock map cannot do this — 560 + 900 outruns a turn —
    /// which is exactly why the gap would not have shown up on it.
    /// </summary>
    static WorldState FastRoad()
    {
        var world = WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 1);

        return WorldValidation.Validate(world with
        {
            Lanes = world.Lanes
                .Select(l => l.LaneId == "l-ember-ash" ? l with { Length = 300 } : l)
                .ToList(),
            // Move the wild pack out of the way so the march is not halted by zone of control.
            Entities = world.Entities
                .Select(e => e.EntityId == "e-wild-pack-1" ? e with { AtSectorId = "black-gate" } : e)
                .ToList()
        });
    }

    static WorldCommand March() => new()
    {
        CommanderId = "dave",
        CommandId = "m1",
        Kind = WorldCommandKinds.Move,
        EntityId = "e-dave-legion-1",
        LanePath = new[] { "l-home-ember", "l-ember-ash" }
    };

    [Fact]
    public void A_legion_that_marches_clean_through_a_sector_surveys_it()
    {
        var result = TurnEngine.Step(FastRoad(), new[] { March() }, seed: 1);

        var legion = result.World.Entities.Single(e => e.EntityId == "e-dave-legion-1");
        Assert.Equal("ash-waste", legion.AtSectorId);   // it really did cross both lanes

        var dave = result.World.Intel.Single(i => i.FactionId == "dave");
        var passedThrough = dave.Of("ember-hollow");

        Assert.NotNull(passedThrough);
        Assert.Equal(SectorSight.Full, passedThrough!.Detail);
        Assert.NotEmpty(passedThrough.Slots);
        Assert.Equal(result.World.CurrentTurn, passedThrough.LastSeenTurn);
    }

    [Fact]
    public void Walking_through_tells_you_what_is_buried_there()
    {
        // The whole reason a survey differs from a glimpse: ember-hollow's guarded veins are only
        // knowable from the ground, and a legion that walked over them knows.
        var result = TurnEngine.Step(FastRoad(), new[] { March() }, seed: 1);

        var ember = result.World.Intel.Single(i => i.FactionId == "dave").Of("ember-hollow")!;
        var truth = result.World.Sectors.Single(s => s.SectorId == "ember-hollow");

        Assert.Equal(truth.Slots.Count, ember.Slots.Count);
        Assert.Equal(
            truth.Slots.Where(s => s.GuardState == GuardState.Intact).Select(s => s.SlotIndex),
            ember.Slots.Where(s => s.GuardState == GuardState.Intact).Select(s => s.SlotIndex));
    }

    [Fact]
    public void Marching_through_still_leaves_the_far_side_a_glimpse_at_best()
    {
        // Passing through reveals the ground you crossed, not the country beyond it.
        //
        // The far side is black-gate rather than verdant-shelf since the 2026-08-22 reshape: Verdant
        // Shelf now hangs off Black Gate, two lanes past the end of this march, so it is not
        // glimpsed at all. That is the reshape working — the far corner stays dark.
        var result = TurnEngine.Step(FastRoad(), new[] { March() }, seed: 1);
        var dave = result.World.Intel.Single(i => i.FactionId == "dave");

        Assert.Equal(SectorSight.Glimpse, dave.Of("black-gate")!.Detail);
        Assert.Equal(SectorSight.Full, dave.Of("ash-waste")!.Detail);
        Assert.Null(dave.Of("verdant-shelf"));
    }

    [Fact]
    public void A_march_that_stops_short_reveals_only_what_it_reached()
    {
        // Same order on the stock map: the legion runs out of budget on the ley lane, so it never
        // stands in ash-waste and must not claim to have surveyed it.
        var stock = WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 1);
        var result = TurnEngine.Step(stock, new[] { March() }, seed: 1);

        var dave = result.World.Intel.Single(i => i.FactionId == "dave");
        Assert.Equal(SectorSight.Full, dave.Of("ember-hollow")!.Detail);
        Assert.Equal(SectorSight.Glimpse, dave.Of("ash-waste")!.Detail);
    }
}
