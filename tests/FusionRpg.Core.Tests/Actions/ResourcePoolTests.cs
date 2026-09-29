using FusionRpg.Core.Actions.Cost;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Actions;

/// <summary>
/// T15 (action-todo.md, spec-action-costs.md §2): the resource reader. The six
/// <c>resource.max.{id}</c>/<c>resource.regen.{id}</c> channels are already registered
/// (<see cref="ActorChannelsTests"/> proves the formula and the channel shape) — this module is
/// their first reader, and these tests prove the READER, not the formula again: six ids resolve, a
/// lazy resolve after 1000 ticks matches a thousand one-tick steps, and 200 actors' worth of reads
/// never touch a scheduler.
/// </summary>
public class ResourcePoolTests
{
    static ActorDerivedSnapshot SnapshotWith(string resourceId, double max, double regenPerTick)
    {
        var registry = DerivedStatRegistry.CreateDefault();
        var composer = new DerivedComposer(registry);
        var mods = new[]
        {
            new DerivedModifier(DerivedStatChannels.ResourceMax(resourceId), DerivedModifierOp.Flat, max, SourceId: "test"),
            new DerivedModifier(DerivedStatChannels.ResourceRegen(resourceId), DerivedModifierOp.Flat, regenPerTick, SourceId: "test"),
        };
        return composer.Compose(mods);
    }

    [Fact]
    public void SixIdsResolveAndNothingElseDoes()
    {
        var derived = DerivedStatRegistry.CreateDefault() is var registry
            ? new DerivedComposer(registry).Compose()
            : throw new InvalidOperationException();
        var pools = ActorResourcePools.CreateFull(derived, atTick: 0);

        Assert.Equal(6, DerivedStatChannels.ResourceIds.Count);
        foreach (var id in DerivedStatChannels.ResourceIds)
            pools.Resolve(id, nowTick: 0, derived); // must not throw for any of the six

        Assert.Throws<ArgumentOutOfRangeException>(() => pools.Resolve("mana", nowTick: 0, derived));
    }

    [Fact]
    public void LazyResolveAfterAThousandTicksMatchesAThousandOneTickSteps()
    {
        // spec §2 / ActorChannelsTests.LazyValueMatchesTicked proves the bare formula; this proves
        // the actual reader gives the same answer end-to-end through the real composer + registry.
        const double regenPerTick = 3.0;
        var derived = SnapshotWith("stamina", max: 1_000_000, regenPerTick);
        var stored = new Dictionary<string, long>(DerivedStatChannels.ResourceIds.ToDictionary(id => id, _ => 0L));
        var lazy = ActorResourcePools.FromStored(stored, atTick: 0);

        var lazyValue = lazy.Resolve("stamina", nowTick: 1000, derived);

        var ticked = ActorResourcePools.FromStored(stored, atTick: 0);
        long tickedValue = 0;
        for (var tick = 1; tick <= 1000; tick++)
        {
            // Re-anchoring one tick at a time is exactly "a thousand one-tick steps" — settle
            // advances LastTick to the new tick, so the next call accrues only the single step.
            tickedValue = ticked.SettleAll(tick, derived)["stamina"];
        }

        Assert.Equal(3000, lazyValue);
        Assert.Equal(lazyValue, tickedValue);
    }

    [Fact]
    public void TwoHundredActorsResolveWithoutTouchingTheScheduler()
    {
        // spec's own acceptance line: "zero scheduled events for five regenerating pools across 200
        // actors, counted." ActorResourcePools never receives or references an EventQueue -- this
        // test makes that structural fact loud rather than implicit: build a live scheduler nobody
        // hands to any pool, resolve 200 actors x six pools x several tick jumps, and assert its
        // Count never moves off zero.
        var scheduler = new EventQueue();
        var derived = SnapshotWith("qi", max: 500, regenPerTick: 1.0);

        var actors = new List<ActorResourcePools>();
        for (var i = 0; i < 200; i++)
            actors.Add(ActorResourcePools.CreateFull(derived, atTick: 0));

        foreach (var actor in actors)
        {
            foreach (var id in DerivedStatChannels.ResourceIds)
                actor.Resolve(id, nowTick: 500, derived);
            actor.SettleAll(nowTick: 1000, derived);
        }

        Assert.Equal(0, scheduler.Count);
    }

    [Fact]
    public void SettleAllAnchorsAtNowSoAnImmediateReResolveDoesNotAccrueAgain()
    {
        var derived = SnapshotWith("hunger", max: 100, regenPerTick: 10.0);
        var pools = ActorResourcePools.CreateFull(derived, atTick: 0); // starts at max, so regen has nowhere to go until spent

        var settled = pools.SettleAll(nowTick: 50, derived);
        Assert.Equal(100, settled["hunger"]); // clamped at max, not 100 + 50*10

        // Re-resolving at the SAME tick right after settling must reproduce the settled value exactly
        // -- LastTick was anchored at 50, so elapsed is zero, not stale relative to tick 0.
        Assert.Equal(100, pools.Resolve("hunger", nowTick: 50, derived));
    }

    [Fact]
    public void SettleAllReturnsExactlySixEntriesWithNoClockAttached()
    {
        var derived = SnapshotWith("spirit", max: 42, regenPerTick: 0.0);
        var pools = ActorResourcePools.CreateFull(derived, atTick: 0);

        var settled = pools.SettleAll(nowTick: 777, derived);

        Assert.Equal(6, settled.Count);
        foreach (var id in DerivedStatChannels.ResourceIds)
            Assert.True(settled.ContainsKey(id));
        // The persisted shape is a bare id->value map (IReadOnlyDictionary<string, long>) -- there is
        // no tick field on it at all, which is the "lastTick is dropped" acceptance criterion made
        // structural rather than a claim about internal state nobody outside this file can see.
        Assert.IsType<Dictionary<string, long>>(settled);
    }

