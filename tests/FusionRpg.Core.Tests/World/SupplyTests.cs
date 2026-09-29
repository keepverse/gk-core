using FusionRpg.Core.World;
using FusionRpg.Core.World.Movement;
using FusionRpg.Core.World.Turn;
using Xunit;

namespace FusionRpg.Core.Tests.World;

/// <summary>
/// W12 (spec-world-movement.md §Supply connectivity): the chain back to the homeworld. It is
/// recomputed from scratch every turn rather than cached, because a stored "in supply" flag is
/// exactly the kind of derived state that rots the first time a lane is cut.
/// </summary>
public class SupplyTests
{
    static WorldState World() => WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 1);

    static WorldState Own(WorldState w, string factionId, params string[] sectorIds) => w with
    {
        Sectors = w.Sectors
            .Select(s => sectorIds.Contains(s.SectorId)
                ? s with { OwnerFactionId = factionId, Phase = SectorPhase.Held }
                : s)
            .ToList()
    };

    static WorldState Sever(WorldState w, string laneId) => w with
    {
        Lanes = w.Lanes.Select(l => l.LaneId == laneId ? l with { State = LaneState.Severed } : l).ToList()
    };

    static WorldState Place(WorldState w, string entityId, string sectorId) => w with
    {
        Entities = w.Entities
            .Select(e => e.EntityId == entityId
                ? e with
                {
                    AtSectorId = sectorId, OnLaneId = null, OnLaneTowardSectorId = null,
                    LaneProgressMilli = 0, MovementRemaining = 1000
                }
                : e)
            .ToList()
    };

    static WorldState WithCarriedLoam(WorldState w, string entityId, long amount) => w with
    {
        Entities = w.Entities
            .Select(e => e.EntityId == entityId ? e with { CarriedLoam = amount } : e)
            .ToList()
    };

    static long CarriedLoam(WorldState w, string entityId) =>
        w.Entities.Single(e => e.EntityId == entityId).CarriedLoam;

    /// <summary>
    /// Moves the wild pack off the frontier. Every sector but ash-waste carries a Seat, and a Seat
    /// you hold is a supply source in its own right — so ash-waste is the only holding that can be
    /// cut off at all, and these tests need it empty.
    /// </summary>
    static WorldState Banish(WorldState w) => Place(w, "e-wild-pack-1", "black-gate");

    static WorldCommand Stand() => new()
    {
        CommanderId = "dave", CommandId = "s1", Kind = WorldCommandKinds.StandFast
    };

    [Fact]
    public void The_homeworld_is_its_own_supply_source()
    {
        var connected = SupplyGraph.ConnectedSectors(World(), "dave");
        Assert.Contains("homeworld", connected);
    }

    [Fact]
    public void A_held_neighbour_of_the_homeworld_is_in_supply()
    {
        var world = Own(World(), "dave", "ember-hollow");
        var connected = SupplyGraph.ConnectedSectors(world, "dave");

        Assert.Contains("ember-hollow", connected);
        Assert.DoesNotContain("ash-waste", connected);   // not held, so not part of the chain
    }

    [Fact]
    public void Cutting_one_junction_disconnects_exactly_what_was_behind_it()
    {
        var world = Own(Banish(World()), "dave", "ember-hollow", "ash-waste");
        Assert.Equal(
            new[] { "ash-waste", "ember-hollow", "homeworld" },
            SupplyGraph.ConnectedSectors(world, "dave").OrderBy(s => s, StringComparer.Ordinal));

        // l-ember-ash is the only owned way into ash-waste; ember-hollow keeps its own Seat.
        var cut = Sever(world, "l-ember-ash");
        var connected = SupplyGraph.ConnectedSectors(cut, "dave");

        Assert.Equal(
            new[] { "ember-hollow", "homeworld" },
            connected.OrderBy(s => s, StringComparer.Ordinal));
    }

    [Fact]
    public void Reconnecting_the_lane_restores_the_chain()
    {
        var world = Own(Banish(World()), "dave", "ember-hollow", "ash-waste");
        var cut = Sever(world, "l-ember-ash");
        var healed = cut with
        {
            Lanes = cut.Lanes.Select(l => l.LaneId == "l-ember-ash" ? l with { State = LaneState.Open } : l).ToList()
        };

        Assert.Equal(3, SupplyGraph.ConnectedSectors(healed, "dave").Count);
    }

    /// <summary>
    /// base-defense siege-supply (audit F1/F1b, 2026-09-05): updated from the pre-fix expectation.
    /// A besieged sector is now a source for ITSELF ("a base with stores is not a legion in the
    /// field") even though nothing can route THROUGH it — so `ember-hollow` (besieged) is correctly
    /// `connected` now, while `ash-waste` (reachable only by routing THROUGH ember-hollow) is
    /// correctly still excluded. This is the intended, audited consequence of the fix, not a
    /// regression: the traversal half of the rule (cannot route through contested ground) is
    /// unchanged; only the source half (a besieged sector no longer silently vanishes from its own
    /// supply) was corrected.
    /// </summary>
    [Fact]
    public void Supply_refuses_to_ROUTE_THROUGH_a_sector_a_hostile_force_stands_in_but_it_still_supplies_itself()
    {
        var world = Own(World(), "dave", "ember-hollow", "ash-waste");
        world = Place(world, "e-wild-pack-1", "ember-hollow");

        var connected = SupplyGraph.ConnectedSectors(world, "dave");

        Assert.Contains("ember-hollow", connected);      // F1/F1b: besieged, but still a source for itself
        Assert.DoesNotContain("ash-waste", connected);   // the only way there ran through ember-hollow
    }

    /// <summary>
    /// Rewritten for spec-loam-legions.md: the currency is carried loam now, not wounds, but the
    /// property the old test proved is the same one — a faction with no supply network at all is
    /// exempt from the mechanic entirely, per this program's own G-C precedent (an exemption is
    /// re-proven at every place its logic moves, not assumed to survive a refactor untested).
    /// </summary>
    [Fact]
    public void A_faction_with_no_seat_of_its_own_has_no_supply_and_never_burns()
    {
        var world = WithCarriedLoam(World(), "e-wild-pack-1", amount: 5);
        Assert.Empty(SupplyGraph.ConnectedSectors(world, "wild"));

        var result = TurnEngine.Step(world, new[] { Stand() }, seed: 1);

        Assert.Contains(result.World.Entities, e => e.EntityId == "e-wild-pack-1");
        Assert.Equal(5, CarriedLoam(result.World, "e-wild-pack-1"));
    }

    /// <summary>
    /// Rewritten for spec-loam-legions.md: an in-supply legion tops up for free and never burns —
    /// the replacement for the old "takes no attrition" property.
    /// </summary>
    [Fact]
    public void A_legion_standing_in_supply_never_burns()
    {
        var world = WithCarriedLoam(World(), "e-dave-legion-1", amount: 5);
        var result = TurnEngine.Step(world, new[] { Stand() }, seed: 1);

        Assert.Contains(result.World.Entities, e => e.EntityId == "e-dave-legion-1");
        // In supply, so it can only ever top up toward capacity, never burn down.
        Assert.True(CarriedLoam(result.World, "e-dave-legion-1") >= 5);
        Assert.DoesNotContain(result.Report.Entries, e => e.Detail.StartsWith("legion.starved") || e.Detail.StartsWith("legion.burn"));
    }

    [Fact]
    public void A_disconnected_holding_is_reported()
    {
        var world = Own(Banish(World()), "dave", "ember-hollow", "ash-waste");
        var cut = Sever(world, "l-ember-ash");

        var result = TurnEngine.Step(cut, new[] { Stand() }, seed: 1);

        Assert.Contains(result.Report.Entries, e => e.Detail == "supply.cut:ash-waste");
    }

    [Fact]
    public void Supply_is_recomputed_every_turn_rather_than_carried_forward()
    {
        var world = Own(Banish(World()), "dave", "ember-hollow", "ash-waste");
        var first = TurnEngine.Step(world, new[] { Stand() }, seed: 1);
        Assert.DoesNotContain(first.Report.Entries, e => e.Detail.StartsWith("supply.cut"));

        // Nothing was stored, so cutting the lane between turns changes the answer immediately.
        var second = TurnEngine.Step(Sever(first.World, "l-ember-ash"), new[] { Stand() }, seed: 1);
        Assert.Contains(second.Report.Entries, e => e.Detail == "supply.cut:ash-waste");
    }

    // ---- gates ----------------------------------------------------------------------------

    [Fact]
    public void A_gate_you_have_no_key_to_stops_a_supply_column_as_surely_as_an_army()
    {
        // Found by putting the supply filter and the topology filter side by side: topology refused
        // a shut gate and supply did not, so an empire could be provisioned through a door nobody
        // could open, while the lifeline overlay simultaneously reported the chain as cut.
        // The homeworld keeps the only Seat: `first-light` gives nearly every sector one, and a
        // sector that seeds its own supply can never be cut off from anywhere.
        var reachable = OnlyHomeworldHasASeat() with { };

        Assert.Contains("ember-hollow", SupplyGraph.ConnectedSectors(reachable, "dave"));

        var barred = reachable with
        {
            Lanes = reachable.Lanes
                .Select(l => l.LaneId == "l-home-ember"
                    ? l with { TypeId = "gated", GateKeyId = "key-of-ash" }
                    : l)
                .ToList()
        };

        Assert.DoesNotContain("ember-hollow", SupplyGraph.ConnectedSectors(barred, "dave"));
    }

    [Fact]
    public void A_gate_standing_open_carries_supply_like_any_other_lane()
    {
        var open = OnlyHomeworldHasASeat();
        open = open with
        {
            Lanes = open.Lanes
                .Select(l => l.LaneId == "l-home-ember" ? l with { TypeId = "gated" } : l)
                .ToList()
        };

        Assert.Contains("ember-hollow", SupplyGraph.ConnectedSectors(open, "dave"));
    }

    /// <summary>
    /// `first-light` with Dave holding ember-hollow and the homeworld holding the map's only Seat.
    /// Without the second half, every sector seeds its own supply and nothing is ever cut off —
    /// which is how a supply test built on this template quietly stops asserting anything.
    /// </summary>
    static WorldState OnlyHomeworldHasASeat()
    {
        var world = WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 1);

        return world with
        {
            Sectors = world.Sectors
                .Select(s => s with
                {
                    OwnerFactionId = s.SectorId == "ember-hollow" ? "dave" : s.OwnerFactionId,
                    Slots = s.SectorId == "homeworld"
                        ? s.Slots
                        : s.Slots.Where(sl => sl.SlotTypeId != SlotTypeCatalog.SeatSlotTypeId).ToList()
                })
                .ToList()
        };
    }
}
