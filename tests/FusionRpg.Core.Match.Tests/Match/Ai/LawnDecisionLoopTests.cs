using System;
using System.Collections.Generic;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Ai;
using FusionRpg.Core.Actions.Cost;
using FusionRpg.Core.Actions.Unlock;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Match.Ai;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Match.Ai;

/// <summary>
/// combat-ai wave 4 — <b>the four Core halves run together</b>.
///
/// <para>Each of `lawn-held-actions` (CAI4.2), `lawn-cast-activation` (CAI4.6), `lawn-cast-trigger`
/// (CAI4.7) and `commander-direct-orders` (CAI4.9) has its own focused suite. None of them ever ran
/// against another: the store was never asked by the real profiled policy, the plan never paid a cost
/// built from the action's own compiled rows, and the admission rules never fed the queue the real
/// `IntentRouter` reads. Those are the seams a bundle of separately-green halves can still get wrong, so
/// they are composed here — in Core, with no injector and nothing faked in the middle.</para>
///
/// <para><b>What this is not.</b> It is not the production host. The injector's frame slot
/// (`LawnDecisionHost`, CAI4.8) is what will actually run this loop, and its feature switch, tick entry,
/// registry drops and `PerfSection` remain owed. A green test here says the halves fit; it is not
/// evidence that a lawn creature casts in a live match.</para>
/// </summary>
public class LawnDecisionLoopTests
{
    const string Self = "ptr.self";
    const string Enemy = "ptr.enemy";
    const string Species = "species.alpha";
    const long CooldownTicks = 20;

    // ---- fixtures -------------------------------------------------------------------------

    static CompiledAction Action(string id, int rung, params ActionTag[] tags) => new(
        id, ActionKind.Skill, rung, tags, true, 1, false, false, "container." + id,
        ActionEnvelope.NoOp with { ActionId = id },
        new CompiledTargetSpec(true, Array.Empty<FusionRpg.Contracts.TargetSpec>()),
        0, int.MaxValue, null, false, PredicateCompiler.Always,
        Array.Empty<CompiledActionCost>(), Array.Empty<ActionScopeRow>());

    static CompiledAction Action(string id, params ActionTag[] tags) => Action(id, 1, tags);

    /// <summary>An action that COSTS, so the plan's pay step has a real row to consume, and whose
    /// envelope declares a SPECIFIC cooldown — `CooldownClass.None` makes `CooldownLedger.Start` a
    /// deliberate no-op, which would leave the plan's second step untestable.</summary>
    static CompiledAction Costed(string id, int rung, int stamina) => Action(id, rung) with
    {
        Costs = new[] { new CompiledActionCost("stamina", ValueSpec.Of(stamina), ActionCostTiming.OnCommit) },
        Envelope = ActionEnvelope.NoOp with
        {
            ActionId = id,
            Class = CooldownClass.Specific,
            CooldownTicks = CooldownTicks,
        },
    };

    /// <summary>The four intrinsic basics plus the granted action the store will hold. A catalog must
    /// resolve every id the assembly emits, or the store refuses the whole key on purpose.</summary>
    static CompiledAction[] Kit() => new[]
    {
        Action("act.attack", ActionTag.Offensive), Action("act.guard", ActionTag.Defensive),
        Action("act.move", ActionTag.Movement), Action("act.innate", ActionTag.Utility),
        Action("act.skill", ActionTag.Heal),
    };

    static SpeciesBasicsRow Basics() => new(Species, "act.attack", "act.guard", "act.move", "act.innate");

    /// <summary>A real derived snapshot: the scorer reads progression power, and a pool is measured
    /// against `resource.max.<id>`, so an empty snapshot would make every pool read as zero.</summary>
    static ActorDerivedSnapshot Derived()
    {
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

        return new DerivedComposer(DerivedStatRegistry.CreateDefault()).Compose(mods);
    }

