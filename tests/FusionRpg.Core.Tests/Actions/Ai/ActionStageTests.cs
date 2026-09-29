using System;
using System.Collections.Generic;
using System.IO;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Ai;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Actions.Ai;

/// <summary>
/// combat-ai `core-scorer` (module 1, CAI1.3, spec-core-scorer.md §5): one pass over held actions,
/// reaching `UsabilityEvaluator`, the resolvable-here seam, the reserve-floor decorator and the three
/// waste guards, each at its identity default.
/// </summary>
public class ActionStageTests
{
    static readonly EntityFacts SelfFacts = new(0, 0, 1000, -1, -1, -1, false, false, 0);
    static EntityFacts TargetFacts(int hpMilli = 1000) => new(1, 0, hpMilli, -1, -1, -1, false, false, 0);

    static CompiledAction Action(
        string id, ActionTag[]? tags = null, ActionKind kind = ActionKind.Skill,
        CooldownClass cooldownClass = CooldownClass.None, long cooldownTicks = 0,
        ICompiledPredicate? condition = null) => new(
        id, kind, 1, tags ?? Array.Empty<ActionTag>(), true, 1, false, false, "item.test",
        ActionEnvelope.NoOp with { ActionId = id, Class = cooldownClass, CooldownTicks = cooldownTicks },
        new CompiledTargetSpec(true, Array.Empty<FusionRpg.Contracts.TargetSpec>()),
        0, int.MaxValue, null, false, condition ?? PredicateCompiler.Always,
        Array.Empty<CompiledActionCost>(), Array.Empty<ActionScopeRow>());

    sealed class NeverAffordable : IAffordabilityCheck
    {
        public UsabilityResult Check(string actorKey, string actionId) =>
            UsabilityResult.Refuse(UsabilityReason.CannotAfford, "stamina");
    }

    static bool TryPick(
        IReadOnlyList<CompiledAction> held, out string actionId,
        IAffordabilityCheck? affordability = null, CooldownLedger? cooldowns = null,
        Func<string, bool>? resolvableHere = null, bool runWasteGuards = true,
        WasteGuardThresholds? guards = null, EntityFacts? targetFacts = null,
        Func<CompiledAction, int>? liveTargetsInAreaOf = null, Func<int>? opposingSideLiveCountOf = null) =>
        ActionStage.TryPick(
            "me", nowTick: 0, held, cooldowns ?? new CooldownLedger(), NoStanceHeld.Instance,
            affordability ?? AlwaysAffordable.Instance, casterPos: null, targetPos: null,
            SelfFacts, targetFacts ?? TargetFacts(), out actionId,
            resolvableHere, runWasteGuards, guards ?? WasteGuardThresholds.Off,
            liveTargetsInAreaOf: liveTargetsInAreaOf, opposingSideLiveCountOf: opposingSideLiveCountOf);

    [Fact]
    public void One_pass_reaches_the_first_usable_action_in_preference_order()
    {
        var held = new[] { Action("act.a"), Action("act.b") };
        Assert.True(TryPick(held, out var picked));
        Assert.Equal("act.a", picked);
    }

