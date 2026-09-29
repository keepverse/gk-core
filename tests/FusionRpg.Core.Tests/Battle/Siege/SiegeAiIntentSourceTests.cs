using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Ai;
using FusionRpg.Core.Battle.Board;
using FusionRpg.Core.Battle.Siege;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Siege;

/// <summary>
/// base-defense `siege-ai` (spec-siege-ai.md), task 17.4: the first live `IIntentSource`.
/// Mirrors `ActionSelectionTests`' own `StubIntentSource` coverage for steps 1/3/4/5 (this class
/// copies them verbatim), then adds real coverage for step 2 — scored target selection actually
/// discriminating on hit chance, existing damage, and counter-safety.
/// </summary>
public class SiegeAiIntentSourceTests
{
    sealed class FakeBattleView : IBattleView
    {
        public readonly List<string> Actors = new();
        public readonly Dictionary<string, int> Sides = new(StringComparer.Ordinal);
        public readonly Dictionary<string, GridPos?> Positions = new(StringComparer.Ordinal);
        public readonly Dictionary<string, EntityFacts> Facts = new(StringComparer.Ordinal);
        public readonly Dictionary<string, List<CompiledAction>> Held = new(StringComparer.Ordinal);
        public readonly Dictionary<string, ActorDerivedSnapshot?> Derived = new(StringComparer.Ordinal);
        public readonly Dictionary<string, string> Garrisoning = new(StringComparer.Ordinal);
        public readonly Dictionary<string, GridPos?> Objectives = new(StringComparer.Ordinal);
        public readonly Dictionary<string, long?> MaxHps = new(StringComparer.Ordinal);
        public readonly Dictionary<string, int> Aggressions = new(StringComparer.Ordinal);

        /// <summary>CAI1.14: a one-shot hook so a test can re-enter the decision from inside its guarded
        /// window (a real view never calls back into the source).</summary>
        public Action? OnDerivedOf;

        public FakeBattleView Add(string key, int side, GridPos? pos, int hpMilli = 1000,
            ActorDerivedSnapshot? derived = null, long maxHp = 1000, int aggression = 0, params CompiledAction[] held)
        {
            Actors.Add(key);
            Sides[key] = side;
            Positions[key] = pos;
            Facts[key] = new EntityFacts(side, 0, hpMilli, -1, pos?.Row ?? -1, pos?.Col ?? -1, false, false, 0);
            Held[key] = new List<CompiledAction>(held);
            Derived[key] = derived ?? ActorDerivedSnapshot.StubNeutral();
            MaxHps[key] = maxHp;
            Aggressions[key] = aggression;
            return this;
        }

        public IReadOnlyList<string> LiveActorKeys => Actors;
        public int SideOf(string actorKey) => Sides[actorKey];
        public GridPos? PositionOf(string actorKey) => Positions[actorKey];
        public EntityFacts FactsOf(string actorKey) => Facts[actorKey];
        public IReadOnlyList<CompiledAction> HeldActionsOf(string actorKey) =>
            Held.TryGetValue(actorKey, out var list) ? list : Array.Empty<CompiledAction>();
        public ActorDerivedSnapshot? DerivedOf(string actorKey)
        {
            OnDerivedOf?.Invoke();
            return Derived.TryGetValue(actorKey, out var d) ? d : null;
        }
        public string? GarrisonedStructureKeyOf(string actorKey) =>
            Garrisoning.TryGetValue(actorKey, out var structureKey) ? structureKey : null;
        public GridPos? ObjectivePositionOf(string actorKey) =>
            Objectives.TryGetValue(actorKey, out var pos) ? pos : null;
        public long? MaxHpOf(string actorKey) =>
            MaxHps.TryGetValue(actorKey, out var maxHp) ? maxHp : null;
        public int AggressionOf(string actorKey) =>
            Aggressions.TryGetValue(actorKey, out var a) ? a : 0;
    }

    static ActorDerivedSnapshot Snapshot(double accuracy = 0, double dodge = 0, double power = 0) =>
        ActorDerivedSnapshot.FromValues(new[]
        {
            new KeyValuePair<string, double>(DerivedStatChannels.CombatAccuracyOmni, accuracy),
            new KeyValuePair<string, double>(DerivedStatChannels.CombatDodgeOmni, dodge),
            new KeyValuePair<string, double>(DerivedStatChannels.CombatPowerOmni, power),
        });

