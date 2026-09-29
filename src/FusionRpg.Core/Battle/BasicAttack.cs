using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Cost;
using FusionRpg.Core.Actions.Rungs;
using FusionRpg.Core.Battle.Board;
using FusionRpg.Core.Battle.Siege;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Status;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.World;

namespace FusionRpg.Core.Battle;

// This file lives under Core/Actions/ (spec-basic-attack-adoption.md's own Structure section), but
// declares part of BattleEngine itself (namespace FusionRpg.Core.Battle, `partial`) rather than a
// free-standing type: the four adopted steps need ActorState, SelectTarget and IsCcLocked, all
// private to BattleEngine, and duplicating them would be the second private-state copy this whole
// program refuses to create. The authored row lives here; the loop that calls it lives in
// BattleEngine.cs — one seam, two files.

public static partial class BattleEngine
{
    /// <summary>
    /// The authored row for the basic attack (spec-basic-attack-adoption.md §2). Degenerate on
    /// purpose — every timing field at zero proves plumbing and nothing else. `A12`'s real actions
    /// under a real profile are where non-zero timing gets exercised.
    /// </summary>
    public static readonly ActionEnvelope BasicAttackEnvelope = ActionEnvelope.NoOp with
    {
        ActionId = "act.attack",
        // `battle-tempo` `timeline-dispatch` (D14, found 2026-09-05): Commitment is left UNSET
        // (null = "inherit the active profile's DefaultCommitment", D6) rather than hardcoded to
        // `Commitment.LateBound` as an earlier draft had it. `ActionRunner.TryCommit`'s own precedence
        // (`envelope.Commitment ?? _defaultCommitment`) means a hardcoded value here would make EVERY
        // profile's own `DefaultCommitment` permanently unreachable for the basic attack -- the ONLY
        // action any live battle dispatches today -- regardless of how complete a future dispatch
        // branch is. Confirmed inert for the atomic path: `RunBasicAttackStep`/`DeclareBasicAttack`
        // never read `envelope.Commitment` at all (grep-confirmed) -- only `ActionRunner` does, and it
        // has no production caller yet, so this changes nothing observable until one exists.
        // S2/S3 (species-skills): the basic attack opts BOTH readers in, so the two channels are live
        // in every battle rather than sitting behind content that does not exist yet. Byte-identical
        // today because both channels register with a default of 0, which is neutral for each: 0%
        // cooldown reduction, and a x1.0 effectiveness multiplier.
        CooldownChannel = DerivedStatChannels.SkillCooldown(DerivedStatChannels.ActionCategoryAttack),
        EffectivenessChannel = DerivedStatChannels.SkillEffectiveness(DerivedStatChannels.ActionCategoryAttack),
    };

    /// <summary>
    /// The authored targeting rule (spec-targeting.md §2a). `Ordering = SourceOrder` is the field
    /// §3's hazard exists to name: <c>TargetResolver</c> sorts by ordinal ptr, but
    /// <see cref="StubIntentSource.TryDeclare"/>'s own <c>NearestEnemy</c> falls back to list order
    /// whenever <see cref="IBattleView.PositionOf"/> is null (no board) — so the basic attack states
    /// which order it means rather than silently disagreeing with the resolver. <c>bloodthirsty</c>
    /// and <c>loyal</c> stay engine-side (spec-action-selection-adoption.md §5, <see cref="BloodthirstyViewFor"/>
    /// below and the bodyguard check in <see cref="RunBasicAttackStep"/>) — trait behaviour a generic
    /// target spec does not (and should not) express.
    /// </summary>
    public static readonly ActionTargetSpec BasicAttackTargeting = new()
    {
        Mode = ActionTargetMode.Single,
        Relation = ActionRelation.Enemy,
        Ordering = ActionTargetOrdering.SourceOrder,
    };

    enum AttackStepOutcome
    {
        /// <summary>No valid target, or the attacker cannot act — the round's attack phase ends for this actor.</summary>
        Continue,
        /// <summary>No valid target for this attacker at all — hazard 3: the ROUND breaks, it does not continue.</summary>
        Break,
        /// <summary>A hit landed; the caller applies it and runs the trait tail.</summary>
        Proceed,
        /// <summary>
        /// base-defense `siege-construction`/`siege-ai` (2026-09-07, session 5): the actor had no
        /// combat target (what would otherwise be <see cref="Break"/>'s own "hazard 3" case) but
        /// placed a structure instead — <see cref="ConstructionActivation.Fire"/> already ran the
        /// ENTIRE effect synchronously, so there is nothing left to dispatch (no target, no damage).
        /// The pass continues to the next actor rather than breaking the whole round, and counts as
        /// this actor having acted (<c>anyActed = true</c>) since something real happened.
        /// </summary>
        ActedWithNoTarget,
    }

    readonly record struct AttackStep(AttackStepOutcome Outcome, ActorState? Target, long SignedDelta);

