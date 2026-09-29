using FusionRpg.Core.Actions.Rungs;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Battle.Siege;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Actions.Ai;

/// <summary>combat-ai `ai-tiers-personality` (module 3, CAI1.9): module 10's future decision-inspector
/// seam, in the minimal shape this module needs to prove its own seven-step order — never the real
/// trace record `decision-inspector` (module 10, not yet built) will define, and NOT
/// `FusionRpg.Core.Battle.Timeline.TracedDecision` (T10's own, unrelated real-input replay record —
/// named differently here on purpose to avoid colliding with it). <c>null</c> (the default everywhere
/// this is optional) costs nothing, matching every other opt-in trace seam in this program.</summary>
public readonly record struct AiDecisionTrace(
    string ActorKey, long NowTick, AiTier Tier, int RankIndex, string? TargetKey, string? ActionId);

/// <summary>
/// combat-ai `ai-tiers-personality` (module 3, CAI1.9, spec-ai-tiers-personality.md §6): the ONE
/// scored <see cref="IIntentSource"/> for every place OTHER than siege — <c>SiegeAiIntentSource</c>
/// stays consumer #1 (module 1's own "Objective"), and this is the first type two later specs
/// (`delve-automated-wiring`, `auto-policy-switch`) already assume and none declares. It COMPOSES; it
/// decides nothing itself — module 1's stages, module 2's profile and this module's own tier/
/// personality are the only mechanisms at work here.
///
/// <para><b>Nothing in production constructs this in this module's own commit.</b> `delve-automated-
/// wiring` (module 13) is its first real caller, so this type exists and nothing walks it yet — the
/// SAME byte-identity posture module 1 and module 2 shipped under.</para>
///
/// <para><b>Deviation from the spec's own illustrative snippet, stated so a reviewer does not treat it
/// as a typo</b>: the spec's §6 pseudocode names a <c>string profileId</c> constructor parameter
/// "resolved through <c>CombatAiProfilePolicy.For</c>" — but module 2's SHIPPED `For` takes
/// <c>(AiPlace, AiRole)</c>, never a raw string (`CombatAiProfilePolicy.cs:23`). This type takes the
/// real <c>(AiPlace place, AiRole role)</c> pair instead, exactly what the cited method actually
/// accepts, matching CAI1.3's own precedent for reconciling a spec snippet against the module it
/// actually depends on.</para>
/// </summary>
public sealed class CoreIntentPolicy : IIntentSource
{
    readonly IBattleView _view;
    readonly CooldownLedger _cooldowns;
    readonly IStanceCheck _stance;
    readonly IAffordabilityCheck _affordability;
    /// <summary>The ONLY place this type touches `CombatAiProfilePolicy` — one call, resolved lazily
    /// per actor and cached (<see cref="StateFor"/>), never per decision. Kept behind a delegate rather
    /// than a stored `(AiPlace, AiRole)` pair so a test can supply an isolated profile without touching
    /// the process-wide hub at all — CAI1.8's own diagnosed race (`CombatAiTuningTests` mutating the
    /// same hub `DistrictAssaultResolverTests` depends on staying configured) is exactly the failure
    /// mode this indirection exists to keep this module's OWN tests from repeating.</summary>
    readonly Func<CombatAiProfile> _resolveProfile;
    readonly AiActorClassOf? _actorClassOf;
    readonly ulong _matchSeed;
    readonly RetargetLedger _retarget;
    readonly Func<string, string>? _loyalRedirect;
    readonly Action<AiDecisionTrace>? _trace;
    /// <summary>Not in the spec's own §6 snippet — added for the SAME reason
    /// `SiegeAiIntentSource.cs`'s own `roundOf` constructor parameter exists (CAI1.1): `AiRowFacts.Round`
    /// and `CandidateScorer.Score`'s `currentRound` both need a round number, and nothing about "how a
    /// tick maps to a round" is this module's own domain knowledge — a caller-supplied seam, defaulting
    /// to the same identity fallback (`tick => (int)tick`) every existing caller already gets.</summary>
    readonly Func<long, int> _roundOf;
    /// <summary>"the downed predicate the place supplies" — `AiVocabulary.cs`'s own `AllyDowned`
    /// selector comment. `IBattleView` has no generic "is this actor downed" read (`EntityFacts` has no
    /// such field), so this is a caller-supplied seam, defaulting to "nobody is ever downed" — the
    /// safe, byte-identical-at-landing answer (0 downed allies matches `AiCensusCondition`'s own
    /// identity reading), never a guess.</summary>
    readonly Func<string, bool>? _isDownedOf;