    static ActorResourcePools Pools(long stamina, ActorDerivedSnapshot derived)
    {
        var stored = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var id in DerivedStatChannels.ResourceIds) stored[id] = 1000;
        stored["stamina"] = stamina;
        return ActorResourcePools.FromStored(stored, atTick: 0);
    }

    /// <summary>The production-shaped view: held actions come from module 16's store, the two actors from
    /// one flat board. Deliberately permissive — a refusing gate would hide which half answered.</summary>
    sealed class LoopView : IBattleView
    {
        readonly LawnHeldActionSets _sets;
        readonly ActorDerivedSnapshot _derived;
        readonly string _speciesKey;

        public LoopView(LawnHeldActionSets sets, ActorDerivedSnapshot derived, string speciesKey = Species)
        {
            _sets = sets;
            _derived = derived;
            _speciesKey = speciesKey;
        }

        public IReadOnlyList<string> LiveActorKeys { get; } = new[] { Self, Enemy };
        public int SideOf(string actorKey) => actorKey == Self ? 0 : 1;
        public GridPos? PositionOf(string actorKey) => new GridPos(0, actorKey == Self ? 0 : 3);
        public EntityFacts FactsOf(string actorKey) =>
            new(SideOf(actorKey), 0, actorKey == Self ? 1000 : 500, -1, 0, actorKey == Self ? 0 : 3, false, false, 0);
        public IReadOnlyList<CompiledAction> HeldActionsOf(string actorKey) =>
            actorKey == Self ? _sets.HeldFor(_speciesKey) : Array.Empty<CompiledAction>();
        public ActorDerivedSnapshot? DerivedOf(string actorKey) => _derived;
        public string? GarrisonedStructureKeyOf(string actorKey) => null;
        public GridPos? ObjectivePositionOf(string actorKey) => null;
        public long? MaxHpOf(string actorKey) => 1000;
        public int AggressionOf(string actorKey) => 0;
    }

    /// <summary>A ptr whose seeded offset is 0 (and whose timer is not already due), so "the N-th swing"
    /// is literally N — the same deterministic scan the trigger's own suite uses.</summary>
    static string KeyWithOffsetZero(ulong seed, int n, int t, string stream)
    {
        for (var i = 0; i < 10_000; i++)
        {
            var key = "ptr." + i;
            var roll = SeededRng.DeriveStream(seed, stream + ":" + key);
            var offset = (int)((long)roll.NextPerMille() * n / 1000);
            var delay = (int)((long)roll.NextPerMille() * t / 1000);
            if (offset == 0 && delay > 0) return key;
        }

        throw new InvalidOperationException("no ptr with a zero seeded offset was found");
    }

    static IReadOnlyList<string> HeldIds(LawnHeldActionSets sets)
    {
        var held = sets.HeldFor(Species);
        var ids = new List<string>(held.Count);
        for (var i = 0; i < held.Count; i++) ids.Add(held[i].ActionId);
        return ids;
    }

    // ---- group A: module 16's store is what the REAL profiled policy chooses from ------------

    [Fact]
    public void The_profiled_lawn_policy_chooses_from_the_stores_held_list()
    {
        var catalog = ActionCatalog.Build(Kit());
        var sets = new LawnHeldActionSets(catalog);
        sets.PushSpecies(Species, Basics(), new[] { new ActionGrantRow(OwnerKind.UniqueActor, "subject.1", "act.skill", "item.1") }, _ => false);
        var view = new LoopView(sets, Derived());

        // The REAL profiled policy at the lawn's own place, over the real store — nothing stubbed but
        // the two actors' board reads. It resolves `lawn/default` -> `*/default` through
        // `CombatAiProfilePolicy`, whose hub the test bootstrap's module initializer configured.
        var policy = CoreIntentPolicy.Create(
            view, new CooldownLedger(), NoStanceHeld.Instance, AlwaysAffordable.Instance,
            AiPlace.Lawn, AiRole.Default);

        var intent = policy.TryDeclare(Self, 0);

        Assert.False(intent.IsNone);
        Assert.Equal(Enemy, intent.TargetKey);
        // The chosen action is one the STORE holds — never an id from anywhere else.
        Assert.Contains(intent.ActionId, HeldIds(sets));
    }

    [Fact]
    public void The_profiled_policy_declares_nothing_for_a_species_the_store_never_pushed()
    {
        var catalog = ActionCatalog.Build(Kit());
        var sets = new LawnHeldActionSets(catalog);
        // ANOTHER species has a kit — so "empty" cannot be satisfied by the store simply being empty,
        // which is the difference between a store that answers per key and one that answers per store.
        sets.PushSpecies(Species, Basics(), new[] { new ActionGrantRow(OwnerKind.UniqueActor, "subject.1", "act.skill", "item.1") }, _ => false);
        var view = new LoopView(sets, Derived(), speciesKey: "species.never-pushed");
        var policy = CoreIntentPolicy.Create(
            view, new CooldownLedger(), NoStanceHeld.Instance, AlwaysAffordable.Instance,
            AiPlace.Lawn, AiRole.Default);

        Assert.NotEmpty(sets.HeldFor(Species));
        Assert.Empty(sets.HeldFor("species.never-pushed"));
        Assert.True(policy.TryDeclare(Self, 0).IsNone);
    }

    // ---- group B: the edge -> budget -> token -> plan, paying the action's OWN cost -----------

    [Fact]
    public void The_edge_is_served_through_the_budget_and_the_cast_pays_the_actions_own_cost_once()
    {
        const int N = 7;
        const int T = 50;
        const int L = 10;
        const ulong MatchSeed = 0x5EED_1234UL;
        const string Stream = "lawn.ai.offset";

        var actions = new[]
        {
            Action("act.attack", ActionTag.Offensive), Action("act.guard", ActionTag.Defensive),
            Action("act.move", ActionTag.Movement), Action("act.innate", ActionTag.Utility),
            Costed("act.low", rung: 1, stamina: 80),
            Costed("act.high", rung: 5, stamina: 80),
        };
        var catalog = ActionCatalog.Build(actions);
        var derived = Derived();

        // The REAL lawn rung resolution (CAI4.4): the held row's authored rung, the lawn's floor of 1.
        int RungOf(string actorKey, string actionId) => EffectiveRungResolver.Resolve(
            actorKey, actionId, catalog, unlockStateFor: null, unlockTuning: null,
            heldActionOf: (_, id) =>
            {
                for (var i = 0; i < actions.Length; i++)
                    if (string.Equals(actions[i].ActionId, id, StringComparison.Ordinal)) return actions[i];
                return null;
            },
            floorWhenUnknown: 1);

        var pools = Pools(stamina: 1000, derived);
        var ledger = new CostLedger(CostsFromCompiled(actions), _ => pools, _ => derived, RungOf, () => 0L);
        var cooldowns = new CooldownLedger();

        // The frame, in the order the host will run it: settle the actor's edge, serve it from the
        // budget, lease a cast token, then pay and build the event.
        var key = KeyWithOffsetZero(MatchSeed, N, T, Stream);
        var trigger = new LawnDecisionTrigger(N, T, L, MatchSeed, Stream);
        var budget = new LawnDecisionBudget(decisionsPerFrame: 8);
        var tokens = new LawnCastTokenPool(capacity: 4);
        trigger.Register(key, nowTick: 0);
        for (var i = 0; i < N; i++) Assert.True(trigger.RecordSwing(key, isFirstOfSwing: true, castOrigin: false));
        Assert.True(trigger.IsDue(key, 0));

        budget.Offer(key, dueTick: 0);
        var served = new List<string>();
        Assert.Equal(1, budget.Take(served));
        Assert.Equal(new[] { key }, served.ToArray());

        Assert.True(tokens.TryLease(key, 0));
        var lowIntent = new ActionIntent("act.low", Enemy, catalog.Get("act.low")!.Envelope);
        var before = pools.Resolve("stamina", 0, derived);
        var plan = LawnCastPlan.Build(key, lowIntent, nowTick: 0, ledger, cooldowns);
        var charge = before - pools.Resolve("stamina", 0, derived);

        Assert.True(plan.Fired);
        Assert.True(charge > 0, "the cast must have paid the action's own cost");
        Assert.Equal(FusionRpg.Contracts.EffectTriggers.OnActivate, plan.Event!.Trigger);
        Assert.Equal(1, plan.Event.HitCount);
        Assert.Equal(Enemy, plan.Event.TargetPtr);
        Assert.Equal(CooldownTicks, cooldowns.ReadyAt(key, lowIntent.Envelope));

        // A HIGHER-rung action costs strictly more, which is what proves the resolution reaches the
        // payment rather than stopping at the ledger's door.
        var highIntent = new ActionIntent("act.high", Enemy, catalog.Get("act.high")!.Envelope);
        var beforeHigh = pools.Resolve("stamina", 0, derived);
        Assert.True(LawnCastPlan.Build(key, highIntent, nowTick: 0, ledger, cooldowns).Fired);
        var chargeHigh = beforeHigh - pools.Resolve("stamina", 0, derived);
        Assert.True(chargeHigh > charge, $"rung 5 ({chargeHigh}) must cost more than rung 1 ({charge})");

        // Charged ONCE per cast: with exactly one charge left the next cast is refused and nothing moves.
        var tight = Pools(stamina: charge, derived);
        var tightLedger = new CostLedger(CostsFromCompiled(actions), _ => tight, _ => derived, RungOf, () => 0L);
        Assert.True(LawnCastPlan.Build(key, lowIntent, 0, tightLedger, cooldowns).Fired);
        Assert.Equal(0, tight.Resolve("stamina", 0, derived));
        var refused = LawnCastPlan.Build(key, lowIntent, 0, tightLedger, cooldowns);
        Assert.False(refused.Fired);
        Assert.Equal("stamina", refused.ShortfallResourceId);
        Assert.Equal(0, tight.Resolve("stamina", 0, derived)); // a refusal charges nothing

        // The token comes back on every path, and the commit applies the lock.
        Assert.True(tokens.Release(key));
        Assert.Equal(0, tokens.InUse);
        trigger.OnCommittedCast(key, 0);
        Assert.False(trigger.IsDue(key, L));
        Assert.True(trigger.TryState(key, out var afterCast));
        Assert.Equal(L, afterCast.LockUntilTick);
    }

    /// <summary>The ledger's rows, built from each action's OWN compiled costs — the same one-line
    /// projection battle uses (`BattleRunState.cs:662`), so the number paid is the authored cost and not
    /// a test literal.</summary>
    static IReadOnlyDictionary<string, IReadOnlyList<ActionCostRow>> CostsFromCompiled(IReadOnlyList<CompiledAction> actions)
    {
        var rows = new Dictionary<string, IReadOnlyList<ActionCostRow>>(StringComparer.Ordinal);
        for (var a = 0; a < actions.Count; a++)
        {
            var action = actions[a];
            if (action.Costs.Count == 0) continue;
            var list = new List<ActionCostRow>(action.Costs.Count);
            for (var c = 0; c < action.Costs.Count; c++)
            {
                var cost = action.Costs[c];
                list.Add(new ActionCostRow(action.ActionId, cost.ResourceId, cost.Amount, cost.When));
            }

            rows[action.ActionId] = list;
        }

        return rows;
    }

    // ---- group C: the admission rules feed the queue the REAL router reads -------------------

    [Fact]
    public void An_admitted_order_is_live_through_the_real_router_and_a_terminal_refusal_removes_it()
    {
        var catalog = ActionCatalog.Build(Kit());
        var sets = new LawnHeldActionSets(catalog);
        sets.PushSpecies(Species, Basics(), new[] { new ActionGrantRow(OwnerKind.UniqueActor, "subject.1", "act.skill", "item.1") }, _ => false);
        var queue = new LawnOrderQueue();

        // Every §2 fact, resolved the way the host will resolve it: same scope, a bound subject whose
        // ptr is the one the order names and which is live, and an action the store actually holds.
        Assert.Equal(DirectOrderRefusal.None, DirectOrderAdmission.CheckScope("match.1", "match.1"));
        Assert.Equal(DirectOrderRefusal.None,
            DirectOrderAdmission.CheckSubject(new OrderSubject(Self, IsBound: true, IsLive: true), Self));
        Assert.True(DirectOrderAdmission.IsHeld("act.skill", sets.HeldFor(Species)));
        // …and one failing fact keeps an order out entirely: an action the store does NOT hold.
        Assert.False(DirectOrderAdmission.IsHeld("act.not-held", sets.HeldFor(Species)));

        var offer = queue.Offer(Self, new DirectOrder(Self, "act.skill", Enemy, IssuedTick: 100));
        Assert.True(offer.Accepted);

        // The shipped router's own step 1 reads that queue: live before the lifetime, expired after.
        var router = new IntentRouter(policy: NoneIntentSource.Instance, orders: queue, orderTimeoutTicks: 80);
        router.TryDeclare(Self, 179);
        Assert.True(queue.TryPeek(Self, out var live));
        Assert.Equal("act.skill", live.ActionId);
        Assert.Equal(Enemy, live.TargetKey);
        router.TryDeclare(Self, 180);
        Assert.False(queue.TryPeek(Self, out _));

        // A terminal gate refusal takes the order with it; a retryable one leaves it live for the next
        // edge. Both through the same call the host will make.
        queue.Offer(Self, new DirectOrder(Self, "act.skill", Enemy, IssuedTick: 200));
        Assert.False(queue.HandleGateRefusal(Self, new UsabilityResult(UsabilityReason.OnCooldown, "act.skill")));
        Assert.True(queue.TryPeek(Self, out _));
        Assert.True(queue.HandleGateRefusal(Self, new UsabilityResult(UsabilityReason.NotBound)));
        Assert.False(queue.TryPeek(Self, out _));
    }
}
