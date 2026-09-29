using FusionRpg.Core.Actions.Cost;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Actions;

/// <summary>
/// T18 (action-todo.md, spec-action-costs.md §9): run pools and rest — the Core-side half.
/// Persistence itself (save/load/rest-delete, "no run row means a run of one") is proved against a
/// real database in <c>FusionRpg.Data.Tests.RunPoolStoreTests</c>; this covers the two claims that
/// are Core's to make: cooldowns never cross a battle boundary, and pools resolve to a concrete
/// value with the clock dropped at that same boundary (T15's own <c>SettleAll</c>, exercised here
/// specifically as the run-boundary operation rather than as a generic reader property).
/// </summary>
public class RunPoolBoundaryTests
{
    static ActorDerivedSnapshot Snapshot(string resourceId, double max, double regen)
    {
        var registry = DerivedStatRegistry.CreateDefault();
        var composer = new DerivedComposer(registry);
        return composer.Compose(new[]
        {
            new DerivedModifier(DerivedStatChannels.ResourceMax(resourceId), DerivedModifierOp.Flat, max, SourceId: "test"),
            new DerivedModifier(DerivedStatChannels.ResourceRegen(resourceId), DerivedModifierOp.Flat, regen, SourceId: "test"),
        });
    }

    [Fact]
    public void ACooldownStartedInOneBattleHasNoEffectOnAFreshCooldownLedger()
    {
        // Every battle constructs its own CooldownLedger (BattleEngine.Resolve, the Rig helpers in
        // the timeline tests) -- there is no save/load path for one anywhere in this codebase. This
        // test pins that as an actual property rather than an absence nobody checks: a cooldown
        // started against one ledger is invisible to a second, fresh one for the SAME actor and the
        // SAME envelope.
        var envelope = new ActionEnvelope { ActionId = "act.strike", CooldownTicks = 1000, Class = CooldownClass.Specific };

        var battleOne = new CooldownLedger();
        battleOne.Start("wave:0", envelope, atTick: 0);
        Assert.False(battleOne.IsReady("wave:0", envelope, nowTick: 1)); // on cooldown, same ledger

        var battleTwo = new CooldownLedger(); // a new battle -- nothing carried over
        Assert.True(battleTwo.IsReady("wave:0", envelope, nowTick: 1));
    }

    [Fact]
    public void PoolsResolveToAConcreteValueAtTheBoundaryWithNoClockAttached()
    {
        // The Core half of "refill at rest / persist at an encounter boundary": SettleAll's return
        // shape IS what a caller hands to RpgStore.SaveRunPools -- a bare id->value map, proven here
        // as the exact boundary operation rather than a generic mid-battle reader property.
        var derived = Snapshot("stamina", max: 100, regen: 5);
        var pools = ActorResourcePools.CreateFull(derived, atTick: 0);

        var atBoundary = pools.SettleAll(nowTick: 10, derived);

        Assert.Equal(100, atBoundary["stamina"]); // clamped at max -- started full, only regen accrued
        Assert.IsType<Dictionary<string, long>>(atBoundary); // a bare value map: exactly RpgStore.SaveRunPools' input shape
    }

    [Fact]
    public void HpIsPersistedLikeEveryOtherPoolNeverOmitted()
    {
        var derived = Snapshot("hp", max: 500, regen: 0);
        var pools = ActorResourcePools.CreateFull(derived, atTick: 0);

        var atBoundary = pools.SettleAll(nowTick: 0, derived);

        Assert.True(atBoundary.ContainsKey("hp"));
        Assert.Equal(500, atBoundary["hp"]);
    }
}
