using System;
using System.Collections.Generic;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Ai;
using FusionRpg.Core.Actions.Cost;
using FusionRpg.Core.Balance.Analytic;
using FusionRpg.Core.Battle.Board;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Balance;

/// <summary>
/// combat-ai `action-schedule-twin` (CAI2.3, spec-action-schedule-twin.md §1 "a twin is not a second
/// mechanism"): the parity test the spec calls the module's mechanism of the guarantee. The twin cannot
/// CALL the core policy — different inputs, different domain — so what keeps them honest is a PROVEN
/// agreement, and this is it: the real <see cref="CoreIntentPolicy"/> in its PERFORMANCE tier and the
/// analytic twin walk the same fixture round by round and must choose the same action each round.
///
/// <para><b>The two correspondences are made EXPLICITLY here, and that is a test-side choice, not a
/// production model.</b> (1) The twin's <c>PoolState.Regen</c> is units per ROUND while
/// <c>ActorResourcePools</c>' regen is per-mille per TICK, and mapping one to the other is the spec's own
/// Open question 2 whose recommended default is to avoid inventing a mapping — so this fixture runs both
/// sides with regen <b>zero</b>, which makes the trajectories pure subtraction and removes the question
/// entirely. (2) The twin prices by <c>CostShareOfOutputMilli</c> while the real ledger prices an
/// absolute amount through `<c>anchorCost(Θ) × costMulti(rung)</c>`; with <c>Θ = 0</c> (identity theta
/// scale) and rung 1 (identity multiplier) the real cost IS the authored amount, so the test derives each
/// share as <c>amount × 1000 / (baseDamage × multiplier)</c>. Neither correspondence decides who owns the
/// production projection (module 2 vs this module) — it only makes the two walkers comparable.</para>
/// </summary>
public class ActionScheduleMatchesCorePolicyTests
{
    const string Actor = "squad:0";
    const string Enemy = "wave:1";
    const double Base = 100.0;

    sealed class FakeView : IBattleView
    {
        public readonly List<string> Actors = new() { Actor, Enemy };
        public readonly Dictionary<string, int> Sides = new(StringComparer.Ordinal) { [Actor] = 0, [Enemy] = 1 };
        public readonly Dictionary<string, List<CompiledAction>> Held = new(StringComparer.Ordinal);
        readonly List<CompiledAction> _enemyHeld = new();

        /// <summary>The overkill witness drives this. `ActionStage.PassesWasteGuards` reads the
        /// TARGET's `FactsOf(...).HpMilli` for its `KillMarginMilli` guard, so a fixture that wants
        /// the core's waste guard to fire must be able to move that number; every other case leaves it
        /// at full.</summary>
        public int TargetHpMilli = 1000;

        public FakeView(IReadOnlyList<CompiledAction> held) { Held[Actor] = new List<CompiledAction>(held); Held[Enemy] = _enemyHeld; }

        /// <summary>All six pools at max 1000 with regen 0 — `resource.max.stamina` is what
        /// `ActorResourcePools` CLAMPS to, so a snapshot without it resolves every pool to 0 and
        /// every costed action would be refused for a reason that has nothing to do with policy.
        /// Static and built once: composing it per call would be a per-decision fold.</summary>
        internal static readonly ActorDerivedSnapshot _full = BuildFull();
        internal static ActorDerivedSnapshot Full() => _full;

        static ActorDerivedSnapshot BuildFull()
        {
            var composer = new DerivedComposer(DerivedStatRegistry.CreateDefault());
            var mods = new List<DerivedModifier>
            {
                new(DerivedStatChannels.ProgressionPower, DerivedModifierOp.Flat, 0.0, SourceId: "test"),
                new(DerivedStatChannels.ProgressionRealm, DerivedModifierOp.Flat, 1.0, SourceId: "test"),
            };
            foreach (var id in DerivedStatChannels.ResourceIds)
            {
                mods.Add(new DerivedModifier(DerivedStatChannels.ResourceMax(id), DerivedModifierOp.Flat, 1000.0, SourceId: "test"));
                mods.Add(new DerivedModifier(DerivedStatChannels.ResourceRegen(id), DerivedModifierOp.Flat, 0.0, SourceId: "test"));
            }
            return composer.Compose(mods);
        }