    // combat-ai `decision-perf` CAI1.14 (site 3/4, spec-decision-perf.md): the decision path's own
    // scratch, owned by this policy instance. `_stateByActor`'s own "resolved once per actor" posture
    // extends to the profile's derived `ScoringWeights`/`SelectionPolicy` (inside `PerActorState`); the
    // buffers below carry the candidate set, and the three delegates were per-decision lambdas/closures
    // until CAI1.14, so they are bound once. `_selectionScratch`'s own guard throws on a nested
    // decision rather than sharing the buffers.
    /// <summary>
    /// Structural (`tunables-ssot.md` T2) — the retained CAPACITY of the two per-decision scratch lists,
    /// not a balance number and not a cap on any magnitude: a decision with more candidates than this still
    /// works, the list simply grows once (which is why the zero-allocation assertions warm first). Same
    /// shape as `CostLedger.StackCostRows`, and named for the same reason: a bare `new(64)` in a
    /// balance-surface file is indistinguishable from a balance dial to `guard-magic-numbers.ps1`, and the
    /// name is what makes the structural role reviewable.
    /// </summary>
    const int DecisionScratchCapacity = 64;

    readonly List<string> _eligibleScratch = new(DecisionScratchCapacity);
    readonly List<TargetCandidate> _candidateScratch = new(DecisionScratchCapacity);
    readonly SelectionScratch _selectionScratch = new();
    readonly Func<string, int> _sideOf;
    readonly Func<string, bool> _isReadable;
    readonly TargetStage.TryBuildCandidate _tryBuildCandidate;
    ActorDerivedSnapshot? _scratchSelfDerived;
    IReadOnlyList<CompiledAction> _scratchHeldActions = Array.Empty<CompiledAction>();

    /// <summary>Per-actor state resolved ONCE (§4: "computed once per actor per battle", the SAME
    /// posture the profile resolution itself gets — "resolved once per actor and the census once per
    /// decision, both counted" is this module's own acceptance line) and held for the rest of the
    /// battle, never recomputed per decision.</summary>
    readonly Dictionary<string, PerActorState> _stateByActor = new(StringComparer.Ordinal);

    readonly record struct PerActorState(
        AiTier Tier, CombatAiProfile AppliedProfile, ScoringWeights Weights, SelectionPolicy Selection);

    public static CoreIntentPolicy Create(
        IBattleView view,
        CooldownLedger cooldowns,
        IStanceCheck stance,
        IAffordabilityCheck affordability,
        AiPlace place,
        AiRole role,
        AiActorClassOf? actorClassOf = null,
        ulong matchSeed = 0UL,
        RetargetLedger? retarget = null,
        Func<string, string>? loyalRedirect = null,
        Action<AiDecisionTrace>? trace = null,
        Func<long, int>? roundOf = null,
        Func<string, bool>? isDownedOf = null) =>
        new(view, cooldowns, stance, affordability, () => CombatAiProfilePolicy.For(place, role),
            actorClassOf, matchSeed, retarget, loyalRedirect, trace, roundOf, isDownedOf);

    /// <summary>Tests only — see <see cref="_resolveProfile"/>'s own doc comment for why this exists
    /// instead of every test calling <c>CombatAiProfilePolicy.Configure</c> on the shared hub.</summary>
    internal static CoreIntentPolicy CreateForTest(
        IBattleView view,
        CooldownLedger cooldowns,
        IStanceCheck stance,
        IAffordabilityCheck affordability,
        CombatAiProfile profile,
        AiActorClassOf? actorClassOf = null,
        ulong matchSeed = 0UL,
        RetargetLedger? retarget = null,
        Func<string, string>? loyalRedirect = null,
        Action<AiDecisionTrace>? trace = null,
        Func<long, int>? roundOf = null,
        Func<string, bool>? isDownedOf = null) =>
        new(view, cooldowns, stance, affordability, () => profile,
            actorClassOf, matchSeed, retarget, loyalRedirect, trace, roundOf, isDownedOf);