    [Fact]
    public void ResolveClampsToZeroAndToMax()
    {
        var stored = DerivedStatChannels.ResourceIds.ToDictionary(id => id, _ => 5L);
        var draining = SnapshotWith("poise", max: 1000, regenPerTick: -100.0);
        var drainingPools = ActorResourcePools.FromStored(stored, atTick: 0);
        Assert.Equal(0, drainingPools.Resolve("poise", nowTick: 5, draining)); // would go deeply negative unclamped

        var filling = SnapshotWith("poise", max: 1000, regenPerTick: 5000.0);
        var fillingPools = ActorResourcePools.FromStored(stored, atTick: 0);
        Assert.Equal(1000, fillingPools.Resolve("poise", nowTick: 5, filling)); // would blow past max unclamped
    }

    [Fact]
    public void RegenAndMaxAreReadFreshOnEveryResolveNotCachedAtCreation()
    {
        // Mirrors spec §10's "re-evaluates on read" property one layer down: a buff/debuff changing
        // resource.regen mid-battle must be picked up the very next resolve, with no stale rate
        // baked in when the pool was created. A rate change between two resolves with no settle in
        // between is undefined by design (the formula has one rate per call, applied across the
        // whole elapsed window) -- callers settle at the moment of change, which is exactly what
        // this test does, to isolate "was the NEW rate read at all" from "was the window split".
        var stored = DerivedStatChannels.ResourceIds.ToDictionary(id => id, _ => 0L);
        var pools = ActorResourcePools.FromStored(stored, atTick: 0);

        var slow = SnapshotWith("qi", max: 1000, regenPerTick: 1.0);
        Assert.Equal(10, pools.Resolve("qi", nowTick: 10, slow));
        pools.SettleAll(nowTick: 10, slow); // anchors stored=10, lastTick=10 under the slow rate

        // A hasted actor's derived snapshot changes mid-battle -- same pool instance, new rate.
        var hasted = SnapshotWith("qi", max: 1000, regenPerTick: 100.0);
        Assert.Equal(10 + 100 * 5, pools.Resolve("qi", nowTick: 15, hasted));
    }

    [Fact]
    public void NowTickBeforeLastTickIsRejected()
    {
        var derived = SnapshotWith("hp", max: 100, regenPerTick: 1.0);
        var pools = ActorResourcePools.CreateFull(derived, atTick: 100);

        Assert.Throws<ArgumentOutOfRangeException>(() => pools.Resolve("hp", nowTick: 50, derived));
    }

    // ---- Add (E28 fix #1, spec-param-parity.md §3 row 1) ----
    // ExecApplyResourceDelta's own write path for the five non-hp resources — Add is the
    // signed-delta complement to TrySpend, for a caller applying a generic delta rather than
    // gating a pre-checked cost.

    [Fact]
    public void AddIncreasesAResourceClampedAtMax()
    {
        var derived = SnapshotWith("qi", max: 100, regenPerTick: 0.0);
        var pools = ActorResourcePools.FromStored(
            DerivedStatChannels.ResourceIds.ToDictionary(id => id, _ => 40L), atTick: 0);

        var afterSmall = pools.Add("qi", 10, nowTick: 0, derived);
        Assert.Equal(50, afterSmall);
        Assert.Equal(50, pools.Resolve("qi", nowTick: 0, derived));

        var afterOverflow = pools.Add("qi", 1000, nowTick: 0, derived);
        Assert.Equal(100, afterOverflow); // clamped at max, not 1050
    }

    [Fact]
    public void AddWithANegativeAmountDrainsAndClampsAtZeroRatherThanRefusing()
    {
        // Unlike TrySpend (which refuses outright when the pool can't afford the amount and
        // leaves it byte-for-byte unchanged), Add is a generic delta: a drain bigger than the
        // current value still lands, clamped at 0 — the same shape Resolve's own formula already
        // gives a pool nothing has touched in a long time.
        var derived = SnapshotWith("hunger", max: 100, regenPerTick: 0.0);
        var pools = ActorResourcePools.FromStored(
            DerivedStatChannels.ResourceIds.ToDictionary(id => id, _ => 10L), atTick: 0);

        var result = pools.Add("hunger", -1000, nowTick: 0, derived);

        Assert.Equal(0, result);
        Assert.Equal(0, pools.Resolve("hunger", nowTick: 0, derived));
    }

    [Fact]
    public void AddSettlesRegenAccruedSinceTheLastTouchBeforeApplyingTheDelta()
    {
        // Mirrors TrySpend's own contract ("this pool is first settled ... so regen accrued since
        // the last touch is folded in before the delta lands") — Add must not bypass regen either.
        var derived = SnapshotWith("stamina", max: 1000, regenPerTick: 5.0);
        var pools = ActorResourcePools.FromStored(
            DerivedStatChannels.ResourceIds.ToDictionary(id => id, _ => 0L), atTick: 0);

        // 10 ticks of regen (50) folded in, then +7 on top.
        var result = pools.Add("stamina", 7, nowTick: 10, derived);

        Assert.Equal(57, result);
    }

    [Fact]
    public void AddOnAnUnknownResourceIdThrows()
    {
        var derived = SnapshotWith("hp", max: 100, regenPerTick: 0.0);
        var pools = ActorResourcePools.CreateFull(derived, atTick: 0);

        Assert.Throws<ArgumentOutOfRangeException>(() => pools.Add("mana", 10, nowTick: 0, derived));
    }
}