    static CompiledAction Action(
        string id, ActionTag[]? tags = null, int minRange = 0, int maxRange = int.MaxValue,
        ICompiledPredicate? condition = null) => new(
        id, ActionKind.Skill, 1, tags ?? Array.Empty<ActionTag>(), true, 1, false, false, "item.test",
        ActionEnvelope.NoOp with { ActionId = id }, new CompiledTargetSpec(true, Array.Empty<FusionRpg.Contracts.TargetSpec>()),
        minRange, maxRange, null, false, condition ?? PredicateCompiler.Always,
        Array.Empty<CompiledActionCost>(), Array.Empty<ActionScopeRow>());

    // combat-ai `profile-schema` CAI1.8 (H7): AiTuning narrowed to geometry/dead keys only. The ten
    // scorer weights (the shipped siege.v1.json identity) are supplied directly, matching
    // combat-ai.v1.json's own profiles["siege/default"].scoring row -- the SAME numbers, not read
    // through CombatAiProfilePolicy, so this test file stays decoupled from that hub's configuration.
    static ScoringWeights DefaultWeights() =>
        new(HitChance: 70, Objective: 50, Kill: 15, LowHp: 10, CannotCounter: 10, Round: 1, Risk: 120,
            AggressionRange: 2, MaxCandidatesScored: 32);

    const long DefaultRetargetLatencyTicks = 0;

    static SiegeAiIntentSource Ai(FakeBattleView view, Func<long, int>? roundOf = null) =>
        new(view, new CooldownLedger(), NoStanceHeld.Instance, AlwaysAffordable.Instance,
            DefaultWeights(), DefaultRetargetLatencyTicks, SiegeTuningPolicy.Ai, roundOf ?? (tick => (int)tick));

    /// <summary>CAI1.14 site 3: the siege decision writes into per-source scratch (the two candidate
    /// buffers, the selection scratch, and the retarget closure's captured values), so a nested decision
    /// through the same source must THROW rather than overwrite the outer decision's state. Driven from
    /// the view's own `DerivedOf`, i.e. inside the guarded window; asserted, not assumed.</summary>
    [Fact]
    public void Re_entering_the_siege_decision_throws_instead_of_sharing_its_scratch()
    {
        var view = new FakeBattleView();
        view.Add("wave:0", side: 0, pos: null, held: new[] { Action("act.attack") });
        view.Add("wave:1", side: 1, pos: null);

        var source = Ai(view);
        view.OnDerivedOf = () =>
        {
            view.OnDerivedOf = null;
            Assert.Throws<InvalidOperationException>(() => source.TryDeclare("wave:0", nowTick: 0));
        };

        // The outer decision still completes once the guard has refused the nested one...
        var intent = source.TryDeclare("wave:0", nowTick: 0);
        Assert.False(intent.IsNone);
        Assert.Equal("wave:1", intent.TargetKey);

        // ...and the source is usable again once the window closed.
        Assert.False(source.TryDeclare("wave:0", nowTick: 1).IsNone);
    }

    // ---- steps 1/3/4/5, copied from ActionSelectionTests' own StubIntentSource coverage ----------

    [Fact]
    public void NoHeldActionsPassesRatherThanHanging()
    {
        var view = new FakeBattleView();
        view.Add("wave:0", side: 0, pos: null);
        view.Add("wave:1", side: 1, pos: null, held: new[] { Action("act.attack") });

        var intent = Ai(view).TryDeclare("wave:0", nowTick: 0);
        Assert.True(intent.IsNone);
    }

    [Fact]
    public void NoLiveEnemyPassesRatherThanHanging()
    {
        var view = new FakeBattleView();
        view.Add("wave:0", side: 0, pos: null, held: new[] { Action("act.attack") });
        view.Add("wave:1", side: 0, pos: null, held: new[] { Action("act.attack") }); // same side

        var intent = Ai(view).TryDeclare("wave:0", nowTick: 0);
        Assert.True(intent.IsNone);
    }

    [Fact]
    public void NothingUsableAndNoMovementActionPassesRatherThanHanging()
    {
        var view = new FakeBattleView();
        view.Add("wave:0", side: 0, pos: new GridPos(0, 0), held: new[] { Action("act.attack", minRange: 5, maxRange: 5) });
        view.Add("wave:1", side: 1, pos: new GridPos(0, 1));

        var intent = Ai(view).TryDeclare("wave:0", nowTick: 0);
        Assert.True(intent.IsNone);
    }

