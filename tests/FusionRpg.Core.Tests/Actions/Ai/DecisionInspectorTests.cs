using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Ai;
using FusionRpg.Core.Battle.Board;
using FusionRpg.Core.Battle.Siege;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Actions.Ai;

/// <summary>
/// combat-ai `decision-inspector` (module 10, CAI2.4, spec-decision-inspector.md §1/§2/§3): the record,
/// the sink seam, and the turn-mode wiring. The property this file exists for is §2's identity claim —
/// siege records THROUGH the sink and the emitted line is byte-identical, so
/// `SiegeAiIntentSourceTests`'s line-shape assertions and
/// `AggressionTierMapTests.The_trace_line_names_a_saturated_choice` pass UNCHANGED.
/// </summary>
public class DecisionInspectorTests
{
    sealed class FakeView : IBattleView
    {
        public readonly List<string> Actors = new();
        public readonly Dictionary<string, int> Sides = new(StringComparer.Ordinal);
        public readonly Dictionary<string, List<CompiledAction>> Held = new(StringComparer.Ordinal);
        public readonly Dictionary<string, int> Aggressions = new(StringComparer.Ordinal);

        public FakeView Add(string key, int side, params CompiledAction[] held)
        {
            Actors.Add(key);
            Sides[key] = side;
            Held[key] = new List<CompiledAction>(held);
            Aggressions[key] = 0;
            return this;
        }

        public IReadOnlyList<string> LiveActorKeys => Actors;
        public int SideOf(string actorKey) => Sides[actorKey];
        public GridPos? PositionOf(string actorKey) => null;
        public EntityFacts FactsOf(string actorKey) => new(Sides[actorKey], 0, 1000, -1, -1, -1, false, false, 0);
        public IReadOnlyList<CompiledAction> HeldActionsOf(string actorKey) =>
            Held.TryGetValue(actorKey, out var list) ? list : Array.Empty<CompiledAction>();
        public ActorDerivedSnapshot? DerivedOf(string actorKey) => ActorDerivedSnapshot.StubNeutral();
        public string? GarrisonedStructureKeyOf(string actorKey) => null;
        public GridPos? ObjectivePositionOf(string actorKey) => null;
        public long? MaxHpOf(string actorKey) => 1000;
        public int AggressionOf(string actorKey) => Aggressions.TryGetValue(actorKey, out var a) ? a : 0;
    }

    sealed class SpySink : IAiDecisionSink
    {
        public readonly List<AiDecisionRecord> Records = new();

        // The interface takes `in`, so a spy must copy to keep the record past the call.
        public void Record(in AiDecisionRecord record) => Records.Add(record);
    }

    static CompiledAction Action(string id) => new(
        id, ActionKind.Skill, 1, Array.Empty<ActionTag>(), true, 1, false, false, "item.test",
        ActionEnvelope.NoOp with { ActionId = id },
        new CompiledTargetSpec(true, Array.Empty<FusionRpg.Contracts.TargetSpec>()),
        0, int.MaxValue, null, false, PredicateCompiler.Always,
        Array.Empty<CompiledActionCost>(), Array.Empty<ActionScopeRow>());

    static ScoringWeights Weights() =>
        new(HitChance: 70, Objective: 50, Kill: 15, LowHp: 10, CannotCounter: 10, Round: 1, Risk: 120,
            AggressionRange: 2, MaxCandidatesScored: 32);

    static SiegeAiIntentSource Ai(
        FakeView view, RetargetLedger? retarget = null, long latency = 0,
        BattleTrace? trace = null, IAiDecisionSink? sink = null) =>
        new(view, new CooldownLedger(), NoStanceHeld.Instance, AlwaysAffordable.Instance,
            Weights(), retargetLatencyTicks: latency, SiegeTuningPolicy.Ai, tick => (int)tick,
            retarget, trace, effectiveRungOf: null, sink: sink);

    static FakeView TwoEnemyView()
    {
        var view = new FakeView().Add("wave:0", 0, Action("act.attack"));
        view.Add("enemy:a", 1);
        view.Add("enemy:b", 1);
        return view;
    }