    /// <summary>
    /// A17 (spec-action-selection-adoption.md §3): active check → CC-lock →
    /// <see cref="StubIntentSource.TryDeclare"/> → loyal-bodyguard redirect → <c>calculator.Compute</c>.
    /// `SelectTarget` is gone as the live targeting path — the intent source decides who, using
    /// <see cref="BloodthirstyViewFor"/> to steer it exactly where the old bloodthirsty branch did,
    /// without teaching it the trait. Everything from the berserker ramp onward is `EngineBehavior`
    /// trait logic and stays in the loop.
    ///
    /// <para>`battle-tempo` `timeline-dispatch` (D14, spec-timeline-dispatch.md §2.2): split into
    /// <see cref="DeclareBasicAttack"/> (who, with what envelope) and <see cref="ApplyBasicAttack"/>
    /// (the hit itself) so a future timeline-dispatch path can commit at one tick and apply at a
    /// later one. This method is now a two-line wrapper calling both in sequence — same statements,
    /// same order, same early-return conditions as before the split. The atomic dispatch call site
    /// (`BattleEngine.cs`'s round loop) is unchanged by this split.</para>
    /// </summary>
    static AttackStep RunBasicAttackStep(
        ActorState attacker, BattleRunState state, DateTimeOffset now, long nowTick,
        OverlayCombatCalculator calculator, ICombatRng critRng, BattleTrace? trace, int round,
        IIntentSource? intentSource = null)
    {
        var (outcome, target, envelope) = DeclareBasicAttack(attacker, state, now, nowTick, trace, round, intentSource);
        if (outcome != AttackStepOutcome.Proceed) return new AttackStep(outcome, target, 0);

        // A19 (T56.2): the atomic path's own point of no return -- OnActivate has already fired
        // inside DeclareBasicAttack and nothing after this can un-declare the intent, so this is
        // where "committing is what costs, not landing" (spec-action-costs.md §3) actually happens.
        // `null` rng: every authored cost today is a Fixed ValueSpec (no spread), so Amount
        // never rolls; a real Spread-costed action would need a dedicated cost-roll stream here,
        // mirroring EssenceRng/RidersRng's own "never a second roll butterfly" convention -- not
        // built speculatively ahead of content that needs it (AuraUpkeepDriver's own real caller
        // defaults to the same `null`).
        state.CostLedger.TryPay(attacker.Setup.Key, envelope.ActionId, ActionCostTiming.OnCommit, rng: null);

        // The commit-time half of the `holdsStock` precondition (spec-siege-construction.md §11 /
        // action-program's "ActionStockCommit.TryCommit has ZERO production callers" bug): same
        // point of no return as the cost line above, same discard-the-result posture (affordability
        // and stock are both already gated pre-commit, by CostLedger.Check and the `holdsStock` leaf
        // respectively — a commit-time shortfall here is the same rare TOCTOU race CostLedger.TryPay
        // already accepts without branching on). An action with no compiled `StockDemands` — every
        // action shipped today — never reaches the ledger at all (ActionStockCommit.TryCommit's own
        // early-out).
        if (state.ActionCatalog?.Get(envelope.ActionId) is { } stockAction)
            new ActionStockCommit(state.StockLedger).TryCommit(attacker.Setup.Key, stockAction);

        return ApplyBasicAttack(attacker, target!, envelope, state, now, nowTick, calculator, critRng);
    }

