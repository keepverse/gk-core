using FusionRpg.Core.World;
using FusionRpg.Core.World.Ai;
using FusionRpg.Core.World.Intel;
using FusionRpg.Core.World.Movement;
using FusionRpg.Core.World.Turn;
using Xunit;

namespace FusionRpg.Core.Tests.World.Ai;

/// <summary>
/// W30 (spec-ai-commander.md §Believed supply): the same rule, asked of what a faction knows.
///
/// Every input is less reliable than the truth, and all of them err the same way — towards
/// confidence. A faction believes its chain is intact right up until it starves, which is the
/// behaviour we want: fog you can plan around is not fog.
/// </summary>
public class BelievedSupplyTests
{
    static WorldState World() => WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 1);

    /// <summary>Belief written the way the Intel phase writes it, so nothing here is hand-forged.</summary>
    static WorldState Observed(WorldState world, int turn = 0) =>
        world with { Intel = IntelRecorder.Observe(world, world, turn) };

    static IWorldView View(WorldState world, string faction = "dave") =>
        new BelievedWorldView(Observed(world), faction);

    /// <summary>Dave's legion, standing somewhere else.</summary>
    static WorldState At(WorldState world, string sectorId) => world with
    {
        Entities = world.Entities
            .Select(e => e.EntityId == "e-dave-legion-1" ? e with { AtSectorId = sectorId } : e)
            .ToList()
    };

    // ---- the extraction did not change the truth-side answer ------------------------------

    [Fact]
    public void The_engines_own_supply_answer_is_unchanged_by_the_refactor()
    {
        // W30 moved the traversal into SupplyReach. If that moved a single sector, everything
        // downstream of attrition and recovery moves with it.
        var world = World();

        Assert.Equal(
            new[] { "homeworld" },
            SupplyGraph.ConnectedSectors(world, "dave").OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public void Standing_on_your_own_ground_you_believe_exactly_what_is_true()
    {
        // Where sight is perfect, belief and truth must agree — otherwise the fog is not fog, it is
        // a bug that happens to look like one.
        var world = World();

        Assert.Equal(
            SupplyGraph.ConnectedSectors(world, "dave").OrderBy(id => id, StringComparer.Ordinal),
            BelievedSupply.ConnectedSectors(View(world)).OrderBy(id => id, StringComparer.Ordinal));
    }

    // ---- and then the two come apart --------------------------------------------------------

    [Fact]
    public void Ground_taken_from_you_while_you_were_not_looking_is_still_yours_as_far_as_you_know()
    {
        // The headline, and finding the right scenario for it taught us something about the model:
        // **a faction always has full sight of ground it owns**, so every lane inside its own supply
        // chain has both ends visible and can never be the masked one. Believing a cut lane is
        // intact is a march-planning mistake, not a supply one.
        //
        // What *can* diverge is ownership. Dave holds a four-sector chain — homeworld → ember-hollow
        // → ash-waste → black-gate — and ash-waste is taken from him two lanes out, beyond his
        // sight. He no longer owns it, so he no longer sees it, so he never learns; his believed
        // chain still runs straight through it to a sector that is now cut off entirely.
        //
        // The far end is black-gate rather than verdant-shelf since the 2026-08-22 reshape moved
        // Verdant Shelf behind it.
        // Two things about `first-light` have to go first, and finding them is half the value of
        // writing this test: the wild pack sits at ash-waste and projects a zone of control, which
        // would break the chain there for reasons that have nothing to do with fog; and almost every
        // sector holds a Seat, so each would seed its own supply and nothing could ever be cut off.
        var world = World();
        var mine = new[] { "ember-hollow", "ash-waste", "black-gate" };

        var wide = world with
        {
            Sectors = world.Sectors
                .Select(s => s with
                {
                    OwnerFactionId = mine.Contains(s.SectorId) ? "dave" : s.OwnerFactionId,
                    Slots = s.SectorId == "homeworld"
                        ? s.Slots
                        : s.Slots.Where(sl => sl.SlotTypeId != SlotTypeCatalog.SeatSlotTypeId).ToList()
                })
                .ToList(),
            Entities = world.Entities.Where(e => e.OwnerFactionId == "dave").ToList()
        };

        // Belief taken while the whole chain was his, with his legion at home.
        var known = wide with { Intel = IntelRecorder.Observe(wide, wide, turn: 0) };

        // Zomboss takes ash-waste. Dave is at homeworld, two lanes away, and no longer owns it.
        var lost = known with
        {
            Sectors = known.Sectors
                .Select(s => s.SectorId == "ash-waste" ? s with { OwnerFactionId = "zomboss" } : s)
                .ToList()
        };

        // The truth: everything past ash-waste is off the chain, and ash-waste with it.
        var truth = SupplyGraph.ConnectedSectors(lost, "dave");
        Assert.DoesNotContain("ash-waste", truth);
        Assert.DoesNotContain("black-gate", truth);

        // The belief: an empire in perfect health. He finds out by taking attrition, which is
        // exactly how it should feel.
        var believed = BelievedSupply.ConnectedSectors(new BelievedWorldView(lost, "dave"));
        Assert.Contains("ash-waste", believed);
        Assert.Contains("black-gate", believed);
    }

    [Fact]
    public void An_empire_with_no_believed_seat_has_no_chain_at_all()
    {
        // Supply starts at a Seat. Without this the walk would seed from any owned sector and every
        // faction would be permanently, invisibly supplied — attrition would simply never fire.
        var world = World();
        var seatless = world with
        {
            Sectors = world.Sectors
                .Select(s => s with
                {
                    Slots = s.Slots.Where(sl => sl.SlotTypeId != SlotTypeCatalog.SeatSlotTypeId).ToList()
                })
                .ToList()
        };

        Assert.Empty(BelievedSupply.ConnectedSectors(View(seatless)));
    }

    // Deliberately absent: "a Seat you have only glimpsed does not seed supply". It cannot happen.
    // Seeding requires *believed* ownership, and there is no way to believe you own a sector you have
    // only glimpsed — you get full sight of ground you hold, and ownership only ever transfers to you
    // by standing on it. A test for it would have to forge a belief the recorder cannot produce, and
    // would then be testing the forgery. See the note on BelievedSupply for the two divergences that
    // are real.

    [Fact]
    public void An_enemy_you_believe_is_standing_on_your_ground_breaks_the_chain_there()
    {
        // Zone of control, read from memory. Without it a faction would keep routing supply through
        // a sector an enemy army is sitting in, which is the one thing a zone of control is for.
        var world = World();
        var invaded = world with
        {
            Sectors = world.Sectors
                .Select(s => s.SectorId == "ember-hollow" ? s with { OwnerFactionId = "dave" } : s)
                .ToList(),
            Entities = world.Entities
                .Append(new WorldEntity
                {
                    EntityId = "e-zomboss-raid-1",
                    Kind = WorldEntityKind.Warband,
                    OwnerFactionId = "zomboss",
                    AtSectorId = "ember-hollow",
                    Stance = "march",
                    Members = new[] { new WorldEntityMember { SpeciesId = "normalzombie", Level = 2, Hp = 140 } }
                })
                .OrderBy(e => e.EntityId, StringComparer.Ordinal)
                .ToList()
        };

        var view = View(invaded);
        Assert.Contains(view.Believed("ember-hollow")!.Forces, f => f.OwnerFactionId == "zomboss");
        Assert.DoesNotContain("ember-hollow", BelievedSupply.ConnectedSectors(view));
    }

    [Fact]
    public void A_faction_that_believes_it_holds_no_seat_has_no_supply_network_at_all()
    {
        // The wild hold nothing and never had a capital, so they do not starve for want of one —
        // an empty set means "not applicable", not "everything is cut".
        Assert.Empty(BelievedSupply.ConnectedSectors(View(World(), "wild")));
    }

    [Fact]
    public void Ground_you_have_never_laid_eyes_on_is_never_part_of_your_chain()
    {
        var believed = BelievedSupply.ConnectedSectors(View(World()));

        foreach (var sectorId in believed)
            Assert.NotNull(View(World()).Believed(sectorId));
    }

    // ---- ordering ----------------------------------------------------------------------------

    [Fact]
    public void Reversing_the_world_changes_no_answer()
    {
        var world = World();
        var reversed = world with
        {
            Sectors = world.Sectors.Reverse().ToList(),
            Lanes = world.Lanes.Reverse().ToList(),
            Entities = world.Entities.Reverse().ToList()
        };

        Assert.Equal(
            BelievedSupply.ConnectedSectors(View(world)).OrderBy(id => id, StringComparer.Ordinal),
            BelievedSupply.ConnectedSectors(View(reversed)).OrderBy(id => id, StringComparer.Ordinal));
    }
}
