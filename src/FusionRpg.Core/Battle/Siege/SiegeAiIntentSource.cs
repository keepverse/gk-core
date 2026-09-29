using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Ai;
using FusionRpg.Core.Actions.Rungs;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Battle.Siege;

/// <summary>
/// base-defense `siege-ai` (spec-siege-ai.md), task 17.4: the first LIVE `IIntentSource` this program
/// has ever built — `AiScoring`'s own pure functions (R1/R2/R5/R6) get their first real caller.
/// Copies `StubIntentSource`'s own exact steps 1/3/4/5 (cannot-act check, first-usable-action loop,
/// move-if-out-of-reach, pass) VERBATIM — this module's only new work is step 2, "who": nearest-enemy
/// replaced by `AiScoring.ChooseTarget` over real, scored candidates.
///
/// <para><b>Stateless by default, optionally stateful (17.8).</b> Omitting the constructor's
/// `retarget` parameter reproduces the ORIGINAL 17.4 shape exactly — every `TryDeclare` call recomputes
/// the best target fresh from `IBattleView`, zero memory between ticks, the same shape `StubIntentSource`
/// itself has. Supplying a `RetargetLedger` enables §5.20 rule 3's own retarget-latency enforcement: a
/// still-valid held target inside `ai.retargetLatencyTicks` of its last retarget is returned WITHOUT
/// rescoring, exactly the genuinely NEW, stateful shape this class's own doc comment once named as
/// separate, deferred work — see `RetargetLedger`'s own doc comment below.</para>
///
/// <para><b>17.9 (§5.20 rule 5):</b> an actor currently garrisoning a `CombatantKind.Structure`
/// emplacement never falls through to step 4's movement fallback — see `TryDeclare`'s own inline
/// comment for the full reasoning and `EmplacementFireMode`'s own doc comment for the replacement
/// vocabulary this represents.</para>
///
/// <para><b>Five of `AiCandidate`'s 7 scoring inputs are real (resolved 2026-09-07 — see
/// spec-siege-ai.md's own "Open questions" for the full reasoning behind each): `HitChanceMilli`
/// (`SiegeHitChance.EstimateMilli`, reusing `OverlayCombatCalculator`'s own Omni formula),
/// `TargetMissingHpMilli` (direct from `EntityFacts.HpMilli`), `TargetCanCounter` (does the target
/// hold any action at all), `ObjectiveClassMilli` (Chebyshev distance from the candidate to MY OWN
/// objective — an honest v1: `IBattleView` exposes only per-actor facts, never board geometry, so a
/// real `BoardPathfinder` route is not reachable from here), and `IsKillingBlow`
/// (`SiegeExpectedDamage.IsKillingBlow`, the Omni-fallback branch of `OverlayCombatCalculator`'s own
/// formula against the target's REAL current HP via the new `IBattleView.MaxHpOf`).</b>
/// `IncomingThreatMilli` is ALSO real and live by default (corrected 2026-09-07, same session):
/// nearby same-side raw power around a candidate, AS A FRACTION OF THE DECIDING ACTOR'S OWN power —
/// no tunable, since an absolute milli reference could never have a safe default once `CombatPowerOmni`
/// scales with `P(Theta)` (a private per-subsystem curve is exactly what the power-ladder rule
/// forbids). Still cover-free v1: the full model needs a structure-to-cover-data mechanism
/// `BattleActorSetup` does not have yet (a separate, un-started task). `Aggression` is ALSO wired
/// (resolved 2026-09-07, same session) via a new `IBattleView.AggressionOf(actorKey) -> int` — the
/// exact accessor §5.20 rule 4's own spec snippet names — but reads 0 (neutral) from every real
/// implementor today since no taunt/stealth/decoy content exists anywhere yet; this is the wiring
/// seam, matching `EmplacementFireMode`'s own precedent, not a promise of live behavior. `BaseTier`
/// stays a structural 0 for every candidate, not a gap: R1/R2's own §2 table names signed aggression
/// as the ONLY per-candidate input into tier, so there is no independent starting-tier concept to
/// wire. None of this makes the scorer inert: six of `AiCandidate`'s seven fields are real AND live by
/// default, and the seventh (`Aggression`) is wired and correctly neutral pending content — see
/// `AiScoring.Score`'s own additive formula.</para>
/// </summary>
public sealed class SiegeAiIntentSource : IIntentSource
{
    readonly IBattleView _view;
    readonly CooldownLedger _cooldowns;
    readonly IStanceCheck _stance;
    readonly IAffordabilityCheck _affordability;
    /// <summary>combat-ai `profile-schema` CAI1.8 (2026-09-20), narrowed again by `stance-wiring`
    /// CAI3.1: the two-member geometry tuning (`ObjectiveReferenceDistanceCells`/`ThreatRadiusCells` are
    /// read). The two dead keys (`StanceDefault`/`AutoResolveHandicapMilli`) are gone from the record —
    /// CAI3.1 deleted them from `siege.v3.json` and switched this reader in the same commit.</summary>
    readonly AiTuning _tuning;
    /// <summary>combat-ai `core-scorer`/`profile-schema` (2026-09-20): the scorer's weights, supplied
    /// directly by the caller — `BattleRunState` reads them from
    /// `CombatAiProfilePolicy.For(AiPlace.Siege, AiRole.Default).Scoring`/`.Selection` in production;
    /// this class no longer derives them from <see cref="_tuning"/>, which lost its scoring fields
    /// when they moved to `combat-ai.v1.json` (H7).</summary>
    readonly ScoringWeights _weights;
    /// <summary>combat-ai `profile-schema` CAI1.8: moved out of <see cref="_tuning"/> into
    /// `combat-ai.v1.json`'s `profiles["siege/default"].antiRepeat.retargetLatencyTicks` — supplied
    /// directly rather than read off the (now-narrower) tuning record.</summary>
    readonly long _retargetLatencyTicks;
    readonly Func<long, int> _roundOf;
    readonly RetargetLedger? _retarget;
    /// <summary>combat-ai `decision-inspector` CAI2.4: the ONE recording seam. `trace` is sugar — a
    /// `BattleTrace` supplied without an explicit sink is wrapped in the byte-identical
    /// <see cref="BattleTraceDecisionSink"/>; a sink supplied directly (a spy, a ring, a future FE
    /// panel) replaces it. Null means the inspector is off, and the record is built only inside the
    /// `_sink is not null` guard, so a sink-less decision allocates nothing.</summary>
    readonly IAiDecisionSink? _sink;
    /// <summary>CAI2.4: set by `ChooseTarget` on a decision that ACTUALLY SCORED, so the record is
    /// emitted once from `TryDeclare` (where the chosen ACTION is known) and never on a held-target
    /// tick or a no-candidate tick.</summary>
    bool _scoredThisDecision;
    /// <summary>CAI2.4: the `UsabilityResult` the step-3 gate loop returned for each held action, in
    /// loop order — retained per decision so the record reports the verdicts the loop ACTUALLY
    /// produced instead of re-running the gates (which §1 forbids: a gate re-checked one tick later
    /// can answer differently).</summary>
    readonly List<(string ActionId, UsabilityResult Result)> _gateScratch = new();
    /// <summary>combat-ai `core-scorer` CAI1.5 (2026-09-20): `(actorKey, actionId) -> effective rung`,
    /// the SAME shape `BattleRunState.EffectiveRungOf` already is and the resolver
    /// <see cref="Cost.CostLedger"/>'s own `rungOf` reads — a caller-supplied seam, never assumed,
    /// matching every other domain-knowledge callback this class already takes. `null` (the default,
    /// and every existing test's shape) falls back to the held action's own AUTHORED `Rung` — exactly
    /// `EffectiveRungOf`'s own no-unlock-state fallback, so a caller that supplies nothing behaves as
    /// if no actor had any unlock progress yet, never a guessed rung.</summary>
    readonly Func<string, string, int>? _effectiveRungOf;

