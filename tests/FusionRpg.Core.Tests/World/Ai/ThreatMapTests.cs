using FusionRpg.Core.Tests.World.Topology;
using FusionRpg.Core.World;
using FusionRpg.Core.World.Ai;
using FusionRpg.Core.World.Intel;
using Xunit;

namespace FusionRpg.Core.Tests.World.Ai;

/// <summary>
/// W31 (spec-ai-commander.md §ThreatMap): fear, spread by ignorance.
///
/// A remembered enemy is not *there*. It is somewhere within however far it could have marched since
/// you looked, so the older the sighting the wider and vaguer the fear — until it decays to nothing
/// and stops mattering at all, which is what makes scouting worth paying for.
/// </summary>
public class ThreatMapTests
{
    /// <summary>A line of five sectors, so "three hops away" is a thing that can be pointed at.</summary>
    static WorldState Line() => GraphShapes.From("a-b", "b-c", "c-d", "d-e") with
    {
        Factions = new[]
        {
            new WorldFaction { FactionId = "dave", Kind = WorldFactionKind.Player, Name = "Dave" },
            new WorldFaction { FactionId = "zomboss", Kind = WorldFactionKind.Zomboss, Name = "Z" }
        }
    };

    /// <summary>Dave watching from `a`, with a hostile band standing wherever you put it.</summary>
    static WorldState Facing(string enemyAt, int strength = 1000, string enemyKind = "warband")
    {
        var world = Line();
        return world with
        {
            Entities = new[]
            {
                Force("e-dave-1", "dave", "a", 1, WorldEntityKind.Legion),
                Force("e-zomboss-1", "zomboss", enemyAt, strength,
                    enemyKind == "guard" ? WorldEntityKind.Guard : WorldEntityKind.Warband)
            }.OrderBy(e => e.EntityId, StringComparer.Ordinal).ToList()
        };
    }

    static WorldEntity Force(string id, string owner, string at, int hp, WorldEntityKind kind) => new()
    {
        EntityId = id,
        Kind = kind,
        OwnerFactionId = owner,
        AtSectorId = at,
        Stance = "march",
        Members = new[] { new WorldEntityMember { SpeciesId = "normalzombie", Level = 1, Hp = hp } }
    };

    /// <summary>Belief recorded now, then read <paramref name="age"/> turns later without looking again.</summary>
    static IWorldView Aged(WorldState world, int age)
    {
        var seen = world with { Intel = IntelRecorder.Observe(world, world, turn: 0) };
        return new BelievedWorldView(seen with { CurrentTurn = age }, "dave");
    }

    static IReadOnlyDictionary<string, long> Threat(WorldState world, int age = 0,
        ThreatReading reading = ThreatReading.Defensive) =>
        ThreatMap.For(Aged(world, age), reading);

    // ---- a fresh sighting is a sharp, local fear ------------------------------------------

    [Fact]
    public void A_sighting_today_is_worst_where_it_was_seen()
    {
        var threat = Threat(Facing("b"));

        Assert.True(threat["b"] > threat["c"]);
        Assert.True(threat["c"] > 0);
    }

    [Fact]
    public void A_fresh_sighting_does_not_reach_across_the_whole_map()
    {
        // age 0 means it has had no time to go anywhere: two hops is the edge of the falloff.
        var threat = Threat(Facing("a"));

        Assert.Equal(0, Get(threat, "d"));
        Assert.Equal(0, Get(threat, "e"));
    }

    // ---- an old one is a wide, vague one ----------------------------------------------------

    [Fact]
    public void A_three_turn_old_sighting_is_the_same_worry_everywhere_within_three_hops()
    {
        // It could be anywhere in that radius by now, so nowhere inside it is safer than anywhere
        // else. Uncertainty makes you defend more places, which is the correct response to it.
        var threat = Threat(Facing("a"), age: 3);

        Assert.Equal(threat["a"], threat["b"]);
        Assert.Equal(threat["b"], threat["c"]);
        Assert.Equal(threat["c"], threat["d"]);
    }

    [Fact]
    public void An_older_sighting_frightens_you_less_than_a_new_one()
    {
        Assert.True(Threat(Facing("b"), age: 1)["b"] > Threat(Facing("b"), age: 4)["b"]);
    }