    CoreIntentPolicy(
        IBattleView view, CooldownLedger cooldowns, IStanceCheck stance, IAffordabilityCheck affordability,
        Func<CombatAiProfile> resolveProfile, AiActorClassOf? actorClassOf, ulong matchSeed, RetargetLedger? retarget,
        Func<string, string>? loyalRedirect, Action<AiDecisionTrace>? trace, Func<long, int>? roundOf,
        Func<string, bool>? isDownedOf)
    {
        _view = view ?? throw new ArgumentNullException(nameof(view));
        _cooldowns = cooldowns ?? throw new ArgumentNullException(nameof(cooldowns));
        _stance = stance ?? throw new ArgumentNullException(nameof(stance));
        _affordability = affordability ?? throw new ArgumentNullException(nameof(affordability));
        _resolveProfile = resolveProfile ?? throw new ArgumentNullException(nameof(resolveProfile));
        _actorClassOf = actorClassOf;
        _matchSeed = matchSeed;
        _retarget = retarget ?? new RetargetLedger();
        _loyalRedirect = loyalRedirect;
        _trace = trace;
        _roundOf = roundOf ?? (tick => (int)tick);
        _isDownedOf = isDownedOf;

        // CAI1.14 site 3: bind the decision path's three delegates once, over the scratch above.
        _sideOf = _view.SideOf;
        _isReadable = IsReadableCore;
        _tryBuildCandidate = TryBuildCandidateCore;
    }

    /// <summary>Phase A's readability filter, cached as a delegate so a decision does not allocate one
    /// (CAI1.14 site 3).</summary>
    bool IsReadableCore(string candidateKey) => _view.DerivedOf(candidateKey) is not null;

    /// <summary>Phase C's callback, cached as a delegate over the per-decision scratch fields
    /// (CAI1.14 site 3) instead of a closure capturing two locals.</summary>
    bool TryBuildCandidateCore(string candidateKey, out TargetCandidate candidate) =>
        TryBuildCandidate(candidateKey, _scratchSelfDerived!, _scratchHeldActions, out candidate);

    public ActionIntent TryDeclare(string actorKey, long nowTick)
    {
        var heldActions = _view.HeldActionsOf(actorKey);
        if (heldActions.Count == 0) return ActionIntent.None;

        var state = StateFor(actorKey); // step 1+2+3: profile (once/actor), tier (once/actor), personality applied (once/actor)

        var mySide = _view.SideOf(actorKey);
        // CAI1.11: viewer-relative — a `bloodthirsty` decorator (`TraitAwareBattleView`) reorders this
        // per deciding actor; the interface default is the identity, so every unwrapped view is
        // byte-identical.
        var liveActorKeys = _view.LiveActorKeysFor(actorKey);
        var selfFacts = _view.FactsOf(actorKey);

        // step 4: the row walk -- census gathered ONCE per decision, never per row.
        var facts = BuildRowFacts(actorKey, mySide, liveActorKeys, selfFacts, nowTick);
        var pickedRow = AiRowSelector.TryPick(state.AppliedProfile, in facts, out var selector, out var actionFilter, out var rankIndex);
        if (!pickedRow)
        {
            Trace(actorKey, nowTick, state.Tier, -1, null, null);
            return ActionIntent.None; // an empty Rows list only -- module 2's parser forbids this for any real file
        }

        // step 5: the target stage. Smart tier runs module 1's full pipeline; performance tier never
        // invokes TryBuildCandidate at all -- the fixed selector short-circuits it (spec §2 table).
        var targetKey = state.Tier == AiTier.Smart
            ? ScoredTarget(actorKey, mySide, liveActorKeys, nowTick, heldActions, state)
            : FixedSelect(selector, actorKey, mySide, liveActorKeys);

        if (_loyalRedirect is not null && targetKey is not null)
            targetKey = _loyalRedirect(targetKey); // module 4's ITraitDecorator.EffectiveTargetOf seam

        if (targetKey is null)
        {
            Trace(actorKey, nowTick, state.Tier, rankIndex, null, null);
            return ActionIntent.None;
        }

        var casterPos = _view.PositionOf(actorKey);
        var targetPos = _view.PositionOf(targetKey);
        var targetFacts = _view.FactsOf(targetKey);

        // step 6: the action stage -- gates, resolvable-here (not yet wired: module 5's default admits
        // everything), reserve floor (the affordability decorator IS the caller's own, not re-decorated
        // here -- module 5's ReserveFloorAffordability wraps _affordability BEFORE construction), waste
        // guards gated on tier exactly as spec §2's one-line mapping states.
        var picked = ActionStage.TryPick(
            actorKey, nowTick, heldActions, _cooldowns, _stance, _affordability,
            casterPos, targetPos, selfFacts, targetFacts, out var actionId,
            actionFilter: CompileFilter(actionFilter),
            runWasteGuards: state.Tier == AiTier.Smart,
            guards: state.AppliedProfile.Guards.ToWasteGuardThresholds(),
            // MinTargetsForArea's own census (live targets inside ONE action's area) needs an
            // envelope-shape oracle that does not exist in Core.Actions today -- module 1's own
            // ActionStage.cs doc names this exact gap ("no envelope-shape oracle exists"), so that
            // guard structurally never fires here, matching the SAME identity every existing caller
            // gets until that oracle is built. FightEndingLiveCount's census is cheap and already in
            // hand -- the same enemy count BuildRowFacts just gathered, wired directly.
            opposingSideLiveCountOf: () => CountEnemies(actorKey, mySide, liveActorKeys));

        if (!picked)
        {
            Trace(actorKey, nowTick, state.Tier, rankIndex, targetKey, null);
            return ActionIntent.None;
        }

        // step 7: record + trace, then the intent. Never a nullable ActionIntent (IntentSource.cs:12-18).
        _retarget.RecordRetarget(actorKey, targetKey, nowTick);
        _retarget.RecordActionChosen(actorKey, actionId, nowTick);
        Trace(actorKey, nowTick, state.Tier, rankIndex, targetKey, actionId);

        var chosenAction = FindAction(heldActions, actionId);
        return new ActionIntent(actionId, targetKey, chosenAction.Envelope);
    }