    [Fact]
    public void AUsableActionProducesAnIntentAgainstTheChosenTarget()
    {
        var view = new FakeBattleView();
        view.Add("wave:0", side: 0, pos: null, held: new[] { Action("act.attack") });
        view.Add("wave:1", side: 1, pos: null);

        var intent = Ai(view).TryDeclare("wave:0", nowTick: 0);

        Assert.False(intent.IsNone);
        Assert.Equal("act.attack", intent.ActionId);
        Assert.Equal("wave:1", intent.TargetKey);
    }

    [Fact]
    public void SelfWithNoDerivedSnapshotPassesRatherThanCrashing()
    {
        var view = new FakeBattleView();
        view.Add("wave:0", side: 0, pos: null, held: new[] { Action("act.attack") });
        view.Add("wave:1", side: 1, pos: null);
        view.Derived["wave:0"] = null; // explicitly unreadable -- distinct from Add's own "unspecified" default

        var intent = Ai(view).TryDeclare("wave:0", nowTick: 0);
        Assert.True(intent.IsNone); // cannot estimate a hit chance for an unreadable self -- no target
    }

    // ---- step 2: real scoring discriminates --------------------------------------------------------

    [Fact]
    public void Prefers_the_enemy_it_is_more_likely_to_hit()
    {
        var view = new FakeBattleView();
        view.Add("wave:0", side: 0, pos: null, derived: Snapshot(accuracy: 500), held: new[] { Action("act.attack") });
        view.Add("hard-to-hit", side: 1, pos: null, derived: Snapshot(dodge: 500)); // big dodge -> low pHit
        view.Add("easy-to-hit", side: 1, pos: null, derived: Snapshot(dodge: -500)); // negative dodge -> high pHit

        var intent = Ai(view).TryDeclare("wave:0", nowTick: 0);

        Assert.False(intent.IsNone);
        Assert.Equal("easy-to-hit", intent.TargetKey);
    }

    [Fact]
    public void Prefers_the_more_already_damaged_enemy_when_hit_chance_ties()
    {
        var view = new FakeBattleView();
        view.Add("wave:0", side: 0, pos: null, held: new[] { Action("act.attack") });
        view.Add("full-hp", side: 1, pos: null, hpMilli: 1000);
        view.Add("half-hp", side: 1, pos: null, hpMilli: 500);

        var intent = Ai(view).TryDeclare("wave:0", nowTick: 0);

        Assert.False(intent.IsNone);
        Assert.Equal("half-hp", intent.TargetKey); // WeightLowHp rewards the already-damaged target
    }

    // ---- Aggression (§5.20 rule 4): a signed tier shift, absolute within it, resolved 2026-09-07 ----

    /// <summary>R1's own thesis, proven live: a taunt is a TIER shift, not a score bonus — it wins even
    /// against a candidate that would score strictly higher on every additive term, because tier is
    /// decided first and the additive score is never consulted outside the best tier.</summary>
    [Fact]
    public void A_taunting_target_is_chosen_over_a_target_that_scores_higher()
    {
        var view = new FakeBattleView();
        view.Add("wave:0", side: 0, pos: null, derived: Snapshot(accuracy: 500), held: new[] { Action("act.attack") });
        // Scores WORSE on every term (hard to hit) but taunts -- pulls into EffectiveTier -2 (BETTER).
        view.Add("weak-taunting", side: 1, pos: null, derived: Snapshot(dodge: 500), aggression: 2);
        // Scores BEST on every term (easy to hit, no aggression) but stays at EffectiveTier 0 (worse).
        view.Add("easy-target", side: 1, pos: null, derived: Snapshot(dodge: -500));

        var intent = Ai(view).TryDeclare("wave:0", nowTick: 0);

        Assert.False(intent.IsNone);
        Assert.Equal("weak-taunting", intent.TargetKey);
    }

    /// <summary>The stealth half of the same mechanism: a target that would score HIGHEST is never even
    /// considered once its own aggression pushes it into a worse tier than a live alternative.</summary>
    [Fact]
    public void A_stealthed_target_is_never_chosen_even_though_it_would_score_highest()
    {
        var view = new FakeBattleView();
        view.Add("wave:0", side: 0, pos: null, derived: Snapshot(accuracy: 500), held: new[] { Action("act.attack") });
        // Would score BEST (easiest to hit) but stealthed -- pushed to EffectiveTier +2 (worse).
        view.Add("stealthed-easy-target", side: 1, pos: null, derived: Snapshot(dodge: -500), aggression: -2);
        // Ordinary candidate, EffectiveTier 0 -- the only one in the best tier once stealth applies.
        view.Add("normal-target", side: 1, pos: null);

        var intent = Ai(view).TryDeclare("wave:0", nowTick: 0);

        Assert.False(intent.IsNone);
        Assert.Equal("normal-target", intent.TargetKey);
    }