    [Fact]
    public void A_sighting_you_can_no_longer_trust_at_all_contributes_nothing_anywhere()
    {
        // Seven turns: freshness has decayed past zero. Not "a little" — nothing. If stale intel
        // never stopped mattering, scouting would buy you nothing and the map would never go quiet.
        var threat = Threat(Facing("b"), age: 7);

        Assert.All(threat.Values, v => Assert.Equal(0, v));
    }

    // ---- the two readings ---------------------------------------------------------------------

    [Fact]
    public void Defending_assumes_the_worst_and_attacking_assumes_the_likely()
    {
        // The band's ceiling when being wrong is fatal, its midpoint when being wrong is merely
        // expensive. That asymmetry *is* the estimation model — no probability, no priors.
        //
        // One lane out, not two: sight reaches exactly one lane, so a force at `c` would not be
        // believed to exist at all and both readings would be a very equal zero.
        var world = Facing("b");                       // glimpsed from next door, never counted

        var defending = Threat(world, reading: ThreatReading.Defensive);
        var attacking = Threat(world, reading: ThreatReading.Offensive);

        Assert.True(defending["b"] > attacking["b"], "a glimpse should read high when defending");
    }

    [Fact]
    public void A_force_you_counted_yourself_reads_the_same_either_way()
    {
        // Standing next to it, both readings are the exact number — there is nothing to estimate.
        var world = Facing("a");

        Assert.Equal(
            Threat(world, reading: ThreatReading.Defensive)["a"],
            Threat(world, reading: ThreatReading.Offensive)["a"]);
    }

    // ---- who counts ---------------------------------------------------------------------------

    [Fact]
    public void Your_own_army_is_not_something_to_be_afraid_of()
    {
        var world = Line() with
        {
            Entities = new[] { Force("e-dave-1", "dave", "a", 5000, WorldEntityKind.Legion) }
        };

        Assert.All(Threat(world).Values, v => Assert.Equal(0, v));
    }

    [Fact]
    public void A_guard_dug_into_a_slot_frightens_only_the_slot_it_is_in()
    {
        // A guard defends the thing, not the ground: it projects no zone of control, so it cannot
        // come and find you. Spreading its threat outward would make every guarded sector radiate
        // menace it has no way to deliver.
        var threat = Threat(Facing("b", enemyKind: "guard"));

        Assert.Equal(0, Get(threat, "c"));
    }

    // ---- the lens ------------------------------------------------------------------------------

    [Fact]
    public void An_enemy_across_a_rift_no_supply_can_cross_is_still_two_days_march_away()
    {
        // The test that fails if anyone builds this on LaneGraph's supply lens. `first-light` cannot
        // catch it — every one of its lanes carries supply — so the shape is built here.
        var world = Facing("b") with
        {
            Lanes = Line().Lanes
                .Select(l => l.LaneId == "l-a-b" ? l with { TypeId = "deep" } : l)
                .ToList()
        };

        Assert.True(Threat(world)["a"] > 0, "a deep rift stops grain, not legions");
    }

    // ---- shape ---------------------------------------------------------------------------------

    [Fact]
    public void Every_sector_gets_an_answer_even_if_it_is_nothing()
    {
        // A missing key and a zero mean the same thing to a reader and different things to a caller.
        var threat = Threat(Facing("a"));
        Assert.Equal(Line().Sectors.Count, threat.Count);
    }

    [Fact]
    public void Two_enemies_in_one_place_frighten_you_more_than_one()
    {
        var one = Facing("b");
        var two = one with
        {
            Entities = one.Entities
                .Append(Force("e-zomboss-2", "zomboss", "b", 1000, WorldEntityKind.Warband))
                .OrderBy(e => e.EntityId, StringComparer.Ordinal)
                .ToList()
        };

        Assert.True(Threat(two)["b"] > Threat(one)["b"]);
    }

    [Fact]
    public void Reversing_the_world_changes_no_number()
    {
        var world = Facing("c");
        var reversed = world with
        {
            Sectors = world.Sectors.Reverse().ToList(),
            Lanes = world.Lanes.Reverse().ToList(),
            Entities = world.Entities.Reverse().ToList()
        };

        Assert.Equal(
            Threat(world).OrderBy(kv => kv.Key, StringComparer.Ordinal),
            Threat(reversed).OrderBy(kv => kv.Key, StringComparer.Ordinal));
    }

    static long Get(IReadOnlyDictionary<string, long> map, string id) => map.TryGetValue(id, out var v) ? v : 0;
}