    [Fact]
    public void A_scored_decision_records_exactly_one_record()
    {
        var sink = new SpySink();
        var intent = Ai(TwoEnemyView(), sink: sink).TryDeclare("wave:0", nowTick: 5);

        var record = Assert.Single(sink.Records);
        Assert.Equal(5, record.NowTick);
        Assert.Equal(5, record.Round);
        Assert.Equal("wave:0", record.ActorKey);
        Assert.Equal(AiDecisionOrigin.Policy, record.Origin);      // nothing ordered or steered this
        Assert.Equal(AiTriggerState.None, record.Trigger);         // a turn mode has no trigger of its own
        // The row's "ChosenActionId/ChosenTargetKey equal the ActionIntent returned" line, both halves.
        Assert.Equal(intent.TargetKey, record.ChosenTargetKey);
        Assert.Equal(intent.ActionId, record.ChosenActionId);
        Assert.NotEmpty(record.TopThree);
    }

    /// <summary>
    /// CAI2.4's candidate contract, in both directions: every TARGET the decision scored appears with the
    /// breakdown the scorer already computed, and every gate verdict step 3's loop actually returned
    /// appears with its <see cref="UsabilityReason"/>. The two kinds are distinguished by which field is
    /// set, and neither is re-derived — filling a gate field by re-running a gate is what §1 forbids.
    /// </summary>
    [Fact]
    public void Every_scored_candidate_and_every_gate_verdict_appears()
    {
        var sink = new SpySink();
        Ai(TwoEnemyView(), sink: sink).TryDeclare("wave:0", nowTick: 0);

        var record = Assert.Single(sink.Records);

        // The scored targets, each with a real breakdown and no gate (step 3 never gated a target).
        var scored = record.Candidates.Where(v => v.Breakdown is not null).ToList();
        Assert.Equal(2, scored.Count);
        Assert.Equal(new[] { "enemy:a", "enemy:b" }, scored.Select(v => v.TargetKey));
        Assert.All(scored, v => Assert.Null(v.Gate));

        // The gate loop's own verdicts, for the held action against the chosen target.
        var gated = record.Candidates.Where(v => v.Gate is not null).ToList();
        Assert.Single(gated);
        Assert.Equal("act.attack", gated[0].ActionId);
        Assert.Equal(UsabilityReason.Usable, gated[0].Gate!.Value.Reason);
        Assert.Null(gated[0].Breakdown);
    }

    /// <summary>§1: "zero on a held-target tick" — the record site sits after both early returns, so a
    /// decision that returned a held target records nothing.</summary>
    [Fact]
    public void A_held_target_tick_records_nothing()
    {
        var view = TwoEnemyView();
        var sink = new SpySink();
        var ai = Ai(view, new RetargetLedger(), latency: 10, sink: sink);

        Assert.False(ai.TryDeclare("wave:0", nowTick: 0).IsNone);   // scores, records once
        Assert.Equal("enemy:a", Assert.Single(sink.Records).ChosenTargetKey);

        // Inside the latency window with the target still valid: the held target is served, nothing is
        // scored, and nothing is recorded.
        Assert.False(ai.TryDeclare("wave:0", nowTick: 1).IsNone);
        Assert.Single(sink.Records);
    }

    /// <summary>§2, the identity proof: a `BattleTrace` supplied as sugar and one supplied as an explicit
    /// `BattleTraceDecisionSink` emit the SAME line, because the sink is the only formatter.</summary>
    [Fact]
    public void The_emitted_line_is_byte_identical_whether_the_trace_or_the_sink_is_supplied()
    {
        var viaTrace = new BattleTrace();
        Ai(TwoEnemyView(), trace: viaTrace).TryDeclare("wave:0", nowTick: 0);

        var viaSink = new BattleTrace();
        Ai(TwoEnemyView(), sink: new BattleTraceDecisionSink(viaSink)).TryDeclare("wave:0", nowTick: 0);

        Assert.Equal(viaTrace.AiDecisions, viaSink.AiDecisions);
        Assert.StartsWith("0 wave:0 #1=enemy:a(", Assert.Single(viaSink.AiDecisions));
    }

