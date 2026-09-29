using FusionRpg.Core.Balance.Analytic;
using Xunit;

namespace FusionRpg.Core.Tests.Balance;

/// <summary>class-system-todo.md P4.5 — <see cref="ActionSchedule"/>'s deterministic per-round
/// action-selection walk, ported from <c>gk-core/tools/CombatSim/ActionEconomy.cs</c>
/// (<c>ActorPools</c>/<c>ActionPolicy</c>) since no shipped Core resolver exists for the action-cost
/// system yet (spec-action-costs.md: registered, "has no reader"). The three-action fixture below
/// mirrors <c>gk-core/tools/CombatSim/actions/basic.json</c> exactly — the action set
/// spec-deterministic-core.md §3's own <c>--actions basic</c> command targets.</summary>
public class ActionScheduleTests
{
    // Mirrors gk-core/tools/CombatSim/actions/basic.json exactly.
    static readonly ActionSchedule.ActionOption SkillStrike = new("skill-strike", Priority: 1, DamageMultiplier: 1.8, CostResourceId: "qi", CostShareOfOutputMilli: 300);
    static readonly ActionSchedule.ActionOption Strike = new("strike", Priority: 2, DamageMultiplier: 1.0, CostResourceId: "stamina", CostShareOfOutputMilli: 220);
    static readonly ActionSchedule.ActionOption Pass = new("pass", Priority: 99, DamageMultiplier: 0.0, CostResourceId: null, CostShareOfOutputMilli: 0);
    static readonly ActionSchedule.ActionOption[] BasicSet = { SkillStrike, Strike, Pass };

    // ---- NominalOutput / CostOf ----------------------------------------------------------------

    [Fact]
    public void NominalOutput_isBaseDamageTimesMultiplier()
    {
        Assert.Equal(180.0, ActionSchedule.NominalOutput(SkillStrike, 100), 9);
    }

    [Fact]
    public void CostOf_freeAction_isZero()
    {
        Assert.Equal(0.0, ActionSchedule.CostOf(Pass, 100));
    }

    [Fact]
    public void CostOf_handComputedForBothCostedBasicActions()
    {
        // skill-strike: NominalOutput=180, share=300/1000=0.3 -> 54.0
        Assert.Equal(54.0, ActionSchedule.CostOf(SkillStrike, 100), 9);
        // strike: NominalOutput=100, share=220/1000=0.22 -> 22.0
        Assert.Equal(22.0, ActionSchedule.CostOf(Strike, 100), 9);
    }

    // ---- Walk: argument validation -------------------------------------------------------------

    [Fact]
    public void Walk_nullArguments_reject()
    {
        var pools = new Dictionary<string, ActionSchedule.PoolState>();
        Assert.Throws<ArgumentNullException>(() => ActionSchedule.Walk(null!, pools, 100, 1));
        Assert.Throws<ArgumentNullException>(() => ActionSchedule.Walk(BasicSet, null!, 100, 1));
    }

    [Fact]
    public void Walk_emptyOptions_throws()
    {
        Assert.Throws<ArgumentException>(() => ActionSchedule.Walk(Array.Empty<ActionSchedule.ActionOption>(), new Dictionary<string, ActionSchedule.PoolState>(), 100, 1));
    }

    [Fact]
    public void Walk_noFreeFallbackAction_throws()
    {
        var noFallback = new[] { SkillStrike, Strike };
        Assert.Throws<ArgumentException>(() => ActionSchedule.Walk(noFallback, new Dictionary<string, ActionSchedule.PoolState>(), 100, 1));
    }