    [Fact]
    public void Prefers_a_target_that_cannot_counter_when_everything_else_ties()
    {
        var view = new FakeBattleView();
        view.Add("wave:0", side: 0, pos: null, held: new[] { Action("act.attack") });
        view.Add("can-counter", side: 1, pos: null, held: new[] { Action("act.counter") });
        view.Add("cannot-counter", side: 1, pos: null); // no held actions at all

        var intent = Ai(view).TryDeclare("wave:0", nowTick: 0);

        Assert.False(intent.IsNone);
        Assert.Equal("cannot-counter", intent.TargetKey);
    }

    /// <summary>base-defense `siege-ai` (resolved 2026-09-07): `ObjectiveClassMilli`.</summary>
    [Fact]
    public void Prefers_the_enemy_closer_to_its_own_objective()
    {
        var view = new FakeBattleView();
        view.Add("wave:0", side: 0, pos: null, held: new[] { Action("act.attack") });
        view.Objectives["wave:0"] = new GridPos(10, 10);
        view.Add("far-from-objective", side: 1, pos: new GridPos(0, 0));
        view.Add("near-objective", side: 1, pos: new GridPos(10, 9)); // Chebyshev 1 from the objective

        var intent = Ai(view).TryDeclare("wave:0", nowTick: 0);

        Assert.False(intent.IsNone);
        Assert.Equal("near-objective", intent.TargetKey);
    }

    /// <summary>base-defense `siege-ai` (resolved 2026-09-07): `IsKillingBlow`. Both candidates share
    /// the SAME `hpMilli` (so `TargetMissingHpMilli` ties and cannot be what discriminates them) but
    /// different `maxHp`, isolating the RAW-hp comparison `IsKillingBlow` alone reads.</summary>
    [Fact]
    public void Prefers_the_enemy_it_can_actually_kill_over_one_it_cannot()
    {
        var view = new FakeBattleView();
        view.Add("wave:0", side: 0, pos: null, derived: Snapshot(power: 100), held: new[] { Action("act.attack") });
        view.Add("fragile", side: 1, pos: null, hpMilli: 500, maxHp: 10); // raw HP 5 -- a killing blow
        view.Add("sturdy", side: 1, pos: null, hpMilli: 500, maxHp: 10_000); // raw HP 5,000 -- survives easily

        var intent = Ai(view).TryDeclare("wave:0", nowTick: 0);

        Assert.False(intent.IsNone);
        Assert.Equal("fragile", intent.TargetKey);
    }

    /// <summary>base-defense `siege-ai` (resolved 2026-09-07, corrected same session): `IncomingThreatMilli`
    /// is live by default (self-relative, no tunable) as long as the DECIDING actor has positive
    /// `CombatPowerOmni` — set via `Snapshot(power:)` on `wave:0`, so this goes through the shared
    /// `Ai()` helper like every other discrimination test.</summary>
    [Fact]
    public void Avoids_the_enemy_standing_near_a_reinforcement_when_everything_else_ties()
    {
        var view = new FakeBattleView();
        view.Add("wave:0", side: 0, pos: null, derived: Snapshot(power: 500), held: new[] { Action("act.attack") });
        view.Add("guarded", side: 1, pos: new GridPos(5, 5));
        // "reinforcement" is ALSO a scoreable candidate (same side, live, readable) -- held: act.counter
        // makes it strictly worse on its own TargetCanCounter term, so it cannot tie with "alone" on
        // the two terms this test actually discriminates on (both would otherwise read cannotCounter =
        // false, incomingThreat = 0, an unrelated tie this test does not intend to depend on).
        view.Add("reinforcement", side: 1, pos: new GridPos(5, 6), derived: Snapshot(power: 1000),
            held: new[] { Action("act.counter") }); // within threatRadiusCells of "guarded"
        view.Add("alone", side: 1, pos: new GridPos(0, 0)); // no ally within threatRadiusCells

        var intent = Ai(view).TryDeclare("wave:0", nowTick: 0);

        Assert.False(intent.IsNone);
        Assert.Equal("alone", intent.TargetKey); // "guarded" carries incoming threat from "reinforcement"; "alone" does not
    }

