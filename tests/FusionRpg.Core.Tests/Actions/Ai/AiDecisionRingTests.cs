using System;
using System.Collections.Generic;
using System.Linq;
using FusionRpg.Core.Actions.Ai;
using Xunit;

namespace FusionRpg.Core.Tests.Actions.Ai;

/// <summary>
/// combat-ai `decision-inspector` B (module 10, CAI2.5, spec-decision-inspector.md §4): the lawn ring's
/// own logic, tested in Core because CI never builds the injector. The two properties that matter are
/// that the per-actor index SURVIVES ring eviction, and that a reused IL2CPP pointer cannot inherit the
/// previous actor's decisions.
/// </summary>
public class AiDecisionRingTests
{
    static AiDecisionRecord Decision(string actorKey, long tick) => new(
        NowTick: tick, Round: 0, ActorKey: actorKey,
        Tier: null, ProfileId: null, Personality: null,
        Trigger: AiTriggerState.None, Origin: AiDecisionOrigin.Policy,
        Candidates: Array.Empty<AiCandidateVerdict>(),
        ChosenActionId: null, ChosenTargetKey: null, ChosenSaturatedBy: 0,
        TopThree: Array.Empty<(string ActorKey, ScoreBreakdown Breakdown)>());

    [Fact]
    public void The_ring_is_bounded_and_evicts_oldest_first()
    {
        var ring = new AiDecisionRing();
        for (var i = 0; i < AiDecisionRing.Cap + 3; i++) ring.Record(Decision("a", i));

        var recent = ring.Recent();
        Assert.Equal(AiDecisionRing.Cap, recent.Count);
        Assert.Equal(3, recent[0].NowTick);                                  // oldest three evicted
        Assert.Equal(AiDecisionRing.Cap + 2, recent[^1].NowTick);
    }

    /// <summary>The reason the index exists: a busy actor's entries age out of the ring while the
    /// answer to "why did THIS creature do that" must stay available.</summary>
    [Fact]
    public void The_last_per_actor_index_survives_eviction()
    {
        var ring = new AiDecisionRing();
        ring.Record(Decision("busy", 0));
        for (var i = 1; i <= AiDecisionRing.Cap + 2; i++) ring.Record(Decision($"other:{i}", i));

        Assert.DoesNotContain(ring.Recent(), r => r.ActorKey == "busy");      // evicted from the ring
        Assert.Equal(0, ring.LastFor("busy")!.Value.NowTick);                 // still indexed
    }

    [Fact]
    public void A_read_returns_a_copy_of_the_ring()
    {
        var ring = new AiDecisionRing();
        ring.Record(Decision("a", 1));

        var first = ring.Recent();
        Assert.Single(first);
        // The returned list is the caller's; clearing it cannot evict from the ring.
        (first as List<AiDecisionRecord>)?.Clear();
        Assert.Single(ring.Recent());
    }

    [Fact]
    public void Reading_an_unknown_actor_returns_nothing_never_a_synthesised_record()
    {
        var ring = new AiDecisionRing();
        Assert.Null(ring.LastFor("never-decided"));
    }

    [Fact]
    public void Death_then_spawn_and_spawn_then_death_both_clear_the_index()
    {
        // Order-independent by construction: the only correct action on either membership edge is the
        // same one, which is why ForgetActor takes no "which edge" argument.
        var deathFirst = new AiDecisionRing();
        deathFirst.Record(Decision("entity:100", 5));
        deathFirst.ForgetActor("entity:100");                                  // death
        Assert.Null(deathFirst.LastFor("entity:100"));                         // spawn (reused ptr) sees none
        deathFirst.Record(Decision("entity:100", 9));
        Assert.Equal(9, deathFirst.LastFor("entity:100")!.Value.NowTick);

        var spawnFirst = new AiDecisionRing();
        spawnFirst.Record(Decision("entity:100", 5));
        spawnFirst.ForgetActor("entity:100");                                  // spawn pre-empts any stale entry
        spawnFirst.ForgetActor("entity:100");                                  // death
        Assert.Null(spawnFirst.LastFor("entity:100"));
    }

    /// <summary>IL2CPP reuses pointers: a later decision for the SAME key must not rewrite the
    /// historical entries, only the live index.</summary>
    [Fact]
    public void A_reused_pointer_does_not_rewrite_an_already_recorded_entry()
    {
        var ring = new AiDecisionRing();
        ring.Record(Decision("entity:100", 5));       // the OLD actor's decision
        ring.ForgetActor("entity:100");               // it dies; the ptr is released
        ring.Record(Decision("entity:100", 40));      // the NEW actor reusing that ptr decides

        var history = ring.Recent();
        Assert.Equal(2, history.Count);
        Assert.Equal(5, history[0].NowTick);          // history is not falsified by the reuse
        Assert.Equal(40, history[1].NowTick);
        Assert.Equal(40, ring.LastFor("entity:100")!.Value.NowTick);  // only the index moved on
    }

    [Fact]
    public void Clear_empties_both_the_ring_and_the_index()
    {
        var ring = new AiDecisionRing();
        ring.Record(Decision("a", 1));
        ring.Clear();

        Assert.Empty(ring.Recent());
        Assert.Null(ring.LastFor("a"));
    }

    /// <summary>The instrument's size is STRUCTURAL (`tunables-ssot.md` T2), pinned with its reason so
    /// nobody reads it as a balance dial.</summary>
    [Fact]
    public void The_cap_is_the_structural_eight() => Assert.Equal(8, AiDecisionRing.Cap);
}