    /// <summary>
    /// `timeline-dispatch` (D14) front half: active check → CC-lock → declare → bodyguard redirect →
    /// `OnActivate` → trace. Everything `RunBasicAttackStep` did before calling `calculator.Compute`,
    /// verbatim — the only change from the pre-split code is returning the resolved target and
    /// envelope instead of falling through to the hit computation inline.
    /// </summary>
    static (AttackStepOutcome Outcome, ActorState? Target, ActionEnvelope Envelope) DeclareBasicAttack(
        ActorState attacker, BattleRunState state, DateTimeOffset now, long nowTick,
        BattleTrace? trace, int round, IIntentSource? intentSource = null)
    {
        if (!attacker.Active) return (AttackStepOutcome.Continue, null, ActionEnvelope.NoOp);
        if (IsCcLocked(state.Status, attacker.Setup.Key, now)) return (AttackStepOutcome.Continue, null, ActionEnvelope.NoOp);

        // A19 (T56.1): CostLedger's own Func<long> nowTick reads this -- assigned here, at the one
        // point every dispatch path (atomic and timeline) already knows the real current tick,
        // before it is ever consulted for affordability below.
        state.NowTick = nowTick;

        var view = BloodthirstyViewFor(state, attacker);
        // combat-ai `intent-router` (module 4, CAI1.10): the raw `??` chain replaced by the one
        // shared router (spec-intent-router.md §1a) -- both this site and TimelineDispatch.cs's own
        // Reselect now resolve through the SAME method, closing M5 (Reselect used to omit
        // `state.DefaultAiIntentSource` entirely). Byte-identical: `intentSource` (an interactive/
        // replay override that, when supplied here, always covers every actor) maps to `steered` with
        // no key restriction; `state.DefaultAiIntentSource` (null for every non-siege battle) maps to
        // `policy`, backstopped by `NoneIntentSource` so the mandatory `policy` argument is never
        // actually null; `fallback` is the SAME stub literal this site always built. `policy` only
        // cascades to `fallback` when it declares NOTHING (`ActionIntent.None`) -- for siege,
        // `SiegeAiIntentSource.TryDeclare` already reproduces the stub's own no-target/no-action/pass
        // shape internally, so it returns None in exactly the cases the stub would too; this cascade
        // is therefore inert on every reachable production path, proven by running
        // `SiegeAiLiveWiringTests`/`BattleGoldenTests`/`ExpeditionResolverTests` unchanged (see
        // CAI1.10's own evidence).
        //
        // combat-ai `intent-router` (module 4, CAI1.11): `loyal`'s post-decision redirect is now the
        // router's own `ITraitDecorator.EffectiveTargetOf` call (one declared body, one call site),
        // replacing the inline `FindAdjacentWithTrait` block this method used to carry below. Its
        // CC gate is unchanged (`LoyalTargetRedirect` wraps `IsCcLocked` verbatim). `view` above stays
        // the per-decision `bloodthirsty` decoration for the STUB, exactly as before; the automated
        // POLICY reads `state.TraitView` (see `BattleRunState`), so `bloodthirsty` now reaches it too
        // (audit M1).
        var router = IntentRouter.Compose(
            policy: state.DefaultAiIntentSource ?? (IIntentSource)NoneIntentSource.Instance,
            fallback: new StubIntentSource(view, state.Cooldowns, state.Stance, state.CostLedger),
            steeredSourceFor: intentSource is null ? null : _ => intentSource,
            decorators: new ITraitDecorator[] { new LoyalTargetRedirect(state, now) },
            // CAI2.4's router sink, reached at last (lane `cai3`, 2026-09-23): measured, NO production
            // caller passed a `sink` to `Compose` (`grep -rn "sink:" src/` returned nothing), so the four
            // arms the router wraps recorded nothing in any shipped battle — the acceptance line was met
            // at the router and unreached in the pipeline. Gated on the trace being ACTIVE, so the
            // no-trace path stays allocation-free: `AiDecisionRecordingSource` allocates one wrapper per
            // arm, and CAI1.14's zero-allocation acceptance line is about the sink-NULL path.
            sink: trace is null ? null : new FusionRpg.Core.Actions.Ai.BattleTraceDecisionSink(trace));
        var intent = router.TryDeclare(attacker.Setup.Key, nowTick);
        if (intent.IsNone)
        {
            // base-defense siege-ai/siege-construction (2026-09-07, session 5, owner-authorized
            // default policy): before conceding hazard 3's own round-break, give an actor holding the
            // `Built` acquisition path one last option -- construction is tried ONLY when combat
            // found nothing at all, matching ActionTagPreference.Rank's own already-shipped ordering
            // (Construct ranks last, "the least urgent default"). A no-op for every actor that does
            // not hold ConstructionActions.BuiltActionId (every battle before this session) or whose
            // battle never wired a ConstructionBoard (every non-siege battle kind) -- both checked
            // before touching anything, so this is provably byte-identical until both are true.
            if (TryDeclareBuilt(attacker, state, nowTick, out var builtIntent))
                return builtIntent;
            // siege-ai R3 (spec-siege-ai.md §4, 2026-09-07): the SAME "combat found nothing at all"
            // gate, tried second -- an actor with no target in reach advances toward its own objective
            // instead of standing idle. A no-op for every actor holding no Movement-tagged action, every
            // battle with no siege objective wired (every non-siege battle kind), and every battle where
            // no path exists at all ("hold and defend, never a random move" -- the spec's own words).
            if (TryDeclareObjectiveAdvance(attacker, state, out var advanceIntent))
                return advanceIntent;
            return (AttackStepOutcome.Break, null, ActionEnvelope.NoOp); // hazard 3: round breaks
        }

        var target = state.ByKey[intent.TargetKey!];
        // CAI1.11: the `loyal` bodyguard redirect is ALREADY applied -- `router.TryDeclare` above ran
        // the ONE declared `ITraitDecorator.EffectiveTargetOf` (`LoyalTargetRedirect`, which carries
        // this exact body including its `IsCcLocked` gate). `intent.TargetKey` is therefore the
        // post-redirect actor, and re-running the lookup here would be the second call site the
        // "declared once, applied once on each side" rule forbids.

        // A25 (battle-runner-path-integration): the runner runs BEFORE the bag, not after --
        // EffectBag.OnEvent calls Funnel.Flush() inside itself, so a dispatch enqueued afterwards
        // would sit in the mailbox until the next event (spec-atom-runner.md's own documented trap,
        // already avoided once by SimEffectHost.OnEvent, mirrored here). A no-op (`Runner` null) for
        // every battle with no runner-path atom bound -- byte-identical to today.
        state.Host.Runner?.OnEvent(new RunnerEvent(
            TriggerIndex.Ordinal(AtomTriggers.OnActivate),
            attacker.Setup.Key, target.Setup.Key,
            state.FactsOf(attacker.Setup.Key), state.FactsOf(target.Setup.Key)));

        // A18b (spec-on-activate-trigger.md §2): fires once per resolved (non-Break) intent,
        // independent of hit/miss -- a cast succeeds even if the attack roll misses -- at the
        // post-redirect target. A no-op today for every actor without a bound OnActivate grant
        // (A18a's own scope: nothing binds one without a real ContainerId).
        state.Host.Bag.OnEvent(new Contracts.EffectEventDto
        {
            Trigger = AtomTriggers.OnActivate,
            ActorPtr = attacker.Setup.Key,
            TargetPtr = target.Setup.Key,
            Tick = nowTick,
            HitCount = 1,
        });
        state.Host.Flush();

        trace?.Target(round, attacker.Setup.Key, target.Setup.Key);

        return (AttackStepOutcome.Proceed, target, intent.Envelope);
    }