        public IReadOnlyList<string> LiveActorKeys => Actors;
        public int SideOf(string actorKey) => Sides[actorKey];
        public GridPos? PositionOf(string actorKey) => new GridPos(0, actorKey == Actor ? 0 : 1);
        public EntityFacts FactsOf(string actorKey) =>
            new(Sides[actorKey], 0, actorKey == Actor ? 1000 : TargetHpMilli, -1, 0, 0, false, false, 0);
        public IReadOnlyList<CompiledAction> HeldActionsOf(string actorKey) =>
            Held.TryGetValue(actorKey, out var list) ? list : Array.Empty<CompiledAction>();
        public ActorDerivedSnapshot? DerivedOf(string actorKey) => Full();
        public string? GarrisonedStructureKeyOf(string actorKey) => null;
        public GridPos? ObjectivePositionOf(string actorKey) => null;
        public long? MaxHpOf(string actorKey) => 1000;
        public int AggressionOf(string actorKey) => 0;
    }

    static CompiledAction Action(string id, double multiplier, int staminaCost) => new(
        id, ActionKind.Skill, 1, Array.Empty<ActionTag>(), true, 1, false, false, "container." + id,
        ActionEnvelope.NoOp with { ActionId = id },
        new CompiledTargetSpec(true, Array.Empty<FusionRpg.Contracts.TargetSpec>()),
        0, int.MaxValue, null, false, PredicateCompiler.Always,
        staminaCost == 0
            ? Array.Empty<CompiledActionCost>()
            : new[] { new CompiledActionCost("stamina", ValueSpec.Of(staminaCost), ActionCostTiming.OnCommit) },
        Array.Empty<ActionScopeRow>());

    /// <summary>The three held actions, in the actor's own preference order, which IS the twin's priority
    /// order: an expensive strong action, a cheap one, and the mandatory free fallback `Walk` requires.</summary>
    static CompiledAction[] Held() => new[]
    {
        Action("act.skill", multiplier: 2.0, staminaCost: 80),
        Action("act.basic", multiplier: 1.0, staminaCost: 40),
        Action("act.pass", multiplier: 0.0, staminaCost: 0),
    };

    /// <summary>The twin's projection of those three, with each share derived from the authored amount
    /// exactly as the class doc explains.</summary>
    static ActionSchedule.ActionOption[] Options() => new[]
    {
        new ActionSchedule.ActionOption("act.skill", Priority: 1, DamageMultiplier: 2.0, "stamina", ShareOf(80, 2.0)),
        new ActionSchedule.ActionOption("act.basic", Priority: 2, DamageMultiplier: 1.0, "stamina", ShareOf(40, 1.0)),
        new ActionSchedule.ActionOption("act.pass", Priority: 99, DamageMultiplier: 0.0, null, 0),
    };

    static long ShareOf(int amount, double multiplier) => (long)(amount * 1000 / (Base * multiplier));

    /// <summary>A profile whose rows admit ANY held action. The tier decides what the CALLER's own
    /// choice means: performance is "a fixed selector, then the first usable action after the gates,
    /// subject to the reserve floor" (what the twin's `Choose` does), and smart additionally runs
    /// `ActionStage`'s waste guards, which is the core's half of the overkill correspondence below.</summary>
    static CombatAiProfile Profile(long reserveFloorMilli, string tier = "performance", int killMarginMilli = 0) => CombatAiTuningLoader.Parse($$"""
        {
          "schemaVersion": 1, "version": 1,
          "profiles": {
            "*/default": {
              "tierOverride": "{{tier}}",
              "tierByActorClass": { "unique": "performance", "general": "performance" },
              "rows": [ { "selector": "nearest", "condition": "always", "census": "none", "actions": {} } ],
              "scoring": { "weightHitChance": 70, "weightObjective": 50, "weightKill": 15, "weightLowHp": 10,
                           "weightCannotCounter": 10, "weightRound": 1, "weightRisk": 120,
                           "aggressionRange": 2, "maxCandidatesScored": 32 },
              "selection": { "mode": "argmax", "keepPctMilli": 1000, "rngStreamName": "ai.select" },
              "reserves": [ { "resourceId": "stamina", "floorMilliOfMax": {{reserveFloorMilli}} } ],
              "guards": { "minTargetsForArea": 1, "killMarginMilli": {{killMarginMilli}}, "fightEndingLiveCount": 0 },
              "antiRepeat": { "retargetLatencyTicks": 0, "commitmentBonus": 0, "repeatDecayHalfLifeTicks": 0 },
              "personality": { "bounds": { "aggression": 0, "recklessness": 0, "focus": 0, "thrift": 0 } }
            }
          },
          "router": { "orderTimeoutTicks": 5000, "reactionsPerRoundExpected": 1000 }
        }
        """).Profiles["*/default"];

