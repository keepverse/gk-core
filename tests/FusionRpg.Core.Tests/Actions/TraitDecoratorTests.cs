using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Ai;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Actions;

/// <summary>
/// combat-ai `intent-router` (module 4, CAI1.11, spec-intent-router.md §2): trait decorators reaching
/// EVERY policy, not only the stub fallback (audit M1), and the `loyal` post-decision redirect landing
/// exactly once on each side. `bloodthirsty`'s real engine-side implementation and its production
/// reach through `BattleEngine.Resolve` are proven in
/// <c>Battle/Siege/SiegeAiLiveWiringTests</c>; the fakes here prove the CONTRACT
/// (<see cref="TraitAwareBattleView"/> decorates a policy's own bound view; the router applies
/// <see cref="ITraitDecorator.EffectiveTargetOf"/> once and never chains it).
/// </summary>
public class TraitDecoratorTests
{
    sealed class FakeBattleView : IBattleView
    {
        public readonly List<string> Actors = new();
        public readonly Dictionary<string, int> Sides = new(StringComparer.Ordinal);
        public readonly Dictionary<string, GridPos?> Positions = new(StringComparer.Ordinal);
        public readonly Dictionary<string, EntityFacts> Facts = new(StringComparer.Ordinal);
        public readonly Dictionary<string, List<CompiledAction>> Held = new(StringComparer.Ordinal);
        public readonly Dictionary<string, ActorDerivedSnapshot?> Derived = new(StringComparer.Ordinal);

        public FakeBattleView Add(string key, int side, GridPos? pos, int hpMilli = 1000,
            ActorDerivedSnapshot? derived = null, params CompiledAction[] held)
        {
            Actors.Add(key);
            Sides[key] = side;
            Positions[key] = pos;
            Facts[key] = new EntityFacts(side, 0, hpMilli, -1, pos?.Row ?? -1, pos?.Col ?? -1, false, false, 0);
            Held[key] = new List<CompiledAction>(held);
            Derived[key] = derived;
            return this;
        }

        public IReadOnlyList<string> LiveActorKeys => Actors;
        public int SideOf(string actorKey) => Sides[actorKey];
        public GridPos? PositionOf(string actorKey) => Positions[actorKey];
        public EntityFacts FactsOf(string actorKey) => Facts[actorKey];
        public IReadOnlyList<CompiledAction> HeldActionsOf(string actorKey) =>
            Held.TryGetValue(actorKey, out var list) ? list : Array.Empty<CompiledAction>();
        public ActorDerivedSnapshot? DerivedOf(string actorKey) =>
            Derived.TryGetValue(actorKey, out var d) ? d : null;
        public string? GarrisonedStructureKeyOf(string actorKey) => null;
        public GridPos? ObjectivePositionOf(string actorKey) => null;
        public long? MaxHpOf(string actorKey) => 1000;
        public int AggressionOf(string actorKey) => 0;
    }

    /// <summary>Mirrors `BasicAttack.BloodthirstyDecorator`'s shape without the engine: a pre-decision
    /// reorder of the live set for ONE actor.</summary>
    sealed class PriorityDecorator : ITraitDecorator
    {
        readonly string _actorKey;
        readonly string _priorityKey;

        public PriorityDecorator(string actorKey, string priorityKey)
        {
            _actorKey = actorKey;
            _priorityKey = priorityKey;
        }

        public bool AppliesTo(string actorKey) => string.Equals(actorKey, _actorKey, StringComparison.Ordinal);

        public IBattleView Decorate(string actorKey, IBattleView inner)
        {
            var ordered = new List<string>(inner.LiveActorKeys.Count) { _priorityKey };
            var live = inner.LiveActorKeys;
            for (var i = 0; i < live.Count; i++)
                if (!string.Equals(live[i], _priorityKey, StringComparison.Ordinal))
                    ordered.Add(live[i]);
            return new ReorderedView(inner, ordered);
        }

        public string EffectiveTargetOf(string actorKey, string targetKey) => targetKey;
    }