    /// <summary>
    /// The whole reason this term is self-relative rather than an absolute tunable: the SAME nearby
    /// reinforcement power (500) must read as more dangerous to a weaker self than to a stronger one —
    /// a fixed milli reference could never satisfy this once `CombatPowerOmni` scales with `P(Theta)`.
    /// "guarded" carries a fixed, hand-computed +8000 low-hp advantage over "alone" (hpMilli 200 vs
    /// 1000) so a TINY risk penalty (strong self) does not erase it, but a LARGE one (weak self, same
    /// absolute reinforcement) does — proving the SAME board reads oppositely depending only on self power.
    /// </summary>
    [Fact]
    public void The_same_reinforcement_reads_as_more_dangerous_to_a_weaker_self()
    {
        ActionIntent WithSelfPower(double power)
        {
            var view = new FakeBattleView();
            view.Add("wave:0", side: 0, pos: null, derived: Snapshot(power: power), held: new[] { Action("act.attack") });
            view.Add("guarded", side: 1, pos: new GridPos(5, 5), hpMilli: 200); // +8000 on LowHp vs "alone"
            view.Add("reinforcement", side: 1, pos: new GridPos(5, 6), derived: Snapshot(power: 500)); // within threatRadiusCells of "guarded"
            view.Add("alone", side: 1, pos: new GridPos(0, 0)); // no ally within threatRadiusCells
            return Ai(view).TryDeclare("wave:0", nowTick: 0);
        }

        // Self power 100,000: threatSum(guarded)=500 -> ratio 5/1000 -> risk cost 120*5=600, far below
        // "guarded"'s own +8000 low-hp edge. "guarded" wins despite carrying (trivial) threat.
        Assert.Equal("guarded", WithSelfPower(100_000).TargetKey);

        // Self power 500: the SAME threatSum=500 now ratios to 1000/1000 (capped) -> risk cost
        // 120*1000=120,000, which swamps the +8000 edge many times over. "alone" wins instead.
        Assert.Equal("alone", WithSelfPower(500).TargetKey);
    }

    [Fact]
    public void A_self_with_no_combat_power_reads_zero_threat_rather_than_dividing_by_zero()
    {
        var view = new FakeBattleView();
        view.Add("wave:0", side: 0, pos: null, held: new[] { Action("act.attack") }); // StubNeutral: power 0
        view.Add("guarded", side: 1, pos: new GridPos(5, 5));
        view.Add("reinforcement", side: 1, pos: new GridPos(5, 6), derived: Snapshot(power: 1000));

        var intent = Ai(view).TryDeclare("wave:0", nowTick: 0); // must not throw

        Assert.False(intent.IsNone);
    }

    [Fact]
    public void Same_board_same_decision_10000_times()
    {
        var view = new FakeBattleView();
        view.Add("wave:0", side: 0, pos: null, derived: Snapshot(accuracy: 300), held: new[] { Action("act.attack") });
        for (var i = 0; i < 5; i++)
            view.Add($"zombie:{i}", side: 1, pos: null, hpMilli: 700 + i * 10, derived: Snapshot(dodge: i * 40));

        ActionIntent? first = null;
        for (var i = 0; i < 10_000; i++)
        {
            var intent = Ai(view).TryDeclare("wave:0", nowTick: 0);
            first ??= intent;
            Assert.Equal(first.Value.TargetKey, intent.TargetKey);
            Assert.Equal(first.Value.ActionId, intent.ActionId);
        }
    }

    // ---- 17.8: retarget latency (§5.20 rule 3) -----------------------------------------------------

    // combat-ai `profile-schema` CAI1.8 (H7): RetargetLatencyTicks moved off AiTuning into
    // combat-ai.v1.json's own antiRepeat block -- supplied directly here, matching the shipped
    // identity's 0 default.
    static long WithLatency(long ticks) => ticks;

    static SiegeAiIntentSource AiWithRetarget(FakeBattleView view, RetargetLedger ledger, long retargetLatencyTicks) =>
        new(view, new CooldownLedger(), NoStanceHeld.Instance, AlwaysAffordable.Instance,
            DefaultWeights(), retargetLatencyTicks, SiegeTuningPolicy.Ai, tick => (int)tick, ledger);

