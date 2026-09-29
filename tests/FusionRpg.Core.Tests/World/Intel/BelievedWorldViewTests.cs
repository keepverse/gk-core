using FusionRpg.Core.World;
using FusionRpg.Core.World.Intel;
using Xunit;

namespace FusionRpg.Core.Tests.World.Intel;

/// <summary>
/// W21 (spec-world-intel.md): the only read path anything outside the engine gets.
///
/// The point is what it *cannot* answer. A policy handed one of these has no way to ask what is
/// really in a sector it has never visited, so an AI consulting the truth stops being a discipline
/// problem and becomes a compile error.
/// </summary>
public class BelievedWorldViewTests
{
    static WorldState World(int turn = 0)
    {
        var w = WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 1);
        return w with { CurrentTurn = turn };
    }

    static IWorldView View(WorldState w, string faction) => new BelievedWorldView(w, faction);

    [Fact]
    public void The_shape_of_the_map_is_public_knowledge()
    {
        // "nobody" is a faction id nobody plays, rather than Zomboss: since 2026-08-22 the template gives him a
        // warband and a Seat, so he is no longer anybody's example of knowing nothing.
        var blind = View(World(), "nobody");   // holds nothing, stands nowhere, believes nothing

        Assert.Equal(6, blind.SectorIds.Count);
        Assert.Equal(6, blind.Lanes.Count);
        Assert.Equal(3, blind.Factions.Count);
    }

    [Fact]
    public void What_is_in_those_sectors_is_not()
    {
        // "nobody" rather than Zomboss: since 2026-08-22 the template gives him a warband,
        // so he is no longer anybody's example of a faction that knows nothing.
        var blind = View(World(), "nobody");

        Assert.All(blind.SectorIds, id => Assert.Null(blind.Believed(id)));
        Assert.All(blind.SectorIds, id => Assert.Equal(IntelState.Unknown, blind.StateOf(id)));
    }

    [Fact]
    public void You_always_know_your_own_forces_in_full()
    {
        var dave = View(World(), "dave");

        var legion = Assert.Single(dave.OwnForces);
        Assert.Equal("e-dave-legion-1", legion.EntityId);
        Assert.Equal(3, legion.Members.Count);
        Assert.DoesNotContain(dave.OwnForces, e => e.OwnerFactionId != "dave");
    }

    [Fact]
    public void Ground_you_are_standing_on_reads_as_watched()
    {
        Assert.Equal(IntelState.Watched, View(World(), "dave").StateOf("homeworld"));
    }

    [Fact]
    public void The_ladder_decays_as_the_turns_pass_without_another_look()
    {
        var world = World();

        // Send the legion east and stop looking at the eastern frontier's rumour.
        var away = world with
        {
            Entities = world.Entities
                .Select(e => e.EntityId == "e-dave-legion-1" ? e with { AtSectorId = "homeworld" } : e)
                .ToList()
        };

        Assert.Equal(IntelState.Scouted, View(away with { CurrentTurn = 3 }, "dave").StateOf("ash-waste"));
        Assert.Equal(IntelState.Rumored, View(away with { CurrentTurn = 40 }, "dave").StateOf("ash-waste"));
        Assert.Equal(40, View(away with { CurrentTurn = 40 }, "dave").AgeOf("ash-waste"));
    }

    [Fact]
    public void Seeing_it_now_beats_however_old_the_memory_is()
    {
        // The homeworld was last snapshotted at turn 0 and it is now turn 99 — but Dave is standing
        // in it, so it is Watched, not Rumored.
        Assert.Equal(IntelState.Watched, View(World(turn: 99), "dave").StateOf("homeworld"));
    }

    [Fact]
    public void A_survey_carries_slots_and_a_rumour_does_not()
    {
        var dave = View(World(), "dave");

        Assert.NotEmpty(dave.Believed("homeworld")!.Slots);       // authored Watched → surveyed
        Assert.Empty(dave.Believed("ash-waste")!.Slots);          // authored Rumored → a glimpse
    }

    [Fact]
    public void A_faction_with_no_intel_row_at_all_does_not_throw()
    {
        // A world built before world-intel, or a faction added mid-campaign.
        var world = World() with { Intel = Array.Empty<FactionIntel>() };
        var view = View(world, "dave");

        Assert.Null(view.Believed("homeworld"));
        Assert.Equal(IntelState.Watched, view.StateOf("homeworld"));   // still sees where it stands
        Assert.Equal(IntelState.Unknown, view.StateOf("black-gate"));
    }

    [Fact]
    public void Two_views_of_the_same_world_answer_identically()
    {
        var world = World(turn: 7);
        var a = View(world, "dave");
        var b = View(world, "dave");

        Assert.Equal(
            a.SectorIds.Select(a.StateOf),
            b.SectorIds.Select(b.StateOf));
    }

    [Fact]
    public void Each_faction_gets_its_own_answers()
    {
        var world = World();

        Assert.Equal(IntelState.Watched, View(world, "dave").StateOf("homeworld"));
        Assert.Equal(IntelState.Unknown, View(world, "zomboss").StateOf("homeworld"));
        Assert.Equal(IntelState.Watched, View(world, "wild").StateOf("ash-waste"));   // the pack lives there
    }
}
