using System.Collections.Generic;
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
/// combat-ai `aggression-tier-map` (module 6, CAI1.13, spec-aggression-tier-map.md): the tier bound is
/// a CLOSED VOCABULARY the reader saturates onto, not a throw. `ai.aggression` is a FlatSum channel
/// with no `Cap` and it stays that way — the channel composes freely, one reader projects it onto its
/// own set, and the discarded amount is reported so the clamp has a symptom.
///
/// <para>Nothing here pins a content population; the one pinned literal is
/// `2 * aggressionRange + 1`, the tier vocabulary's own width, with its reason stated in the test.</para>
/// </summary>
public class AggressionTierMapTests
{
    [Fact]
    public void Inside_the_range_is_unchanged()
    {
        // The byte-identity proof: every production implementor of IBattleView.AggressionOf returns 0
        // today, so every live value is inside ±2 — and inside the range the arithmetic is IDENTICAL to
        // the pre-CAI1.13 body (`checked(baseTier - aggression)`, saturatedBy 0).
        for (var aggression = -2; aggression <= 2; aggression++)
        {
            var tier = CandidateScorer.EffectiveTier(baseTier: 0, aggression, aggressionRange: 2, out var saturatedBy);
            Assert.Equal(-aggression, tier);
            Assert.Equal(0, saturatedBy);
        }

        // Non-zero baseTier: same subtraction, still reported as unsaturated.
        Assert.Equal(7, CandidateScorer.EffectiveTier(baseTier: 10, aggression: 3, aggressionRange: 5, out var s));
        Assert.Equal(0, s);
    }

    [Fact]
    public void Outside_the_range_saturates_to_the_edge()
    {
        Assert.Equal(-2, CandidateScorer.EffectiveTier(baseTier: 0, aggression: 5, aggressionRange: 2));
        Assert.Equal(2, CandidateScorer.EffectiveTier(baseTier: 0, aggression: -5, aggressionRange: 2));

        // Saturation is onto the EDGE, not a modulo and not a no-op: +int.MaxValue is the same tier as
        // +2, which is what "your third taunt adds nothing" means.
        Assert.Equal(-2, CandidateScorer.EffectiveTier(baseTier: 0, aggression: int.MaxValue, aggressionRange: 2));
        Assert.Equal(2, CandidateScorer.EffectiveTier(baseTier: 0, aggression: int.MinValue, aggressionRange: 2));
    }

    [Fact]
    public void SaturatedBy_reports_the_signed_overshoot()
    {
        CandidateScorer.EffectiveTier(0, aggression: 3, aggressionRange: 2, out var plus);
        CandidateScorer.EffectiveTier(0, aggression: -4, aggressionRange: 2, out var minus);
        CandidateScorer.EffectiveTier(0, aggression: 2, aggressionRange: 2, out var exact);
        CandidateScorer.EffectiveTier(0, aggression: 0, aggressionRange: 2, out var none);

        Assert.Equal(1, plus);
        Assert.Equal(-2, minus);
        Assert.Equal(0, exact);   // at the edge is not past it
        Assert.Equal(0, none);

        // Direction is preserved, which is the whole reason the sign is kept: "my taunt is being
        // ignored" (+1) and "my stealth is being ignored" (−1) are different player complaints.
        Assert.Equal(1, CandidateScorer.SaturatedByOf(aggression: 4, aggressionRange: 3));
        Assert.Equal(-1, CandidateScorer.SaturatedByOf(aggression: -4, aggressionRange: 3));
    }

    [Fact]
    public void A_stacked_channel_plus_a_personality_offset_saturates_as_a_sum()
    {
        // §2: the input to EffectiveTier is the TOTAL. A +2 channel and a +2 personality offset compose
        // (FlatSum, uncapped) to +4 and saturate ONCE at the edge — the same tier as +2, reporting +2.
        // Saturating the channel first and then adding the offset would leave the vocabulary again
        // (2 + 2 = 4), which is exactly the defect this module removes.
        var channel = 2;
        var personalityOffset = 2;
        var total = channel + personalityOffset;

        var tier = CandidateScorer.EffectiveTier(baseTier: 0, total, aggressionRange: 2, out var saturatedBy);
        Assert.Equal(-2, tier);
        Assert.Equal(2, saturatedBy);

        // The negative direction too: −1 channel with a −2 offset is −3, saturating at −2 with −1 lost.
        var stacked = -1 + -2;
        var negativeTier = CandidateScorer.EffectiveTier(baseTier: 0, stacked, aggressionRange: 2, out var negativeLost);
        Assert.Equal(2, negativeTier);
        Assert.Equal(-1, negativeLost);
    }

    [Fact]
    public void Non_positive_range_still_throws()
    {
        // DELIBERATELY still a throw, and the asymmetry with the saturation above is the point: a
        // non-positive range is MALFORMED CONFIGURATION (there is no vocabulary to saturate ONTO),
        // while an out-of-range aggression is a legal runtime value. Module 2's loader refuses the
        // former at parse; this is the last line of defence.
        Assert.Throws<System.ArgumentOutOfRangeException>(() => CandidateScorer.EffectiveTier(0, 0, aggressionRange: 0));
        Assert.Throws<System.ArgumentOutOfRangeException>(() => CandidateScorer.EffectiveTier(0, 0, aggressionRange: -1));
        Assert.Throws<System.ArgumentOutOfRangeException>(() => CandidateScorer.SaturatedByOf(aggression: 0, aggressionRange: 0));
    }