    /// <summary>CAI1.14 (sites 3/4): the trace's own top-three buffer, owned by this source for the
    /// source's whole life — one array per battle rather than one chain per traced decision.</summary>
    readonly (string ActorKey, ScoreBreakdown Breakdown)[] _topThreeBuffer = new (string, ScoreBreakdown)[3];

    // combat-ai `decision-perf` CAI1.14 (site 3, spec-decision-perf.md): the decision path's own scratch.
    // One source instance decides one actor at a time (re-entry depth 0, `overlay-control-loops.md` §6
    // rule 7), enforced by `ChooseTarget`'s guard; `Clear()` keeps capacity, so a warm source allocates
    // nothing here. The four delegates were `candidateKey => ...` lambdas and a local function until
    // CAI1.14 — each allocated a display class and/or a delegate per decision — and are bound once below.
    readonly List<string> _eligibleScratch = new(64);
    readonly List<TargetCandidate> _candidateScratch = new(64);
    readonly SelectionScratch _selectionScratch = new();
    readonly Func<string, int> _sideOf;
    readonly Func<string, bool> _isReadable;
    readonly TargetStage.TryBuildCandidate _tryBuildCandidate;
    readonly Func<string, bool> _stillScoreable;
    bool _inChooseTarget;
    /// <summary>Per-decision scratch the three callbacks above read; written by `ChooseTarget` before
    /// any of them runs, read only inside its guarded window.</summary>
    int _scratchMySide;
    string _scratchActorKey = "";
    IReadOnlyList<string> _scratchLive = Array.Empty<string>();
    ActorDerivedSnapshot? _scratchSelfDerived;
    IReadOnlyList<CompiledAction> _scratchHeldActions = Array.Empty<CompiledAction>();