    /// <summary>§3: the trace list is kept out of <see cref="BattleTrace.Digest"/> (<c>BattleTrace.cs:121-129</c>),
    /// so a decision record can never become indistinguishable from a behaviour change in the fixture the
    /// parity ladder compares. Asserted, not assumed.</summary>
    [Fact]
    public void The_record_is_golden_neutral()
    {
        var withSink = new BattleTrace();
        Ai(TwoEnemyView(), sink: new BattleTraceDecisionSink(withSink)).TryDeclare("wave:0", nowTick: 0);

        Assert.NotEmpty(withSink.AiDecisions);
        Assert.Equal(new BattleTrace().Digest, withSink.Digest);
    }

    /// <summary>
    /// CAI2.4's PRODUCTION wire, proven through a real battle rather than at the router.
    ///
    /// <para>Measured before this: no production caller passed a `sink` to `IntentRouter.Compose`
    /// (`grep -rn "sink:" src/` returned nothing), so the four arms the router wraps recorded nothing in
    /// any shipped battle — the acceptance line was met at the router and unreached in the pipeline. The
    /// router's own records carry an EMPTY top-three (it knows no candidate detail), so its line has no
    /// `#1=` slot while every policy-level record does; a traced battle containing one proves the wire is
    /// live rather than merely compiled.</para>
    /// </summary>
    [Fact]
    public void A_real_battle_reaches_the_routers_sink()
    {
        var trace = new BattleTrace();
        FusionRpg.Core.Battle.BattleEngine.Resolve(
            FusionRpg.Core.Tests.Battle.BattleGoldenTests.CloseSetup(), 2002, trace);

        Assert.Contains(trace.AiDecisions, line =>
            line.StartsWith("0 ", StringComparison.Ordinal) && !line.Contains("#1=", StringComparison.Ordinal));
        // The trace list is digest-excluded, which `The_record_is_golden_neutral` already pins at the
        // source level; the golden suite is what proves the whole battle is unmoved.
    }

    /// <summary>
    /// The RESELECT wire, which no test can enter: `Reselect` is a LOCAL FUNCTION inside
    /// `TimelineDispatch`'s dispatch method, so its call site is pinned by scanning the source — the
    /// pattern `StanceSeamTests` already uses for a seam no test can reach. Without this, removing the
    /// sink there would leave every test green while a reselect silently stopped recording, which is the
    /// "one wire remains" shape this repo treats as not-done.
    /// </summary>
    [Fact]
    public void The_reselect_path_passes_the_same_sink()
    {
        var file = Path.Combine(RepoRoot(), "src", "FusionRpg.Core", "Battle", "TimelineDispatch.cs");
        Assert.True(File.Exists(file), $"dispatch source not found: {file}");

        // Comments stripped: the block's own comment names the sink on purpose, and the criterion is
        // about code. Line numbers deliberately not pinned — an absolute line goes stale on the next edit.
        var sinkLines = File.ReadAllLines(file)
            .Select(line => line.Split("//")[0])
            .Where(code => code.Contains("sink:", StringComparison.Ordinal))
            .ToList();

        var only = Assert.Single(sinkLines);
        Assert.Contains("state.Trace", only, StringComparison.Ordinal);
        Assert.Contains("BattleTraceDecisionSink", only, StringComparison.Ordinal);
    }