    [Fact]
    public void Walk_negativeOrNanBaseDamage_throws()
    {
        var pools = new Dictionary<string, ActionSchedule.PoolState>();
        Assert.Throws<ArgumentOutOfRangeException>(() => ActionSchedule.Walk(BasicSet, pools, -1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ActionSchedule.Walk(BasicSet, pools, double.NaN, 1));
    }

    [Fact]
    public void Walk_negativeRounds_throws()
    {
        var pools = new Dictionary<string, ActionSchedule.PoolState>();
        Assert.Throws<ArgumentOutOfRangeException>(() => ActionSchedule.Walk(BasicSet, pools, 100, -1));
    }

    [Fact]
    public void Walk_zeroRounds_returnsEmpty()
    {
        var pools = new Dictionary<string, ActionSchedule.PoolState>();
        var result = ActionSchedule.Walk(BasicSet, pools, 100, 0);
        Assert.Empty(result);
    }

    // ---- Walk: behavior -------------------------------------------------------------------------

    [Fact]
    public void Walk_fullPools_round1PicksTheHighestPriorityAffordableAction()
    {
        var pools = new Dictionary<string, ActionSchedule.PoolState>
        {
            ["qi"] = new(Value: 54, Max: 54, Regen: 20),
            ["stamina"] = new(Value: 100, Max: 100, Regen: 25),
        };
        var result = ActionSchedule.Walk(BasicSet, pools, 100, 1);
        Assert.Equal("skill-strike", result[0].ActionId);
        Assert.Equal(1.8, result[0].DamageMultiplier);
    }

    [Fact]
    public void Walk_everyResourceStarved_alwaysFallsBackToTheFreeAction()
    {
        var pools = new Dictionary<string, ActionSchedule.PoolState>
        {
            ["qi"] = new(Value: 0, Max: 54, Regen: 0),
            ["stamina"] = new(Value: 0, Max: 100, Regen: 0),
        };
        var result = ActionSchedule.Walk(BasicSet, pools, 100, 5);
        Assert.All(result, r => Assert.Equal("pass", r.ActionId));
    }

    [Fact]
    public void Walk_missingPoolEntryForACostedResource_treatsItAsNeverAffordable()
    {
        // No "qi" entry at all -- skill-strike must never be chosen; falls through to strike.
        var pools = new Dictionary<string, ActionSchedule.PoolState>
        {
            ["stamina"] = new(Value: 100, Max: 100, Regen: 25),
        };
        var result = ActionSchedule.Walk(BasicSet, pools, 100, 3);
        Assert.All(result, r => Assert.Equal("strike", r.ActionId));
    }

    [Fact]
    public void Walk_sevenRounds_matchesTheHandTracedCycleExactly()
    {
        // Hand-traced against ActionEconomy.cs's own formulas (CostOf=54 qi / 22 stamina at
        // baseDamage=100): qi starts full (54) and takes exactly 3 rounds of +20 regen to climb back
        // from 0 to 54 (0->20->40->60 clamped 54), so skill-strike recurs every 3rd round; stamina's
        // regen (25) exceeds strike's cost (22) so it is replenished to full every single round and
        // strike is always affordable whenever it is checked -- the same "regen exceeds cost, never
        // runs dry" shape class-system-ideal.md §8.1b documents for the real game's own tuning defect.
        var pools = new Dictionary<string, ActionSchedule.PoolState>
        {
            ["qi"] = new(Value: 54, Max: 54, Regen: 20),
            ["stamina"] = new(Value: 100, Max: 100, Regen: 25),
        };
        var result = ActionSchedule.Walk(BasicSet, pools, 100, 7);
        var ids = result.Select(r => r.ActionId).ToArray();
        Assert.Equal(
            new[] { "skill-strike", "strike", "strike", "skill-strike", "strike", "strike", "skill-strike" },
            ids);
    }

    [Fact]
    public void Walk_isPure_sameInputsSameOutputs()
    {
        var pools = new Dictionary<string, ActionSchedule.PoolState>
        {
            ["qi"] = new(Value: 54, Max: 54, Regen: 20),
            ["stamina"] = new(Value: 100, Max: 100, Regen: 25),
        };
        var a = ActionSchedule.Walk(BasicSet, pools, 100, 10);
        var b = ActionSchedule.Walk(BasicSet, pools, 100, 10);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Walk_doesNotMutateTheCallersInitialPoolsDictionary()
    {
        var pools = new Dictionary<string, ActionSchedule.PoolState>
        {
            ["qi"] = new(Value: 54, Max: 54, Regen: 20),
            ["stamina"] = new(Value: 100, Max: 100, Regen: 25),
        };
        ActionSchedule.Walk(BasicSet, pools, 100, 5);
        Assert.Equal(54, pools["qi"].Value);
        Assert.Equal(100, pools["stamina"].Value);
    }
}