    /// <summary>
    /// `roundOf` converts the kernel's own `nowTick` into `AiScoring`'s own `currentRound` — a
    /// caller-supplied callback, never assumed, matching the SAME "domain knowledge the compiler-shaped
    /// class does not itself need to know" precedent `AtomCompiler`'s `grantOwnerKeys`/`externalRefs`
    /// and `siege-fog`'s own `visionRangeOf` callbacks already established, rather than guessing at a
    /// tick-to-round relationship this pass has not verified.
    ///
    /// <para><c>retarget</c> (task 17.8, §5.20 rule 3) is OPTIONAL and defaults to <c>null</c> —
    /// omitting it reproduces 17.4's own shipped stateless behavior byte-for-byte (every call rescores
    /// fresh). Supplying one is the genuinely NEW, stateful shape 17.4's own doc comment named as
    /// separate, deferred work: a caller that wants `ai.retargetLatencyTicks` (17.3) actually enforced
    /// constructs ONE `RetargetLedger` and reuses it across every `TryDeclare` call for the SAME
    /// battle, so the "last target, last retarget tick" memory persists tick to tick.</para>
    ///
    /// <para><c>trace</c> (R6, §7) is ALSO OPTIONAL and defaults to <c>null</c> — omitting it costs
    /// nothing (matching `BattleTrace`'s own opt-in design). Supplying one records R6's own top-3
    /// scored candidates with their full per-term breakdown via `BattleTrace.AiDecision` every time
    /// `ChooseTarget` actually rescores (never on a 17.8 held-target tick, since nothing was scored).</para>
    /// </summary>
    public SiegeAiIntentSource(
        IBattleView view, CooldownLedger cooldowns, IStanceCheck stance, IAffordabilityCheck affordability,
        ScoringWeights weights, long retargetLatencyTicks, AiTuning tuning, Func<long, int> roundOf,
        RetargetLedger? retarget = null, BattleTrace? trace = null,
        Func<string, string, int>? effectiveRungOf = null, IAiDecisionSink? sink = null)
    {
        _view = view ?? throw new ArgumentNullException(nameof(view));
        _cooldowns = cooldowns ?? throw new ArgumentNullException(nameof(cooldowns));
        _stance = stance ?? throw new ArgumentNullException(nameof(stance));
        _affordability = affordability ?? throw new ArgumentNullException(nameof(affordability));
        _weights = weights ?? throw new ArgumentNullException(nameof(weights));
        _retargetLatencyTicks = retargetLatencyTicks;
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));
        _roundOf = roundOf ?? throw new ArgumentNullException(nameof(roundOf));
        _retarget = retarget;
        _sink = sink ?? (trace is null ? null : new BattleTraceDecisionSink(trace));
        _effectiveRungOf = effectiveRungOf;

        // CAI1.14 site 3: bind the decision path's four delegates once, over the scratch fields above.
        _sideOf = _view.SideOf;
        _isReadable = IsReadableCore;
        _tryBuildCandidate = TryBuildCandidateCore;
        _stillScoreable = StillScoreableCore;
    }

    /// <summary>Phase A's readability filter, cached as a delegate so the per-candidate decision does
    /// not allocate one (CAI1.14 site 3).</summary>
    bool IsReadableCore(string candidateKey) => _view.DerivedOf(candidateKey) is not null;

    /// <summary>The retarget ledger's `stillValid` callback, reading the per-decision scratch fields
    /// instead of capturing two locals (CAI1.14 site 3).</summary>
    bool StillScoreableCore(string candidateKey) => IsStillScoreable(candidateKey, _scratchMySide, _scratchLive);

    public ActionIntent TryDeclare(string actorKey, long nowTick)
    {
        // CAI2.4: the ONE emission point. It sits after the decision so `ChosenActionId`/`ChosenTargetKey`
        // are the ActionIntent's own fields -- the row's "equal the ActionIntent returned" line -- and
        // after `_scoredThisDecision`, so a held-target tick (which never scored) records nothing.
        _scoredThisDecision = false;
        _gateScratch.Clear();
        var intent = TryDeclareCore(actorKey, nowTick);
        if (_sink is not null && _scoredThisDecision) EmitDecisionRecord(actorKey, nowTick, in intent);
        return intent;
    }

    ActionIntent TryDeclareCore(string actorKey, long nowTick)
    {
        var heldActions = _view.HeldActionsOf(actorKey);
        if (heldActions.Count == 0) return ActionIntent.None; // step 1: cannot act at all

        var targetKey = ChooseTarget(actorKey, nowTick, heldActions); // step 2: who -- real scoring, not nearest
        if (targetKey is null) return ActionIntent.None; // no scoreable live enemy exists at all

        var casterPos = _view.PositionOf(actorKey);
        var targetPos = _view.PositionOf(targetKey);
        var selfFacts = _view.FactsOf(actorKey);
        var targetFacts = _view.FactsOf(targetKey);

        // step 3: with what -- first usable action, in the actor's own preference order, against the
        // ONE chosen target. Identical to StubIntentSource's own step 3, verbatim.
        for (var i = 0; i < heldActions.Count; i++)
        {
            var action = heldActions[i];
            var facts = new FactReader(selfFacts, targetFacts);
            var result = UsabilityEvaluator.Evaluate(
                actorKey, action.ActionId, action.Envelope, action.MinRange, action.MaxRange,
                actorHoldsAction: true, nowTick, _cooldowns, _stance, _affordability,
                casterPos, targetPos, action.Condition, ref facts);

            if (_sink is not null) _gateScratch.Add((action.ActionId, result));

            if (result.IsUsable)
                return new ActionIntent(action.ActionId, targetKey, action.Envelope);
        }

        // 17.9 (§5.20 rule 5): a garrisoned emplacement's occupant NEVER falls through to R3's own
        // movement fallback below -- "advance toward the objective" is meaningless for something that
        // cannot move, and abandoning the post to chase a target would defeat the whole point of
        // garrisoning it. Its replacement, two-entry vocabulary (Hold fire / Fire at will,
        // EmplacementFireMode) resolves to Fire-at-will for every actor today -- no content sets Hold
        // fire yet, so this v1 is honestly just "the movement fallback never fires for a garrisoned
        // occupant," matching 17.7's own precedent of a named vocabulary shipping before any content
        // gives its other value a real trigger.
        if (_view.GarrisonedStructureKeyOf(actorKey) is not null) return ActionIntent.None;

        // step 4: can't reach with anything -- move toward them if any held action is tagged
        // Movement. Identical to StubIntentSource's own step 4, verbatim.
        for (var i = 0; i < heldActions.Count; i++)
        {
            var action = heldActions[i];
            if (!Contains(action.Tags, ActionTag.Movement)) continue;

            var facts = new FactReader(selfFacts, targetFacts);
            var result = UsabilityEvaluator.Evaluate(
                actorKey, action.ActionId, action.Envelope, minRange: 0, maxRange: int.MaxValue,
                actorHoldsAction: true, nowTick, _cooldowns, _stance, _affordability,
                casterPos: null, targetPos: null, action.Condition, ref facts);

            if (_sink is not null) _gateScratch.Add((action.ActionId, result));

            if (result.IsUsable)
                return new ActionIntent(action.ActionId, targetKey, action.Envelope);
        }

        return ActionIntent.None; // step 5: pass -- a requirement, not a fallback
    }

    /// <summary>Step 2, real: builds one `AiCandidate` per live enemy actor this view's own
    /// `DerivedOf` can evaluate, scores them with `AiScoring.ChooseTarget` (R1/R2/R5), and returns the
    /// winner's key — or `null` when no enemy exists or none is scoreable (e.g., every enemy is
    /// currently hidden under fog, so `DerivedOf` returns null for all of them).
    ///
    /// <para>17.8: when a `RetargetLedger` was supplied, a still-valid held target inside
    /// `ai.retargetLatencyTicks` of its own last retarget is returned WITHOUT rescoring — "a unit that
    /// keeps swinging at a target which just moved," §5.20 rule 3's own words, honored literally: a
    /// strictly better candidate appearing mid-window does not trigger an early switch. A held target
    /// that becomes invalid (dead, or hidden under fog) is abandoned immediately rather than honored
    /// through the rest of its window, since there is nothing left to keep attacking.</para>
    /// </summary>
    /// <summary>
    /// combat-ai `decision-perf` CAI1.14 (site 3, spec-decision-perf.md): this decision writes into
    /// per-source scratch — the two candidate buffers (<see cref="_eligibleScratch"/>,
    /// <see cref="_candidateScratch"/>), the selection scratch, and the values the retarget closure used
    /// to capture. One owner, one decision at a time: a nested decision would overwrite the outer one's
    /// state, so it THROWS rather than silently sharing. Production re-entry depth is 0
    /// (`overlay-control-loops.md` §6 rule 7), and the `finally` closes the window either way.
    /// </summary>
    string? ChooseTarget(string actorKey, long nowTick, IReadOnlyList<CompiledAction> heldActions)
    {
        if (_inChooseTarget)
            throw new InvalidOperationException(
                "SiegeAiIntentSource is not re-entrant: a nested decision would overwrite the outer " +
                "decision's candidate/selection scratch. One source instance decides one actor at a time.");

        _inChooseTarget = true;
        try { return ChooseTargetGuarded(actorKey, nowTick, heldActions); }
        finally { _inChooseTarget = false; }
    }

    string? ChooseTargetGuarded(string actorKey, long nowTick, IReadOnlyList<CompiledAction> heldActions)
    {
        var mySide = _view.SideOf(actorKey);
        var selfDerived = _view.DerivedOf(actorKey);
        if (selfDerived is null) return null; // cannot estimate a hit chance for a self we cannot read

        // combat-ai `intent-router` CAI1.11 (spec-intent-router.md §2): the VIEWER-RELATIVE read, not
        // the actor-less property. `_view` is the one view this source binds for its whole life, so a
        // trait that changes only the live order (`bloodthirsty`) reaches this scoring path only if the
        // view is asked "in what order does THIS actor see the board". For every unwrapped view the
        // default is the identity (`IBattleView.LiveActorKeysFor => LiveActorKeys`), so this is
        // byte-identical for every caller that supplies no trait decorators.
        var liveActorKeys = _view.LiveActorKeysFor(actorKey);

        // CAI1.14 site 3: the two values this closure used to capture are per-decision scratch
        // fields now, and `_stillScoreable` was allocated once in the constructor -- so this call no
        // longer builds a display class and a delegate per decision. The delegate reads those fields
        // synchronously, inside `TryGetHeld`, inside the re-entry guard.
        _scratchMySide = mySide;
        _scratchActorKey = actorKey;
        _scratchLive = liveActorKeys;

        if (_retarget is not null && _retarget.TryGetHeld(
                actorKey, nowTick, _retargetLatencyTicks, _stillScoreable, out var held))
            return held;

        // combat-ai `core-scorer` (CAI1.2, 2026-09-20): the cap moves in front of the work. TargetStage
        // runs phase A (self/side/readability, in view order) and phase B (truncate to
        // MaxCandidatesScored) BEFORE this local function -- phase C, the expensive per-candidate
        // build below -- ever runs. Because phase A preserves view order, this is a provable identity
        // with the pre-move "build all, keep the first N" shape (TargetStageCapTests), not a behaviour
        // change: which candidates are BUILT changes (only the capped set, not every live enemy), but
        // which candidate is ultimately CHOSEN does not.
        _scratchSelfDerived = selfDerived;
        _scratchHeldActions = heldActions;
        TargetStage.BuildCappedInto(
            actorKey, mySide, liveActorKeys,
            _sideOf, _isReadable,
            _weights.MaxCandidatesScored,
            _tryBuildCandidate, _eligibleScratch, _candidateScratch);
        var capped = _candidateScratch;

        if (capped.Count == 0)
        {
            _retarget?.Forget(actorKey);
            return null;
        }

        // combat-ai `aggression-tier-map` CAI1.13 (spec-aggression-tier-map.md §4): the CHOICE comes
        // first so the trace line can name the chosen candidate's own saturation. Reordering is
        // behaviour-neutral -- `_trace` draws no RNG and `AiDecision` is kept out of `Digest`
        // (BattleTrace.cs), so no golden and no replay depends on when the line is formatted.
        var chosen = CandidateScorer.ChooseTargetInto(
            _selectionScratch, capped, _roundOf(nowTick), _weights);

        // CAI2.4: this decision SCORED -- `TryDeclare` emits the record after it knows the chosen
        // ACTION. A held-target tick and an empty candidate set both return before here, so neither
        // records anything.
        _scoredThisDecision = true;

        if (chosen is not null) _retarget?.RecordRetarget(actorKey, chosen.Value.ActorKey, nowTick);
        return chosen?.ActorKey;
    }

    /// <summary>
    /// combat-ai `decision-inspector` CAI2.4: the one record for one decision, built here because this is
    /// where the chosen ACTION is known — `ChosenActionId`/`ChosenTargetKey` are the returned
    /// <see cref="ActionIntent"/>'s own fields, which is the row's "equal the ActionIntent returned" line.
    ///
    /// <para><b>Two kinds of candidate entry, both real facts.</b> A scored TARGET candidate carries the
    /// breakdown the scorer already computed for it; a gate-loop verdict carries the
    /// <see cref="UsabilityResult"/> step 3/4 actually returned for one held action against the chosen
    /// target. Nothing is re-derived: re-running a gate to fill a field is what §1 forbids, because a gate
    /// re-checked one tick later can answer differently.</para>
    /// </summary>
    void EmitDecisionRecord(string actorKey, long nowTick, in ActionIntent intent)
    {
        var round = _roundOf(nowTick);

        var topThreeCount = CandidateScorer.TopThreeInto(_candidateScratch, round, _weights, _topThreeBuffer);
        var topThree = new (string ActorKey, ScoreBreakdown Breakdown)[topThreeCount];
        for (var i = 0; i < topThreeCount; i++) topThree[i] = _topThreeBuffer[i];

        var candidates = new AiCandidateVerdict[_candidateScratch.Count + _gateScratch.Count];
        var at = 0;
        for (var i = 0; i < _candidateScratch.Count; i++)
            candidates[at++] = new AiCandidateVerdict(
                _candidateScratch[i].ActorKey, ActionId: null, Gate: null,
                Breakdown: CandidateScorer.ScoreBreakdownOf(_candidateScratch[i], round, _weights));
        for (var i = 0; i < _gateScratch.Count; i++)
            candidates[at++] = new AiCandidateVerdict(
                intent.TargetKey ?? "", _gateScratch[i].ActionId, _gateScratch[i].Result, Breakdown: null);

        // The saturation fact belongs to the chosen TARGET candidate, as it did before CAI2.4.
        var chosenSaturatedBy = 0;
        for (var i = 0; i < _candidateScratch.Count; i++)
        {
            if (!string.Equals(_candidateScratch[i].ActorKey, intent.TargetKey, StringComparison.Ordinal)) continue;
            chosenSaturatedBy = CandidateScorer.SaturatedByOf(_candidateScratch[i].Aggression, _weights.AggressionRange);
            break;
        }

        var record = new AiDecisionRecord(
            NowTick: nowTick, Round: round, ActorKey: actorKey,
            Tier: null, ProfileId: null, Personality: null,
            Trigger: AiTriggerState.None, Origin: AiDecisionOrigin.Policy,
            Candidates: candidates,
            ChosenActionId: intent.IsNone ? null : intent.ActionId,
            ChosenTargetKey: intent.TargetKey,
            ChosenSaturatedBy: chosenSaturatedBy,
            TopThree: topThree);
        _sink!.Record(in record);
    }

    /// <summary>Phase C, moved out of the decision body by CAI1.14 site 3: as a local function passed
    /// as a delegate it captured two locals and allocated a display class per decision. As an instance
    /// method whose inputs are the per-decision scratch fields, `_tryBuildCandidate` is allocated once,
    /// in the constructor. The body is otherwise verbatim.</summary>
    bool TryBuildCandidateCore(string candidateKey, out TargetCandidate candidate)
    {
        var selfDerived = _scratchSelfDerived;
        var heldActions = _scratchHeldActions;
        var liveActorKeys = _scratchLive;
        if (selfDerived is null) { candidate = default; return false; }
        var targetDerived = _view.DerivedOf(candidateKey);
        if (targetDerived is null) { candidate = default; return false; } // e.g. hidden under fog

        var facts = _view.FactsOf(candidateKey);
        var hitChanceMilli = SiegeHitChance.EstimateMilli(selfDerived, targetDerived);
        var missingHpMilli = Math.Clamp(1000 - facts.HpMilli, 0, 1000);
        var canCounter = _view.HeldActionsOf(candidateKey).Count > 0;
        var candidatePos = _view.PositionOf(candidateKey);

        // Resolved 2026-09-07 (spec-siege-ai.md, Open questions): Chebyshev-to-objective, an
        // honest v1 -- IBattleView exposes only per-actor facts, never board geometry, so a real
        // BoardPathfinder route is not reachable from here without widening that seam far beyond
        // its own established "one fact per actor" shape.
        var objectiveClassMilli = 0;
        if (_view.ObjectivePositionOf(_scratchActorKey) is { } myObjective && candidatePos is { } cPos)
        {
            var toObjective = GridDistance.Chebyshev(cPos, myObjective);
            objectiveClassMilli = (int)(1000 - Math.Clamp(
                checked((long)toObjective * 1000 / _tuning.ObjectiveReferenceDistanceCells), 0, 1000));
        }

        // Resolved 2026-09-07: Omni-only expected damage vs the target's own real (not per-mille)
        // current HP -- MaxHpOf is the one extra fact EntityFacts.HpMilli alone cannot supply.
        //
        // combat-ai `core-scorer` CAI1.5 (2026-09-20, spec-core-scorer.md S8 commit 2): closes
        // SiegeAiIntentSource.cs:214's own omission -- `baseOverlayDamage` no longer defaults to
        // 0.0. The real base comes from the actor's own MOST-PREFERRED held action (heldActions[0],
        // already frozen in the actor's own held-action preference order -- offensive first), through the one
        // estimator ActionBaseDerivation.BasePowerMilli at EffectiveRungOf, the same path
        // BasicAttack.cs's own real hit uses. Target selection runs before action selection, so
        // there is no "the swung action" yet to read; the preferred action is the honest stand-in,
        // matching what step 3 will, in the common case, actually pick. **This is the one cause
        // that may move siege target choice** -- see predicted-delta-kill-estimate.md.
        var isKillingBlow = false;
        if (_view.MaxHpOf(candidateKey) is { } targetMaxHp)
        {
            var targetCurrentHp = checked(targetMaxHp * facts.HpMilli / 1000);
            var baseOverlayDamage = 0.0;
            if (heldActions.Count > 0)
            {
                var preferred = heldActions[0];
                var theta = checked((int)selfDerived.Get(DerivedStatChannels.ProgressionPower));
                var effectiveRung = _effectiveRungOf?.Invoke(_scratchActorKey, preferred.ActionId) ?? preferred.Rung;
                var basePowerMilli = ActionBaseDerivation.BasePowerMilli(
                    preferred.Kind, preferred.ActionId, effectiveRung,
                    RungPolicy.Table, ActionBaseTuningHub.Tuning);
                baseOverlayDamage = ActionBaseMath.BasePerHit(basePowerMilli, BattleRuleset.PowerValue(theta));
            }
            isKillingBlow = SiegeExpectedDamage.IsKillingBlow(selfDerived, targetDerived, targetCurrentHp, baseOverlayDamage);
        }

        // Corrected 2026-09-07, same session: the first version divided by an ABSOLUTE
        // `ReferenceThreatPowerMilli` tunable -- re-examined after the stop-hook's own repeated
        // "not blocked, re-investigate" pressure and found to be a real design defect, not a
        // genuine playtest-data gap. CombatPowerOmni scales with P(Theta) (One Power Ladder,
        // quadratic) -- a FIXED milli reference can never have a safe default, because "a lot of
        // power" at Theta=10 is trivial at Theta=200, exactly the private-per-subsystem-curve
        // defect AGENTS.md's power-ladder rule exists to prevent. `OverlayCombatCalculator`'s own
        // shape is the precedent this should have followed from the start: it never compares a
        // stat to an absolute constant, only to ANOTHER read stat (`atk.Power - def.Defense`,
        // `accuracy - dodge`). Fixed the same way: threat is now the CANDIDATE side's nearby raw
        // power AS A FRACTION OF THE DECIDING ACTOR'S OWN power -- a Theta-invariant ratio, live
        // from day one, no tunable, no unset gate. Cover-free is still v1 (the full model needs a
        // structure-to-cover-data mechanism that does not exist yet -- BattleActorSetup carries no
        // StructureId at all, a separate, un-started task).
        var incomingThreatMilli = 0L;
        var selfPowerOmni = (long)selfDerived.Get(DerivedStatChannels.CombatPowerOmni);
        if (selfPowerOmni > 0 && candidatePos is { } threatCenter)
        {
            var threatSum = 0L;
            for (var t = 0; t < liveActorKeys.Count; t++)
            {
                var threatKey = liveActorKeys[t];
                if (_view.SideOf(threatKey) != _view.SideOf(candidateKey)) continue;
                if (_view.PositionOf(threatKey) is not { } threatPos) continue;
                if (GridDistance.Chebyshev(threatPos, threatCenter) > _tuning.ThreatRadiusCells) continue;
                if (_view.DerivedOf(threatKey) is not { } threatDerived) continue;
                threatSum = checked(threatSum + (long)threatDerived.Get(DerivedStatChannels.CombatPowerOmni));
            }
            incomingThreatMilli = Math.Clamp(checked(threatSum * 1000 / selfPowerOmni), 0, 1000);
        }

        // BaseTier: always 0, structurally -- R1/R2's own §2 table names signed aggression as
        // the ONLY per-candidate input into tier ("which tier a candidate lands in is decided
        // by signed aggression, not a band-membership flag"), so there is no independent
        // "starting tier" concept to read; it is the anchor aggression offsets FROM.
        // Aggression: resolved 2026-09-07 -- IBattleView.AggressionOf is the exact accessor
        // §5.20 rule 4's own snippet names. Reads 0 (neutral) from every real implementor today
        // since no taunt/stealth/decoy content exists anywhere yet -- a wiring seam for that
        // future content, matching EmplacementFireMode's own "vocabulary before its second
        // value has a real consumer" precedent, not a promise of live behavior today.
        candidate = new TargetCandidate(
            ActorKey: candidateKey, BaseTier: 0, Aggression: _view.AggressionOf(candidateKey),
            HitChanceMilli: hitChanceMilli, ObjectiveClassMilli: objectiveClassMilli, IsKillingBlow: isKillingBlow,
            TargetMissingHpMilli: missingHpMilli, TargetCanCounter: canCounter,
            IncomingThreatMilli: incomingThreatMilli);
        return true;
    }

    bool IsStillScoreable(string candidateKey, int mySide, IReadOnlyList<string> liveActorKeys)
    {
        var stillLive = false;
        for (var i = 0; i < liveActorKeys.Count; i++)
        {
            if (!string.Equals(liveActorKeys[i], candidateKey, StringComparison.Ordinal)) continue;
            stillLive = true;
            break;
        }
        if (!stillLive) return false; // dead/removed from the board
        if (_view.SideOf(candidateKey) == mySide) return false; // defensive: a side swap invalidates it too
        return _view.DerivedOf(candidateKey) is not null; // e.g. newly hidden under fog
    }

    static bool Contains(IReadOnlyList<ActionTag> tags, ActionTag tag)
    {
        for (var i = 0; i < tags.Count; i++)
            if (tags[i] == tag) return true;
        return false;
    }
}

// RetargetLedger moved to Actions/Ai/RetargetLedger.cs on 2026-09-20 (combat-ai core-scorer, CAI1.4)
// -- the ONLY anti-repeat mechanism in the repo. `using FusionRpg.Core.Actions.Ai;` above resolves it.