    /// <summary>
    /// base-defense `siege-construction`/`siege-ai` (2026-09-07, session 5, owner-authorized): the
    /// live construction decision this program's own `ConstructionAi.ChooseBuiltSite` proved
    /// standalone. Returns `true` (with a ready-to-return outcome tuple) when it found and fired a
    /// legal, affordable `Built`-path placement; `false` when nothing changed — the caller's own
    /// existing `Break` fires unaltered in that case. Deliberately scoped to `Built` only: `Assembled`/
    /// `Summoned`/`Laboured` cost through the ordinary `ActionCostRow`/`CostLedger` mechanism already
    /// (no world-scoped budget to source here), and `Built` is the one path `ConstructionAi`/
    /// `ConstructionCost` were built for.
    /// </summary>
    static bool TryDeclareBuilt(
        ActorState attacker, BattleRunState state, long nowTick,
        out (AttackStepOutcome Outcome, ActorState? Target, ActionEnvelope Envelope) result)
    {
        result = (AttackStepOutcome.Break, null, ActionEnvelope.NoOp);

        // Every battle without a wired siege board (every non-siege battle kind, and every siege
        // battle before DistrictAssaultResolver's own onEffectHostReady hook runs) reads null here —
        // a quiet no-op, the same posture ExecPlaceStructure's own unwired case already establishes.
        var constructionBoard = state.Host.ConstructionBoard;
        if (constructionBoard is null) return false;

        var held = state.HeldActionsOf(attacker.Setup.Key);
        var holdsBuilt = false;
        for (var i = 0; i < held.Count; i++)
        {
            if (!string.Equals(held[i].ActionId, ConstructionActions.BuiltActionId, StringComparison.Ordinal)) continue;
            holdsBuilt = true;
            break;
        }
        if (!holdsBuilt) return false;

        if (!constructionBoard.Board.Positions.TryGetValue(attacker.Setup.Key, out var builderPos))
            return false; // off-board -- cannot measure adjacency to build

        // Content-scope reality, not a shortcut: `moat` is the ONE real structure the shipped
        // `Built` action's own atom names (ConstructionActions.cs's own hardcoded `structureId`) —
        // a fuller roster is structure-corpus's own future content job, not invented here.
        var moat = StructureCatalog.Get("moat");
        var choice = ConstructionAi.ChooseBuiltSite(
            new[] { moat }, builderPos, constructionBoard.Board, constructionBoard.Board.Spec,
            constructionBoard.BoardSide, constructionBoard.CoreSideMilli, constructionBoard.RampartThickness,
            constructionBoard.RemainingRubble, constructionBoard.RemainingIronwork,
            requiredSlotKindSatisfiedAt: (def, cell) =>
                constructionBoard.SlotByCell.TryGetValue(cell, out var slot) && slot.Kind == def.RequiredSlotKind);
        if (choice is null) return false; // nothing affordable and legal right now

        constructionBoard.SpendBuilt(choice.Value.Structure);
        ConstructionActivation.Fire(state.Host, attacker.Setup.Key, choice.Value.Cell.Row, choice.Value.Cell.Col, nowTick);

        result = (AttackStepOutcome.ActedWithNoTarget, null, ActionEnvelope.NoOp);
        return true;
    }