    /// <summary>
    /// The parity walk. Both sides start from the SAME stamina pool with regen zero, and after every
    /// round each pays the action it chose, so the two trajectories stay identical by construction —
    /// which is what makes a divergence a statement about the POLICY rather than about the pools.
    /// </summary>
    /// <param name="tier">The tier BOTH sides run at. The twin carries `Tier` without branching on it
    /// (`ActionSchedule.SchedulePolicy`'s own doc), while the core's `runWasteGuards: tier == Smart`
    /// decides whether its half of the overkill correspondence even runs — so the two sides must be
    /// told the same tier for the pair to be comparable.</param>
    /// <param name="killMarginMilli">The core's `KillMarginMilli` waste guard threshold. Only consulted
    /// at smart tier (see above). The twin has no threshold: its overkill predicate is the caller's.</param>
    /// <param name="targetHpAt">Round -> the target's `HpMilli` at that round, so the core's guard can be
    /// made to fire part-way through a walk. `null` means full HP, which is the shape every other case
    /// wants. This is a fake's own knob, not a production input.</param>
    /// <param name="fightEndsThisRound">The twin's overkill predicate (round -> "the free option already
    /// finishes it"). `null` means the twin runs with `SkipOverkill: false`, which never reads it.</param>
    static (string?[] Twin, string?[] Real) WalkBoth(
        long reserveFloorMilli, int rounds, long stamina,
        string tier = "performance", int killMarginMilli = 0,
        Func<int, int>? targetHpAt = null, Func<int, bool>? fightEndsThisRound = null)
    {
        const long maxStamina = 1000;
        var held = Held();
        var view = new FakeView(held);

        // The real side: a real CostLedger over real pools, so gate 3 is the production one.
        var derived = FakeView.Full();
        // All six ids: `ActorResourcePools.FromStored` requires the whole closed vocabulary, and
        // `ResourceIds` IS that vocabulary (a contract assertion, not a population count).
        var stored = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var id in DerivedStatChannels.ResourceIds) stored[id] = 1000;
        stored["stamina"] = stamina;
        var pools = ActorResourcePools.FromStored(stored, atTick: 0);
        var ledger = new CostLedger(
            new Dictionary<string, IReadOnlyList<ActionCostRow>>(StringComparer.Ordinal)
            {
                ["act.skill"] = new[] { new ActionCostRow("act.skill", "stamina", ValueSpec.Of(80), ActionCostTiming.OnCommit) },
                ["act.basic"] = new[] { new ActionCostRow("act.basic", "stamina", ValueSpec.Of(40), ActionCostTiming.OnCommit) },
            },
            _ => pools, _ => derived, (_, _) => 1, () => 0L);

        // THE FLOOR IS THE CALLER'S TO COMPOSE on the real side: `CoreIntentPolicy`'s own comment says
        // module 5's `ReserveFloorAffordability` "wraps `_affordability` BEFORE construction", so passing
        // the bare ledger would give the real policy no floor at all and the parity test would be
        // comparing a floored walker against an unfloored one.
        // The fixture is built BELOW this composition; the callback is only read at decision time, so
        // the capture is deliberately deferred rather than duplicating the projection here.
        IReadOnlyList<ActionSchedule.ActionOption>? optionsForCost = null;
        IAffordabilityCheck affordability = reserveFloorMilli == 0
            ? ledger
            : new ReserveFloorAffordability(
                ledger,
                new[] { new AiReserveFloor("stamina", (int)reserveFloorMilli) },
                kindOf: _ => ActionKind.Skill,
                currentBalanceOf: resourceId => pools.Resolve(resourceId, 0, derived),
                maxOf: resourceId => FusionRpg.Core.Stats.Derived.ResourceChannelReader.Max(derived, resourceId),
                // CAI2.6's ruling (2026-09-23): the floor binds what REMAINS after paying, so the seam
                // must be able to see the action's own cost. Read through the ONE cost formula
                // (`ActionSchedule.CostOf`) from the fixture's own projection — whose shares this test
                // already derives from the authored amounts (class doc), so the real side and the twin
                // price the same action identically by construction.
                costOf: (_, actionId, resourceId) => (optionsForCost ?? Array.Empty<ActionSchedule.ActionOption>())
                    .Where(o => o.Id == actionId && o.CostResourceId == resourceId)
                    .Select(o => (long)ActionSchedule.CostOf(o, Base))
                    .FirstOrDefault());

