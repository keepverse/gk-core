using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Ai;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Actions.Ai;

/// <summary>
/// combat-ai `ai-tiers-personality` (module 3, CAI1.9, spec-ai-tiers-personality.md §6): the ONE
/// composition entry point for every non-siege place. Nothing in production constructs it in this
/// module's own commit (`delve-automated-wiring`, module 13, is its first real caller), so every test
/// here exercises it directly against fakes — proving the SEQUENCE and the COUNTS the spec names, never
/// a specific score value (that would be `core-scorer`'s own job, already proven in
/// `CandidateScorerTests`).
/// </summary>
public class CoreIntentPolicyTests
{
    sealed class FakeBattleView : IBattleView
    {
        public readonly List<string> Actors = new();
        public readonly Dictionary<string, int> Sides = new(StringComparer.Ordinal);
        public readonly Dictionary<string, GridPos?> Positions = new(StringComparer.Ordinal);
        public readonly Dictionary<string, EntityFacts> Facts = new(StringComparer.Ordinal);
        public readonly Dictionary<string, List<CompiledAction>> Held = new(StringComparer.Ordinal);
        public readonly Dictionary<string, ActorDerivedSnapshot?> Derived = new(StringComparer.Ordinal);
        public int SideOfCalls;
        public int DerivedOfCalls;

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
        public int SideOf(string actorKey) { SideOfCalls++; return Sides[actorKey]; }
        public GridPos? PositionOf(string actorKey) => Positions[actorKey];
        public EntityFacts FactsOf(string actorKey) => Facts[actorKey];
        public IReadOnlyList<CompiledAction> HeldActionsOf(string actorKey) =>
            Held.TryGetValue(actorKey, out var list) ? list : Array.Empty<CompiledAction>();
        public ActorDerivedSnapshot? DerivedOf(string actorKey)
        {
            DerivedOfCalls++;
            return Derived.TryGetValue(actorKey, out var d) ? d : null;
        }
        public string? GarrisonedStructureKeyOf(string actorKey) => null;
        public GridPos? ObjectivePositionOf(string actorKey) => null;
        public long? MaxHpOf(string actorKey) => 1000;
        public int AggressionOf(string actorKey) => 0;
    }

    static ActorDerivedSnapshot Snapshot(double accuracy = 500, double dodge = 0, double power = 100) =>
        ActorDerivedSnapshot.FromValues(new[]
        {
            new KeyValuePair<string, double>(DerivedStatChannels.CombatAccuracyOmni, accuracy),
            new KeyValuePair<string, double>(DerivedStatChannels.CombatDodgeOmni, dodge),
            new KeyValuePair<string, double>(DerivedStatChannels.CombatPowerOmni, power),
        });

    static CompiledAction Action(string id, ActionTag[]? tags = null) => new(
        id, ActionKind.Skill, 1, tags ?? Array.Empty<ActionTag>(), true, 1, false, false, "item.test",
        ActionEnvelope.NoOp with { ActionId = id }, new CompiledTargetSpec(true, Array.Empty<FusionRpg.Contracts.TargetSpec>()),
        0, int.MaxValue, null, false, PredicateCompiler.Always,
        Array.Empty<CompiledActionCost>(), Array.Empty<ActionScopeRow>());

    static AiPersonalityBounds ZeroBounds() => new(new Dictionary<PersonalityAxis, int>
    {
        [PersonalityAxis.Aggression] = 0, [PersonalityAxis.Recklessness] = 0,
        [PersonalityAxis.Focus] = 0, [PersonalityAxis.Thrift] = 0,
    });

    static CombatAiProfile Profile(
        AiTier tierOverride, TargetSelector selector = TargetSelector.Nearest,
        IReadOnlyList<AiProfileRow>? rows = null, AiWasteGuards? guards = null) => new(
        "test/default", AiPlace.Battle, AiRole.Default, tierOverride,
        new Dictionary<AiActorClass, AiTier> { [AiActorClass.Unique] = AiTier.Smart, [AiActorClass.General] = AiTier.Performance },
        rows ?? new[] { new AiProfileRow(selector, AiRowCondition.Always, 0, "", AiCensusCondition.None, 0, new AiActionFilter(null, null, null, null)) },
        new AiScoringBlock(70, 50, 15, 10, 10, 1, 120, 2, 32),
        new AiSelectionBlock(SelectionMode.Argmax, 1000, "ai.select"),
        Array.Empty<AiReserveFloor>(), guards ?? new AiWasteGuards(0, -1, -1), new AiAntiRepeat(0, 0, 0),
        Trigger: null, ZeroBounds());