    sealed class ReorderedView : IBattleView
    {
        readonly IBattleView _inner;
        readonly IReadOnlyList<string> _order;

        public ReorderedView(IBattleView inner, IReadOnlyList<string> order)
        {
            _inner = inner;
            _order = order;
        }

        public IReadOnlyList<string> LiveActorKeys => _order;
        public int SideOf(string actorKey) => _inner.SideOf(actorKey);
        public GridPos? PositionOf(string actorKey) => _inner.PositionOf(actorKey);
        public EntityFacts FactsOf(string actorKey) => _inner.FactsOf(actorKey);
        public IReadOnlyList<CompiledAction> HeldActionsOf(string actorKey) => _inner.HeldActionsOf(actorKey);
        public ActorDerivedSnapshot? DerivedOf(string actorKey) => _inner.DerivedOf(actorKey);
        public string? GarrisonedStructureKeyOf(string actorKey) => _inner.GarrisonedStructureKeyOf(actorKey);
        public GridPos? ObjectivePositionOf(string actorKey) => _inner.ObjectivePositionOf(actorKey);
        public long? MaxHpOf(string actorKey) => _inner.MaxHpOf(actorKey);
        public int AggressionOf(string actorKey) => _inner.AggressionOf(actorKey);
    }

    /// <summary>A policy that reads ONLY the viewer-relative live order — the exact call
    /// `SiegeAiIntentSource`/`CoreIntentPolicy` now make. It records the view it was constructed with,
    /// which is the "view a policy was handed" the acceptance line names.</summary>
    sealed class ViewOrderPolicy : IIntentSource
    {
        readonly IBattleView _view;
        public readonly IBattleView HandedView;
        public IReadOnlyList<string> LastOrder = Array.Empty<string>();

        public ViewOrderPolicy(IBattleView view)
        {
            _view = view;
            HandedView = view;
        }

        public ActionIntent TryDeclare(string actorKey, long nowTick)
        {
            var order = _view.LiveActorKeysFor(actorKey);
            LastOrder = order;
            return new ActionIntent("act.attack", order[0], ActionEnvelope.NoOp);
        }
    }

    sealed class NeverActs : IIntentSource
    {
        public ActionIntent TryDeclare(string actorKey, long nowTick) => ActionIntent.None;
    }

    /// <summary>Mirrors `BasicAttack.LoyalTargetRedirect`'s shape without the engine, and deliberately
    /// CHAINS (`b` -> `c`) so a second application is observable.</summary>
    sealed class ChainingRedirect : ITraitDecorator
    {
        public int Calls;
        public bool AppliesTo(string actorKey) => true;
        public IBattleView Decorate(string actorKey, IBattleView inner) => inner;
        public string EffectiveTargetOf(string actorKey, string targetKey)
        {
            Calls++;
            return targetKey switch { "a" => "b", "b" => "c", _ => targetKey };
        }
    }

    [Fact]
    public void Every_policy_sees_the_bloodthirsty_view_not_only_the_fallback()
    {
        var raw = new FakeBattleView()
            .Add("me", 0, new GridPos(0, 0))
            .Add("far", 1, new GridPos(9, 9), hpMilli: 900)
            .Add("weakest", 1, new GridPos(9, 8), hpMilli: 10);

        var traitView = new TraitAwareBattleView(raw, new ITraitDecorator[] { new PriorityDecorator("me", "weakest") });
        var policy = new ViewOrderPolicy(traitView);
        var router = new IntentRouter(policy, fallback: new NeverActs());

        // The POLICY (not the fallback) answers, and what it reads is the decorated order.
        Assert.IsType<TraitAwareBattleView>(policy.HandedView);
        Assert.Equal("weakest", router.TryDeclare("me", 0).TargetKey);
        Assert.Equal("weakest", policy.LastOrder[0]);

        // An actor the decorator does not apply to sees the raw order unchanged.
        Assert.Equal("me", router.TryDeclare("someone-else", 0).TargetKey);
        Assert.Equal(raw.Actors, policy.LastOrder);
    }