    /// <summary>module 4's `RetargetFor` contract (`ActionRunner.cs:393-396`): target-only re-query for
    /// an already-committed action. Runs the target stage alone -- no action stage, no row walk past
    /// the row that already chose the selector for this decision's own tier.</summary>
    public string? RetargetFor(string actorKey, string actionId, string? deadTargetKey, long nowTick)
    {
        var state = StateFor(actorKey);
        var mySide = _view.SideOf(actorKey);
        var liveActorKeys = _view.LiveActorKeysFor(actorKey); // CAI1.11: viewer-relative, see TryDeclare
        var selfFacts = _view.FactsOf(actorKey);
        var heldActions = _view.HeldActionsOf(actorKey);

        var facts = BuildRowFacts(actorKey, mySide, liveActorKeys, selfFacts, nowTick);
        if (!AiRowSelector.TryPick(state.AppliedProfile, in facts, out var selector, out _, out _))
            return null;

        return state.Tier == AiTier.Smart
            ? ScoredTarget(actorKey, mySide, liveActorKeys, nowTick, heldActions, state)
            : FixedSelect(selector, actorKey, mySide, liveActorKeys);
    }

    PerActorState StateFor(string actorKey)
    {
        if (_stateByActor.TryGetValue(actorKey, out var cached)) return cached;

        var profile = _resolveProfile(); // step 1
        var actorClass = _actorClassOf?.Invoke(actorKey) ?? AiActorClass.Unique; // §1: unwired => Unique
        var tier = AiTierResolver.For(profile, actorClass); // step 2

        // step 3: draw once, apply once. Same identity string (actorKey) feeds both branches -- a
        // unique's own actor key IS the instance id this program already uses everywhere else (§1:
        // "a host's resolver is 'does this actor key map to a row with an instance id'").
        var personality = actorClass == AiActorClass.Unique
            ? AiPersonalityFactory.ForUnique(actorKey, profile.Personality)
            : AiPersonalityFactory.ForGeneral(_matchSeed, actorKey, profile.Personality);
        var applied = AiPersonalityApply.Apply(profile, personality);

        var state = new PerActorState(
            tier, applied, applied.Scoring.ToScoringWeights(), applied.Selection.ToSelectionPolicy());
        _stateByActor[actorKey] = state;
        return state;
    }