    /// <summary>
    /// base-defense `siege-ai` R3 (spec-siege-ai.md §4, 2026-09-07): "no target in reach -> path
    /// toward the objective." Tried after <see cref="TryDeclareBuilt"/>, same shape (returns `true`
    /// with a ready outcome when it moved the actor at least one cell; `false` when nothing changed —
    /// the caller's own `Break` fires unaltered). The actual pathing/stepping is
    /// <see cref="BattleRunState.TryMoveTowardObjective"/> — this method is purely the two gate checks
    /// that decide whether to even attempt it (holds a Movement-tagged action; has positive move
    /// range), matching <see cref="ApplyBasicAttack"/>'s own existing A9 Movement-category dispatch
    /// gate exactly so a real battle behaves the same way whether it reaches this fallback or the
    /// ordinary dispatch path.
    /// </summary>
    static bool TryDeclareObjectiveAdvance(
        ActorState attacker, BattleRunState state,
        out (AttackStepOutcome Outcome, ActorState? Target, ActionEnvelope Envelope) result)
    {
        result = (AttackStepOutcome.Break, null, ActionEnvelope.NoOp);

        var held = state.HeldActionsOf(attacker.Setup.Key);
        var holdsMovement = false;
        for (var i = 0; i < held.Count; i++)
        {
            var tags = held[i].Tags;
            var tagged = false;
            for (var t = 0; t < tags.Count; t++)
            {
                if (tags[t] != FusionRpg.Core.Actions.ActionTag.Movement) continue;
                tagged = true;
                break;
            }
            if (!tagged) continue;
            holdsMovement = true;
            break;
        }
        if (!holdsMovement) return false;

        var moveRange = (int)Math.Round(attacker.Derived.Get(DerivedStatChannels.MoveRange));
        if (moveRange <= 0) return false; // byte-identical for every actor shipped today (move.range defaults to 0)

        if (state.TryMoveTowardObjective(attacker.Setup.Key, moveRange) == 0) return false;

        result = (AttackStepOutcome.ActedWithNoTarget, null, ActionEnvelope.NoOp);
        return true;
    }