    [Fact]
    public void Retarget_latency_is_authored_and_honoured()
    {
        var view = new FakeBattleView();
        view.Add("wave:0", side: 0, pos: null, held: new[] { Action("act.attack") });
        view.Add("target-a", side: 1, pos: null, derived: Snapshot(dodge: -500)); // easy to hit
        view.Add("target-b", side: 1, pos: null, derived: Snapshot(dodge: 500));  // hard to hit
        var ledger = new RetargetLedger();
        var ai = AiWithRetarget(view, ledger, WithLatency(3));

        var first = ai.TryDeclare("wave:0", nowTick: 0);
        Assert.Equal("target-a", first.TargetKey); // objectively better at tick 0

        // target-b becomes objectively better -- simulates "the situation changed mid-window"
        view.Derived["target-a"] = Snapshot(dodge: 500);
        view.Derived["target-b"] = Snapshot(dodge: -500);

        var stillHeld = ai.TryDeclare("wave:0", nowTick: 1); // elapsed 1 < latency 3
        Assert.Equal("target-a", stillHeld.TargetKey); // held -- "keeps swinging at a target which just moved"

        var retargeted = ai.TryDeclare("wave:0", nowTick: 3); // elapsed 3, not < latency 3
        Assert.Equal("target-b", retargeted.TargetKey); // latency elapsed -- now rescores
    }

    [Fact]
    public void Retargeting_is_immediate_when_latency_is_the_shipped_default_of_zero()
    {
        var view = new FakeBattleView();
        view.Add("wave:0", side: 0, pos: null, held: new[] { Action("act.attack") });
        view.Add("target-a", side: 1, pos: null, derived: Snapshot(dodge: -500));
        view.Add("target-b", side: 1, pos: null, derived: Snapshot(dodge: 500));
        var ledger = new RetargetLedger();
        var ai = AiWithRetarget(view, ledger, DefaultRetargetLatencyTicks); // RetargetLatencyTicks: 0

        Assert.Equal("target-a", ai.TryDeclare("wave:0", nowTick: 0).TargetKey);

        view.Derived["target-a"] = Snapshot(dodge: 500);
        view.Derived["target-b"] = Snapshot(dodge: -500);

        // latency 0 -- rescores on the very next tick, no holding at all
        Assert.Equal("target-b", ai.TryDeclare("wave:0", nowTick: 1).TargetKey);
    }

    [Fact]
    public void A_held_target_that_dies_is_abandoned_before_the_latency_elapses()
    {
        var view = new FakeBattleView();
        view.Add("wave:0", side: 0, pos: null, held: new[] { Action("act.attack") });
        view.Add("target-a", side: 1, pos: null);
        var ledger = new RetargetLedger();
        var ai = AiWithRetarget(view, ledger, WithLatency(10));

        Assert.Equal("target-a", ai.TryDeclare("wave:0", nowTick: 0).TargetKey);

        view.Actors.Remove("target-a"); // simulates death -- no longer live
        view.Add("target-b", side: 1, pos: null);

        // well within the 10-tick latency window, but the held target no longer exists
        var intent = ai.TryDeclare("wave:0", nowTick: 1);
        Assert.False(intent.IsNone);
        Assert.Equal("target-b", intent.TargetKey);
    }

    [Fact]
    public void Same_stateful_decision_sequence_is_reproducible_10000_times()
    {
        ActionIntent? firstStep1 = null, firstStep2 = null;
        for (var i = 0; i < 10_000; i++)
        {
            var view = new FakeBattleView();
            view.Add("wave:0", side: 0, pos: null, held: new[] { Action("act.attack") });
            view.Add("target-a", side: 1, pos: null, derived: Snapshot(dodge: -500));
            view.Add("target-b", side: 1, pos: null, derived: Snapshot(dodge: 500));
            var ai = AiWithRetarget(view, new RetargetLedger(), WithLatency(3));

            var step1 = ai.TryDeclare("wave:0", nowTick: 0);
            firstStep1 ??= step1;
            Assert.Equal(firstStep1.Value.TargetKey, step1.TargetKey);

            view.Derived["target-a"] = Snapshot(dodge: 500);
            view.Derived["target-b"] = Snapshot(dodge: -500);
            var step2 = ai.TryDeclare("wave:0", nowTick: 1); // still held
            firstStep2 ??= step2;
            Assert.Equal(firstStep2.Value.TargetKey, step2.TargetKey);
        }
    }

    // ---- R6: decision trace wiring (§7) --------------------------------------------------------

