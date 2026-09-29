using FusionRpg.Core.World;
using FusionRpg.Core.World.Intel;
using Xunit;

namespace FusionRpg.Core.Tests.World.Intel;

/// <summary>
/// W19 (spec-world-intel.md §Remembering): sight becomes memory, and memory ages.
///
/// The two properties that matter most are that a survey equals the truth exactly — belief must
/// never drift from what you are standing on — and that a glimpse carries a band rather than a
/// count, because an exact number from next door would make fog cosmetic.
/// </summary>
public class IntelRecorderTests
{
    static WorldState World() => WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 1);

    static WorldState Remembering(WorldState w, int turn) => w with { Intel = IntelRecorder.Observe(w, w, turn) };

    static IntelSnapshot? Believes(WorldState w, string faction, string sector) =>
        w.Intel.FirstOrDefault(f => f.FactionId == faction)?.Of(sector);

    static WorldState Place(WorldState w, string entityId, string sectorId) => w with
    {
        Entities = w.Entities
            .Select(e => e.EntityId == entityId
                ? e with { AtSectorId = sectorId, OnLaneId = null, OnLaneTowardSectorId = null, LaneProgressMilli = 0 }
                : e)
            .ToList()
    };

    [Fact]
    public void Standing_on_ground_means_believing_it_exactly()
    {
        var world = Remembering(World(), turn: 1);
        var home = Believes(world, "dave", "homeworld")!;
        var truth = world.Sectors.Single(s => s.SectorId == "homeworld");

        Assert.Equal(SectorSight.Full, home.Detail);
        Assert.Equal(truth.OwnerFactionId, home.OwnerFactionId);
        Assert.Equal(truth.Phase, home.Phase);
        Assert.Equal(truth.DangerBand, home.DangerBand);
        Assert.Equal(truth.Slots.Count, home.Slots.Count);
        Assert.Equal(1, home.LastSeenTurn);
    }

    [Fact]
    public void A_glimpse_carries_no_slots_at_all()
    {
        // black-gate, not ember-hollow (re-anchored 2026-08-22): the template authors ember-hollow as
        // `Scouted`, so Dave begins already knowing its insides. This test used to pass only because
        // the recorder threw that authored survey away on the first turn — the bug `SurveyMemoryTests`
        // now pins. A sector authored `Unknown` is the only true glimpse there is.
        var world = Remembering(World() with
        {
            Entities = World().Entities
                .Select(e => e.EntityId == "e-dave-legion-1" ? e with { AtSectorId = "ash-waste" } : e)
                .ToList()
        }, 1);

        var gate = Believes(world, "dave", "black-gate")!;
        var truth = world.Sectors.Single(s => s.SectorId == "black-gate");

        // Black Gate holds a Seat and two guarded slots. From next door you learn none of that —
        // only that it is there, whose it is, and how dangerous it looks.
        Assert.Equal(SectorSight.Glimpse, gate.Detail);
        Assert.NotEmpty(truth.Slots);
        Assert.Empty(gate.Slots);
        Assert.Equal(truth.OwnerFactionId, gate.OwnerFactionId);
        Assert.Equal(truth.DangerBand, gate.DangerBand);
    }

    [Fact]
    public void What_you_have_never_seen_has_no_entry_at_all()
    {
        // An absent snapshot *is* "never heard of it" — there is no separate flag to get wrong.
        Assert.Null(Believes(Remembering(World(), 1), "dave", "black-gate"));
    }

    [Fact]
    public void A_survey_counts_heads_and_a_glimpse_only_bands_them()
    {
        var world = Remembering(Place(World(), "e-wild-pack-1", "ember-hollow"), turn: 1);

        // Dave glimpses ember-hollow from the homeworld: he can see a force, not count it.
        var glimpsed = Believes(world, "dave", "ember-hollow")!.Forces.Single();
        Assert.False(glimpsed.Exact);
        Assert.True(glimpsed.BandIndex > 0);
        Assert.True(glimpsed.Defensive >= glimpsed.Offensive, "the defensive reading must never be the cheaper one");

        // The wild pack is standing in it, and knows exactly what it brought.
        var surveyed = Believes(world, "wild", "ember-hollow")!.Forces.Single();
        Assert.True(surveyed.Exact);
        Assert.Equal(560, surveyed.Strength);   // 2 members × 140 hp × level 2
    }

    [Fact]
    public void A_force_on_the_road_is_recorded_against_the_ground_it_is_walking_toward()
    {
        var world = World();
        var marching = world with
        {
            Entities = world.Entities
                .Select(e => e.EntityId == "e-wild-pack-1"
                    ? e with
                    {
                        AtSectorId = null, OnLaneId = "l-ember-ash",
                        OnLaneTowardSectorId = "ember-hollow", LaneProgressMilli = 400
                    }
                    : e)
                .ToList()
        };

        var believed = Believes(Remembering(marching, 1), "dave", "ember-hollow")!;
        var incoming = believed.Forces.Single();

        Assert.Equal("e-wild-pack-1", incoming.EntityId);
        Assert.False(incoming.Exact, "you can see a column on the road, not read its muster roll");
    }

    [Fact]
    public void Leaving_keeps_the_memory_and_the_turn_it_was_taken()
    {
        var atHome = Remembering(World(), turn: 1);

        // March away, several turns pass, and nothing looks at the homeworld again.
        var away = Place(atHome, "e-dave-legion-1", "ash-waste") with
        {
            Sectors = atHome.Sectors
                .Select(s => s.SectorId == "homeworld" ? s with { OwnerFactionId = null } : s)
                .ToList()
        };
        var later = away with { Intel = IntelRecorder.Observe(away, away, turn: 9) };

        var home = Believes(later, "dave", "homeworld")!;
        Assert.Equal(1, home.LastSeenTurn);
        Assert.Equal("dave", home.OwnerFactionId);   // still believed his, though it no longer is
    }

    [Fact]
    public void A_force_that_dies_in_front_of_you_is_forgotten_rather_than_haunting_the_map()
    {
        var withEnemy = Remembering(Place(World(), "e-wild-pack-1", "ember-hollow"), turn: 1);
        Assert.Single(Believes(withEnemy, "dave", "ember-hollow")!.Forces);

        var destroyed = withEnemy with
        {
            Entities = withEnemy.Entities.Where(e => e.EntityId != "e-wild-pack-1").ToList()
        };
        var after = destroyed with { Intel = IntelRecorder.Observe(destroyed, destroyed, turn: 2) };

        Assert.Empty(Believes(after, "dave", "ember-hollow")!.Forces);
    }

    [Fact]
    public void Every_faction_gets_its_own_belief_and_nobody_shares()
    {
        var world = Remembering(World(), turn: 1);

        Assert.Equal(
            world.Factions.Select(f => f.FactionId).OrderBy(f => f, StringComparer.Ordinal),
            world.Intel.Select(i => i.FactionId));

        // Zomboss holds nothing and stands nowhere, so he believes nothing.
        // Zomboss now has a warband of his own, so his belief is no longer empty — what this
        // asserts is that it is *his*, built from his own eyes and sharing nothing with Dave.
        var zomboss = world.Intel.Single(i => i.FactionId == "zomboss").Sectors;
        var dave = world.Intel.Single(i => i.FactionId == "dave").Sectors;
        Assert.NotEqual(
            dave.Select(x => x.SectorId).OrderBy(x => x, StringComparer.Ordinal),
            zomboss.Select(x => x.SectorId).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void Belief_is_written_in_stable_order()
    {
        var world = Remembering(World(), turn: 1);

        foreach (var faction in world.Intel)
            Assert.Equal(
                faction.Sectors.Select(s => s.SectorId).OrderBy(s => s, StringComparer.Ordinal),
                faction.Sectors.Select(s => s.SectorId));
    }

    [Fact]
    public void Observing_the_same_world_twice_believes_the_same_thing_twice()
    {
        var once = Remembering(World(), turn: 3);
        var twice = Remembering(World(), turn: 3);

        Assert.Equal(WorldCanonical.Write(once), WorldCanonical.Write(twice));
    }

    [Fact]
    public void The_ladder_is_derived_from_the_turn_it_was_seen()
    {
        var snapshot = new IntelSnapshot { SectorId = "x", LastSeenTurn = 10 };

        Assert.Equal(IntelState.Unknown, IntelLadder.StateOf(null, 10, seenThisTurn: false));
        Assert.Equal(IntelState.Watched, IntelLadder.StateOf(snapshot, 10, seenThisTurn: true));
        Assert.Equal(IntelState.Watched, IntelLadder.StateOf(null, 10, seenThisTurn: true));
        Assert.Equal(IntelState.Scouted, IntelLadder.StateOf(snapshot, 15, seenThisTurn: false));
        Assert.Equal(IntelState.Rumored, IntelLadder.StateOf(snapshot, 16, seenThisTurn: false));
        Assert.Equal(6, IntelLadder.AgeOf(snapshot, 16));
    }

    // ---- L43: prospecting -----------------------------------------------------------------------

    static WorldSlot Rootbed() => new() { SlotIndex = 0, SlotTypeId = SlotTypeCatalog.RootbedSlotTypeId };

    /// <summary>A five-hop chain, "c0".."c5", with a rootbed placeable at any one hop — everything
    /// else barren, so `Habitability.For` only ever fires where the fixture puts it.</summary>
    static WorldState Chain(string? rootbedAt = null, string dowserStance = Prospecting.DowserStance) => new()
    {
        Factions = new[] { new WorldFaction { FactionId = "dave", Kind = WorldFactionKind.Player, Name = "Dave" } },
        Sectors = Enumerable.Range(0, 6)
            .Select(i => new WorldSector
            {
                SectorId = "c" + i, TypeId = "stable",
                Slots = string.Equals("c" + i, rootbedAt, StringComparison.Ordinal)
                    ? new[] { Rootbed() } : Array.Empty<WorldSlot>()
            })
            .ToArray(),
        Lanes = Enumerable.Range(0, 5)
            .Select(i => new WorldLane { LaneId = "l" + i, FromSectorId = "c" + i, ToSectorId = "c" + (i + 1) })
            .ToArray(),
        Entities = new[]
        {
            new WorldEntity
            {
                EntityId = "e-dowser", Kind = WorldEntityKind.Legion, OwnerFactionId = "dave", AtSectorId = "c0",
                Stance = dowserStance, Members = new[] { new WorldEntityMember { SpeciesId = "grunt" } }
            }
        }
    };

    [Fact]
    public void A_dowser_reveals_a_loam_source_sector_beyond_ordinary_scouting_range()
    {
        // c4 is four hops from the dowser at c0 — past Visibility.ScoutSightLanes (2), inside
        // Prospecting.DowserSightLanes (4).
        var revealed = Prospecting.Reveal(Chain(rootbedAt: "c4"), "dave");
        Assert.Contains("c4", revealed);
    }

    [Fact]
    public void A_dowser_does_not_reach_past_its_own_range()
    {
        // c5 is five hops out — one past DowserSightLanes.
        var revealed = Prospecting.Reveal(Chain(rootbedAt: "c5"), "dave");
        Assert.DoesNotContain("c5", revealed);
    }

    [Fact]
    public void Prospecting_reveals_nothing_for_ground_with_no_loam_source()
    {
        var revealed = Prospecting.Reveal(Chain(rootbedAt: null), "dave");
        Assert.Empty(revealed);
    }

    [Fact]
    public void An_ordinary_stance_reveals_nothing_a_dowser_would()
    {
        var revealed = Prospecting.Reveal(Chain(rootbedAt: "c4", dowserStance: "march"), "dave");
        Assert.Empty(revealed);
    }
}