    /// <summary>
    /// `timeline-dispatch` (D14) back half: `calculator.Compute` → `OnDamageDealt` → cooldown arm.
    /// Everything `RunBasicAttackStep` did after resolving the target, verbatim, against a target
    /// that is now a parameter (already resolved — by <see cref="DeclareBasicAttack"/> on the atomic
    /// path, or by <c>ActionRunner.CurrentTarget</c> after commitment-binding re-selection on the
    /// timeline-dispatch path).
    /// </summary>
    static AttackStep ApplyBasicAttack(
        ActorState attacker, ActorState target, ActionEnvelope envelope, BattleRunState state,
        DateTimeOffset now, long nowTick, OverlayCombatCalculator calculator, ICombatRng critRng)
    {
        // A22 (spec-action-resolution-by-category.md §2): Category lives on CompiledAction, not on
        // ActionEnvelope -- resolved here via the catalog reference BattleRunState now keeps as a
        // field. null (every action authored before A-E1 shipped, and the basic attack itself, which
        // is hand-built and never reaches this lookup at all since it is not in any real catalog) maps
        // to Attack deliberately -- changing that reading would silently alter every already-blessed
        // golden's own resolution.
        var category = state.ActionCatalog?.Get(envelope.ActionId)?.Category ?? ActionCategory.Attack;
        if (category != ActionCategory.Attack)
        {
            // A9 (spec-movement-actions.md §3): "destination legality is A10's, not a second rule" --
            // this is the ONLY new behavior a Movement-category action adds to the existing
            // Defense/Support/Status dispatch below; every existing atom-granted effect still resolves
            // through the unconditional OnActivate path exactly as before. Byte-identical for every
            // actor shipped today: move.range defaults to 0 (nothing grants it yet), and
            // TryMoveTowardNearestEnemy returns 0 cells moved without touching the board at all when
            // maxCells <= 0 -- confirmed by reading MoveAction.MoveToward directly, not assumed.
            if (category == FusionRpg.Core.Actions.ActionCategory.Movement)
            {
                var moveRange = (int)Math.Round(attacker.Derived.Get(FusionRpg.Core.Stats.Derived.DerivedStatChannels.MoveRange));
                if (moveRange > 0) state.TryMoveTowardNearestEnemy(attacker.Setup.Key, moveRange);
            }

            // Defense/Support/Movement/Status: the hit/crit roll is meaningless for a non-attack
            // action (it was never supposed to "miss") -- calculator.Compute is never called, so its
            // own OnDamageDealt trigger never fires either (there is no hit to trigger it; OnActivate,
            // A18b, already covers this action's real effect, unconditionally, regardless of category).
            // The cooldown arms the moment the action resolves -- unconditional, never gated on an
            // outcome that was never rolled, unlike the Attack branch below.
            state.Cooldowns.Start(attacker.Setup.Key, envelope, nowTick,
                SkillCooldownReductionPm(attacker, envelope));
            return new AttackStep(AttackStepOutcome.Proceed, target, 0);
        }

        // action-enrich `action-base` (H6): the hit's base comes from the ACTION, not the actor's
        // `atk`. This is the ONE production read-site the module changes, and the `LiveAtk` damage
        // read leaves in this same change — there is never a window where both feed damage.
        //
        // The swung row comes from `HeldActionOf`, never a catalog lookup: the basic attack is
        // hand-built and in no catalog, a catalog-less battle falls every equipped id back to it,
        // siege `AdditionalHeldActions` and a garrison-lent row are compiled but uncatalogued, and
        // `HeldActionsOf` covers all four. An envelope the actor does not hold throws, naming both —
        // never a silent basic-attack base.
        var held = state.HeldActionOf(attacker.Setup.Key, envelope.ActionId)
            ?? throw new InvalidOperationException(
                $"'{attacker.Setup.Key}' swung '{envelope.ActionId}', which it does not hold");
        // Θ is Hub output (`progression.power`) — the magnitude side, exactly as `CostLedger` already
        // reads it. The value is an integer carried as `double`; the narrowing is `checked` (numeric
        // rule 4), so a non-integral or out-of-range value throws rather than truncating silently.
        var theta = checked((int)attacker.Derived.Get(DerivedStatChannels.ProgressionPower));
        var baseOverlayDamage = ActionBaseMath.BasePerHit(
            ActionBaseDerivation.BasePowerMilli(
                held.Kind, held.ActionId,
                state.EffectiveRungOf(attacker.Setup.Key, envelope.ActionId),
                RungPolicy.Table, ActionBaseTuningHub.Tuning),
            BattleRuleset.PowerValue(theta));

        var (signedDelta, breakdown) = calculator.Compute(new OverlayCombatRequest
        {
            BaseOverlayDamage = baseOverlayDamage,
            Components = attacker.AttackComponents,
            Attacker = new CombatActorSnapshot(attacker.Derived, attacker.ElementTypes),
            Defender = new CombatActorSnapshot(target.Derived, target.ElementTypes),
            // S3 (species-skills): the attacker's own `skill.effectiveness.{category}` scales the
            // payload, applied INSIDE the resolver (never as a second multiplier afterwards, which
            // would put combat math outside the SSOT and trip the parity tests by design). 0 is
            // neutral and yields exactly 1.0, so an envelope that names no channel is byte-identical.
            EffectivenessMultiplier =
                OverlayCombatRequest.MultiplierFromPerMille(SkillEffectivenessPm(attacker, envelope)),
            Profile = CombatProfile.BattleSim
        }, critRng);

        if (!breakdown.Hit) return new AttackStep(AttackStepOutcome.Continue, null, 0);

        // A25 (battle-runner-path-integration): same "runner before the bag" ordering as the
        // OnActivate site above.
        state.Host.Runner?.OnEvent(new RunnerEvent(
            TriggerIndex.Ordinal(AtomTriggers.OnDamageDealt),
            attacker.Setup.Key, target.Setup.Key,
            state.FactsOf(attacker.Setup.Key), state.FactsOf(target.Setup.Key)));

        // A18c (spec-battle-resource-shield-grants.md §2): resource.delta's existing shipped content
        // (fx.poison_on_hit, fx.freeze_on_hit, ...) is OnDamageDealt-triggered, not OnActivate -- a
        // skill's on-hit rider fires when the hit actually lands, mirroring existing content exactly.
        // Only on a landed hit (this line is unreachable on a miss, above); before DispatchHit's own
        // trait tail runs, so a rider fires alongside the calculator-resolved damage, not nested
        // inside EngineBehavior.
        state.Host.Bag.OnEvent(new Contracts.EffectEventDto
        {
            Trigger = AtomTriggers.OnDamageDealt,
            ActorPtr = attacker.Setup.Key,
            TargetPtr = target.Setup.Key,
            Damage = -signedDelta,
            Tick = nowTick,
            HitCount = 1,
        });
        state.Host.Flush();

        // S2 (species-skills): the cooldown is reduced by the attacker's own
        // `skill.cooldown.{category}` — the channel the envelope itself names — resolved HERE, at the
        // arming site, because CooldownLedger stores an absolute tick. An envelope with no
        // CooldownChannel reads nothing and arms at base ticks; that is the neutral path and it stays
        // allocation-free. Inert for Class.None, as before.
        //
        // Real, latent bug found and fixed 2026-09-07 (cooldown-arming-double-call, named 2026-09-06):
        // this call was UNCONDITIONAL, with no `StartsAt` check at all -- independent of, and
        // redundant with, ALL THREE of `ActionRunner`'s own StartsAt-gated arming sites
        // (`ActionRunner.cs:236` Commit, `:361` Resolve, `:289` RecoveryEnd). Since
        // `CooldownLedger.Start` is a plain overwrite, this call firing unconditionally after
        // `ActionRunner.TryCommit`'s own `StartsAt==Commit` arm (or before `OnRecoveryDue`'s own
        // `StartsAt==RecoveryEnd` arm) would silently re-arm at the WRONG tick the moment content ever
        // authored `StartsAt: Commit`/`RecoveryEnd` -- latent only because no shipped content does yet.
        // Gated behind `StartsAt == Resolve` (the enum's own declared default,
        // `ActionEnvelope.cs:110`), matching `ActionRunner`'s own three-way split exactly and provably
        // byte-identical for every envelope that does not override it, which is every one that exists
        // today.
        if (envelope.StartsAt == CooldownStart.Resolve)
            state.Cooldowns.Start(attacker.Setup.Key, envelope, nowTick,
                SkillCooldownReductionPm(attacker, envelope));
        return new AttackStep(AttackStepOutcome.Proceed, target, signedDelta);
    }

    /// <summary>
    /// The acting actor's cooldown reduction for this action, per-mille — 0 when the envelope names
    /// no channel, which is every action that does not opt in. Reads the actor's already-composed
    /// derived snapshot, so this adds no resolve.
    /// </summary>
    static long SkillCooldownReductionPm(ActorState actor, ActionEnvelope envelope) =>
        envelope.CooldownChannel is { } channel ? (long)actor.Derived.Get(channel) : 0;