    AiRowFacts BuildRowFacts(string actorKey, int mySide, IReadOnlyList<string> liveActorKeys, EntityFacts selfFacts, long nowTick)
    {
        var enemiesLive = 0;
        var alliesDowned = 0;
        for (var i = 0; i < liveActorKeys.Count; i++)
        {
            var key = liveActorKeys[i];
            if (string.Equals(key, actorKey, StringComparison.Ordinal)) continue;
            if (_view.SideOf(key) != mySide) { enemiesLive++; continue; }
            if (_isDownedOf?.Invoke(key) == true) alliesDowned++;
        }

        // A held target (from a prior decision) supplies the target-fact half; none held reads as the
        // "no target" sentinel (module 2 §4 rule 3) so TargetHpBelowMilli/TargetHasStatus are
        // mechanically false rather than needing a separate "is there a target" flag.
        var hasHeld = _retarget.TryGetHeld(actorKey, nowTick, retargetLatencyTicks: long.MaxValue,
            stillValid: _ => true, out var heldTarget);
        var targetHpMilli = AiRowFacts.NoTargetHpMilli;
        ulong targetStatusMask = 0;
        if (hasHeld)
        {
            var targetFacts = _view.FactsOf(heldTarget);
            targetHpMilli = targetFacts.HpMilli;
            targetStatusMask = targetFacts.StatusMask;
        }

        // No generic resource-pool reader exists on IBattleView (only CostLedger/IAffordabilityCheck
        // read pools, and neither exposes a per-resource per-mille value) -- SelfResourceBelowMilli
        // rows are therefore always false today, matching every SHIPPED combat-ai.v1.json row (none
        // author that condition). A wiring gap, not a design gap: the fix is a resource-pool reader on
        // IBattleView, not a decision made here.
        return new AiRowFacts(
            SelfHpMilli: selfFacts.HpMilli, SelfResourceMilli: 0, SelfResourceId: "",
            TargetHpMilli: targetHpMilli, SelfStatusMask: selfFacts.StatusMask, TargetStatusMask: targetStatusMask,
            EnemiesLive: enemiesLive, AlliesDowned: alliesDowned, Round: _roundOf(nowTick));
    }

    /// <summary>Smart tier: module 1's full pipeline -- phase A/B/C capped candidate building, then
    /// `CandidateScorer.ChooseTarget`'s tier-rank + additive score + selection. No scoring arithmetic
    /// lives HERE (this method builds INPUTS, it never compares two candidates' scores itself --
    /// `CandidateScorer` does that, once, in the one place it is allowed to).</summary>
    string? ScoredTarget(
        string actorKey, int mySide, IReadOnlyList<string> liveActorKeys, long nowTick,
        IReadOnlyList<CompiledAction> heldActions, in PerActorState state)
    {
        var selfDerived = _view.DerivedOf(actorKey);
        if (selfDerived is null) return null; // cannot estimate a hit chance for a self we cannot read

        // CAI1.14 site 3/4: the phase-C callback and the two buffers are this policy's own scratch, and
        // the applied profile's derived weights/selection are resolved once per actor in `StateFor`
        // rather than rebuilt per decision (`ToScoringWeights`/`ToSelectionPolicy` allocate records).
        _scratchSelfDerived = selfDerived;
        _scratchHeldActions = heldActions;
        TargetStage.BuildCappedInto(
            actorKey, mySide, liveActorKeys,
            _sideOf, _isReadable,
            state.Weights.MaxCandidatesScored,
            _tryBuildCandidate, _eligibleScratch, _candidateScratch);
        var capped = _candidateScratch;

        if (capped.Count == 0)
        {
            _retarget.Forget(actorKey);
            return null;
        }

        var chosen = CandidateScorer.ChooseTargetInto(
            _selectionScratch, capped, _roundOf(nowTick), state.Weights, state.Selection, _matchSeed);
        return chosen?.ActorKey;
    }

