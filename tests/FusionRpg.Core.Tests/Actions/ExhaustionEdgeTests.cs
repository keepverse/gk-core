using System.Linq;
using FusionRpg.Core.Actions.Cost;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Status;
using Xunit;

namespace FusionRpg.Core.Tests.Actions;

/// <summary>
/// lawn `LW1.3` (<c>lawn-playable/spec-exhaustion-event.md</c>): exhaustion as an <b>event</b> — one
/// transition per window per actor per resource, both directions, plus the status lifecycle on the same
/// edge.
///
/// <para><b>The defect this test would have caught:</b> the observer's counter incremented once per
/// refused swing, so a clean-player run read <c>ExhaustionEvents 591</c> against <c>totalHits 551</c> —
/// more "events" than hits. A count that cannot tell "entered exhaustion" from "was still exhausted"
/// is not an event; <see cref="ExhaustionEdgeDetector"/> answers the question the counter could not.</para>
///
/// <para>The status half is proved against a real <see cref="StatusRuntime"/> (the same fixture shape
/// <c>ExhaustionPolicyTests</c> uses) with the payload the injector ships: ONE entry, ZERO stat mods —
/// a legal, validated payload that makes exhaustion bindable without moving a number.</para>
/// </summary>
[Trait("VerificationId", "core.exhaustion-edge")]
public class ExhaustionEdgeTests
{
    const string Ptr = "1a2b3c40";
    const string Match = "mk1";

    static ExhaustionPolicy EmptyPayloadPolicy(StatusCatalog catalog, string resourceId) =>
        new(catalog, new Dictionary<string, IReadOnlyList<StatusStatMod>>
        {
            [resourceId] = Array.Empty<StatusStatMod>(),
        });

    static StatusRuntime MakeRuntime(StatusCatalog catalog) =>
        new(catalog, (_, _) => ActorDerivedSnapshot.Empty);

    static bool HasStatus(StatusRuntime runtime, string ptr, string statusId) =>
        runtime.ForHost(ptr).Any(i => i.StatusId == statusId);

    [Fact]
    public void Consecutive_refused_swings_inside_one_window_produce_exactly_one_entering_edge()
    {
        var detector = new ExhaustionEdgeDetector();
        var entered = 0;

        // The swing that empties the pool, then ninety-nine more refusals inside the same window.
        for (var swing = 0; swing < 100; swing++)
        {
            var transition = detector.Observe(Ptr, "stamina", Match, resolvedValue: 0, nowTick: 100 + swing);
            if (transition.Edge == ExhaustionEdge.Entered) entered++;
            else Assert.Equal(ExhaustionEdge.None, transition.Edge);
        }

        Assert.Equal(1, entered);
        Assert.True(detector.IsExhausted(Ptr, "stamina", Match));
    }

    [Fact]
    public void The_next_afforded_swing_produces_exactly_one_recovering_edge_carrying_its_duration()
    {
        var detector = new ExhaustionEdgeDetector();
        detector.Observe(Ptr, "stamina", Match, 0, nowTick: 500);

        var recovered = 0;
        for (var swing = 0; swing < 10; swing++)
        {
            var transition = detector.Observe(Ptr, "stamina", Match, resolvedValue: 40, nowTick: 700 + swing);
            if (transition.Edge == ExhaustionEdge.None) continue;
            recovered++;
            Assert.Equal(ExhaustionEdge.Recovered, transition.Edge);
            // 200 ticks after entering, at 100 ms per tick.
            Assert.Equal(20_000, transition.ExhaustedForMs);
            Assert.Equal(40, transition.PoolValue);
        }

        Assert.Equal(1, recovered);
        Assert.False(detector.IsExhausted(Ptr, "stamina", Match));
    }

    [Fact]
    public void An_observation_without_an_actor_identity_or_a_resource_emits_nothing()
    {
        var detector = new ExhaustionEdgeDetector();

        Assert.Equal(ExhaustionEdge.None, detector.Observe(null, "stamina", Match, 0, 10).Edge);
        Assert.Equal(ExhaustionEdge.None, detector.Observe("   ", "stamina", Match, 0, 10).Edge);
        Assert.Equal(ExhaustionEdge.None, detector.Observe(Ptr, "", Match, 0, 10).Edge);
        Assert.Equal(0, detector.TrackedActors);

        // And a non-edge has no event and no payload — asking for one is a programming error.
        Assert.Throws<ArgumentOutOfRangeException>(() => ExhaustionEdgeEvents.KindFor(ExhaustionEdge.None));
        Assert.Throws<ArgumentException>(() => ExhaustionEdgeEvents.Build(default, "plant", Match));
    }