    /// <summary>Gate order is `UsabilityEvaluator`'s, unchanged: a cooldown refusal and an
    /// unaffordable refusal on the SAME action are both refusals -- ActionStage never re-derives which
    /// one "wins"; it calls `UsabilityEvaluator.Evaluate` and trusts its answer, proven here by a
    /// direct call with the identical inputs `ActionStage` would use.</summary>
    [Fact]
    public void Gate_order_is_the_shipped_UsabilityEvaluator_order_not_reimplemented()
    {
        var envelope = ActionEnvelope.NoOp with { ActionId = "act.a", Class = CooldownClass.Specific, CooldownTicks = 100 };
        var cooldowns = new CooldownLedger();
        cooldowns.Start("me", envelope, atTick: 0);

        var facts = new FactReader(SelfFacts, TargetFacts());
        var direct = UsabilityEvaluator.Evaluate(
            "me", envelope.ActionId, envelope, 0, int.MaxValue, actorHoldsAction: true, nowTick: 1,
            cooldowns, NoStanceHeld.Instance, new NeverAffordable(), null, null, PredicateCompiler.Always, ref facts);

        Assert.Equal(UsabilityReason.OnCooldown, direct.Reason);

        // ActionStage.cs calls UsabilityEvaluator.Evaluate rather than re-deriving gate precedence --
        // a source scan, not an argument, is what proves no second copy of the gate order exists.
        var text = File.ReadAllText(FindSourceFile("ActionStage.cs"));
        Assert.Contains("UsabilityEvaluator.Evaluate(", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolvable_here_default_admits_everything()
    {
        var held = new[] { Action("act.a") };
        Assert.True(TryPick(held, out var picked, resolvableHere: null));
        Assert.Equal("act.a", picked);
    }

    [Fact]
    public void Resolvable_here_false_skips_to_the_next_action()
    {
        var held = new[] { Action("act.a"), Action("act.b") };
        Assert.True(TryPick(held, out var picked, resolvableHere: id => id != "act.a"));
        Assert.Equal("act.b", picked);
    }

    [Fact]
    public void Reserve_floor_refuses_through_gate_3_and_never_starves_a_basic_attack()
    {
        var basic = Action("act.basic", kind: ActionKind.Basic);
        var skill = Action("act.skill");

        var reserveFloor = new ReserveFloorAffordability(
            AlwaysAffordable.Instance,
            new[] { new AiReserveFloor("stamina", FloorMilliOfMax: 500) },
            kindOf: id => id == basic.ActionId ? ActionKind.Basic : ActionKind.Skill,
            currentBalanceOf: _ => 10,   // at/below the floor
            maxOf: _ => 100);            // floor = 500 permille of 100 = 50 -- 10 <= 50, refused

        // The skill is refused by the reserve floor; the basic attack, structurally exempt, still fires.
        Assert.True(TryPick(new[] { basic }, out var pickedBasic, affordability: reserveFloor));
        Assert.Equal(basic.ActionId, pickedBasic);

        Assert.False(TryPick(new[] { skill }, out _, affordability: reserveFloor));
    }

    /// <summary>
    /// combat-ai CAI2.6 (found by CAI2.3's parity test): the shipped floor is a PRE-CONDITION on the
    /// balance, not a post-payment check — `ReserveFloorAffordability.cs:106` refuses when
    /// `currentBalanceOf(resourceId) <= EffectiveFloorAbsolute(resourceId)`, evaluated BEFORE any cost is
    /// considered. This pins the `<=` boundary exactly, which is where the seam and the ideal's own
    /// wording ("may not drop below a fraction of its max **after paying**", the rule the analytic twin
    /// implements) diverge: at balance == floor the seam refuses, where a post-payment rule would admit
    /// any action cheaper than the slack. If CAI2.6 is ruled the other way, THIS test is the one that
    /// changes.
    /// </summary>
    /// <summary>
    /// The ruled rule's boundary (CAI2.6, 2026-09-23). The floor binds what REMAINS after paying, so a
    /// cost that would take the pool BELOW the floor is refused and one that lands EXACTLY on it is
    /// admitted — that is what "may not drop below a fraction of its max after paying"
    /// (`combat-ai-ideal.md:318`) means, and it is the reading the analytic twin already implemented.
    /// </summary>
    [Fact]
    public void The_floor_admits_landing_on_it_and_refuses_dropping_below_it()
    {
        var skill = Action("act.skill");

        // floor = 500 permille of 100 = 50.
        ReserveFloorAffordability FloorAt(long balance, long cost) => new(
            AlwaysAffordable.Instance,
            new[] { new AiReserveFloor("stamina", FloorMilliOfMax: 500) },
            kindOf: _ => ActionKind.Skill,
            currentBalanceOf: _ => balance,
            maxOf: _ => 100,
            costOf: (_, _, _) => cost);

        Assert.True(TryPick(new[] { skill }, out _, affordability: FloorAt(balance: 100, cost: 50)));   // lands ON it
        Assert.False(TryPick(new[] { skill }, out _, affordability: FloorAt(balance: 100, cost: 51))); // one below it
        Assert.True(TryPick(new[] { skill }, out _, affordability: FloorAt(balance: 50, cost: 0)));     // zero-cost at it
    }

    /// <summary>
    /// The consequence the OLD rule had, and the reason CAI2.6 ruled it wrong: because the floor was
    /// checked before the action's own cost, a ZERO-cost non-Basic action was refused too — so the actor
    /// returned `ActionIntent.None` and idled rather than falling through to a free option. The ruled
    /// rule (post-payment, `combat-ai-ideal.md:318`) admits it, which is what matches the twin's
    /// deliberately unfloored free fallback (a floor that could starve the walk is a hang).
    /// </summary>
    [Fact]
    public void A_zero_cost_action_is_admitted_at_the_floor_so_the_actor_never_idles()
    {
        var free = Action("act.free");       // no costs authorise anything: the inner check is AlwaysAffordable

        // balance == floor == 50, cost 0 -> `50 - 0 >= 50` -> admitted. Under the OLD pre-condition rule
        // (`current <= floor`) this was REFUSED, which is the idling CAI2.6 ruled wrong.
        var atFloor = new ReserveFloorAffordability(
            AlwaysAffordable.Instance,
            new[] { new AiReserveFloor("stamina", FloorMilliOfMax: 500) },
            kindOf: _ => ActionKind.Skill,
            currentBalanceOf: _ => 50,
            maxOf: _ => 100,
            costOf: (_, _, _) => 0);

        Assert.True(TryPick(new[] { free }, out _, affordability: atFloor));

        // The residual edge the ruled comparison still refuses, named rather than hidden: a pool ALREADY
        // below the floor refuses even a zero-cost non-Basic action, because `10 - 0 >= 50` is false.
        // The actor is not idle because `ActionKind.Basic` is structurally exempt (the assertion above
        // it in this file) -- which is the seam's own answer to the starvation question, and the reason
        // the twin's unfloored free fallback and this exemption are the same rule stated twice.
        var belowFloor = new ReserveFloorAffordability(
            AlwaysAffordable.Instance,
            new[] { new AiReserveFloor("stamina", FloorMilliOfMax: 500) },
            kindOf: _ => ActionKind.Skill,
            currentBalanceOf: _ => 10,
            maxOf: _ => 100,
            costOf: (_, _, _) => 0);

        Assert.False(TryPick(new[] { free }, out _, affordability: belowFloor));
    }

    [Fact]
    public void Poise_floor_is_the_max_of_the_authored_floor_and_the_expected_reaction_spend()
    {
        var floors = new[] { new AiReserveFloor("poise", FloorMilliOfMax: 100), new AiReserveFloor("stamina", FloorMilliOfMax: 200) };

        // poise max=1000 -> authored floor = 100 permille of 1000 = 100. Expected reaction spend =
        // 40 * 5000 / 1000 = 200, which is HIGHER -- the max wins.
        var boosted = new ReserveFloorAffordability(
            AlwaysAffordable.Instance, floors, kindOf: _ => ActionKind.Skill,
            currentBalanceOf: _ => 0, maxOf: id => id == "poise" ? 1000 : 500,
            reactionPoiseSpend: 40, reactionsPerRoundExpectedMilli: 5000);

        Assert.Equal(200, boosted.EffectiveFloorAbsolute("poise"));
        // A second resource with a floor is unaffected by the poise boost -- stamina's floor stays at
        // its authored value: 200 permille of 500 = 100.
        Assert.Equal(100, boosted.EffectiveFloorAbsolute("stamina"));
    }

    [Fact]
    public void Both_reaction_arguments_zero_leaves_every_floor_at_its_authored_value()
    {
        var floors = new[] { new AiReserveFloor("poise", FloorMilliOfMax: 300), new AiReserveFloor("stamina", FloorMilliOfMax: 200) };
        var identity = new ReserveFloorAffordability(
            AlwaysAffordable.Instance, floors, kindOf: _ => ActionKind.Skill,
            currentBalanceOf: _ => 0, maxOf: id => id == "poise" ? 1000 : 500);
            // reactionPoiseSpend/reactionsPerRoundExpectedMilli both default to 0

        Assert.Equal(300, identity.EffectiveFloorAbsolute("poise"));   // 300 permille of 1000
        Assert.Equal(100, identity.EffectiveFloorAbsolute("stamina")); // 200 permille of 500
    }

    [Fact]
    public void RunWasteGuards_false_skips_the_census_entirely()
    {
        var areaCalls = 0;
        var opposingCalls = 0;
        int LiveTargetsInArea(CompiledAction a) { areaCalls++; return 0; }
        int OpposingSideLiveCount() { opposingCalls++; return 0; }

        var held = new[] { Action("act.a", tags: new[] { ActionTag.Buff }) };
        Assert.True(TryPick(held, out _, runWasteGuards: false,
            guards: new WasteGuardThresholds(MinTargetsForArea: 5, KillMarginMilli: 999, FightEndingLiveCount: 5),
            liveTargetsInAreaOf: LiveTargetsInArea, opposingSideLiveCountOf: OpposingSideLiveCount));

        Assert.Equal(0, areaCalls);
        Assert.Equal(0, opposingCalls);
    }

    [Fact]
    public void MinTargetsForArea_off_at_seed_never_refuses()
    {
        var held = new[] { Action("act.a") };
        Assert.True(TryPick(held, out _, runWasteGuards: true,
            guards: WasteGuardThresholds.Off, liveTargetsInAreaOf: _ => 0));
    }

    [Fact]
    public void MinTargetsForArea_switched_on_refuses_exactly_its_own_case()
    {
        var held = new[] { Action("act.a") };
        Assert.False(TryPick(held, out _, runWasteGuards: true,
            guards: new WasteGuardThresholds(MinTargetsForArea: 3, KillMarginMilli: -1, FightEndingLiveCount: -1),
            liveTargetsInAreaOf: _ => 1));
    }

    [Fact]
    public void KillMargin_off_at_seed_never_refuses_a_nearly_dead_target()
    {
        var held = new[] { Action("act.a") };
        Assert.True(TryPick(held, out _, runWasteGuards: true, guards: WasteGuardThresholds.Off,
            targetFacts: TargetFacts(hpMilli: 1)));
    }

    [Fact]
    public void KillMargin_switched_on_refuses_a_target_already_at_or_below_the_margin()
    {
        var held = new[] { Action("act.a") };
        Assert.False(TryPick(held, out _, runWasteGuards: true,
            guards: new WasteGuardThresholds(MinTargetsForArea: 0, KillMarginMilli: 50, FightEndingLiveCount: -1),
            targetFacts: TargetFacts(hpMilli: 30)));
    }

    [Fact]
    public void FightEndingLiveCount_off_at_seed_never_refuses_a_buff()
    {
        var held = new[] { Action("act.a", tags: new[] { ActionTag.Buff }) };
        Assert.True(TryPick(held, out _, runWasteGuards: true, guards: WasteGuardThresholds.Off,
            opposingSideLiveCountOf: () => 0));
    }

    [Fact]
    public void FightEndingLiveCount_switched_on_refuses_a_buff_but_never_a_non_buff()
    {
        var buff = new[] { Action("act.buff", tags: new[] { ActionTag.Buff }) };
        var attack = new[] { Action("act.attack", tags: new[] { ActionTag.Offensive }) };
        var guards = new WasteGuardThresholds(MinTargetsForArea: 0, KillMarginMilli: -1, FightEndingLiveCount: 1);

        Assert.False(TryPick(buff, out _, runWasteGuards: true, guards: guards, opposingSideLiveCountOf: () => 1));
        Assert.True(TryPick(attack, out _, runWasteGuards: true, guards: guards, opposingSideLiveCountOf: () => 1));
    }

    static string FindSourceFile(string fileName)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10 && dir != null; i++)
        {
            var candidate = Directory.GetFiles(dir, fileName, new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
            });
            if (candidate.Length > 0) return candidate[0];
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException($"{fileName} not found by walking up from {AppContext.BaseDirectory}");
    }
}