        var policy = CoreIntentPolicy.CreateForTest(
            view, new CooldownLedger(), NoStanceHeld.Instance, affordability, Profile(reserveFloorMilli, tier, killMarginMilli),
            roundOf: tick => (int)tick);

        // The twin side: the same pool as a per-round PoolState, regen zero (see the class doc).
        var poolsForTwin = new Dictionary<string, ActionSchedule.PoolState>(StringComparer.Ordinal)
        {
            ["stamina"] = new(stamina, maxStamina, 0),
        };
        var options = Options();
        optionsForCost = options;
        var schedule = new ActionSchedule.SchedulePolicy(
            "test/" + tier, tier == "smart" ? AiTier.Smart : AiTier.Performance,
            reserveFloorMilli, SkipOverkill: fightEndsThisRound is not null);

        var realIds = new List<string?>(rounds);
        var twinIds = new List<string?>(rounds);
        for (var round = 0; round < rounds; round++)
        {
            if (targetHpAt is not null) view.TargetHpMilli = targetHpAt(round);
            var real = policy.TryDeclare(Actor, nowTick: round);

            // The twin is walked ONE round at a time with a persistent pool, so its own round index is
            // always 0 -- the overkill predicate has to be re-bound to the OUTER round, or every round
            // would ask the caller's question of round 0 and the guard could never fire late.
            var twinPredicate = fightEndsThisRound is null ? null : (Func<int, bool>)(_ => fightEndsThisRound(round));
            var twin = ActionSchedule.Walk(options, poolsForTwin, Base, rounds: 1, schedule, twinPredicate)[0];

            realIds.Add(real.ActionId);
            twinIds.Add(twin.ActionId);

            // EACH SIDE PAYS FOR ITS OWN CHOICE. Paying both for one side's choice would keep the pools
            // artificially identical and mask a divergence in the chosen ids — which is exactly what an
            // earlier version of this fixture did.
            var twinOption = options[twin.ActionId == "act.skill" ? 0 : twin.ActionId == "act.basic" ? 1 : 2];
            if (twinOption.CostResourceId is not null)
            {
                var twinCost = (long)ActionSchedule.CostOf(twinOption, Base);
                poolsForTwin["stamina"] = poolsForTwin["stamina"] with { Value = poolsForTwin["stamina"].Value - twinCost };
            }
            Assert.Equal(CostPayResult.Success.Outcome,
                ledger.TryPay(Actor, real.ActionId, ActionCostTiming.OnCommit, rng: null).Outcome);

            // The two pools are comparable only while the walkers agree; once they choose differently the
            // trajectories are EXPECTED to part, and asserting equality there would be asserting the very
            // thing under test.
            if (twin.ActionId == real.ActionId)
                Assert.Equal((long)poolsForTwin["stamina"].Value, pools.Resolve("stamina", round, derived));
        }