    bool TryBuildCandidate(
        string candidateKey, ActorDerivedSnapshot selfDerived,
        IReadOnlyList<CompiledAction> heldActions, out TargetCandidate candidate)
    {
        // CAI1.11 (spec-intent-router.md §2): the `loyal` bodyguard redirect is the scorer side's own
        // application of the SAME `ITraitDecorator.EffectiveTargetOf` the engine applies when it moves
        // the hit (`BasicAttack`'s router call). Reading it HERE, while the candidate's inputs are
        // built, is what makes `kill` and `lowHp` score the actor that will actually be hit rather
        // than the one the policy named — the defect audit M1 flagged. The candidate's own identity
        // stays `candidateKey` (it is what phase A ranked and what the tiebreak orders on); only the
        // TARGET-side inputs resolve through the redirect. `_loyalRedirect` is null = identity, so a
        // policy built with no decorator is byte-identical.
        var effectiveKey = _loyalRedirect is null ? candidateKey : _loyalRedirect(candidateKey);
        var targetDerived = _view.DerivedOf(effectiveKey);
        if (targetDerived is null) { candidate = default; return false; }

        var facts = _view.FactsOf(effectiveKey);
        // Real, generic math -- SiegeHitChance/SiegeExpectedDamage read only ActorDerivedSnapshot and
        // global CombatPolicy/CombatProbabilityPolicy, no siege-specific state. Reusing them here is
        // exactly the "no second formula" discipline their own doc comments state, not a layering
        // violation -- the "Siege" in their namespace names where they were BUILT, not what they need.
        var hitChanceMilli = SiegeHitChance.EstimateMilli(selfDerived, targetDerived);
        var missingHpMilli = Math.Clamp(1000 - facts.HpMilli, 0, 1000);
        var canCounter = _view.HeldActionsOf(candidateKey).Count > 0;

        // ObjectiveClassMilli: always 0. IBattleView.ObjectivePositionOf returns null for every place
        // this module serves today ("null when no siege context is wired for this battle... every
        // non-siege battle", IBattleView.cs's own doc), and module 2's general scoring schema carries
        // no reference-distance field to normalize a distance into even if one existed. A wiring gap
        // for a later place that wants it, not a design gap.
        var objectiveClassMilli = 0;

        var isKillingBlow = false;
        if (_view.MaxHpOf(effectiveKey) is { } targetMaxHp)
        {
            var targetCurrentHp = checked(targetMaxHp * facts.HpMilli / 1000);
            var baseOverlayDamage = 0.0;
            if (heldActions.Count > 0)
            {
                var preferred = heldActions[0];
                var theta = checked((int)selfDerived.Get(DerivedStatChannels.ProgressionPower));
                var basePowerMilli = ActionBaseDerivation.BasePowerMilli(
                    preferred.Kind, preferred.ActionId, preferred.Rung, RungPolicy.Table, ActionBaseTuningHub.Tuning);
                baseOverlayDamage = ActionBaseMath.BasePerHit(basePowerMilli, BattleRuleset.PowerValue(theta));
            }
            isKillingBlow = SiegeExpectedDamage.IsKillingBlow(selfDerived, targetDerived, targetCurrentHp, baseOverlayDamage);
        }

        // IncomingThreatMilli: always 0. Siege's own threat term needs a structural radius constant
        // (`ObjectiveReferenceDistanceCells`-shaped) that lives only in the narrowed, siege-local
        // AiTuning (CAI1.8) -- module 2's general scoring schema has no equivalent field. A wiring gap
        // for whichever module first wants a generic threat radius, not a design gap.
        var incomingThreatMilli = 0L;

        candidate = new TargetCandidate(
            ActorKey: candidateKey, BaseTier: 0, Aggression: _view.AggressionOf(candidateKey),
            HitChanceMilli: hitChanceMilli, ObjectiveClassMilli: objectiveClassMilli, IsKillingBlow: isKillingBlow,
            TargetMissingHpMilli: missingHpMilli, TargetCanCounter: canCounter,
            IncomingThreatMilli: incomingThreatMilli);
        return true;
    }

    /// <summary>Performance tier: the row's own fixed <see cref="TargetSelector"/>, and NOTHING else —
    /// no `TargetCandidate` is ever built (`TryBuildCandidate` is never invoked), no `TopThree`, no
    /// scoring. Each branch reads exactly the one fact its own `AiVocabulary.cs` enum comment names.
    /// </summary>
    string? FixedSelect(TargetSelector selector, string actorKey, int mySide, IReadOnlyList<string> liveActorKeys) =>
        selector switch
        {
            TargetSelector.Nearest => NearestBySide(actorKey, mySide, liveActorKeys, wantAlly: false),
            TargetSelector.SameLaneThenAdjacent => SameLaneThenAdjacent(actorKey, mySide, liveActorKeys),
            TargetSelector.LowestHp => LowestHpBySide(actorKey, mySide, liveActorKeys, wantAlly: false),
            TargetSelector.HighestThreat => HighestThreat(actorKey, mySide, liveActorKeys),
            TargetSelector.Objective => NearestToObjective(actorKey, mySide, liveActorKeys),
            TargetSelector.Self => actorKey,
            TargetSelector.AllyLowestHp => LowestHpBySide(actorKey, mySide, liveActorKeys, wantAlly: true),
            TargetSelector.AllyDowned => FirstDownedAlly(actorKey, mySide, liveActorKeys),
            _ => throw new ArgumentOutOfRangeException(nameof(selector), selector, "unknown TargetSelector"),
        };