/// <summary>
/// combat-ai `ai-tiers-personality` (module 3, CAI1.9, spec-ai-tiers-personality.md §"Testing
/// strategy"): performance tier is "the same core with the scoring stage switched off" — placed in
/// module 1's own test file because both assertions exercise `ActionStage.TryPick` directly, module
/// 1's own mechanism, never a second implementation. `Performance_tier_computes_no_candidate_inputs`
/// is asserted in `CoreIntentPolicyTests.cs` instead (module 3's own file) — see that test's own doc
/// comment for why `ActionStage.TryPick` has no notion of a target candidate to observe it with.
/// </summary>
public class PerformanceTierTests
{
    static readonly EntityFacts SelfFacts = new(0, 0, 1000, -1, -1, -1, false, false, 0);
    static EntityFacts TargetFacts(int hpMilli = 1000) => new(1, 0, hpMilli, -1, -1, -1, false, false, 0);

    static CompiledAction Action(string id) => new(
        id, ActionKind.Skill, 1, Array.Empty<ActionTag>(), true, 1, false, false, "item.test",
        ActionEnvelope.NoOp with { ActionId = id },
        new CompiledTargetSpec(true, Array.Empty<FusionRpg.Contracts.TargetSpec>()),
        0, int.MaxValue, null, false, PredicateCompiler.Always,
        Array.Empty<CompiledActionCost>(), Array.Empty<ActionScopeRow>());