    /// <summary>The acting actor's effectiveness bonus for this action, per-mille — 0 when the
    /// envelope names no channel. Returns a <c>long</c> on purpose: the per-mille to multiplier
    /// conversion is double arithmetic and belongs in <c>Combat/</c>, because this directory bans
    /// floating point.</summary>
    static long SkillEffectivenessPm(ActorState actor, ActionEnvelope envelope) =>
        envelope.EffectivenessChannel is { } channel ? (long)actor.Derived.Get(channel) : 0;

    /// <summary>
    /// spec-action-selection-adoption.md §5: `bloodthirsty` stays engine-side, reimplemented as a
    /// pre-filter over the attacker's own enemy view rather than as a branch inside
    /// <see cref="StubIntentSource"/>, which must not gain trait vocabulary it does not own. Moving
    /// the lowest-HP live enemy to the front of <see cref="IBattleView.LiveActorKeys"/> is enough:
    /// <c>NearestEnemy</c>'s own no-board fallback returns the first enemy it finds.
    ///
    /// <para>CAI1.11: this is still the STUB's own per-decision view (`inner` = the run state).
    /// <see cref="BloodthirstyDecorator"/> calls the same body for every OTHER policy, through the
    /// one trait-aware view those policies are constructed with.</para>
    /// </summary>
    static IBattleView BloodthirstyViewFor(BattleRunState state, ActorState attacker) =>
        BloodthirstyViewFor(state, attacker, inner: state);

    static IBattleView BloodthirstyViewFor(BattleRunState state, ActorState attacker, IBattleView inner)
        => BloodthirstyViewFor(state, attacker, inner, reusable: null);

    /// <summary>CAI1.14 (site 2): with a <paramref name="reusable"/> instance this refills instead of
    /// allocating — the shape the decorator uses, since it owns one view for the battle. A null
    /// `reusable` keeps the pre-CAI1.14 shape for the two direct call sites
    /// (`DeclareBasicAttack`/`TimelineDispatch`, which build the stub fallback's view).</summary>
    static IBattleView BloodthirstyViewFor(
        BattleRunState state, ActorState attacker, IBattleView inner, BloodthirstyView? reusable)
    {
        if (!attacker.Has("bloodthirsty")) return inner;

        ActorState? lowest = null;
        foreach (var a in state.Actors)
            if (a.Active && a.Setup.Side != attacker.Setup.Side && (lowest == null || a.Hp < lowest.Hp))
                lowest = a;

        if (lowest is null) return inner;
        if (reusable is null) return new BloodthirstyView(inner, lowest.Setup.Key);

        reusable.Refill(inner, lowest.Setup.Key);
        return reusable;
    }

    /// <summary>
    /// combat-ai `intent-router` (module 4, CAI1.11, spec-intent-router.md §2): `bloodthirsty` as an
    /// <see cref="ITraitDecorator"/> — the pre-decision half, the SAME body
    /// <see cref="BloodthirstyViewFor"/> has always had, now reaching every policy instead of only the
    /// stub (audit M1). `EffectiveTargetOf` is the identity: this trait changes what a policy SEES,
    /// never where its answer lands.
    ///
    /// <para>The implementation stays engine-side (`decisions.md` row 44) and holds the whole trait
    /// vocabulary: <see cref="TraitAwareBattleView"/> lives in `Core/Actions/` and knows only
    /// `AppliesTo`/`Decorate`.</para>
    /// </summary>
    /// <summary>combat-ai `decision-perf` CAI1.14 (site 2, spec-decision-perf.md): the view the
    /// decorator hands a policy is REFILLED, not rebuilt. `_view` is this decorator's own scratch — one
    /// instance per battle, because `TraitViewDecorators` runs once per `BattleRunState` — so a decision
    /// neither allocates the view nor the ordered list once warm.
    ///
    /// <para><b>Single-decision, documented rather than faked.</b> The list a refill produces is valid
    /// until the next refill; the decorator's contract is that a view is consumed synchronously inside
    /// one `TryDeclare` (re-entry depth is 0, `overlay-control-loops.md` §6 rule 7). An interleaved use
    /// cannot be detected at this seam — the caller simply reads `LiveActorKeys` — so it is not guarded
    /// with a throw that could never fire.</para></summary>
    sealed class BloodthirstyDecorator : ITraitDecorator
    {
        readonly BattleRunState _state;
        readonly BloodthirstyView _view;

        public BloodthirstyDecorator(BattleRunState state)
        {
            _state = state;
            _view = new BloodthirstyView(state);
        }

        public bool AppliesTo(string actorKey) =>
            _state.ByKey.TryGetValue(actorKey, out var actor) && actor.Has("bloodthirsty");

        public IBattleView Decorate(string actorKey, IBattleView inner) =>
            _state.ByKey.TryGetValue(actorKey, out var actor)
                ? BloodthirstyViewFor(_state, actor, inner, _view)
                : inner;