    /// <summary>`Nearest` (`StubIntentSource.cs:104-131`'s own algorithm, generalised to either side).
    /// No board (`PositionOf` null): falls back to plain listed order, the SAME `SourceOrder` default
    /// the shipped engine's own target selection already uses with no board.</summary>
    string? NearestBySide(string actorKey, int mySide, IReadOnlyList<string> liveActorKeys, bool wantAlly)
    {
        var myPos = _view.PositionOf(actorKey);
        string? best = null;
        var bestDistance = int.MaxValue;

        for (var i = 0; i < liveActorKeys.Count; i++)
        {
            var candidate = liveActorKeys[i];
            if (string.Equals(candidate, actorKey, StringComparison.Ordinal)) continue;
            var isAlly = _view.SideOf(candidate) == mySide;
            if (isAlly != wantAlly) continue;

            if (myPos is null) return candidate; // no board: SourceOrder -- first qualifying, stop

            var distance = GridDistance.Chebyshev(myPos.Value, _view.PositionOf(candidate)!.Value);
            if (best is null || distance < bestDistance ||
                (distance == bestDistance && string.CompareOrdinal(candidate, best) < 0))
            {
                best = candidate;
                bestDistance = distance;
            }
        }

        return best;
    }

    /// <summary>`SameLaneThenAdjacent`: the lawn's own row/column shape. Same row first (the "lane"),
    /// nearest by column; falls back to `Nearest`'s own Chebyshev pick when no candidate shares a row,
    /// or `PositionOf` is null (no board -- "inert where PositionOf is null", `AiVocabulary.cs`'s own
    /// comment).</summary>
    string? SameLaneThenAdjacent(string actorKey, int mySide, IReadOnlyList<string> liveActorKeys)
    {
        var myPos = _view.PositionOf(actorKey);
        if (myPos is null) return NearestBySide(actorKey, mySide, liveActorKeys, wantAlly: false);

        string? bestInLane = null;
        var bestColDistance = int.MaxValue;
        for (var i = 0; i < liveActorKeys.Count; i++)
        {
            var candidate = liveActorKeys[i];
            if (string.Equals(candidate, actorKey, StringComparison.Ordinal)) continue;
            if (_view.SideOf(candidate) == mySide) continue;
            if (_view.PositionOf(candidate) is not { } candidatePos) continue;
            if (candidatePos.Row != myPos.Value.Row) continue;

            var colDistance = Math.Abs(candidatePos.Col - myPos.Value.Col);
            if (bestInLane is null || colDistance < bestColDistance ||
                (colDistance == bestColDistance && string.CompareOrdinal(candidate, bestInLane) < 0))
            {
                bestInLane = candidate;
                bestColDistance = colDistance;
            }
        }

        return bestInLane ?? NearestBySide(actorKey, mySide, liveActorKeys, wantAlly: false);
    }

    string? LowestHpBySide(string actorKey, int mySide, IReadOnlyList<string> liveActorKeys, bool wantAlly)
    {
        string? best = null;
        var bestHp = int.MaxValue;
        for (var i = 0; i < liveActorKeys.Count; i++)
        {
            var candidate = liveActorKeys[i];
            if (string.Equals(candidate, actorKey, StringComparison.Ordinal)) continue;
            var isAlly = _view.SideOf(candidate) == mySide;
            if (isAlly != wantAlly) continue;

            var hp = _view.FactsOf(candidate).HpMilli;
            if (best is null || hp < bestHp || (hp == bestHp && string.CompareOrdinal(candidate, best) < 0))
            {
                best = candidate;
                bestHp = hp;
            }
        }
        return best;
    }

    /// <summary>`HighestThreat` at performance tier: the candidate's OWN raw Omni power (no radius, no
    /// sum) -- deliberately simpler than the smart tier's `IncomingThreatMilli` term (a nearby-power
    /// sum needing a radius constant this general schema does not carry, see `TryBuildCandidate`'s own
    /// comment). "No candidate inputs computed" (this module's own acceptance line) means no
    /// `TargetCandidate` is built; reading one derived channel per live enemy is the cheap, structural
    /// reader `AiVocabulary.cs`'s own enum comment promises, not the smart tier's scored formula.
    /// </summary>
    string? HighestThreat(string actorKey, int mySide, IReadOnlyList<string> liveActorKeys)
    {
        string? best = null;
        var bestPower = -1L;
        for (var i = 0; i < liveActorKeys.Count; i++)
        {
            var candidate = liveActorKeys[i];
            if (string.Equals(candidate, actorKey, StringComparison.Ordinal)) continue;
            if (_view.SideOf(candidate) == mySide) continue;
            if (_view.DerivedOf(candidate) is not { } derived) continue;

            var power = (long)derived.Get(DerivedStatChannels.CombatPowerOmni);
            if (best is null || power > bestPower || (power == bestPower && string.CompareOrdinal(candidate, best) < 0))
            {
                best = candidate;
                bestPower = power;
            }
        }
        return best;
    }