    static SiegeAiIntentSource AiWithTrace(FakeBattleView view, BattleTrace trace) =>
        new(view, new CooldownLedger(), NoStanceHeld.Instance, AlwaysAffordable.Instance,
            DefaultWeights(), DefaultRetargetLatencyTicks, SiegeTuningPolicy.Ai, tick => (int)tick,
            retarget: null, trace: trace);

    [Fact]
    public void A_real_rescore_records_the_top_three_with_their_breakdown()
    {
        var view = new FakeBattleView();
        view.Add("wave:0", side: 0, pos: null, held: new[] { Action("act.attack") });
        view.Add("target-a", side: 1, pos: null, derived: Snapshot(dodge: -500)); // easy to hit
        view.Add("target-b", side: 1, pos: null, derived: Snapshot(dodge: 500));  // hard to hit
        var trace = new BattleTrace();

        var intent = AiWithTrace(view, trace).TryDeclare("wave:0", nowTick: 5);

        Assert.Equal("target-a", intent.TargetKey);
        var line = Assert.Single(trace.AiDecisions);
        Assert.StartsWith("5 wave:0 #1=target-a(", line);
        Assert.Contains("target-b", line); // both scored candidates are named, not just the winner
    }

    [Fact]
    public void No_trace_supplied_records_nothing_and_does_not_throw()
    {
        var view = new FakeBattleView();
        view.Add("wave:0", side: 0, pos: null, held: new[] { Action("act.attack") });
        view.Add("target-a", side: 1, pos: null);

        var intent = Ai(view).TryDeclare("wave:0", nowTick: 0);

        Assert.Equal("target-a", intent.TargetKey); // the omitted trace changed nothing about the decision
    }

    [Fact]
    public void A_held_retarget_tick_records_no_new_decision()
    {
        // 17.8's whole point is "keeps swinging without rescoring" -- nothing was scored, so R6 has
        // nothing to trace on a held tick.
        var view = new FakeBattleView();
        view.Add("wave:0", side: 0, pos: null, held: new[] { Action("act.attack") });
        view.Add("target-a", side: 1, pos: null, derived: Snapshot(dodge: -500));
        view.Add("target-b", side: 1, pos: null, derived: Snapshot(dodge: 500));
        var trace = new BattleTrace();
        var ai = new SiegeAiIntentSource(view, new CooldownLedger(), NoStanceHeld.Instance,
            AlwaysAffordable.Instance, DefaultWeights(), WithLatency(3), SiegeTuningPolicy.Ai,
            tick => (int)tick, new RetargetLedger(), trace);

        ai.TryDeclare("wave:0", nowTick: 0); // real rescore -- records one line
        Assert.Single(trace.AiDecisions);

        ai.TryDeclare("wave:0", nowTick: 1); // held -- elapsed 1 < latency 3, no new scoring
        Assert.Single(trace.AiDecisions);
    }

    // ---- 17.9: the garrisoned emplacement's replacement vocabulary (§5.20 rule 5) ------------------

    [Fact]
    public void A_non_garrisoned_actor_still_moves_when_nothing_else_is_usable()
    {
        // act.move's OWN range must be restrictive too (matching act.attack's) so step 3's loop --
        // which tries every held action at ITS OWN declared range, movement included -- correctly
        // rejects it out of range; only step 4's hardcoded 0..int.MaxValue override can then let it
        // through, which is the actual behavior this test means to exercise.
        var view = new FakeBattleView();
        view.Add("wave:0", side: 0, pos: new GridPos(0, 0),
            held: new[] { Action("act.attack", minRange: 5, maxRange: 5), Action("act.move", tags: new[] { ActionTag.Movement }, minRange: 5, maxRange: 5) });
        view.Add("wave:1", side: 1, pos: new GridPos(0, 1));

        var intent = Ai(view).TryDeclare("wave:0", nowTick: 0);

        Assert.False(intent.IsNone);
        Assert.Equal("act.move", intent.ActionId); // confirms step 4 DOES fire absent a garrison
    }

    [Fact]
    public void A_garrisoned_occupant_never_abandons_post_to_chase_a_target()
    {
        var view = new FakeBattleView();
        view.Add("wave:0", side: 0, pos: new GridPos(0, 0),
            held: new[] { Action("act.attack", minRange: 5, maxRange: 5), Action("act.move", tags: new[] { ActionTag.Movement }, minRange: 5, maxRange: 5) });
        view.Add("wave:1", side: 1, pos: new GridPos(0, 1));
        view.Garrisoning["wave:0"] = "structure:turret-1";

        var intent = Ai(view).TryDeclare("wave:0", nowTick: 0);

        Assert.True(intent.IsNone, $"expected None, got ActionId={intent.ActionId} TargetKey={intent.TargetKey}");
    }