        return (twinIds.ToArray(), realIds.ToArray());
    }

    /// <summary>The identity case: no reserve floor, so both walkers simply take the first affordably
    /// priced action in preference order until the pool runs dry and the free fallback takes over.</summary>
    [Fact]
    public void The_twin_matches_the_core_policy_round_by_round_with_no_reserve_floor()
    {
        var (twin, real) = WalkBoth(reserveFloorMilli: 0, rounds: 8, stamina: 200);
        Assert.Equal(real, twin);
    }

    /// <summary>
    /// <b>The reserve-floor case does NOT agree, and this test PINS the disagreement so it cannot drift
    /// silently.</b> The parity walk found it rather than it being argued, which is what the test is for.
    ///
    /// <para>Two different rules are implemented for the same word. <c>ReserveFloorAffordability.cs:106</c>
    /// refuses when <c>current &lt;= floor</c> — "do not act while you are AT or below the floor" — while
    /// the ideal's own words (spec §2, quoting §6.1 step 3) are that a pool "may not drop below a fraction
    /// of its max <b>after paying</b>", which is <c>current - cost &gt;= floor</c> and is what the twin
    /// implements. With max 1000, floor 900 and costs 80/40 from a full pool they therefore produce
    /// different sequences: the shipped seam allows the 80-cost action TWICE (1000 &gt; 900 → 920 &gt; 900 →
    /// 840), while the twin allows it once (1000-80 = 920 ≥ 900, then 920-80 = 840 &lt; 900).</para>
    ///
    /// <para>Resolving it is an owner decision and NOT a quiet fix: aligning the seam to the ideal's rule
    /// changes live behaviour and would move goldens, which this row's Golden line forbids
    /// ("byte-identical"); aligning the twin to the seam's rule would make the ideal's wording stale. Filed
    /// on CAI2.3 as a finding with both lines named.</para>
    /// </summary>
    [Fact]
    public void The_reserve_floor_rule_agrees_between_the_twin_and_the_shipped_seam()
    {
        var (twin, real) = WalkBoth(reserveFloorMilli: 900, rounds: 4, stamina: 1000);

        // CAI2.6 RULED on 2026-09-23, and the deciding document is `combat-ai-ideal.md:318` (§6.1 step
        // 3): "a pool may not drop below a fraction of its max **after paying**" -- the post-payment
        // reading, which is what the twin always implemented. The shipped seam's pre-condition reading
        // (`current <= floor`, cost-blind) is the defective side, so it was fixed rather than the twin,
        // and the two walkers now agree round for round. Before the ruling this assertion read
        // `{ "act.skill", "act.skill", null, null }` -- `null` being `ActionIntent.None`, the actor
        // IDLING because the seam refused even the zero-cost action.
        Assert.Equal(new string?[] { "act.skill", "act.pass", "act.pass", "act.pass" }, twin);
        Assert.Equal(twin, real);
    }

    /// <summary>
    /// <b>The overkill case does not agree either, and it is the SAME divergence as the reserve floor's,
    /// one guard along: the shipped seam refuses EVERY action while the twin falls through to its free
    /// option.</b> Pinned here so the acceptance's "and the overkill case" is measured rather than
    /// assumed, exactly as the reserve-floor case is.
    ///
    /// <para><b>The two rules, read rather than paraphrased.</b> The core's is
    /// `ActionStage.PassesWasteGuards` guard 2 (<c>ActionStage.cs:126</c>):
    /// <c>if (guards.KillMarginMilli &gt;= 0 &amp;&amp; targetFacts.HpMilli &lt;= guards.KillMarginMilli) return false;</c>
    /// — a refusal that `TryPick`'s loop applies to every held action in order, so with all of them
    /// refused the actor gets <c>ActionIntent.None</c> and does nothing. The twin's is
    /// `ActionSchedule.Choose` (<c>ActionSchedule.cs:171-187</c>): when the predicate says the free
    /// option already finishes it, costed options are skipped and the free one is taken — the free
    /// fallback short-circuits BEFORE the guard is consulted, deliberately, because a guard that could
    /// starve the walk is a hang. So the same word produces "idles" and "passes".</para>
    ///
    /// <para><b>Why this is reachable in production, not a fixture artefact:</b> the guard runs when
    /// <c>KillMarginMilli &gt;= 0</c> at smart tier, and the SHIPPED `combat-ai.v1.json` profile
    /// `siege/default` is <c>tierOverride: "smart"</c> with <c>killMarginMilli: 0</c> — the guard is
    /// ON today and fires only on an already-zero-HP target, which is why nothing has noticed. A tuning
    /// pass that authors a positive margin (the ordinary reason the key exists) makes the two walkers
    /// disagree about what the actor DOES. Resolving it is an owner ruling of the same shape CAI2.6
    /// holds for the floor: aligning the seam's guard to fall through rather than refuse would change
    /// live smart-tier behaviour, and aligning the twin to idle would contradict its own §2 wording
    /// ("the free one is taken") and the free-fallback-never-floored rule.</para>
    /// </summary>
    [Fact]
    public void The_overkill_rule_differs_the_same_way_the_reserve_floor_does()
    {
        // Round 0..1 the target is healthy -- both walkers take the costed action in preference order.
        // From round 2 the core's kill-margin guard fires (target below the authored 50) and the twin's
        // predicate says the free option already finishes it. Same trigger, two answers.
        var (twin, real) = WalkBoth(
            reserveFloorMilli: 0, rounds: 4, stamina: 1000,
            tier: "smart", killMarginMilli: 50,
            targetHpAt: round => round >= 2 ? 30 : 1000,
            fightEndsThisRound: round => round >= 2);

        // Non-vacuous: the guard actually fired. Without this the test would pass if BOTH sides simply
        // took `act.skill` every round and neither guard existed.
        Assert.Equal(new string?[] { "act.skill", "act.skill", "act.pass", "act.pass" }, twin);
        Assert.Equal(new string?[] { "act.skill", "act.skill", null, null }, real);
    }
}