    string? NearestToObjective(string actorKey, int mySide, IReadOnlyList<string> liveActorKeys)
    {
        if (_view.ObjectivePositionOf(actorKey) is not { } objective)
            return NearestBySide(actorKey, mySide, liveActorKeys, wantAlly: false); // no siege context wired

        string? best = null;
        var bestDistance = int.MaxValue;
        for (var i = 0; i < liveActorKeys.Count; i++)
        {
            var candidate = liveActorKeys[i];
            if (string.Equals(candidate, actorKey, StringComparison.Ordinal)) continue;
            if (_view.SideOf(candidate) == mySide) continue;
            if (_view.PositionOf(candidate) is not { } candidatePos) continue;

            var distance = GridDistance.Chebyshev(candidatePos, objective);
            if (best is null || distance < bestDistance || (distance == bestDistance && string.CompareOrdinal(candidate, best) < 0))
            {
                best = candidate;
                bestDistance = distance;
            }
        }
        return best;
    }

    string? FirstDownedAlly(string actorKey, int mySide, IReadOnlyList<string> liveActorKeys)
    {
        if (_isDownedOf is null) return null; // the place supplies no downed predicate -- nobody is ever downed
        for (var i = 0; i < liveActorKeys.Count; i++)
        {
            var candidate = liveActorKeys[i];
            if (string.Equals(candidate, actorKey, StringComparison.Ordinal)) continue;
            if (_view.SideOf(candidate) != mySide) continue;
            if (_isDownedOf(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>Guard 3's own census (`ActionStage`'s `opposingSideLiveCountOf`) -- invoked only when
    /// `FightEndingLiveCount >= 0` AND the action being checked is tagged Buff/Heal, matching module
    /// 1's own "invoked only when their own guard's threshold is live" contract. A second O(live) walk
    /// from `BuildRowFacts`'s own `enemiesLive`, but a conditional one, never counted against "the
    /// census is gathered once per decision" -- that phrase is the ROW WALK's own `AiRowFacts`.</summary>
    int CountEnemies(string actorKey, int mySide, IReadOnlyList<string> liveActorKeys)
    {
        var count = 0;
        for (var i = 0; i < liveActorKeys.Count; i++)
        {
            var key = liveActorKeys[i];
            if (string.Equals(key, actorKey, StringComparison.Ordinal)) continue;
            if (_view.SideOf(key) != mySide) count++;
        }
        return count;
    }

    static Func<CompiledAction, bool>? CompileFilter(AiActionFilter filter)
    {
        if (filter.Tags is null && filter.Families is null && filter.RungAtLeast is null && filter.RungAtMost is null)
            return null; // empty filter admits every held action -- StubIntentSource's own behaviour

        return action =>
        {
            if (filter.RungAtLeast is { } atLeast && action.Rung < atLeast) return false;
            if (filter.RungAtMost is { } atMost && action.Rung > atMost) return false;
            if (filter.Tags is { } tags && tags.Count > 0)
            {
                var hasAny = false;
                for (var i = 0; i < tags.Count && !hasAny; i++)
                    for (var j = 0; j < action.Tags.Count; j++)
                        if (action.Tags[j] == tags[i]) { hasAny = true; break; }
                if (!hasAny) return false;
            }
            if (filter.Families is { } families && families.Count > 0)
            {
                var matches = false;
                for (var i = 0; i < families.Count && !matches; i++)
                    if (string.Equals(action.ContainerId, families[i], StringComparison.Ordinal)) matches = true;
                if (!matches) return false;
            }
            return true;
        };
    }

    static CompiledAction FindAction(IReadOnlyList<CompiledAction> heldActions, string actionId)
    {
        for (var i = 0; i < heldActions.Count; i++)
            if (string.Equals(heldActions[i].ActionId, actionId, StringComparison.Ordinal))
                return heldActions[i];
        throw new InvalidOperationException($"ActionStage.TryPick returned '{actionId}', not held by this actor.");
    }

    void Trace(string actorKey, long nowTick, AiTier tier, int rankIndex, string? targetKey, string? actionId) =>
        _trace?.Invoke(new AiDecisionTrace(actorKey, nowTick, tier, rankIndex, targetKey, actionId));
}