        public string EffectiveTargetOf(string actorKey, string targetKey) => targetKey;
    }

    /// <summary>
    /// combat-ai `intent-router` (module 4, CAI1.11, spec-intent-router.md §2): `loyal` as an
    /// <see cref="ITraitDecorator"/> — the post-decision half. Same body as the redirect
    /// `DeclareBasicAttack` used to carry inline, CC gate included: an adjacent living ALLY of the
    /// chosen target that carries `loyal` takes the hit, unless it is crowd-controlled. Built once per
    /// decision (it needs that decision's real `now`), so `BattleRunState` cannot hoist it.
    ///
    /// <para><b>`AppliesTo` is unconditionally true, deliberately.</b> Whether `loyal` redirects this
    /// decision depends on the TARGET the policy just chose, not on the deciding actor — nothing about
    /// the decider can answer it in advance. Returning false here would silently drop the trait for
    /// every actor, which is worse than one adjacency scan per decided intent (the same scan the
    /// inline version ran unconditionally too).</para>
    /// </summary>
    sealed class LoyalTargetRedirect : ITraitDecorator
    {
        readonly BattleRunState _state;
        readonly DateTimeOffset _now;

        public LoyalTargetRedirect(BattleRunState state, DateTimeOffset now)
        {
            _state = state;
            _now = now;
        }

        public bool AppliesTo(string actorKey) => true;

        public IBattleView Decorate(string actorKey, IBattleView inner) => inner;

        public string EffectiveTargetOf(string actorKey, string targetKey)
        {
            if (!_state.ByKey.TryGetValue(targetKey, out var target)) return targetKey;
            var bodyguard = FindAdjacentWithTrait(_state.Actors, target, "loyal");
            return bodyguard is not null && !IsCcLocked(_state.Status, bodyguard.Setup.Key, _now)
                ? bodyguard.Setup.Key
                : targetKey;
        }
    }

    /// <summary>combat-ai `intent-router` (module 4, CAI1.11): the pre-decision decorators every
    /// policy's own bound view must carry, built once per battle by `BattleRunState`. `loyal` is NOT
    /// here — it is post-decision and needs the deciding call's real `now`.</summary>
    static IReadOnlyList<ITraitDecorator> TraitViewDecorators(BattleRunState state) =>
        new ITraitDecorator[] { new BloodthirstyDecorator(state) };

    /// <summary>The same live-actor set as <c>inner</c>, with one key moved to the front — the
    /// mechanism <see cref="BloodthirstyViewFor"/> uses.</summary>
    sealed class BloodthirstyView : IBattleView
    {
        IBattleView _inner;
        readonly List<string> _order = new();

        /// <summary>The reusable instance's own initial inner — the run state itself, which is what
        /// `BloodthirstyViewFor` passes on every refill. Assigned (rather than left null) so the field
        /// is non-nullable for its whole life; the first `Refill` overwrites both it and the order.</summary>
        public BloodthirstyView(IBattleView inner) => _inner = inner;

        public BloodthirstyView(IBattleView inner, string priorityKey)
        {
            _inner = inner;
            Refill(inner, priorityKey);
        }

        /// <summary>CAI1.14 (site 2): the same live-actor set as <paramref name="inner"/>, with one key
        /// moved to the front, written into this instance's retained list. `Clear()` keeps capacity, so
        /// a warm instance allocates nothing. See <see cref="BloodthirstyDecorator"/>'s own doc for the
        /// single-decision lifetime this assumes.</summary>
        public void Refill(IBattleView inner, string priorityKey)
        {
            _inner = inner;
            _order.Clear();
            _order.Add(priorityKey);
            var live = inner.LiveActorKeys;
            foreach (var key in live)
                if (!string.Equals(key, priorityKey, StringComparison.Ordinal))
                    _order.Add(key);
        }

        public IReadOnlyList<string> LiveActorKeys => _order;
        public int SideOf(string actorKey) => _inner.SideOf(actorKey);
        public GridPos? PositionOf(string actorKey) => _inner.PositionOf(actorKey);
        public EntityFacts FactsOf(string actorKey) => _inner.FactsOf(actorKey);
        public IReadOnlyList<CompiledAction> HeldActionsOf(string actorKey) => _inner.HeldActionsOf(actorKey);
        public FusionRpg.Core.Stats.Derived.ActorDerivedSnapshot? DerivedOf(string actorKey) => _inner.DerivedOf(actorKey);
        public string? GarrisonedStructureKeyOf(string actorKey) => _inner.GarrisonedStructureKeyOf(actorKey);
        public GridPos? ObjectivePositionOf(string actorKey) => _inner.ObjectivePositionOf(actorKey);
        public long? MaxHpOf(string actorKey) => _inner.MaxHpOf(actorKey);
        public int AggressionOf(string actorKey) => _inner.AggressionOf(actorKey);
        public IReadOnlyList<string> DownedAllyKeysOf(string actorKey) => _inner.DownedAllyKeysOf(actorKey);
    }
}