    [Fact]
    public void Tier_vocabulary_width_is_two_range_plus_one()
    {
        // PINNED LITERAL, with its reason: the tier set is a CLOSED VOCABULARY this code owns, not a
        // derived population. `aggressionRange` is the taunt/stealth/decoy vocabulary's own width
        // (`Spec: the tier range IS the vocabulary`), so the number of distinct tiers an actor can land
        // in is exactly `2 * range + 1` — there is no tier beyond either edge to fall off.
        var range = 2;
        var tiers = new HashSet<int>();
        for (var aggression = -10; aggression <= 10; aggression++)
            tiers.Add(CandidateScorer.EffectiveTier(baseTier: 0, aggression, range));

        Assert.Equal(2 * range + 1, tiers.Count);
    }

    [Fact]
    public void Ai_aggression_keeps_composing_as_FlatSum_with_no_Cap()
    {
        // §5: the saturation is at the READ, never at composition. The channel stays uncapped so the
        // real number survives for the actor sheet and every future reader.
        var registry = DerivedStatRegistry.CreateDefault();
        Assert.True(registry.TryGet(DerivedStatChannels.AiAggression, out var def));
        Assert.Equal(DerivedComposeKind.FlatSum, def.Compose);
        Assert.Null(def.Cap);
    }

    /// <summary>
    /// §4: when the chosen candidate's aggression saturated, the trace line names it. Driven through
    /// the REAL `SiegeAiIntentSource` (the production reader) with a real `BattleTrace`, so this proves
    /// the fact is available off a real decision — never fabricated by a debug path.
    /// </summary>
    [Fact]
    public void The_trace_line_names_a_saturated_choice()
    {
        var view = new FakeView()
            .Add("squad:0", side: 0, pos: new GridPos(0, 0))
            .Add("wave:taunt", side: 1, pos: new GridPos(0, 1), aggression: 3);

        var trace = new BattleTrace();
        var ai = new SiegeAiIntentSource(
            view, new CooldownLedger(), NoStanceHeld.Instance, AlwaysAffordable.Instance,
            new ScoringWeights(1, 0, 0, 0, 0, 0, 0, 2, 32), retargetLatencyTicks: 0,
            SiegeTuningPolicy.Ai, tick => (int)tick, new RetargetLedger(), trace);

        var intent = ai.TryDeclare("squad:0", nowTick: 0);

        Assert.Equal("wave:taunt", intent.TargetKey);
        Assert.Contains(trace.AiDecisions, line => line.Contains("saturatedBy=1(wave:taunt)", System.StringComparison.Ordinal));

        // And an UNSATURATED choice adds nothing to the line — the report is a symptom, not noise.
        var plainView = new FakeView()
            .Add("squad:0", side: 0, pos: new GridPos(0, 0))
            .Add("wave:plain", side: 1, pos: new GridPos(0, 1), aggression: 1);
        var plainTrace = new BattleTrace();
        var plainAi = new SiegeAiIntentSource(
            plainView, new CooldownLedger(), NoStanceHeld.Instance, AlwaysAffordable.Instance,
            new ScoringWeights(1, 0, 0, 0, 0, 0, 0, 2, 32), retargetLatencyTicks: 0,
            SiegeTuningPolicy.Ai, tick => (int)tick, new RetargetLedger(), plainTrace);

        plainAi.TryDeclare("squad:0", nowTick: 0);
        Assert.DoesNotContain(plainTrace.AiDecisions, line => line.Contains("saturatedBy", System.StringComparison.Ordinal));
    }

    sealed class FakeView : IBattleView
    {
        readonly List<string> _actors = new();
        readonly Dictionary<string, int> _sides = new(System.StringComparer.Ordinal);
        readonly Dictionary<string, GridPos?> _positions = new(System.StringComparer.Ordinal);
        readonly Dictionary<string, int> _aggression = new(System.StringComparer.Ordinal);

        public FakeView Add(string key, int side, GridPos? pos, int aggression = 0)
        {
            _actors.Add(key);
            _sides[key] = side;
            _positions[key] = pos;
            _aggression[key] = aggression;
            return this;
        }

        public IReadOnlyList<string> LiveActorKeys => _actors;
        public int SideOf(string actorKey) => _sides[actorKey];
        public GridPos? PositionOf(string actorKey) => _positions[actorKey];
        public EntityFacts FactsOf(string actorKey) => new(_sides[actorKey], 0, 1000, -1, 0, 0, false, false, 0);
        public IReadOnlyList<CompiledAction> HeldActionsOf(string actorKey) =>
            _sides[actorKey] == 0 ? new[] { Action() } : System.Array.Empty<CompiledAction>();
        public ActorDerivedSnapshot? DerivedOf(string actorKey) => ActorDerivedSnapshot.StubNeutral();
        public string? GarrisonedStructureKeyOf(string actorKey) => null;
        public GridPos? ObjectivePositionOf(string actorKey) => null;
        public long? MaxHpOf(string actorKey) => 1000;
        public int AggressionOf(string actorKey) => _aggression[actorKey];

        static CompiledAction Action() => new(
            "act.attack", ActionKind.Skill, 1, System.Array.Empty<ActionTag>(), true, 1, false, false, "item.test",
            ActionEnvelope.NoOp with { ActionId = "act.attack" },
            new CompiledTargetSpec(true, System.Array.Empty<FusionRpg.Contracts.TargetSpec>()),
            0, int.MaxValue, null, false, PredicateCompiler.Always,
            System.Array.Empty<CompiledActionCost>(), System.Array.Empty<ActionScopeRow>());
    }
}