    static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "")
    {
        var testsDir = Path.GetDirectoryName(here)!;
        return Path.GetFullPath(Path.Combine(testsDir, "..", "..", "..", ".."));
    }

    /// <summary>§3: recording is an OBSERVER, so wiring a sink moves no intent — the record is built
    /// after the choice and nothing in the scoring path reads it back.</summary>
    [Fact]
    public void Wiring_a_sink_changes_no_intent()
    {
        var withoutSink = Ai(TwoEnemyView()).TryDeclare("wave:0", nowTick: 0);
        var withSink = Ai(TwoEnemyView(), sink: new SpySink()).TryDeclare("wave:0", nowTick: 0);

        Assert.Equal(withoutSink.ActionId, withSink.ActionId);
        Assert.Equal(withoutSink.TargetKey, withSink.TargetKey);
    }

    /// <summary>A one-member `IIntentSource` that always declares the same action — used only to prove
    /// WHICH arm of the router answered.</summary>
    sealed class FixedSource : IIntentSource
    {
        readonly string _actionId;
        public FixedSource(string actionId) => _actionId = actionId;
        public ActionIntent TryDeclare(string actorKey, long nowTick) =>
            new(_actionId, "target:1", ActionEnvelope.NoOp with { ActionId = _actionId });
    }

    /// <summary>
    /// The row's "all four policies behind the router record through the same sink — not siege alone".
    /// Three arms are exercised (policy, steered, fallback) because those are the `IIntentSource` arms
    /// `IntentRouter.Compose` accepts; the order arm is an `IOrderQueue`, not a source, and records
    /// through whichever arm it delegates to.
    /// </summary>
    [Fact]
    public void Every_router_arm_records_through_the_same_sink()
    {
        // (a) the policy arm — the ordinary chain.
        var policySink = new SpySink();
        var policyRouter = IntentRouter.Compose(new FixedSource("act.policy"), sink: policySink);
        var declared = policyRouter.TryDeclare("actor:1", nowTick: 3);
        var fromPolicy = Assert.Single(policySink.Records);
        Assert.Equal(declared.ActionId, fromPolicy.ChosenActionId);
        Assert.Equal("actor:1", fromPolicy.ActorKey);
        Assert.Equal(3, fromPolicy.NowTick);
        Assert.Equal(AiDecisionOrigin.Policy, fromPolicy.Origin);

        // (b) the steered arm — the override, which reports Steered rather than Policy.
        var steeredSink = new SpySink();
        var steeredRouter = IntentRouter.Compose(
            new FixedSource("act.policy"),
            steeredSourceFor: _ => new FixedSource("act.steered"),
            steeredKeys: new HashSet<string>(StringComparer.Ordinal) { "actor:2" },
            sink: steeredSink);
        steeredRouter.TryDeclare("actor:2", nowTick: 4);
        var fromSteered = Assert.Single(steeredSink.Records);
        Assert.Equal("act.steered", fromSteered.ChosenActionId);
        Assert.Equal(AiDecisionOrigin.Steered, fromSteered.Origin);

        // (c) the fallback arm — the policy declares nothing, so `NoneIntentSource`'s backstop fires and
        // the STUB records. A None intent still records: "the chain reached nothing" is a decision the
        // inspector has to be able to explain.
        var fallbackSink = new SpySink();
        var fallbackRouter = IntentRouter.Compose(
            NoneIntentSource.Instance, fallback: new FixedSource("act.fallback"), sink: fallbackSink);
        fallbackRouter.TryDeclare("actor:3", nowTick: 5);
        // TWO records, and that is the chain being honest rather than chatty: the policy arm was asked
        // first and declared nothing (a None record), then the stub answered. Both are decisions the
        // inspector has to be able to explain, so both reach the sink; the LAST one is the answer.
        Assert.Equal(2, fallbackSink.Records.Count);
        Assert.Null(fallbackSink.Records[0].ChosenActionId);
        Assert.Equal("act.fallback", fallbackSink.Records[^1].ChosenActionId);

        // (d) a null sink leaves every arm exactly as it was — off costs nothing and changes nothing.
        var silent = IntentRouter.Compose(new FixedSource("act.policy"));
        Assert.Equal("act.policy", silent.TryDeclare("actor:4", nowTick: 6).ActionId);
    }

    /// <summary>The closed origin vocabulary, pinned with its reason: the inspector READS an origin and
    /// never infers one, so the width is a contract. Adding a fourth is a reviewed change.</summary>
    [Fact]
    public void AiDecisionOrigin_has_three_members() =>
        Assert.Equal(3, Enum.GetValues(typeof(AiDecisionOrigin)).Length);
}