    [Fact]
    public void A_declaration_runs_the_seven_steps_in_order()
    {
        var view = new FakeBattleView()
            .Add("me", 0, new GridPos(0, 0), derived: Snapshot(), held: Action("act.attack"))
            .Add("enemy", 1, new GridPos(0, 1), derived: Snapshot());

        var traces = new List<AiDecisionTrace>();
        var policy = CoreIntentPolicy.CreateForTest(
            view, new CooldownLedger(), NoStanceHeld.Instance, AlwaysAffordable.Instance,
            Profile(AiTier.Smart), trace: traces.Add);

        var intent = policy.TryDeclare("me", nowTick: 0);

        Assert.False(intent.IsNone);
        Assert.Single(traces);
        var t = traces[0];
        // Each populated field is only reachable if every step BEFORE it already succeeded: a
        // non-negative RankIndex needs the row walk (step 4); a non-null TargetKey needs the target
        // stage (step 5); a non-null ActionId needs the action stage (step 6) -- this IS the sequence
        // proof, not a coincidence of independent fields.
        Assert.Equal(AiTier.Smart, t.Tier);
        Assert.True(t.RankIndex >= 0);
        Assert.Equal("enemy", t.TargetKey);
        Assert.Equal("act.attack", t.ActionId);
    }

    /// <summary>Proven via the private per-actor cache rather than a mock on the static
    /// `CombatAiProfilePolicy` hub (module 2's own hub has no counting hook, and adding one would be a
    /// second surface just for this test) — three declarations for the SAME actor, at three different
    /// ticks, must leave exactly one cached entry.</summary>
    [Fact]
    public void The_profile_is_resolved_once_per_actor_and_not_once_per_decision()
    {
        var view = new FakeBattleView()
            .Add("me", 0, new GridPos(0, 0), derived: Snapshot(), held: Action("act.attack"))
            .Add("enemy", 1, new GridPos(0, 1), derived: Snapshot());

        var policy = CoreIntentPolicy.CreateForTest(
            view, new CooldownLedger(), NoStanceHeld.Instance, AlwaysAffordable.Instance, Profile(AiTier.Smart));

        policy.TryDeclare("me", nowTick: 0);
        policy.TryDeclare("me", nowTick: 1);
        policy.TryDeclare("me", nowTick: 2);

        var field = typeof(CoreIntentPolicy).GetField("_stateByActor", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var cache = (System.Collections.IDictionary)field.GetValue(policy)!;
        Assert.Equal(1, cache.Count);
    }

    /// <summary>Performance tier + a `Self` selector reads zero board facts beyond the census's own
    /// O(live) loop -- so if the row walk gathered its census PER ROW instead of once, a 3-row profile
    /// would triple `SideOf`'s call count against a 1-row profile with the SAME live-actor set. It
    /// does not.</summary>
    [Fact]
    public void The_census_is_gathered_once_per_decision_and_not_once_per_row()
    {
        var threeRows = new[]
        {
            new AiProfileRow(TargetSelector.Self, AiRowCondition.SelfHpBelowMilli, 1, "", AiCensusCondition.EnemiesAtLeast, 99, new AiActionFilter(null, null, null, null)),
            new AiProfileRow(TargetSelector.Self, AiRowCondition.HasStatus, 0, "", AiCensusCondition.RoundAtLeast, 99, new AiActionFilter(null, null, null, null)),
            new AiProfileRow(TargetSelector.Self, AiRowCondition.Always, 0, "", AiCensusCondition.None, 0, new AiActionFilter(null, null, null, null)),
        };
        var view = new FakeBattleView()
            .Add("me", 0, new GridPos(0, 0), held: Action("act.attack"))
            .Add("enemy1", 1, new GridPos(0, 1))
            .Add("enemy2", 1, new GridPos(0, 2));

        var policy = CoreIntentPolicy.CreateForTest(
            view, new CooldownLedger(), NoStanceHeld.Instance, AlwaysAffordable.Instance,
            Profile(AiTier.Performance, selector: TargetSelector.Self, rows: threeRows));

        policy.TryDeclare("me", nowTick: 0);

        // One SideOf call for `mySide` itself, plus BuildRowFacts's own O(live) loop (one call per
        // OTHER live actor, self skipped before the call) -- exactly Actors.Count total, regardless of
        // how many rows the walk crossed to reach the third, unconditional row.
        Assert.Equal(view.Actors.Count, view.SideOfCalls);
    }

    /// <summary>"Performance tier computes no candidate inputs" (this module's own acceptance line) —
    /// asserted here rather than in module 1's `ActionStageTests` (the spec's own stated file), because
    /// the decision to invoke `TargetStage`'s candidate builder AT ALL is this composition's own choice,
    /// not something `ActionStage.TryPick` (which has no notion of a target candidate) could ever
    /// observe. `DerivedOf` is read only inside the smart-tier candidate build and (for `HighestThreat`)
    /// the fixed selector -- a `Nearest`-selector performance-tier decision reads it zero times.</summary>
    [Fact]
    public void Performance_tier_computes_no_candidate_inputs()
    {
        var view = new FakeBattleView()
            .Add("me", 0, new GridPos(0, 0), held: Action("act.attack"))
            .Add("enemy", 1, new GridPos(0, 1));

        var policy = CoreIntentPolicy.CreateForTest(
            view, new CooldownLedger(), NoStanceHeld.Instance, AlwaysAffordable.Instance,
            Profile(AiTier.Performance, selector: TargetSelector.Nearest));

        var intent = policy.TryDeclare("me", nowTick: 0);

        Assert.False(intent.IsNone);
        Assert.Equal(0, view.DerivedOfCalls);
    }

    /// <summary>The one-line mapping (spec §2): `runWasteGuards: tier == AiTier.Smart`. A kill-margin
    /// guard that WOULD refuse a nearly-dead target fires at smart tier and never even runs at
    /// performance tier -- proven by the SAME scenario under both tiers, not two different setups.
    /// </summary>
    [Fact]
    public void Tier_smart_passes_runWasteGuards_true_and_performance_passes_false()
    {
        var guards = new AiWasteGuards(MinTargetsForArea: 0, KillMarginMilli: 50, FightEndingLiveCount: -1);

        var smartView = new FakeBattleView()
            .Add("me", 0, new GridPos(0, 0), derived: Snapshot(), held: Action("act.attack"))
            .Add("enemy", 1, new GridPos(0, 1), hpMilli: 30, derived: Snapshot()); // below the 50 margin
        var smartPolicy = CoreIntentPolicy.CreateForTest(
            smartView, new CooldownLedger(), NoStanceHeld.Instance, AlwaysAffordable.Instance, Profile(AiTier.Smart, guards: guards));
        Assert.True(smartPolicy.TryDeclare("me", nowTick: 0).IsNone); // guard refuses -- no action, no movement tag either

        var perfView = new FakeBattleView()
            .Add("me", 0, new GridPos(0, 0), held: Action("act.attack"))
            .Add("enemy", 1, new GridPos(0, 1), hpMilli: 30);
        var perfPolicy = CoreIntentPolicy.CreateForTest(
            perfView, new CooldownLedger(), NoStanceHeld.Instance, AlwaysAffordable.Instance, Profile(AiTier.Performance, guards: guards));
        Assert.False(perfPolicy.TryDeclare("me", nowTick: 0).IsNone); // guard never runs at performance tier
    }

    /// <summary>module 4's `RetargetFor` contract: no action stage at all. An actor with ZERO held
    /// actions still gets a real re-targeted key -- if this method routed through `ActionStage`, an
    /// empty held-action list would make it return null/None instead.</summary>
    [Fact]
    public void RetargetFor_runs_the_target_stage_alone_and_never_the_action_stage()
    {
        var view = new FakeBattleView()
            .Add("me", 0, new GridPos(0, 0)) // no held actions at all
            .Add("enemy", 1, new GridPos(0, 1));

        var policy = CoreIntentPolicy.CreateForTest(
            view, new CooldownLedger(), NoStanceHeld.Instance, AlwaysAffordable.Instance,
            Profile(AiTier.Performance, selector: TargetSelector.Nearest));

        var target = policy.RetargetFor("me", "act.whatever", deadTargetKey: null, nowTick: 0);
        Assert.Equal("enemy", target);
    }

    [Fact]
    public void A_step_that_yields_nothing_returns_ActionIntent_None_and_never_throws()
    {
        var view = new FakeBattleView()
            .Add("me", 0, new GridPos(0, 0), held: Action("act.attack"));
            // no enemy on the board at all

        var policy = CoreIntentPolicy.CreateForTest(
            view, new CooldownLedger(), NoStanceHeld.Instance, AlwaysAffordable.Instance,
            Profile(AiTier.Performance, selector: TargetSelector.Nearest));

        var intent = policy.TryDeclare("me", nowTick: 0);
        Assert.True(intent.IsNone);
    }

    /// <summary>The Boundary made mechanical (spec §6): a source scan for `CandidateScorer.Score`'s own
    /// accumulator name. `CoreIntentPolicy` legitimately CALLS `CandidateScorer.ChooseTarget` (module
    /// 1's own one scorer) but never re-derives the additive sum itself -- if a reviewer ever finds
    /// `"checked(total"` in this file, the composition has grown a second scorer.</summary>
    [Fact]
    public void No_scoring_arithmetic_lives_in_this_type()
    {
        var text = File.ReadAllText(FindSourceFile("CoreIntentPolicy.cs"));
        Assert.DoesNotContain("checked(total", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Score(c,", text, StringComparison.Ordinal); // CandidateScorer.Score's own call shape
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