    [Fact]
    public void The_event_payload_carries_the_actor_the_resource_and_the_window()
    {
        var detector = new ExhaustionEdgeDetector();

        var entered = detector.Observe(Ptr, "stamina", Match, 0, 1_234);
        var enteredPayload = ExhaustionEdgeEvents.Build(entered, side: "plant", matchKey: Match);

        Assert.Equal(ExhaustionEdgeEvents.ExhaustedKind, ExhaustionEdgeEvents.KindFor(entered.Edge));
        Assert.Equal(Ptr, enteredPayload["ptr"]);
        Assert.Equal("plant", enteredPayload["side"]);
        Assert.Equal("stamina", enteredPayload["resourceId"]);
        Assert.Equal(1_234L, enteredPayload["tick"]);
        Assert.Equal(0L, enteredPayload["poolValue"]);
        Assert.Equal(Match, enteredPayload["matchKey"]);
        Assert.False(enteredPayload.ContainsKey("exhaustedForMs")); // entering has no duration yet

        var recovered = detector.Observe(Ptr, "stamina", Match, 7, 1_334);
        var recoveredPayload = ExhaustionEdgeEvents.Build(recovered, side: "plant", matchKey: Match);

        Assert.Equal(ExhaustionEdgeEvents.RecoveredKind, ExhaustionEdgeEvents.KindFor(recovered.Edge));
        Assert.Equal(10_000L, recoveredPayload["exhaustedForMs"]);
        Assert.Equal(7L, recoveredPayload["poolValue"]);
    }

    [Fact]
    public void The_status_is_applied_on_the_entering_edge_and_withdrawn_on_recovery_for_that_actor_only()
    {
        var catalog = new StatusCatalog();
        var policy = EmptyPayloadPolicy(catalog, "stamina");
        var runtime = MakeRuntime(catalog);
        var detector = new ExhaustionEdgeDetector();

        var entered = detector.Observe(Ptr, "stamina", Match, 0, 100);
        Assert.True(policy.Sync(runtime, entered.HostPtr, entered.ResourceId, resolvedValue: 0, now: DateTimeOffset.UnixEpoch));
        Assert.True(HasStatus(runtime, Ptr, "exhaustion.stamina"));

        // The empty payload really is empty: the status exists and is scoped to this actor and this
        // resource by its grant id, and it moves no channel -- which is the point of shipping the
        // lifecycle now and the debuff content with `lawn-tuning-profile`.
        var live = runtime.ForHost(Ptr).Single(i => i.StatusId == "exhaustion.stamina");
        Assert.Empty(live.StatMods);
        Assert.Equal(ExhaustionStatusIds.GrantIdFor(Ptr, "stamina"), live.GrantId);

        // A sibling resource on the SAME actor, and the same resource on ANOTHER actor, are untouched.
        Assert.False(HasStatus(runtime, Ptr, "exhaustion.qi"));
        Assert.False(HasStatus(runtime, "9999ffff", "exhaustion.stamina"));

        var recovered = detector.Observe(Ptr, "stamina", Match, 3, 200);
        policy.Sync(runtime, recovered.HostPtr, recovered.ResourceId, resolvedValue: 3, now: DateTimeOffset.UnixEpoch);
        Assert.False(HasStatus(runtime, Ptr, "exhaustion.stamina"));
    }

    [Fact]
    public void A_reused_pointer_starts_with_no_exhaustion_state_and_a_new_match_is_a_new_window()
    {
        var detector = new ExhaustionEdgeDetector();
        detector.Observe(Ptr, "stamina", Match, 0, 100);

        // IL2CPP reuses addresses: the death edge drops the window, so the new actor at this address is
        // observed fresh (entering again) instead of inheriting a window it never opened.
        detector.Forget(Ptr);
        Assert.False(detector.IsExhausted(Ptr, "stamina", Match));
        var afterReuse = detector.Observe(Ptr, "stamina", Match, 0, 300);
        Assert.Equal(ExhaustionEdge.Entered, afterReuse.Edge);

        // A new match is a new window WITHOUT any lifecycle call having to remember to clear it —
        // carrying a window across a match edge would attribute a stale exhaustion to a new run.
        var nextMatch = detector.Observe(Ptr, "stamina", "mk2", 0, 400);
        Assert.Equal(ExhaustionEdge.Entered, nextMatch.Edge);

        detector.Clear();
        Assert.Equal(0, detector.TrackedActors);
    }

    [Fact]
    public void Each_resource_is_tracked_independently_on_the_same_actor()
    {
        var detector = new ExhaustionEdgeDetector();

        Assert.Equal(ExhaustionEdge.Entered, detector.Observe(Ptr, "stamina", Match, 0, 100).Edge);
        // qi was never empty and never observed as empty: nothing to report, and no cross-talk.
        Assert.Equal(ExhaustionEdge.None, detector.Observe(Ptr, "qi", Match, 12, 100).Edge);

        // Recovering stamina must not clear (or report) qi's own window.
        Assert.Equal(ExhaustionEdge.Recovered, detector.Observe(Ptr, "stamina", Match, 5, 200).Edge);
        Assert.Equal(ExhaustionEdge.None, detector.Observe(Ptr, "qi", Match, 12, 200).Edge);
        Assert.False(detector.IsExhausted(Ptr, "qi", Match));
    }
}