    [Fact]
    public void The_replacement_vocabulary_has_exactly_two_values_and_no_more() =>
        Assert.Equal(2, Enum.GetValues<EmplacementFireMode>().Length);

    // ---- CAI1.5: baseOverlayDamage no longer defaults to 0.0 (spec-core-scorer.md S8 commit 2) -----

    [Fact]
    public void EffectiveRungOf_is_consulted_with_the_actors_preferred_held_action()
    {
        var view = new FakeBattleView();
        view.Add("squad:0", side: 0, pos: null, derived: Snapshot(power: 10),
            held: new[] { Action("act.preferred") });
        view.Add("wave:0", side: 1, pos: null, hpMilli: 1000, derived: Snapshot(power: 5), maxHp: 1000);

        var calls = new List<(string ActorKey, string ActionId)>();
        var ai = new SiegeAiIntentSource(
            view, new CooldownLedger(), NoStanceHeld.Instance, AlwaysAffordable.Instance,
            DefaultWeights(), DefaultRetargetLatencyTicks, SiegeTuningPolicy.Ai, tick => (int)tick,
            effectiveRungOf: (actorKey, actionId) => { calls.Add((actorKey, actionId)); return 1; });

        ai.TryDeclare("squad:0", nowTick: 0);

        Assert.Contains(("squad:0", "act.preferred"), calls);
    }

    [Fact]
    public void EffectiveRungOf_omitted_falls_back_to_the_held_actions_authored_rung_without_throwing()
    {
        var view = new FakeBattleView();
        view.Add("squad:0", side: 0, pos: null, derived: Snapshot(power: 10),
            held: new[] { Action("act.preferred") });
        view.Add("wave:0", side: 1, pos: null, hpMilli: 1000, derived: Snapshot(power: 5), maxHp: 1000);

        // No effectiveRungOf supplied -- exercises the `preferred.Rung` fallback (Action(...)'s own
        // authored Rung, 1) through the real ActionBaseDerivation/ActionBaseTuningHub/RungPolicy path.
        var ai = Ai(view);

        var intent = ai.TryDeclare("squad:0", nowTick: 0);

        Assert.False(intent.IsNone);
    }
}

public class SiegeHitChanceTests
{
    static ActorDerivedSnapshot Snapshot(double accuracy = 0, double dodge = 0) =>
        ActorDerivedSnapshot.FromValues(new[]
        {
            new KeyValuePair<string, double>(DerivedStatChannels.CombatAccuracyOmni, accuracy),
            new KeyValuePair<string, double>(DerivedStatChannels.CombatDodgeOmni, dodge),
        });

    [Fact]
    public void Neutral_snapshots_give_exactly_half_hit_chance()
    {
        var chance = SiegeHitChance.EstimateMilli(Snapshot(), Snapshot());
        Assert.Equal(500, chance);
    }

    [Fact]
    public void Higher_attacker_accuracy_raises_hit_chance()
    {
        var baseline = SiegeHitChance.EstimateMilli(Snapshot(), Snapshot());
        var higher = SiegeHitChance.EstimateMilli(Snapshot(accuracy: 500), Snapshot());
        Assert.True(higher > baseline);
    }

    [Fact]
    public void Higher_defender_dodge_lowers_hit_chance()
    {
        var baseline = SiegeHitChance.EstimateMilli(Snapshot(), Snapshot());
        var lower = SiegeHitChance.EstimateMilli(Snapshot(), Snapshot(dodge: 500));
        Assert.True(lower < baseline);
    }

    [Fact]
    public void Result_is_always_clamped_to_a_real_per_mille_range()
    {
        var extreme = SiegeHitChance.EstimateMilli(Snapshot(accuracy: 1_000_000), Snapshot());
        Assert.InRange(extreme, 0, 1000);
        var extremeLow = SiegeHitChance.EstimateMilli(Snapshot(), Snapshot(dodge: 1_000_000));
        Assert.InRange(extremeLow, 0, 1000);
    }

    [Fact]
    public void Never_touches_a_float_the_result_is_a_plain_int()
    {
        int chance = SiegeHitChance.EstimateMilli(Snapshot(), Snapshot()); // compiles only if the return type is int
        Assert.Equal(500, chance);
    }
}