    [Fact]
    public void A_loyal_redirect_is_applied_once_on_each_side_and_the_scorer_reads_the_redirected_target()
    {
        // Engine side: the router applies EffectiveTargetOf to the resolved target, exactly once —
        // `a -> b` must NOT be chained into `c`, and the retarget path (which never carried the
        // redirect) must not gain it.
        var redirect = new ChainingRedirect();
        var router = new IntentRouter(
            policy: new FixedIntentSource("act.attack", "a"),
            decorators: new ITraitDecorator[] { redirect });

        Assert.Equal("b", router.TryDeclare("me", 0).TargetKey);
        Assert.Equal(1, redirect.Calls);
        Assert.Equal("a", router.RetargetFor("me", "act.attack", deadTargetKey: null, nowTick: 0));

        // Scorer side: `CoreIntentPolicy`'s candidate inputs resolve through the SAME redirect, so the
        // `lowHp`/`kill` terms score the actor that will actually be hit. `MaxCandidatesScored = 2`
        // truncates `guard` out of the RANKED set, so it never competes as a candidate itself — it is
        // visible only through the redirect. `weak` (900 HP) redirects onto the 1-HP guard, so its
        // effective missing-HP becomes 999 while `other` (400 HP) keeps its own 600: `weak` wins.
        var view = new FakeBattleView()
            .Add("me", 0, new GridPos(0, 0), derived: Snapshot(), held: Action("act.attack"))
            .Add("weak", 1, new GridPos(0, 1), hpMilli: 900, derived: Snapshot())
            .Add("other", 1, new GridPos(0, 2), hpMilli: 400, derived: Snapshot())
            .Add("guard", 1, new GridPos(0, 3), hpMilli: 1, derived: Snapshot());

        var scored = CoreIntentPolicy.CreateForTest(
            view, new CooldownLedger(), NoStanceHeld.Instance, AlwaysAffordable.Instance,
            LowHpProfile(), loyalRedirect: key => string.Equals(key, "weak", StringComparison.Ordinal) ? "guard" : key);

        var intent = scored.TryDeclare("me", nowTick: 0);
        Assert.Equal("guard", intent.TargetKey); // weak won BECAUSE its scorer inputs named the guard
        Assert.Equal("act.attack", intent.ActionId);

        // The control: no redirect -> `weak`'s own 100 missing loses to `other`'s 600, exactly as the
        // scorer's raw inputs say. Proves the assertion above is the redirect, not the tiebreak.
        var unredirected = CoreIntentPolicy.CreateForTest(
            view, new CooldownLedger(), NoStanceHeld.Instance, AlwaysAffordable.Instance,
            LowHpProfile(), loyalRedirect: null);
        Assert.Equal("other", unredirected.TryDeclare("me", nowTick: 0).TargetKey);
    }

    /// <summary>
    /// M5 as a test (spec-intent-router.md §5 cause A): `BasicAttack.DeclareBasicAttack` and
    /// `TimelineDispatch.Reselect` must resolve their source through the ONE router, and neither may
    /// re-derive the chain by hand — the reselect site historically omitted
    /// `state.DefaultAiIntentSource`, which is exactly what let a reselect silently drop the mode's own
    /// policy while a declare used it. CAI1.10 closed the defect; this pins it shut.
    /// </summary>
    [Fact]
    public void Reselect_and_declare_resolve_the_same_source_for_the_same_actor()
    {
        var basicAttack = File.ReadAllText(FindSourceFile("BasicAttack.cs"));
        Assert.Contains("IntentRouter.Compose(", basicAttack, StringComparison.Ordinal);
        Assert.Contains("state.DefaultAiIntentSource", basicAttack, StringComparison.Ordinal);
        Assert.DoesNotContain("?? new StubIntentSource(", basicAttack, StringComparison.Ordinal);

        var dispatch = File.ReadAllText(FindSourceFile("TimelineDispatch.cs"));
        var reselectStart = dispatch.IndexOf("string? Reselect(", StringComparison.Ordinal);
        Assert.True(reselectStart >= 0, "Reselect not found -- TimelineDispatch.cs may have moved");
        var reselectEnd = dispatch.IndexOf("var runner = new Timeline.ActionRunner(", reselectStart, StringComparison.Ordinal);
        Assert.True(reselectEnd >= 0, "the Reselect body's end marker moved");
        var reselect = dispatch.Substring(reselectStart, reselectEnd - reselectStart);

        Assert.Contains("IntentRouter.Compose(", reselect, StringComparison.Ordinal);
        Assert.Contains("state.DefaultAiIntentSource", reselect, StringComparison.Ordinal);
        Assert.DoesNotContain("?? new StubIntentSource(", reselect, StringComparison.Ordinal);
    }