    [Fact]
    public void Performance_tier_still_runs_the_reserve_floor_and_the_six_gates()
    {
        var basic = Action("act.basic");
        var skill = Action("act.skill");
        var reserveFloor = new ReserveFloorAffordability(
            AlwaysAffordable.Instance,
            new[] { new AiReserveFloor("stamina", FloorMilliOfMax: 500) },
            kindOf: id => id == basic.ActionId ? ActionKind.Basic : ActionKind.Skill,
            currentBalanceOf: _ => 10, maxOf: _ => 100); // floor = 50, 10 <= 50 -> refused

        // runWasteGuards: false (the performance-tier flag) never bypasses gate 3 -- the reserve
        // floor is a DIFFERENT lever from the three waste guards (spec §2's own distinction).
        Assert.False(ActionStage.TryPick(
            "me", nowTick: 0, new[] { skill }, new CooldownLedger(), NoStanceHeld.Instance,
            reserveFloor, casterPos: null, targetPos: null, SelfFacts, TargetFacts(), out _,
            runWasteGuards: false));

        Assert.True(ActionStage.TryPick(
            "me", nowTick: 0, new[] { basic }, new CooldownLedger(), NoStanceHeld.Instance,
            reserveFloor, casterPos: null, targetPos: null, SelfFacts, TargetFacts(), out var pickedBasic,
            runWasteGuards: false));
        Assert.Equal(basic.ActionId, pickedBasic);
    }