    // -- fixtures shared with `CoreIntentPolicyTests`' own shape ----------------------------------

    sealed class FixedIntentSource : IIntentSource
    {
        readonly string _actionId;
        readonly string? _targetKey;
        public FixedIntentSource(string actionId, string? targetKey = null) { _actionId = actionId; _targetKey = targetKey; }
        public ActionIntent TryDeclare(string actorKey, long nowTick) => new(_actionId, _targetKey, ActionEnvelope.NoOp);
    }

    static ActorDerivedSnapshot Snapshot(double accuracy = 500, double dodge = 0, double power = 100) =>
        ActorDerivedSnapshot.FromValues(new[]
        {
            new KeyValuePair<string, double>(DerivedStatChannels.CombatAccuracyOmni, accuracy),
            new KeyValuePair<string, double>(DerivedStatChannels.CombatDodgeOmni, dodge),
            new KeyValuePair<string, double>(DerivedStatChannels.CombatPowerOmni, power),
        });

    static CompiledAction Action(string id) => new(
        id, ActionKind.Skill, 1, Array.Empty<ActionTag>(), true, 1, false, false, "item.test",
        ActionEnvelope.NoOp with { ActionId = id },
        new CompiledTargetSpec(true, Array.Empty<FusionRpg.Contracts.TargetSpec>()),
        0, int.MaxValue, null, false, PredicateCompiler.Always,
        Array.Empty<CompiledActionCost>(), Array.Empty<ActionScopeRow>());

    static AiPersonalityBounds ZeroBounds() => new(new Dictionary<PersonalityAxis, int>
    {
        [PersonalityAxis.Aggression] = 0, [PersonalityAxis.Recklessness] = 0,
        [PersonalityAxis.Focus] = 0, [PersonalityAxis.Thrift] = 0,
    });

    /// <summary>`lowHp` is the ONLY live scoring term (weight 1), so the chosen candidate IS the one
    /// with the largest effective `TargetMissingHpMilli` — making the redirect's effect on the
    /// scorer's inputs directly observable, with no other term to mask it. The cap of 2 is what keeps
    /// the redirect TARGET (`guard`) out of the ranked set, so the redirect's effect cannot be
    /// confused with `guard` winning on its own merits.</summary>
    static CombatAiProfile LowHpProfile() => new(
        "test/lowhp", AiPlace.Battle, AiRole.Default, AiTier.Smart,
        new Dictionary<AiActorClass, AiTier> { [AiActorClass.Unique] = AiTier.Smart, [AiActorClass.General] = AiTier.Smart },
        new[] { new AiProfileRow(TargetSelector.Nearest, AiRowCondition.Always, 0, "", AiCensusCondition.None, 0, new AiActionFilter(null, null, null, null)) },
        new AiScoringBlock(0, 0, 0, 1, 0, 0, 0, 2, 2),
        new AiSelectionBlock(SelectionMode.Argmax, 1000, "ai.select"),
        Array.Empty<AiReserveFloor>(), new AiWasteGuards(0, -1, -1), new AiAntiRepeat(0, 0, 0),
        Trigger: null, ZeroBounds());

    static string FindSourceFile(string fileName, [CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
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
        throw new InvalidOperationException($"{fileName} not found by walking up from {here}");
    }
}