    /// <summary>Asserted by construction, not by a mock: passing the SAME `CooldownLedger` instance to
    /// both a `runWasteGuards: true` call and a `runWasteGuards: false` call for the same actor means a
    /// cooldown one call starts is honored by the other -- there is no separate "performance ledger".
    /// </summary>
    [Fact]
    public void Performance_tier_and_smart_tier_share_one_ledger_and_one_gate_set()
    {
        var action = Action("act.a");
        var envelope = ActionEnvelope.NoOp with { ActionId = action.ActionId, Class = CooldownClass.Specific, CooldownTicks = 100 };
        var withCooldown = action with { Envelope = envelope };
        var sharedLedger = new CooldownLedger();
        sharedLedger.Start("me", envelope, atTick: 0);

        // Smart tier (runWasteGuards: true) sees the cooldown this shared ledger already holds.
        Assert.False(ActionStage.TryPick(
            "me", nowTick: 1, new[] { withCooldown }, sharedLedger, NoStanceHeld.Instance,
            AlwaysAffordable.Instance, casterPos: null, targetPos: null, SelfFacts, TargetFacts(), out _,
            runWasteGuards: true));

        // Performance tier, the SAME ledger instance, sees the SAME cooldown -- one gate set.
        Assert.False(ActionStage.TryPick(
            "me", nowTick: 1, new[] { withCooldown }, sharedLedger, NoStanceHeld.Instance,
            AlwaysAffordable.Instance, casterPos: null, targetPos: null, SelfFacts, TargetFacts(), out _,
            runWasteGuards: false));
    }
}
