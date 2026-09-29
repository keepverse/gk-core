using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Ai;
using FusionRpg.Core.Actions.Cost;
using FusionRpg.Core.Actions.Movement;
using FusionRpg.Core.Actions.Unlock;
using FusionRpg.Core.Battle.Board;
using FusionRpg.Core.Battle.Siege;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Combat.Shield;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Stats.Derived.Subsystems;
using FusionRpg.Core.Status;
using FusionRpg.Core.World.District;

namespace FusionRpg.Core.Battle;

// B13 (spec-kernel-adoption.md, battle-timeline-todo.md): BattleRunState is the state object T5
// needs a callback target for. The spec's own Structure section sketches this file at
// Battle/Timeline/BattleRunState.cs — deliberately NOT followed here, and the deviation is recorded
// rather than silent: putting it under Battle/Timeline/ would put every LINQ call and DateTimeOffset
// use in this file under KernelPurityScan's full purity+tick-path rules (that directory has no
// per-file exemption model for "ordinary battle-domain code that happens to be adjacent to the
// kernel"), and it would force ActorState / IsCcLocked / FindAdjacentWithTrait /
// AnyActive / RunBasicAttackStep / EssenceTraits from `private` to `internal` just to stay
// reachable across namespaces — a visibility change with no zero-behavior-change refactor should
// need. Nesting BattleRunState inside `BattleEngine` instead (same trick BasicAttack.cs already
// uses for `RunBasicAttackStep`, a sibling `partial class BattleEngine` file) keeps every one of
// those private members reachable with NO visibility change at all — the smallest possible diff
// for a step whose entire acceptance bar is "if a golden moves here, stop."
public static partial class BattleEngine
{
    /// <summary>
    /// Everything `Resolve` used to hold as local state and closures, extracted verbatim — no
    /// method body below differs from its pre-extraction original by more than "closure over a
    /// local" becoming "instance method over a field." `Resolve` itself still owns the round
    /// skeleton (the `while` loop, initiative ordering, the per-attacker `Continue`/`Break` check) —
    /// turning that skeleton into scheduled kernel events is B14's job, deliberately not this one's.
    /// </summary>
    sealed class BattleRunState : IBattleView
    {
        /// <summary>
        /// A17 (spec-action-selection-adoption.md §2): the fallback held action for any actor whose
        /// `EquippedActionIds` is null or empty — "no loadout" must still produce a legal, single-
        /// action AI decision, never `ActionIntent.None` by construction. Hand-built rather than run
        /// through `ActionCompiler.Compile`: the basic attack has no rung, no container, no atoms —
        /// forcing it through the real-content compiler would mean inventing fake rung/container rows
        /// for something that fundamentally has neither, exactly the trap `A5`'s own degenerate
        /// envelope was designed to sidestep. `TargetSpecCompiler.Compile` and `PredicateCompiler.Always`
        /// are still the REAL compiler pieces, reused rather than re-guessed, for the two fields that
        /// have one.
        ///
        /// <para><b>battle-tempo `action-timing` (2026-09-05):</b> this field moved from `static
        /// readonly` to an ordinary instance field, computed once per `BattleRunState` (i.e. once per
        /// battle) rather than once per process — the ONLY change that made room for
        /// <see cref="ActionTimingDerivation.DeriveBasicAttack"/> to read
        /// <see cref="ActionTimingPolicy.Tuning"/> here: a `static readonly` initializer runs at first
        /// type touch, which could race host startup's `ActionTimingPolicy.Configure` call and throw
        /// for any caller that reaches `BattleRunState` first. An instance initializer runs during
        /// `new BattleRunState(...)`, by which point every real caller (`BattleEngine.Resolve`) has
        /// already configured tuning — the same timing every other `Policy`/`Tuning` read in this
        /// class already assumes.</para>
        ///
        /// <para><b>lawn-combat-wire T8 (spec-lawn-action-bridge.md):</b> the construction itself moved
        /// out to <see cref="BasicAttackFactory.Create"/> — the ONE public factory shared with
        /// the injector's own lawn-side construction, so a second hand-built copy of this row can never
        /// drift from this one. This field's own timing (instance init, not static) is unchanged by
        /// that move; only where the `new CompiledAction(...)` literal lives changed. Field-level golden:
        /// <c>BasicAttackFactoryGoldenTests</c> (<c>FusionRpg.Core.Tests</c>).</para>
        /// </summary>
        readonly CompiledAction BasicAttackCompiled = BasicAttackFactory.Create(ActionTimingPolicy.Tuning);

        /// <summary>`battle-tempo` `timeline-dispatch` (D14): the one field of
        /// <see cref="BasicAttackCompiled"/> the timeline-dispatch action phase needs (the derived
        /// wind-up/recovery envelope) — exposed narrowly rather than widening the whole field's
        /// visibility, since nothing else outside this class needs the rest of it (targeting, costs,
        /// scopes) today.</summary>
        public Timeline.ActionEnvelope BasicAttackEnvelopeCompiled => BasicAttackCompiled.Envelope;

        public readonly List<ActorState> Actors;
        public readonly Dictionary<string, ActorState> ByKey;
        public readonly BattleEffectHost Host;
        public readonly StatusRuntime Status;
        public readonly ShieldRuntime Shields;
        public readonly ShieldGate ShieldGate;

        /// <summary>W3 (battle-derived-wire T2): the ONE actor resolver `CombatMath`, `ShieldGate` and
        /// the reflect step share — stored so <see cref="ApplyHp"/>'s reflect tail reuses it rather
        /// than building a second closure.</summary>
        readonly CombatActorResolve _resolveActor = null!;
        public readonly FunnelHpDeltaSink HpSink;
        public readonly BattlePulseSink PulseSink;
        public readonly OverlayCombatCalculator Calculator;
        public readonly SeededRng InitiativeRng;
        public readonly ICombatRng CritRng;
        public readonly SeededRng EssenceRng;
        public readonly SeededRng RidersRng;

        /// <summary>D4.6 (spec-wild-room.md §5): `act.capture`'s own stream, "the battle's own seed,
        /// never a second RNG" — one line beside `EssenceRng`/`RidersRng`, same reasoning: capture's
        /// content (seal tiers, status bonuses) must not butterfly any other system's rolls.</summary>
        public readonly SeededRng CaptureRng;

        /// <summary>Wave E1: riders decide their own chance on <see cref="RidersRng"/>, so the
        /// evaluator gets a scripted 0.0 rather than a second roll. Same object and same reasoning as
        /// the scripted setup-status path.</summary>
        static readonly FixedStatusRng RiderApplyRng = new(0.0);
        public readonly BattleStatusRng StatusRng;
        public readonly DateTimeOffset T0;
        public readonly List<BattleEventRec> Events = new();
        public readonly HashSet<string> RecordedDeaths = new(StringComparer.Ordinal);
        public readonly Timeline.BattleTrace? Trace;

        /// <summary>A17: real ledger, per spec-action-selection-adoption.md §6 — inert for the
        /// all-zero basic-attack envelope (`Class.None`), so A19's real cooldowns need no further
        /// wiring here when they arrive.</summary>
        public readonly Timeline.CooldownLedger Cooldowns = new();

        /// <summary>D4.6 (spec-wild-room.md §5): "each failed attempt on the same target shifts [the
        /// delta band] toward `far-above`… kept in a per-battle `CaptureAttempts` ledger beside
        /// `CooldownLedger`." Keyed on the target alone (`attempts(target)` in the spec's own
        /// formula takes one argument) — `CooldownLedger`'s own compound actor×slot key is a
        /// different shape for a different question, not a template here.</summary>
        public readonly Dictionary<string, int> CaptureAttempts = new(StringComparer.Ordinal);

        /// <summary>`battle-tempo` `reaction-lane` RL2: one registry per battle, mirroring
        /// `Cooldowns`' own "fresh per battle, not per actor construction elsewhere" shape. Reuses
        /// `LawnActorResourcePools` verbatim rather than a near-duplicate type — its own mechanism
        /// (a `Dictionary&lt;ptr, ActorResourcePools&gt;`, lazily filled, full at first access) carries
        /// no lawn-specific logic despite the name; a battle actor is exactly the second consumer its
        /// own doc comment already anticipates ("Unity-free by construction"). No faction branch: a
        /// `wave` actor gets a pool exactly like a `squad` actor does — resource-hub-ssot.md's own
        /// rule ("one shared set... faction difference is a display label, never a branch").</summary>
        public readonly LawnActorResourcePools ResourcePools = new();

        /// <summary>A19 (spec-action-costs-cooldowns-adoption.md T56.1): whichever tick the caller
        /// currently considers "now" — `BasicAttack.cs`'s functions and `TimelineDispatch`'s
        /// `RunTimelineActionPhase` each compute a tick locally with no single shared read `CostLedger`
        /// (a real, previously-unbuilt dependency) can point its own `Func&lt;long&gt; nowTick` at, so
        /// every call site that already threads a tick locally also assigns it here first. Mutable by
        /// design — this is a live pointer into "what tick is it right now for this battle", not a
        /// snapshot; unlike `T0` (fixed at construction), it moves every time a caller advances.</summary>
        public long NowTick;

        /// <summary>A19 (T56.1): the real, first production `CostLedger` in this repo — grep-confirmed
        /// zero prior construction sites anywhere in `src/`. Built once here, not per-call, mirroring
        /// `Cooldowns`/`ResourcePools`' own "one instance for the whole battle" shape. `costsByActionId`
        /// adapts `CompiledAction.Costs` (`CompiledActionCost`) into `ActionCostRow` — structurally
        /// compatible fields, different types, because the compiled and authored shapes serve different
        /// callers (`ActionCostRow` also carries `AllowLethal`, which a compiled cost's own consumer
        /// never needed until now). Empty when `actionCatalog` is null — vacuously affordable, the same
        /// "an action with no cost table is unaffected" additive discipline this program uses everywhere.</summary>
        public readonly CostLedger CostLedger;

        /// <summary>
        /// base-defense `siege-ai` (2026-09-07, session 5, owner-authorized): a real, live consumer for
        /// `SiegeAiIntentSource` — built once, here, for the whole battle (never per-call, matching
        /// `Cooldowns`/`CostLedger`'s own "one instance" shape, and the reason `RetargetLedger` below
        /// gets real accumulating state rather than resetting every call). Null unless the caller opts
        /// in via `aiTuning` (every existing caller of `Resolve`/this constructor omits it, so this is
        /// byte-identical to today for every battle that does not ask for it). `DeclareBasicAttack`'s own
        /// fallback tries this SECOND, after any explicit `intentSource` override and BEFORE
        /// `StubIntentSource` — an actor gets the smarter, scored decision the moment its battle opts in,
        /// with zero change to any battle that doesn't.
        /// </summary>
        public readonly IIntentSource? DefaultAiIntentSource;

        /// <summary>
        /// combat-ai `intent-router` (module 4, CAI1.11, spec-intent-router.md §2): the SAME live battle
        /// state as an <see cref="IBattleView"/>, wrapped in the trait decorators every POLICY reads
        /// through — the seam the CAI1.11 ruling names. `bloodthirsty`'s reordering is viewer-relative,
        /// so a policy that binds ONE view for its whole life (`DefaultAiIntentSource`, which owns a
        /// persistent `RetargetLedger`/`BattleTrace`) reaches it through
        /// <see cref="IBattleView.LiveActorKeysFor"/> instead of through a per-call view swap.
        ///
        /// <para>Built lazily, once, and shared: the decorator list is the engine's own trait
        /// vocabulary (see `BattleEngine.TraitViewDecorators`), never visible to `Core/Actions`'
        /// scoring code. The STUB keeps its own per-decision decoration
        /// (`DeclareBasicAttack`'s `BloodthirstyViewFor`) — that path is byte-identical and this view
        /// deliberately does not replace it.</para>
        /// </summary>
        public IBattleView TraitView => _traitView ??= new TraitAwareBattleView(this, TraitViewDecorators(this));

        TraitAwareBattleView? _traitView;

        /// <summary>
        /// combat-ai `stance-wiring` CAI3.1 (spec-stance-wiring.md, Outcome 1): the ONE stance seam for
        /// this run. Defaulted to <see cref="NoStanceHeld.Instance"/> — the same no-op every call site
        /// used to hardcode inline — so a caller that assigns nothing is byte-identical to before, and
        /// exactly one place in `gk-core/src/FusionRpg.Core/Battle/**` still names that instance.
        ///
        /// <para><b>No held action is a stance action today</b>, which is the criterion this seam answers
        /// once for all three consumers: an `ActionRow` cannot say *"this action is a stance whose
        /// release is X"* (the `action` program's A8 owns that field), so a live `StanceRuntime` would
        /// be a mechanism with no data behind it. The seam ships; the runtime waits for its trigger, and
        /// `StanceRuntime`/`PoiseLedger`/`Riposte` stay unmodified.</para>
        /// </summary>
        public IStanceCheck Stance { get; set; } = NoStanceHeld.Instance;

        /// <summary>
        /// B38 — one <see cref="Timeline.ActorTurnMachine"/> per actor, for the whole battle.
        ///
        /// <para>Before this the per-actor FSM existed and was fully tested but was never driven by a
        /// real battle: `ActorTurnMachine` appeared nowhere in the engine. An interactive dwell needs a
        /// `Ready` state to occupy, so B20/B21/B22 had nothing to attach to. These machines are what
        /// give them one.</para>
        ///
        /// <para><b>Pure bookkeeping under `classic-round`</b>: with zero wind-up and zero recovery the
        /// cycle collapses to Charging → Ready → Committed → Resolving → Recovering → Charging around
        /// the same attack, in the same order, drawing the same RNG. Byte-identical by construction.</para>
        /// </summary>
        public readonly Dictionary<string, Timeline.ActorTurnMachine> TurnMachines = new(StringComparer.Ordinal);

        public Timeline.ActorTurnMachine MachineFor(string actorKey) =>
            TurnMachines.TryGetValue(actorKey, out var m)
                ? m
                : TurnMachines[actorKey] = new Timeline.ActorTurnMachine(actorKey);

        /// <summary>A18e (spec-battle-live-stat-modifiers.md §1): one instance per battle, same
        /// lifetime as Cooldowns/Shields above.</summary>
        public readonly BattleStatModifierLedger Ledger = new();

        /// <summary>
        /// Live derived contributions are registered here, then resolved by the battle's existing
        /// ActorHub composition path. The ledger owns no arithmetic and no alternative fold.
        /// </summary>
        public readonly BattleDerivedModifierLedger DerivedLedger = new();

        /// <summary>Copies the current one-Hub output into the stable snapshot object consumers hold.</summary>
        public void RecomposeDerived(string actorKey)
        {
            var actor = ByKey[actorKey];
            DerivedLedger.Recompose(actorKey, actor.BaseDerived, actor.Derived);
        }

        /// <summary>Per-round refresh for live status, aura, defense, and host contributions.</summary>
        public void RecomposeDerivedForAllActors()
        {
            if (DerivedLedger.IsEmpty) return;
            foreach (var a in Actors)
                RecomposeDerived(a.Setup.Key);
        }

        ActorDerivedSnapshot ResolveLiveDerivedThroughHub(string actorKey)
        {
            var actor = ByKey[actorKey];
            var liveAtoms = DerivedLedger.BoundAtomsFor(actorKey);
            var inputs = actor.Setup.HubInputs;
            if (liveAtoms.Count > 0)
            {
                var originalAtoms = inputs?.BoundAtoms ?? Array.Empty<BoundDerivedAtom>();
                inputs = (inputs ?? new BattleHubInputs()) with
                {
                    BoundAtoms = originalAtoms.Concat(liveAtoms).ToArray(),
                };
            }

            var resolved = BattleHubCompose.Resolve(actor.Setup with { HubInputs = inputs });
            var bonusDefense = resolved.AppliedCombat.DefenseFlat - resolved.RuntimePrimary.DefenseFlat;
            if (bonusDefense != 0)
            {
                resolved.Derived.Set(
                    DerivedStatChannels.CombatDefenseOmni,
                    resolved.Derived.Get(DerivedStatChannels.CombatDefenseOmni) + bonusDefense);
            }
            return resolved.Derived;
        }

        readonly List<ShieldEventRec> _shieldEventScratch = new();
        readonly Dictionary<string, IReadOnlyList<CompiledAction>> _heldActions = new(StringComparer.Ordinal);

        /// <summary>
        /// combat-ai `resolvable-here` (CAI1.12, spec-resolvable-here.md §2): each held action's effect
        /// footprint, keyed by actionId, computed once at setup (see the constructor). Read with
        /// <see cref="ResolvableHere.Of"/> / <see cref="ResolvableHere.Veto"/>. No policy consumes it in
        /// this commit — `auto-policy-switch` (module 14) is the first — which is what keeps the
        /// refactor byte-identical.
        /// </summary>
        public readonly IReadOnlyDictionary<string, ActionEffectFootprint> EffectFootprints;

        /// <summary>aura-skill T3 (audit D3): equipped-action ids that could not be resolved against
        /// the supplied <see cref="ActionCatalog"/> — the actor degrades to the basic-attack fallback
        /// instead of failing the whole battle. Empty on every setup a golden has ever blessed.</summary>
        public readonly List<string> Warnings = new();

        /// <summary>
        /// base-defense siege-board (spec-siege-board.md §4): null for every caller that does not
        /// supply one, which is every caller until siege-resolver. This is what keeps the module
        /// golden-free — a field nothing sets changes no serialized bytes and no code path.
        /// </summary>
        readonly BoardState? _board;

        /// <summary>base-defense `siege-positions` §3: null for every caller without a board (every
        /// caller until this module wires siege battles through one) — the value `BattleEngine.Resolve`'s
        /// round loop passes to `Status.Tick`'s own optional trailing `board` parameter.
        /// status-rail C2: refreshed before each status pulse so moves/deaths update contagion geometry.</summary>
        public Combat.BoardSnapshot? CombatBoardSnapshot { get; private set; }

        /// <summary>A22 (spec-action-resolution-by-category.md §1): the constructor already received
        /// this — captured only inside the `rungOf` closure below (`:493`), never kept as a field.
        /// `ApplyBasicAttack` needs it too, to resolve `envelope.ActionId`'s own `Category` and branch
        /// resolution shape — the reason this module exists at all.</summary>
        public ActionCatalog? ActionCatalog { get; }

        /// <summary>`ActionStockCommit`'s own ledger (spec-siege-construction.md §11 / action-program's
        /// "ActionStockCommit.TryCommit has ZERO production callers" bug): defaults to
        /// <see cref="NoStockLedger"/>, matching `ConstructionActivation.Fire`'s identical
        /// "unwired means safe, not permissive" posture — every existing caller (no `stockLedger`
        /// argument) is unaffected, since every action shipped today compiles zero `StockDemands`.
        /// </summary>
        public IStockLedger StockLedger { get; }

        /// <summary>ST2 contract 4: stored so the `EffectiveRungOf` instance resolver can read them —
        /// constructor parameters, like the spec's own plan, not a second copy of anything.</summary>
        readonly Func<string, UnlockState>? _unlockStateFor;
        readonly UnlockTuning? _unlockTuning;

        public BattleRunState(BattleSetup setup, ulong seed, Timeline.BattleTrace? trace,
            Action<BattleEffectHost>? onEffectHostReady, ActionCatalog? actionCatalog = null,
            IContainerEffectResolver? containerResolver = null, BoardState? board = null,
            Func<string, UnlockState>? unlockStateFor = null, UnlockTuning? unlockTuning = null,
            IReadOnlyList<RunnerBinding>? runnerBindings = null,
            IReadOnlySet<string>? containersWithRunnerCoverage = null,
            Func<string, IReadOnlyList<string>>? equipEffectIdsFor = null,
            AiTuning? aiTuning = null, IStockLedger? stockLedger = null, Func<long, int>? roundOf = null,
            IStanceCheck? stance = null)
        {
            Trace = trace;
            _board = board;
            ActionCatalog = actionCatalog;
            StockLedger = stockLedger ?? NoStockLedger.Instance;
            _unlockStateFor = unlockStateFor;
            _unlockTuning = unlockTuning;

            // combat-ai `stance-wiring` (CAI3.1): the seam's WRITER. `Stance` keeps its default
            // (`NoStanceHeld.Instance`, the class's one literal, pinned by `StanceSeamTests`), and this
            // is the only assignment site — without it the property CAI3.1 landed had a reader and no
            // writer at all, so no caller (production or test) could ever supply a stance and gate 0 was
            // inert by construction rather than by content. `BattleEngine.Resolve` forwards its own
            // trailing optional here, so every existing caller is byte-identical; the first real
            // production supplier is `StanceRuntime` once `ActionRow` can say which action releases a
            // stance (the `action` program's A8), which is why the seam ships before its runtime.
            if (stance is not null) Stance = stance;

            InitiativeRng = SeededRng.DeriveStream(seed, "initiative");
            ICombatRng critRng = new SeededRngCombatAdapter(SeededRng.DeriveStream(seed, "crit"));
            if (trace != null) critRng = trace.WrapCombat("crit", critRng);
            CritRng = critRng;
            EssenceRng = SeededRng.DeriveStream(seed, "essence");
            // Wave E1: riders draw from their OWN stream, never from "status". The status stream is
            // already the contagion-spread stream, and sharing it would make every rider content
            // change a full-battle butterfly -- the audit fix this wave's spec names explicitly, and
            // the same one-system-one-stream rule `essence` above already follows.
            RidersRng = SeededRng.DeriveStream(seed, "riders");
            CaptureRng = SeededRng.DeriveStream(seed, "capture");
            StatusRng = new BattleStatusRng(seed, trace);
            Calculator = new OverlayCombatCalculator();

            // Stable ordered state — never dictionary-enumerated (determinism discipline).
            Actors = setup.Squad.Select((a, i) => new ActorState(a, i))
                .Concat(setup.Wave.Select((a, i) => new ActorState(a, i)))
                .ToList();
            ByKey = new Dictionary<string, ActorState>(StringComparer.Ordinal);
            foreach (var a in Actors)
                ByKey[a.Setup.Key] = a;
            DerivedLedger.UseHubResolver(ResolveLiveDerivedThroughHub);

            // Battle-local effect stack: funnel → FA10 sink over engine state; statuses over the
            // composed derived profiles; the clock is the synthetic round clock.
            Host = new BattleEffectHost(key => ByKey.TryGetValue(key, out var a) ? a : null, seed);
            T0 = Host.Clock.UtcNow;

            // A25 (battle-runner-path-integration): built here, inside the constructor, rather than
            // via `onEffectHostReady` -- that callback only receives `Host`, and the Secondary
            // runner's own `nowMs` reader must close over THIS instance's own `NowTick` field (a
            // frozen clock silently breaks every ICD gate, see UseRunner's own doc comment). `this` is
            // valid throughout a constructor body, so the closure below is correct despite `NowTick`
            // not yet having a meaningful value at construction time -- it is read lazily, per event,
            // never at wiring time.
            if (runnerBindings is { Count: > 0 })
                Host.UseRunner(runnerBindings, seed, () => NowTick);
            Status = new StatusRuntime(StatusCatalogHub.Current,
                (ptr, attackerLess) => attackerLess || ptr == null || !ByKey.TryGetValue(ptr, out var a)
                    ? ActorDerivedSnapshot.AttackerLess()
                    : a.Derived);

            // status-rail B2: project status-instance StatMods into the battle ledger (lawn does this
            // in EffectRuntime.OnApplied). Distinct from FA1 EffectActions.ModifyStat.
            Status.OnApplied = inst =>
            {
                if (inst.StatMods.Count == 0) return;
                var sourceId = StatusStatPayload.SourceIdOf(inst);
                var touchesDefense = false;
                foreach (var mod in StatusStatPayload.ToModifiers(inst))
                {
                    // W6 (battle-derived-wire T5, the W4 decision): `combat.*` channels are FlatSum in
                    // the registry, and `BattleDerivedModifierLedger` is battle's per-round writer for
                    // them -- routing them to the PRIMARY phased ledger left them stored and never
                    // read. This is the production invoker of the `Host.AddDerivedContribution` seam.
                    // Only `Flat` contributes, matching `DerivedComposer.ComposeChannel`'s own FlatSum
                    // rule (`more` is already refused at parse for a derived channel).
                    if (DerivedStatChannels.IsCombatChannel(mod.Channel))
                    {
                        if (mod.Op == Stats.ModifierOp.Flat)
                            Host.AddDerivedContribution?.Invoke(inst.HostPtr, mod.Channel, sourceId, mod.Value);
                        continue;
                    }

                    Ledger.Add(inst.HostPtr, mod.Channel, sourceId, mod);
                    if (string.Equals(mod.Channel, BattleStatModifierLedger.DefenseChannel, StringComparison.Ordinal))
                        touchesDefense = true;
                }
                // W11 (battle-derived-wire T6): a status's own `defense` mod was stored here and never
                // read -- only the stat.modify executor pushed it into `Derived`, and its absolute
                // write was itself overwritten by the per-round `DerivedLedger.Recompose`. Push
                // through the SAME writer, so there is exactly one gate on `combat.defense.omni`.
                if (touchesDefense && ByKey.TryGetValue(inst.HostPtr, out var owner))
                    BattleStatModifierLedger.PushDefenseToDerived(
                        inst.HostPtr, Ledger, DerivedLedger, owner);
            };
            Status.OnEnded = inst =>
            {
                if (inst.StatMods.Count == 0) return;
                var sourceId = StatusStatPayload.SourceIdOf(inst);
                var touchesDefense = false;
                foreach (var mod in StatusStatPayload.ToModifiers(inst))
                {
                    if (DerivedStatChannels.IsCombatChannel(mod.Channel)) continue;
                    if (string.Equals(mod.Channel, BattleStatModifierLedger.DefenseChannel, StringComparison.Ordinal))
                        touchesDefense = true;
                }
                Ledger.RemoveBySource(inst.HostPtr, sourceId);
                // W6: a `combat.*` contribution must not outlive its status -- withdraw from the
                // derived ledger too (the emptied channel is still visited once more by Recompose, so
                // the live value falls back to the frozen base).
                DerivedLedger.RemoveBySource(inst.HostPtr, sourceId);
                // Withdraw through the same gate: the removed source's contribution falls back to the
                // base (or to whatever other sources remain) on the very next read.
                if (touchesDefense && ByKey.TryGetValue(inst.HostPtr, out var owner))
                    BattleStatModifierLedger.PushDefenseToDerived(
                        inst.HostPtr, Ledger, DerivedLedger, owner);
            };

            // Shield stack (battle-adoption): battle-local runtime + gate; every HP delta goes
            // through the shared pipeline so the one-key discipline holds (single FA10 slot per
            // actor per window) and shields absorb before HP — overlay-identical semantics.
            Shields = new ShieldRuntime();
            // D1/D14 (solid-remediation T2.5): named, because CombatMath, ShieldGate and the element
            // payload fallback must all resolve an actor the SAME way. EffectRuntime's own comment for
            // the injector wiring says exactly this -- "same resolve as combat" -- and building a
            // second closure here would be two rules for who an actor is.
            CombatActorResolve resolveActor = (ptr, attackerLess) =>
                attackerLess || ptr == null || !ByKey.TryGetValue(ptr, out var a)
                    ? CombatActorSnapshot.AttackerLess()
                    : new CombatActorSnapshot(a.Derived, a.ElementTypes);
            _resolveActor = resolveActor;

            ShieldGate = new ShieldGate(Shields, resolveActor);
            HpSink = new FunnelHpDeltaSink(Host.Funnel, Host.ProjectRetainedDamage);

            // T14: the grant path. `ExecGrantShield` requires `Bag.ShieldGate`, which neither
            // `BattleEffectHost` nor `SimEffectHost` ever set (AtomKindRegistry.cs's shield.grant D6
            // comment) — wired here, to the SAME gate ordinary attacks already absorb through, so a
            // granted shield and a swing-dealt hit share one shield stack rather than two.
            Host.Bag.ShieldGate = ShieldGate;

            // D1 (solid-remediation T2.5): battle's bag never set CombatMath, so
            // CombatDamageDispatcher fell back to PassThroughCombatMath and every effect-driven hit in
            // a battle -- DoT tick, on-hit rider, atom damage -- applied its authored number verbatim:
            // no hit roll, crit, element matchup, penetration, parry or block. Battle's BASIC attack
            // always used the resolver; only the effect path did not.
            //
            // The line directly above records the identical defect, already found and fixed once for
            // shields: neither BattleEffectHost nor SimEffectHost ever set Bag.ShieldGate either, so a
            // granted shield and a swing-dealt hit landed on two separate stacks. Same shape, one
            // property along.
            //
            // ActorResolve is wired in the same breath and is not optional here: OverlayCombatMath
            // resolves both sides through it, and EffectBag's own owner-element fallback (T2.4) needs
            // it to know what the acting actor is made of. Wiring CombatMath alone was MEASURED to move
            // 0 of 13,943 tests -- Finalize returns the amount unchanged on an empty payload, so
            // without the payload this whole change is a no-op that looks like a fix.
            // Its OWN seeded stream, derived from the battle seed like every other system here.
            // Bag.CombatRng defaults to an unseeded `SeededCombatRng(42)`, which has been harmless only
            // because CombatMath was null and nothing ever rolled through it. Wiring CombatMath makes
            // that default load-bearing, and a battle whose hit/crit/parry rolls come from a constant
            // unrelated to its seed cannot be replayed -- responsibility 19 in the battle-engine
            // register, and the same one-system-one-stream rule the `riders` stream above already
            // states ("riders draw from their OWN stream, never from status").
            ICombatRng effectCombatRng = new SeededRngCombatAdapter(SeededRng.DeriveStream(seed, "effect-combat"));
            if (trace != null) effectCombatRng = trace.WrapCombat("effect-combat", effectCombatRng);

            Host.Bag.ActorResolve = resolveActor;
            Host.Bag.CombatRng = effectCombatRng;
            Host.Bag.CombatMath = OverlayCombatMath.Create(resolveActor, rng: effectCombatRng);

            // base-defense `siege-positions` §2-3: assigned once, only for a battle that HAS a board
            // (whoever constructs the BoardState is responsible for having placed actors onto it
            // before calling BattleEngine.Resolve — this module does not place them itself, see
            // Board/Placement.cs's own doc comment for why). `Host.Bag.BoardSnapshot` is left at its
            // default `BoardSnapshot.Empty` otherwise, which is every existing caller's exact current
            // behaviour (this line does not even run for them). `CombatBoardSnapshot` is the SAME
            // value but genuinely nullable (Empty is not null) — §3's `Status.Tick` needs true `null`
            // for "no board", not an empty-but-non-null snapshot, to stay byte-identical for every
            // existing battle.
            if (_board is not null)
            {
                CombatBoardSnapshot = Board.BoardSnapshotAdapter.ToCombatSnapshot(this);
                Host.Bag.BoardSnapshot = CombatBoardSnapshot;
            }

            // A18c (spec-battle-resource-shield-grants.md §1): the SAME shape as ShieldGate above,
            // one line down. `EffectBag.cs:439`'s DoT/contagion piggyback (StatusEffectBridge.TryApplyFromGrant,
            // called from the ApplyResourceDelta branch of FireGrant) is gated on Bag.Status/Bag.StatusRng
            // being set -- neither BattleEffectHost nor SimEffectHost ever did, the exact same gap
            // ShieldGate had. Wired to the SAME StatusRuntime/stream the round loop's own Status.Tick
            // already uses -- one status system, one "status" RNG stream, every application path,
            // never a second instance a grant-applied DoT would roll against differently than a
            // scripted or pulse-delivered one.
            Host.Bag.Status = Status;
            Host.Bag.StatusRng = StatusRng;

            // A18d (spec-battle-status-apply.md §1): the SAME shape, one level over -- BattleEffectSink
            // (not Bag) needs its own Status/StatusRng reference to call StatusRuntime.Apply directly
            // for a standalone status.apply (FA2) plan item, forwarded through Host's own settable
            // properties since BattleEffectSink is private to BattleEffectHost.
            Host.Status = Status;
            Host.StatusRng = StatusRng;

            // A18e (spec-battle-live-stat-modifiers.md §3): the same forwarding shape, one more
            // property. ActorState already implements IBattleStatTarget (its own Derived/BaselineDefense
            // are already public), so the SAME lambda shape resolveActor (below) already uses works
            // here too -- just returning the wider interface.
            Host.Ledger = Ledger;
            // W11 (battle-derived-wire T6): the `combat.*` ledger, forwarded so `ExecModifyStat`'s
            // `defense` branch can push through the SAME single writer the status handlers below use.
            Host.DerivedLedger = DerivedLedger;
            Host.ResolveStatTarget = key => ByKey.TryGetValue(key, out var a) ? a : null;

            // G2 (spec-mechanism-wiring.md §4.2): forwards straight to DerivedLedger.Add, the one
            // write this ledger has — a live mid-battle trigger (T13's still-unbuilt aura toggle, or a
            // test simulating one) calls this through `onEffectHostReady` the same way every other
            // Battle-adoption trigger reaches this host's own collaborators.
            Host.AddDerivedContribution = DerivedLedger.Add;
            onEffectHostReady?.Invoke(Host);

            // passive-tree G1 (spec-gate-counters.md §7 P1, R9): the pulse site — every status
            // DoT/HoT delta Status.Tick delivers reaches HP through exactly this sink, so this is the
            // one call P1's defaulted `origin` parameter existed for. Deferred while BattleEngine.cs/
            // BattleRunState.cs were under another session's concurrent edit; both are clean now.
            PulseSink = new BattlePulseSink((hostPtr, attackerPtr, amount, effectId, components) =>
                ByKey.TryGetValue(hostPtr, out var owner)
                    ? ApplyHp(owner, ResolvePulseAmount(hostPtr, attackerPtr, amount, components),
                              effectId, components,
                              attackerPtr != null && ByKey.TryGetValue(attackerPtr, out var src) ? src : null,
                              origin: DamageOrigin.StatusPulse)
                    : new DamageApplyResult(DamageApplyOutcome.SinkRefused, 0, 0));

            foreach (var a in Actors)
            {
                Events.Add(new BattleEventRec(0, BattleEventKinds.Spawn, a.Setup.Key, a.Setup.TypeId, a.Setup.Side));
                RaiseTrigger(Effects.Atoms.AtomTriggers.OnSpawn, a.Setup.Key);
            }

            // Innate shields (battle-adoption): direct apply at setup — snapshots composed in the
            // ActorState ctor, so the shield spec's capacity barrier is satisfied by construction.
            // B17 (battle-timeline-todo.md): `DurationTicks` is now TRUE ms, passed straight
            // through with no round-ceiling — "battle ticks are rounds" stopped being true the
            // moment `ShieldRuntime.Tick` started being called with `roundClock.Now` (below)
            // instead of a round counter. Round-ceiling meant a 100 ms innate shield silently lived
            // a full 1000 ms round; the true value now expires exactly when authored.
            foreach (var a in Actors)
            {
                if (a.Setup.InnateShield is not { } innate) continue;
                Shields.Apply(new ShieldGrant
                {
                    OwnerKey = Contracts.EffectOwnerKeys.Entity(a.Setup.Key),
                    SourceId = "innate:" + a.Setup.TypeId,
                    Element = innate.Element,
                    BaseHp = innate.BaseHp,
                    Priority = innate.Priority,
                    DurationTicks = innate.DurationMs,
                    RefillOnMerge = false,
                    IsInnate = true
                }, a.Derived, nowTick: 0);
            }

            // Initial statuses land attacker-less at t0 (trait/attack riders reuse this path later).
            // Scripted setup statuses apply deterministically — the L2b evaluator still blocks them
            // on immunity/potency floor (resist channels), but the apply roll is bypassed (0.0 roll).
            var scriptedApplyRng = new FixedStatusRng(0.0);
            foreach (var a in Actors)
            {
                foreach (var spec in a.Setup.InitialStatuses)
                {
                    Status.Apply(new StatusApplyInput(
                        spec.StatusId,
                        HostPtr: a.Setup.Key,
                        AttackerPtr: null,
                        GrantId: "battle:init:" + a.Setup.Key + ":" + spec.StatusId,
                        BaseMagnitude: spec.MagnitudePerPulse,
                        BaseDuration: spec.DurationMs,
                        PeriodMs: spec.PeriodMs,
                        DurationMs: spec.DurationMs,
                        GrantChance: spec.GrantChanceMilli / 1000.0,
                        EffectId: "battle.status." + spec.StatusId,
                        PluginId: "battle",
                        AttackerLess: true), scriptedApplyRng, T0);
                }
            }

            // aura-skill T12 (Gate B): "an aura is on" becomes "a channel has a value," via the T4
            // recompose seam. Delivered once, at construction — a live mid-match toggle is T13's own
            // job, not this one's. Friendly = same Setup.Side as the aura's own CommanderSide; battle's
            // squad/wave partition already IS the own-side/enemy-side split (no oracle needed here —
            // T21a's MechanicalOwnSideOracle answers a different, live-lawn question).
            foreach (var aura in setup.ActiveAuras)
            {
                foreach (var a in Actors)
                {
                    if (a.Setup.Side != aura.CommanderSide) continue;
                    DerivedLedger.Add(a.Setup.Key, aura.TargetChannel, aura.SourceId, aura.Value);
                    RecomposeDerived(a.Setup.Key);
                }
            }

            // A17 (spec-action-selection-adoption.md §2): compile each actor's loadout ONCE, here —
            // never per decision, matching HeldActionsOf's own documented contract that the AI relies
            // on for its "Reads scales with targets, not actions" acceptance bar. Null or empty
            // EquippedActionIds (BattleModels.cs's own doc: "null when the caller has no
            // action/loadout system to consult") falls back to the single hand-built basic attack —
            // "no loadout" must still be a legal, single-action decision, never ActionIntent.None by
            // construction.
            //
            // aura-skill T3 (audit D3): a non-empty list that CANNOT resolve — no ActionCatalog
            // supplied, or an id the catalog doesn't have — used to throw and fail the whole battle,
            // which meant the first authored Skill grant broke every web battle AND poisoned any
            // already-stored BattleSetup log row (it re-threw on every replay, forever). There is no
            // production action-authoring path yet (aura-equip-path, unspecced): degrading to "no
            // equipped actions" + a named warning is the honest behavior for content that cannot
            // exist in production today, not a masked bug — T19 wires the real ActionCatalog and this
            // degrade path stops firing for any actor whose loadout it can actually resolve.
            var costsByActionId = new Dictionary<string, IReadOnlyList<ActionCostRow>>(StringComparer.Ordinal);
            foreach (var a in Actors)
            {
                var ids = a.Setup.EquippedActionIds;
                IReadOnlyList<CompiledAction> held;
                if (a.Setup.Kind == CombatantKind.Structure && (ids is null || ids.Count == 0))
                {
                    // base-defense `combatant-kind` §5: a structure with no actions has nothing to do
                    // — it does not fall back to a basic attack. The fallback below exists so an
                    // ANIMATE actor is never inert; a wall being inert is the point. This also keeps
                    // "garrisoning a wall grants nothing" true in HeldActionsOf's union below, since
                    // the empty list is what gets lent.
                    held = Array.Empty<CompiledAction>();
                }
                else if (ids is null || ids.Count == 0)
                {
                    held = new[] { BasicAttackCompiled };
                }
                else if (actionCatalog is null)
                {
                    Warnings.Add(
                        $"Actor '{a.Setup.Key}' has {ids.Count} equipped action id(s) but no ActionCatalog " +
                        "was supplied to resolve them; falling back to the basic attack.");
                    held = new[] { BasicAttackCompiled };
                }
                else
                {
                    var list = new List<CompiledAction>(ids.Count);
                    var unresolved = new List<string>();
                    foreach (var id in ids)
                    {
                        var compiled = actionCatalog.Get(id);
                        if (compiled is null) unresolved.Add(id);
                        else list.Add(compiled);
                    }

                    if (unresolved.Count > 0)
                    {
                        Warnings.Add(
                            $"Actor '{a.Setup.Key}' has equipped action id(s) [{string.Join(", ", unresolved)}] " +
                            "not in the supplied ActionCatalog; falling back to the basic attack.");
                        held = new[] { BasicAttackCompiled };
                    }
                    else
                    {
                        list.Sort(ActionTagPreference.Compare);
                        held = list;
                    }
                }

                // base-defense `siege-construction`/`siege-ai` (2026-09-07, MAJOR finding this
                // session): purely additive, appended AFTER `held` is fully resolved above -- an
                // actor's basic-attack fallback (or its real equipped loadout) is completely
                // untouched; this only adds to it, never replaces it. `null`/empty for every actor
                // outside `DistrictAssaultResolver`'s own siege setup -- the exact byte-identical-to-
                // today default for every other battle mode.
                if (a.Setup.AdditionalHeldActions is { Count: > 0 } additional)
                    held = held.Concat(additional).ToList();

                _heldActions[a.Setup.Key] = held;
                BindContainers(a, held, containerResolver, containersWithRunnerCoverage);
                BindEquip(a, equipEffectIdsFor);

                // A19 (T56.1): collect real cost rows for every action actually reachable in THIS
                // battle -- ActionCatalog exposes no "all actions" enumerator (only Get(id)/Count),
                // so building from each actor's own resolved `held` list is both sufficient (nothing
                // outside a held loadout can ever be committed) and simpler than inventing one.
                foreach (var action in held)
                {
                    if (action.Costs.Count == 0 || costsByActionId.ContainsKey(action.ActionId)) continue;
                    var rows = new List<ActionCostRow>(action.Costs.Count);
                    foreach (var cost in action.Costs)
                        rows.Add(new ActionCostRow(action.ActionId, cost.ResourceId, cost.Amount, cost.When));
                    costsByActionId[action.ActionId] = rows;
                }
            }

            // combat-ai `resolvable-here` (CAI1.12, spec-resolvable-here.md §2): the footprint table is
            // built ONCE here, at setup, from the same action -> ContainerId -> effectIds walk
            // BindContainers just did (and the same `Host.Bag` catalog those grants landed in) -- never
            // per decision, and never keyed by actor, so an actor entering a state cannot move its key
            // set. `EffectFootprintTable.Build` dedups by actionId, so two actors holding the same
            // action cost one walk. No policy reads it yet (`auto-policy-switch`, module 14, is the
            // first consumer), which is what keeps this commit byte-identical.
            EffectFootprints = BuildEffectFootprints(containerResolver);

            CostLedger = new CostLedger(
                costsByActionId,
                poolsFor: key => ResourcePools.GetOrCreate(key, ByKey[key].Derived, NowTick),
                derivedFor: key => ByKey[key].Derived,
                rungOf: EffectiveRungOf,
                nowTick: () => NowTick);

            // base-defense siege-ai: built AFTER Cooldowns/CostLedger exist (both are constructor-scope
            // fields SiegeAiIntentSource reads as `cooldowns`/`affordability`) and using `this` as the
            // live `IBattleView` -- valid here for the same reason Host.UseRunner's own closure over
            // `this`/NowTick above is: SiegeAiIntentSource only reads the view lazily, per ChooseTarget
            // call, never during construction. The run's own `Stance` seam (default: no stance held)/a
            // fresh RetargetLedger match StubIntentSource's own and 17.8's own "one ledger per battle"
            // shape respectively.
            //
            // combat-ai `profile-schema` CAI1.8 (H7): the scorer's weights now come from
            // CombatAiProfilePolicy.For(AiPlace.Siege, AiRole.Default) -- combat-ai.v1.json's own
            // "siege/default" row carries the SAME ten values siege.v1.json's ai block shipped, at a
            // new address -- never from SiegeTuningPolicy.Ai, which lost its scoring fields when they
            // moved (aiTuning, the narrowed four-member geometry/dead record, still supplies
            // ObjectiveReferenceDistanceCells/ThreatRadiusCells).
            if (aiTuning != null)
            {
                var siegeProfile = FusionRpg.Core.Actions.Ai.CombatAiProfilePolicy.For(
                    FusionRpg.Core.Actions.Ai.AiPlace.Siege, FusionRpg.Core.Actions.Ai.AiRole.Default);
                DefaultAiIntentSource = new SiegeAiIntentSource(
                    TraitView, Cooldowns, Stance, CostLedger,
                    siegeProfile.Scoring.ToScoringWeights(), siegeProfile.AntiRepeat.RetargetLatencyTicks,
                    aiTuning, roundOf ?? (tick => (int)tick), retarget: new RetargetLedger(), trace: trace,
                    // combat-ai `core-scorer` CAI1.5 (2026-09-20): the SAME instance rung resolver
                    // CostLedger's own `rungOf` reads two lines above -- one rung authority, not a
                    // second guess at what "effective" means.
                    effectiveRungOf: EffectiveRungOf);
            }
        }

        /// <summary>
        /// ST2 (spec-holder-rung-pricing.md contract 4): the ONE instance rung resolver shared by
        /// `CostLedger` and (via action-enrich's `action-base`) the hit. Created here — not in
        /// `action-enrich` — because the band half of the contract (this module's `CompiledAction`
        /// band, read in the method body below) ships in this task. `AE1.2` adds only its held-row
        /// fallback line to this method's fallback; it never creates a second resolver.
        /// </summary>
        public int EffectiveRungOf(string actorKey, string actionId) =>
            EffectiveRungOf(actorKey, actionId, ActionCatalog, _unlockStateFor, _unlockTuning);

        /// <summary>ST2 contract 4: the same held row the actor actually swung, never a catalog
        /// lookup. Covers the basic attack (hand-built, in no catalog), catalog-less battles (every
        /// equipped id falls back to it), siege `AdditionalHeldActions` (compiled but uncatalogued)
        /// and the garrison-lent union — `HeldActionsOf` already covers all four.</summary>
        public CompiledAction? HeldActionOf(string actorKey, string actionId)
        {
            foreach (var held in HeldActionsOf(actorKey))
                if (held.ActionId == actionId) return held;
            return null;
        }

        /// <summary>
        /// A23 (spec-cost-scaling-holder-rung.md §2): `CostLedger` must scale by the HOLDER's
        /// `effectiveRung` (progression-derived), never the content's authored `Rung`
        /// (spec-rung-semantics.md §3.1 — `StructureBudgetGuard` is the reader that wants the authored
        /// value; this ledger is not). A match in the actor's own <see cref="UnlockState.Held"/> list
        /// resolves through <see cref="UnlockLadder.EffectiveRung"/>; no match (every intrinsic/basic
        /// action, and every caller that supplies no `unlockStateFor` at all — the
        /// exact byte-identical-to-today default) falls back to the SWUNG row's authored `Rung`
        /// (AE1.2: `action-base`'s §Which-rung rule; a held row absent from the catalog — siege
        /// `AdditionalHeldActions`, a lent garrison action — resolves its real rung instead of 0,
        /// byte-identical to the catalog lookup for every catalog action, whose held rows ARE the
        /// catalog's; the catalog lookup stays as the last resort for an action this actor does not
        /// hold at all — the pre-AE1.2 behavior, unchanged).
        /// </summary>
        /// <summary>combat-ai `lawn-cost-authority` CAI4.4: the body moved to
        /// <see cref="EffectiveRungResolver.Resolve"/> so the lawn's cost ledger can share it instead of
        /// carrying its own `rungOf` constant. This is a delegation with the SAME inputs and
        /// `floorWhenUnknown: 0` — battle's own `?? 0` tail — so it is byte-identical by construction,
        /// and the battle goldens are the proof: a moved hash means the extraction changed behaviour.</summary>
        int EffectiveRungOf(string actorKey, string actionId, ActionCatalog? actionCatalog,
            Func<string, UnlockState>? unlockStateFor, UnlockTuning? unlockTuning) =>
            EffectiveRungResolver.Resolve(
                actorKey, actionId, actionCatalog, unlockStateFor, unlockTuning,
                heldActionOf: HeldActionOf, floorWhenUnknown: 0);

        /// <summary>
        /// A18a (spec-action-container-binding.md §2): bind each held action's real atom container,
        /// once, alongside the loadout compile above — never per decision, matching the same
        /// "compile once" discipline A17's own loadout loop already established. A `ContainerId` a
        /// non-empty loadout resolves to (basic attack's own `""` always skips) MUST resolve against a
        /// real, supplied resolver — loud failure on a missing container or an empty result, never a
        /// silent skip, matching this codebase's standing "loud validation over silent corruption"
        /// stance (the same shape the `ActionCatalog` check just above already uses).
        ///
        /// <para>A25 (battle-runner-path-integration): a container whose atoms are ENTIRELY
        /// Runner-path has, correctly, zero Compiled-path effect ids — `IContainerEffectResolver`'s own
        /// contract never covered the Runner path (A18a scoped it to Defs only). Found empirically, not
        /// designed for up front: a real end-to-end test with a purely-Runner-path action threw here
        /// even though `Host.Runner` was correctly wired, because this check could not tell "genuinely
        /// unresolvable" apart from "resolved elsewhere, via the runner." <paramref
        /// name="containersWithRunnerCoverage"/> (built the same pass as `runnerBindings`, from the
        /// SAME per-container loop, never string-parsed back out of a binding id) is what makes that
        /// distinction — a container in this set is real, known content, just not Compiled-path at
        /// all, so the throw below no longer fires for it.</para>
        /// </summary>
        /// <summary>
        /// combat-ai `resolvable-here` (CAI1.12, spec-resolvable-here.md §2/§5): the place's own
        /// declaration (`BattleEffectHost`'s dispatch table + raised-trigger array — never an authored
        /// list), crossed with every action any actor holds in this battle. A runner-covered container
        /// has no bag def, so it contributes zero units and reads `Full` — the same fail-open posture an
        /// uncategorised action gets, and honest: there is nothing for this filter to count.
        /// </summary>
        IReadOnlyDictionary<string, ActionEffectFootprint> BuildEffectFootprints(IContainerEffectResolver? containerResolver)
        {
            var place = PlaceExecutionProfile.FromSink(Host, RuntimeId.Battle, BattleEffectHost.RaisedTriggers);
            var all = new List<CompiledAction>(_heldActions.Count * 2);
            foreach (var held in _heldActions.Values) all.AddRange(held);

            return EffectFootprintTable.Build(
                all,
                containerId => containerResolver?.EffectIdsFor(containerId) ?? Array.Empty<string>(),
                effectId => Host.Bag.Catalog.Get(effectId),
                in place);
        }

        void BindContainers(ActorState a, IReadOnlyList<CompiledAction> held, IContainerEffectResolver? containerResolver,
            IReadOnlySet<string>? containersWithRunnerCoverage)
        {
            foreach (var action in held)
            {
                if (string.IsNullOrEmpty(action.ContainerId)) continue;

                if (containerResolver is null && containersWithRunnerCoverage?.Contains(action.ContainerId) != true)
                    throw new ArgumentException(
                        $"Actor '{a.Setup.Key}' holds action '{action.ActionId}' with container '{action.ContainerId}' but no IContainerEffectResolver was supplied to resolve it.",
                        nameof(containerResolver));

                var effectIds = containerResolver?.EffectIdsFor(action.ContainerId) ?? Array.Empty<string>();
                if (effectIds.Count == 0 && containersWithRunnerCoverage?.Contains(action.ContainerId) != true)
                    throw new ArgumentException(
                        $"Actor '{a.Setup.Key}' holds action '{action.ActionId}' with container '{action.ContainerId}', which the supplied IContainerEffectResolver could not resolve.",
                        nameof(containerResolver));

                foreach (var effectId in effectIds)
                {
                    Host.Bag.Grant(new Contracts.EffectGrantDto
                    {
                        GrantId = $"battle:{a.Setup.Key}:{action.ActionId}:{effectId}",
                        EffectId = effectId,
                        OwnerKind = "entity",
                        OwnerKey = Contracts.EffectOwnerKeys.Entity(a.Setup.Key),
                        PluginId = "battle",
                        Priority = 0,
                    });
                }
            }
        }

        /// <summary>
        /// spec-equip-runtime.md's Battle-half amendment (2026-09-07): the SAME grant pattern
        /// <see cref="BindContainers"/> uses for a held action's container, applied to a specimen's
        /// equipped `stat.modify` atoms instead — <see cref="ActionContainerEffectResolverFactory.BuildEquip"/>
        /// already compiled them (Data layer, where `RpgStore.ResolveBindings` lives) and registered
        /// the resulting defs via the caller's own `onEffectHostReady`; this method's whole job is the
        /// per-actor grant, mirroring `BindContainers` line for line except the source of `effectIds`.
        /// A no-op when the actor carries no `SpecimenId` (every wave/enemy actor, every synthetic SIM
        /// actor) or no resolver was supplied (every caller that hasn't wired equip yet) — exactly as
        /// inert as `containerResolver` being null already is for an actor with no held actions.
        /// </summary>
        void BindEquip(ActorState a, Func<string, IReadOnlyList<string>>? equipEffectIdsFor)
        {
            if (equipEffectIdsFor is null) return;
            if (string.IsNullOrWhiteSpace(a.Setup.SpecimenId)) return;

            var effectIds = equipEffectIdsFor(a.Setup.SpecimenId!);
            foreach (var effectId in effectIds)
            {
                Host.Bag.Grant(new Contracts.EffectGrantDto
                {
                    GrantId = $"battle:{a.Setup.Key}:equip:{effectId}",
                    EffectId = effectId,
                    OwnerKind = "entity",
                    OwnerKey = Contracts.EffectOwnerKeys.Entity(a.Setup.Key),
                    PluginId = "battle",
                    Priority = 0,
                });
            }
        }

        // ---- IBattleView (A17): the read seam StubIntentSource is confined to — never a direct
        // read of Actors/ByKey from outside this class. PositionOf is null with no board (every
        // caller until siege-resolver), which is what makes NearestEnemy's own SourceOrder fallback
        // the live behavior today; a real board makes it return real positions (siege-board §4).
        /// <summary>
        /// combat-ai `delve-automated-wiring` CAI3.4: the actor's own side's downed members, EXCLUDING the
        /// deciding actor itself (`ally-downed` names allies, and a self-revive is not a thing the corpus
        /// can express). `ActorState.WentDowned` is the timeline FSM's `TurnState.Downed` transition, gated
        /// on `DownedOnDeplete` — so this is empty in every battle whose profile does not set that, which is
        /// every profile but `delve`, and byte-identical to <see cref="IBattleView"/>'s own default there.
        /// Iteration order is the actor list's, matching <see cref="LiveActorKeys"/>.
        /// </summary>
        public IReadOnlyList<string> DownedAllyKeysOf(string actorKey)
        {
            var downed = new List<string>();
            if (!ByKey.TryGetValue(actorKey, out var self)) return downed;

            foreach (var a in Actors)
                if (a.WentDowned
                    && !string.Equals(a.Setup.Key, actorKey, StringComparison.Ordinal)
                    && string.Equals(a.Setup.Side, self.Setup.Side, StringComparison.Ordinal))
                    downed.Add(a.Setup.Key);

            return downed;
        }

        public IReadOnlyList<string> LiveActorKeys
        {
            get
            {
                var live = new List<string>(Actors.Count);
                foreach (var a in Actors) if (a.Active) live.Add(a.Setup.Key);
                return live;
            }
        }

        public int SideOf(string actorKey) => ByKey[actorKey].Setup.Side == "squad" ? 0 : 1;

        public GridPos? PositionOf(string actorKey) =>
            _board is null ? null : _board.Positions.TryGetValue(actorKey, out var p) ? p : null;

        /// <summary>
        /// A9 `movement-actions` (spec-movement-actions.md §3): resolves `AnchorSource.ChosenCell` as
        /// "move toward the nearest living enemy" (owner-confirmed 2026-09-07 -- no spec anywhere
        /// resolved it before this). No board, no opposing actor with a real position, or already
        /// adjacent all resolve to zero cells moved -- never a throw, since none of those are a caller
        /// error for a movement action (the same "byte-identical when inert" posture <see cref="UseRunner"/>
        /// and every other A24/A25-era wiring this session added already follows).
        /// </summary>
        public int TryMoveTowardNearestEnemy(string actorKey, int maxCells)
        {
            if (_board is null) return 0;
            if (!_board.Positions.TryGetValue(actorKey, out var from)) return 0;

            var mySide = ByKey[actorKey].Setup.Side;
            GridPos? nearestPos = null;
            var nearestDistance = int.MaxValue;
            foreach (var candidate in Actors)
            {
                if (!candidate.Active || candidate.Setup.Side == mySide) continue;
                if (!_board.Positions.TryGetValue(candidate.Setup.Key, out var candidatePos)) continue;
                var d = GridDistance.Chebyshev(from, candidatePos);
                if (d < nearestDistance) { nearestDistance = d; nearestPos = candidatePos; }
            }
            if (nearestPos is not { } target) return 0;

            return MoveAction.MoveToward(_board, actorKey, target, maxCells);
        }

        /// <summary>
        /// base-defense `siege-ai` R3 (spec-siege-ai.md §4): "no target in reach -> path toward the
        /// objective using TerrainOnlyOccupancy." Deliberately NOT <see cref="MoveAction.MoveToward"/>'s
        /// own greedy Chebyshev step (that primitive's own doc comment: allies block it, which is
        /// exactly the "boxed in by my own units" failure R3 exists to avoid) -- this walks a REAL
        /// <see cref="BoardPathfinder"/> route instead, planned as if allies were not there
        /// (<see cref="TerrainOnlyOccupancy"/>), then executed one real step at a time
        /// (<see cref="BoardState.CanEnter"/>, the same instant-occupancy gate <see cref="MoveAction"/>
        /// itself uses) so a currently-occupied next cell simply stops the advance rather than
        /// skipping past it. "No path at all -> hold and defend, never a random move" (the spec's own
        /// words) is honored by construction: <see cref="BoardPathfinder.Find"/> returning `null` or the
        /// very first real step being blocked both return 0, the same "nothing happened" contract
        /// <see cref="TryMoveTowardNearestEnemy"/> already has.
        /// </summary>
        public int TryMoveTowardObjective(string actorKey, int maxCells)
        {
            if (_board is null) return 0;
            if (!_board.Positions.TryGetValue(actorKey, out var from)) return 0;
            if (ObjectivePositionOf(actorKey) is not { } objective || from == objective) return 0;

            var costs = new MoveCosts(
                SiegeTuningPolicy.MoveCostOpen, SiegeTuningPolicy.MoveCostRough, SiegeTuningPolicy.DiagonalSurcharge);
            var path = BoardPathfinder.Find(_board.Spec, new TerrainOnlyOccupancy(_board.Spec), from, objective, costs);
            if (path is null) return 0;

            var moved = 0;
            for (var i = 1; i < path.Steps.Count && moved < maxCells; i++)
            {
                var next = path.Steps[i];
                if (!_board.CanEnter(next)) break; // a real occupant stands here right now -- stop, do not skip past it
                _board.Move(actorKey, next);
                moved++;
            }
            return moved;
        }

        public EntityFacts FactsOf(string actorKey)
        {
            var a = ByKey[actorKey];
            // Bounded ratio: decimal keeps the integer truncation exact without overflowing the
            // intermediate per-mille multiply. The final narrowing is explicitly bounded to 0..1000.
            var hpMilli = a.MaxHp > 0
                ? (int)Math.Clamp((decimal)a.Hp * 1000m / a.MaxHp, 0m, 1000m)
                : 0;
            var elementId = a.Setup.ElementPrimary is { } e ? (int)e : 0;
            return new EntityFacts(
                Side: SideOf(actorKey), TypeId: a.Setup.TypeId, HpMilli: hpMilli, ElementId: elementId,
                Row: 0, Col: 0, IsMindControlled: false, IsKiller: false, StatusMask: 0);
        }

        /// <summary>
        /// base-defense `combatant-kind` §4: a garrisoned structure lends its actions to its occupant —
        /// the union of the occupant's own held actions and the structure it currently occupies, found
        /// by a linear scan of <see cref="Actors"/> for the one whose <c>GarrisonedBy</c> names this
        /// key (small counts, the same discipline <c>FindAdjacentWithTrait</c> already uses; there is
        /// no reverse index and none is needed at this scale). Byte-identical for every existing
        /// battle: <c>GarrisonedBy</c> is null on every actor there, so the scan never matches and this
        /// always returns exactly <c>own</c>.
        /// </summary>
        public IReadOnlyList<CompiledAction> HeldActionsOf(string actorKey)
        {
            var own = _heldActions.TryGetValue(actorKey, out var held) ? held : Array.Empty<CompiledAction>();
            var garrisonedStructureKey = FindGarrisonedStructureKey(actorKey);
            if (garrisonedStructureKey is null) return own;

            var lent = _heldActions.TryGetValue(garrisonedStructureKey, out var structureHeld)
                ? structureHeld : Array.Empty<CompiledAction>();
            if (lent.Count == 0) return own;
            var union = new List<CompiledAction>(own.Count + lent.Count);
            union.AddRange(own);
            union.AddRange(lent);
            return union;
        }

        /// <summary>
        /// base-defense `siege-ai` 17.9 (spec-siege-ai.md §5.20 rule 5): the SAME "which structure names
        /// this actor as its garrison" scan <see cref="HeldActionsOf"/> already performs, shared rather
        /// than duplicated — <see cref="IBattleView.GarrisonedStructureKeyOf"/>'s own real implementation.
        /// Returns the garrisoned structure's key, or `null` when `actorKey` garrisons nothing (every
        /// existing battle: `GarrisonedBy` is null on every actor, so this always returns `null`).
        /// </summary>
        string? FindGarrisonedStructureKey(string actorKey)
        {
            foreach (var a in Actors)
            {
                if (a.Setup.Kind != CombatantKind.Structure || a.Setup.GarrisonedBy != actorKey) continue;
                return a.Setup.Key;
            }
            return null;
        }

        /// <summary>
        /// base-defense `siege-ai` R3 (spec-siege-ai.md §4), `IBattleView`'s own real implementation.
        /// `null` when no siege context is wired (<see cref="BattleEffectHost.AttackerEdge"/> unset —
        /// every non-siege battle, and every siege battle before <c>DistrictAssaultResolver</c> sets it)
        /// or there is no board at all, both byte-identical to every battle before this task. `side ==
        /// "squad"` means attacker — <c>DistrictAssaultResolver.AttackerSide</c>'s own real convention,
        /// read directly rather than assumed, matching <see cref="SideOf"/>'s existing 0-means-squad
        /// mapping.
        /// </summary>
        public GridPos? ObjectivePositionOf(string actorKey)
        {
            if (Host.AttackerEdge is not { } attackerEdge) return null;
            if (_board is null) return null;
            var isAttacker = SideOf(actorKey) == 0;
            return DistrictLayout.ObjectivePositionFor(isAttacker, attackerEdge, _board.Spec.Rows);
        }

        public long? MaxHpOf(string actorKey) => ByKey[actorKey].MaxHp;

        // Reads the ai.aggression derived channel (DerivedStatChannels.cs H.9, resolved 2026-09-07).
        // No taunt/stealth/decoy status content exists anywhere in the game yet (repo-wide search,
        // 2026-09-07) -- ActorDerivedSnapshot.Get defaults an untouched channel to 0, so this returns
        // 0 (neutral) for every actor today, byte-for-byte identical to the previous hardcoded return.
        // A future taunt/stealth status would contribute to this channel (ActorDerivedSnapshot.OverlayAdd,
        // matching how a patron/commander aura already composes) rather than needing a second mechanism.
        public int AggressionOf(string actorKey) =>
            (int)Math.Round(ByKey[actorKey].Derived.Get(DerivedStatChannels.AiAggression));

        public string? GarrisonedStructureKeyOf(string actorKey) => FindGarrisonedStructureKey(actorKey);

        /// <summary>base-defense `siege-ai` (module 17): the SAME `Derived` snapshot `CostLedger`'s own
        /// `poolsFor`/`derivedFor` callbacks already read from `ByKey[key].Derived` (`:500-501` above) —
        /// exposed here, never recomputed, so a live scoring AI's hit-chance estimate reads the exact
        /// stats every other system in this class already does.</summary>
        public FusionRpg.Core.Stats.Derived.ActorDerivedSnapshot? DerivedOf(string actorKey) =>
            ByKey.TryGetValue(actorKey, out var a) ? a.Derived : null;


        /// <summary>
        /// D1 (`solid-remediation` T2.5): run a status pulse through the same combat resolver the rest
        /// of the engine uses, instead of applying its authored magnitude verbatim.
        ///
        /// <para><b>The defect this closes, precisely.</b> D1 names "DoT tick" first among the hits that
        /// bypassed the resolver, and wiring <c>Bag.CombatMath</c> did not reach them: a battle DoT
        /// never enters <c>CombatDamageDispatcher</c> at all. <c>BattlePulseSink</c> calls
        /// <c>DamageApplyPipeline.Apply</c> directly — <c>StatusEffectBridge</c>'s own comment says so
        /// — so the dispatcher's <c>math.Finalize</c> line was simply never on this path. The bag
        /// wiring fixed the container/action effect path; this fixes the pulse path.</para>
        ///
        /// <para><b>Why this is parity and not a design change.</b> The lawn's pulse sink
        /// (<c>StatusEffectBridge.PulseHp</c>) has always dispatched through the resolver, so a typed
        /// DoT on the lawn already contests accuracy, matchup and mitigation. Battle applying the raw
        /// number is exactly the "same authored content behaves differently by mode" shape the
        /// responsibility register calls rule 2.</para>
        ///
        /// <para>Inert wherever it was inert before: no <c>CombatMath</c>, no attacker, or an untyped
        /// status (which is every status shipped today) returns the authored amount unchanged, because
        /// <c>OverlayCombatMath.Finalize</c> passes an empty payload straight through.</para>
        /// </summary>
        long ResolvePulseAmount(string hostPtr, string? attackerPtr, long amount,
            Combat.Element.ElementPayloadComponent[] components)
        {
            if (Host.Bag.CombatMath is not { } math) return amount;
            if (amount >= 0) return amount;
            if (components.Length == 0) return amount;
            if (string.IsNullOrWhiteSpace(attackerPtr)) return amount;

            var packet = new Contracts.DamagePacket
            {
                ActorPtr = attackerPtr,
                SignedAmount = amount,
                ElementPayload = components
                    .Select(c => new Contracts.ElementPayloadComponentDto
                    {
                        Element = c.Element.ToElementId(),
                        Weight = c.Weight,
                    })
                    .ToList(),
            };

            return math.Finalize(amount, hostPtr, packet, entity: null);
        }


        /// <summary>
        /// D3 (solid-remediation T3.4): raise an atom trigger in battle, but only when something is
        /// actually bound to it.
        ///
        /// <para><b>The gate is the point, not an optimisation.</b> It mirrors the lawn's own
        /// discipline — `EffectRuntime.HasOnDamageTakenGrant`/`HasOnSpawnGrant`/`HasOnDeathGrant` each
        /// check `Bag.HasGrantWithTrigger` before raising — and it has two consequences that matter.
        /// Nothing is allocated on a hot path when no content binds the trigger, which is every battle
        /// today; and because no <c>EffectEventDto</c> is constructed in that case, wiring these
        /// triggers cannot move an existing golden. The change is inert until content arrives, which is
        /// what makes it safe to land as plumbing rather than as a balance change.</para>
        /// </summary>
        void RaiseTrigger(string trigger, string actorKey, string? targetKey = null, long damage = 0, long tick = 0)
        {
            if (!Host.Bag.HasGrantWithTrigger(trigger)) return;

            Host.Bag.OnEvent(new Contracts.EffectEventDto
            {
                Trigger = trigger,
                ActorPtr = actorKey,
                TargetPtr = targetKey ?? actorKey,
                Damage = damage,
                Tick = tick,
            });
            Host.Flush();
        }

        /// <summary>D3: the battle-side raise for match lifecycle and the round clock, called by
        /// <see cref="BattleEngine"/> which owns those moments rather than this state object.</summary>
        internal void RaiseLifecycle(string trigger, long tick = 0)
        {
            if (!Host.Bag.HasGrantWithTrigger(trigger)) return;
            foreach (var a in Actors)
                RaiseTrigger(trigger, a.Setup.Key, tick: tick);
        }

        public DamageApplyResult ApplyHp(
            ActorState owner, long amount, string effectId,
            ElementPayloadComponent[]? components = null, ActorState? attacker = null, string? grantId = null,
            DamageOrigin origin = DamageOrigin.DirectHit)
        {
            // W13 (battle-derived-wire T3): a positive amount is a heal, and this entry point sits
            // BELOW the bag's Finalize step -- DamageApplyPipeline.Apply -- so a direct/trait heal
            // (regenerator, immortal, soul-eater) never picked up the healer's `resource.restore.hp`
            // term the effect path already gets. Route it through the SAME OverlayCombatMath.Finalize
            // the effect path uses, so `signedAmount + restore.hp, floored at 0`
            // (combat-damage-ssot.md §4.3) has exactly one implementation and no second formula.
            // The healer is the attacker where one is named, otherwise the owner itself (a self-heal
            // like regenerator); an attacker-less resolve contributes 0, never a guessed value.
            if (amount > 0 && Host.Bag.CombatMath is { } healMath)
            {
                var healPacket = new Contracts.DamagePacket
                {
                    ActorPtr = attacker?.Setup.Key ?? owner.Setup.Key,
                    SignedAmount = amount,
                };
                amount = healMath.Finalize(amount, owner.Setup.Key, healPacket, entity: null);
            }

            var result = DamageApplyPipeline.Apply(
                owner.Setup.Key, amount, hitCount: 1,
                components ?? Array.Empty<ElementPayloadComponent>(),
                attacker?.Derived, owner.Derived, ShieldGate, HpSink,
                pluginId: "battle", effectId: effectId, grantId: grantId, origin: origin);
            owner.ShieldAbsorbed += result.AbsorbedAmount;

            if (result.Outcome == DamageApplyOutcome.Applied && result.AppliedAmount < 0)
            {
                Host.Bag.OnDamageApplied?.Invoke(
                    result,
                    origin,
                    components ?? Array.Empty<ElementPayloadComponent>(),
                    attacker?.Setup.Key);
            }

            // D3: the defender half of a hit. Raised for damage only — a heal or a regen tick is not
            // damage taken — and after the pipeline, so the event reports what actually landed rather
            // than what was requested. `attacker` is null for an unsourced pulse, in which case the
            // owner is both actor and target, matching how StatusEffectBridge reports a self-pulse.
            if (result.AppliedAmount < 0 && result.Outcome == DamageApplyOutcome.Applied)
            {
                RaiseTrigger(
                    Effects.Atoms.AtomTriggers.OnDamageTaken,
                    attacker?.Setup.Key ?? owner.Setup.Key,
                    owner.Setup.Key,
                    damage: -result.AppliedAmount);
            }

            // W3 (battle-derived-wire T2, audit D2): battle's own HP apply entered at
            // DamageApplyPipeline -- one level BELOW CombatDamageDispatcher.DispatchInstant's reflect
            // step -- so all four `combat.reflect.*` families were inert in every battle. The effect
            // path (EffectBag -> DispatchInstant) already reflects; this is the second, direct path
            // (basic attack, guardian share, DoT pulse). It calls the SAME TryReflect, never a copy,
            // and ProcDepthLimit stays the only termination bound (combat-damage-ssot.md §6.7a).
            // No attacker, no bounce: a self-pulse (attacker == owner) reflects nothing, exactly as
            // the dispatcher's own guard already says.
            if (amount < 0 && result.Outcome == DamageApplyOutcome.Applied
                && attacker is not null && Host.Bag.CombatMath is { } reflectMath
                && Host.Bag.CombatRng is { } reflectRng)
            {
                var reflectPacket = new Contracts.DamagePacket
                {
                    PacketId = "battle.apply-hp",
                    ActorPtr = attacker.Setup.Key,
                    SignedAmount = amount,
                    ElementPayload = components is { Length: > 0 }
                        ? components.Select(c => new Contracts.ElementPayloadComponentDto
                        {
                            Element = c.Element.ToElementId(),
                            Weight = c.Weight,
                        }).ToList()
                        : null,
                };
                var reflectPolicy = Host.Bag.CombatPolicy;
                var reflectSource = reflectPolicy.ReflectReadsPostShield ? result.AppliedAmount : amount;
                if (reflectSource < 0)
                    CombatDamageDispatcher.TryReflect(
                        reflectPacket, owner.Setup.Key, reflectSource, null, Host.Funnel,
                        reflectPolicy, reflectRng, reflectMath, null, ShieldGate, _resolveActor,
                        CombatBoardSnapshot ?? BoardSnapshot.Empty);
            }

            return result;
        }

        /// <summary>
        /// B16: the earliest tick any live status instance's own <c>NextPulse</c> falls due — what
        /// the kernel schedules its next status-pulse event against, so pulses fire at their TRUE
        /// times instead of once per 1000 ms round. A MIN reduction over <c>AllInstances()</c>, so
        /// its dictionary-backed enumeration order (not otherwise guaranteed) cannot matter here —
        /// <see cref="StatusRuntime.Tick"/> itself already host-sorts ordinally before firing any
        /// pulse for exactly the determinism reason this codebase always cites; this method never
        /// touches firing order, only "when is the soonest one due."
        /// </summary>
        public DateTimeOffset? NextStatusPulseAt()
        {
            DateTimeOffset? earliest = null;
            foreach (var inst in Status.AllInstances())
            {
                // MUST mirror StatusRuntime.Tick's own eligibility gate exactly — a status whose
                // Kind isn't OverTime/Contagion never advances its NextPulse there (e.g. `butter`,
                // a pure crowd-control status, is StatusKind.UnityCc with PeriodMs > 0 purely as an
                // artifact of the shared authoring shape). Filtering only on PeriodMs > 0 without
                // this check schedules a pulse Tick() will never actually fire — NextPulse never
                // moves, this method keeps returning the same stuck tick, and the round loop spins
                // forever rescheduling it. A real incident, not a hypothetical: this is the exact
                // bug BasicAttackHazardTests.Hazard2 (a `butter`-CC'd actor) caught during B16.
                if (inst.Kind != StatusKind.OverTime && inst.Kind != StatusKind.Contagion) continue;
                if (inst.PeriodMs <= 0) continue;
                if (inst.NextPulse > inst.ExpiresAt) continue;
                if (earliest is null || inst.NextPulse < earliest.Value) earliest = inst.NextPulse;
            }
            return earliest;
        }

        public void RunRegeneratorPulses()
        {
            foreach (var a in Actors)
            {
                if (a.Active && a.Has("regenerator"))
                {
                    var def = TraitBattleCatalog.Get("regenerator");
                    ApplyHp(
                        a,
                        Math.Max(1L, checked(a.MaxHp * def.RegenPerRoundMilli / 1000L)),
                        "battle.trait.regenerator");
                }
            }
        }

        public void DrainShieldEvents(int round)
        {
            _shieldEventScratch.Clear();   // caller owns the scratch on EVERY path (DrainEvents appends)
            if (Shields.DrainEvents(_shieldEventScratch) == 0) return;
            foreach (var rec in _shieldEventScratch)
            {
                var key = rec.OwnerKey.StartsWith("entity:", StringComparison.Ordinal)
                    ? rec.OwnerKey.Substring("entity:".Length)
                    : rec.OwnerKey;
                if (!ByKey.TryGetValue(key, out var owner)) continue;
                Events.Add(new BattleEventRec(round, rec.Kind,
                    key, owner.Setup.TypeId, owner.Setup.Side, rec.Amount, rec.Element, rec.ShieldId));
            }

            _shieldEventScratch.Clear();
        }

        public void SweepDeaths(int round)
        {
            foreach (var a in Actors)
            {
                if (!a.Alive && RecordedDeaths.Add(a.Setup.Key))
                {
                    Events.Add(new BattleEventRec(round, BattleEventKinds.Die, a.Setup.Key, a.Setup.TypeId, a.Setup.Side));
                    RaiseTrigger(Effects.Atoms.AtomTriggers.OnDeath, a.Setup.Key, tick: round);
                    Shields.RemoveAll(Contracts.EffectOwnerKeys.Entity(a.Setup.Key));
                }
            }
        }

        /// <summary>Immortal death refusal: a queued +1 through the pipeline turns the death into survive-at-1.</summary>
        /// <summary>
        /// Wave E1 — the attacker's on-hit riders, applied to the actor it just LANDED a hit on.
        ///
        /// <para><b>Byte-identical when nobody has riders</b>, and structurally rather than luckily:
        /// the method returns before touching any RNG for an empty list, so the `riders` stream is
        /// never drawn from and no other stream is perturbed. That is the wave's zero-rider invariant.</para>
        ///
        /// <para>Riders carry the ATTACKER, unlike the t0 initial statuses which land attacker-less —
        /// so resist and potency evaluate against real attacker context, which is the point of applying
        /// a status on a hit rather than at setup. The chance roll is the rider's own
        /// `GrantChanceMilli`, drawn from the dedicated stream; the L2b evaluator still independently
        /// blocks on immunity and the potency floor, exactly as it does for scripted statuses.</para>
        /// </summary>
        void ApplyOnHitRiders(ActorState attacker, ActorState target)
        {
            foreach (var traitId in attacker.Setup.TraitIds)
            foreach (var spec in TraitBattleCatalog.Get(traitId).OnHitRiders)
            {
                var roll = RidersRng.NextPerMille();
                Trace?.Draw("riders", roll);
                if (roll >= spec.GrantChanceMilli) continue;

                Status.Apply(new StatusApplyInput(
                    spec.StatusId,
                    HostPtr: target.Setup.Key,
                    AttackerPtr: attacker.Setup.Key,
                    GrantId: "battle:rider:" + attacker.Setup.Key + ":" + spec.StatusId,
                    BaseMagnitude: spec.MagnitudePerPulse,
                    BaseDuration: spec.DurationMs,
                    PeriodMs: spec.PeriodMs,
                    DurationMs: spec.DurationMs,
                    // Already rolled on the riders stream above; the evaluator must not roll a SECOND
                    // time on the status stream, which would both double-gate the rider and consume a
                    // draw that belongs to contagion.
                    GrantChance: 1.0,
                    EffectId: "battle.rider." + spec.StatusId,
                    PluginId: "battle",
                    // Attacker-ful, unlike the t0 initial statuses: the whole point of a rider is that
                    // the attacker's potency meets the defender's resist.
                    AttackerLess: false),
                    // The chance was already decided above on the riders stream, so the evaluator is
                    // handed a scripted 0.0 -- the same FixedStatusRng the scripted setup path uses,
                    // and for the same reason: one roll per decision, on the stream that owns it.
                    RiderApplyRng, Host.Clock.UtcNow);
            }
        }

        public void ReviveImmortals()
        {
            var queued = false;
            foreach (var a in Actors)
            {
                if (!a.Alive && !a.Retreated && a.ImmortalCharges > 0 && !RecordedDeaths.Contains(a.Setup.Key))
                {
                    a.ImmortalCharges--;
                    ApplyHp(a, 1, "battle.trait.immortal");
                    queued = true;
                }
            }

            if (queued)
                Host.Flush();
        }

        /// <summary>
        /// status-rail C2: rebuild the combat board snap from live positions + Active actors so
        /// contagion neighbors track moves and deaths (ctor snap alone goes stale mid-battle).
        /// </summary>
        public void RefreshCombatBoardSnapshot()
        {
            if (_board is null)
            {
                CombatBoardSnapshot = null;
                return;
            }

            CombatBoardSnapshot = Board.BoardSnapshotAdapter.ToCombatSnapshot(this);
            Host.Bag.BoardSnapshot = CombatBoardSnapshot;
        }

        /// <summary>
        /// status-rail C1: drop host statuses without <c>OnEnded</c> (VFX death contract) but withdraw
        /// status-instance StatMods from the battle ledger so death/retreat cannot orphan them.
        /// </summary>
        public void WithdrawStatusHost(string actorKey)
        {
            foreach (var inst in Status.TakeHostInstances(actorKey))
            {
                if (inst.StatMods.Count == 0) continue;
                Ledger.RemoveBySource(inst.HostPtr, StatusStatPayload.SourceIdOf(inst));
            }
        }

        /// <summary>
        /// party-dungeon D2.10 — lifted out of <see cref="CheckRetreats"/> with **no behaviour
        /// change** (the coward-retreat call site below is byte-identical to what it inlined
        /// before), so it can gain producers beyond the coward trait: a capture (`wild-room`) and a
        /// player-issued retreat (`delve-attrition`) both leave a battle the same way a coward
        /// does — alive, no die event, shields released.
        /// </summary>
        public void Withdraw(ActorState actor)
        {
            actor.Retreated = true;
            WithdrawStatusHost(actor.Setup.Key);
            Shields.RemoveAll(Contracts.EffectOwnerKeys.Entity(actor.Setup.Key));
        }

        /// <summary>Coward retreat: below the threshold the actor leaves the battle alive (no die event).</summary>
        public void CheckRetreats()
        {
            foreach (var a in Actors)
            {
                if (!a.Active || !a.Has("coward")) continue;
                var def = TraitBattleCatalog.Get("coward");
                if (a.MaxHp > 0 && (decimal)a.Hp * 1000m < (decimal)a.MaxHp * def.RetreatBelowMilli)
                    Withdraw(a);
            }
        }

        public void PostFlush(int round)
        {
            ReviveImmortals();
            SweepDeaths(round);
            CheckRetreats();
        }

        public bool AnyActive(string side) => BattleEngine.AnyActive(Actors, side);

        /// <summary>
        /// base-defense `siege-waves` §3: roster growth — a reinforcement joining mid-battle.
        ///
        /// <para><b>Runs the SAME key validation `Resolve` applies at setup</b> (extracted to
        /// <see cref="ValidateActorKey"/> for exactly this reuse) — a mid-battle actor that bypassed
        /// those checks would be silently unhittable at the shield gate.</para>
        ///
        /// <para><b>Appends, never inserts or reorders</b> — <see cref="Actors"/> is a plain
        /// <see cref="List{T}"/>; an index shift mid-battle would invalidate every in-flight effect
        /// that captured one (a shield grant, a status instance, anything keyed by list position rather
        /// than actor key). <see cref="ActorState.SideIndex"/> for the newcomer is the count of actors
        /// already on its own side — the same 0-based-per-side numbering the constructor's own
        /// <c>Squad.Select((a,i) => ...)</c>/<c>Wave.Select((a,i) => ...)</c> already establish.</para>
        ///
        /// <para><b>Placed on the board only when both a board exists AND a position is supplied</b> —
        /// resolving a district edge into a real candidate cell is `siege-resolver`'s job (a later
        /// module), the same scoping <see cref="Board.Placement"/> already states.</para>
        ///
        /// <para><b>Scoped out, stated rather than silently skipped</b>: unlike the constructor's own
        /// per-actor setup, this method does not apply <see cref="BattleActorSetup.InnateShield"/>,
        /// <see cref="BattleActorSetup.InitialStatuses"/>, active-aura membership, or loadout/container
        /// compilation for the newcomer — none of those are in this task's own stated contract (append,
        /// validate, place, never reorder), and building them against no real caller yet would be
        /// exactly the unrequested surface this program's standing rule warns against. A reinforcement
        /// still fights (it is `Active`/`Alive`/targetable/damageable the moment it is added) — it just
        /// arrives without whatever a fresh setup-time actor would have gotten from those four systems,
        /// until a real caller (`siege-resolver`) needs one of them.</para>
        /// </summary>
        public void AddActor(BattleActorSetup setup, Actions.GridPos? position, int round)
        {
            var seenKeys = new HashSet<string>(ByKey.Keys, StringComparer.Ordinal);
            BattleEngine.ValidateActorKey(setup, seenKeys);

            var sideIndex = Actors.Count(a => a.Setup.Side == setup.Side);
            var actor = new ActorState(setup, sideIndex);
            Actors.Add(actor);
            ByKey[setup.Key] = actor;
            // Round is the actual arrival round, unlike the constructor's own initial-roster loop
            // (which spawns everyone at round 0, correctly, since that IS when they arrive).
            Events.Add(new BattleEventRec(round, BattleEventKinds.Spawn, setup.Key, setup.TypeId, setup.Side));
            RaiseTrigger(Effects.Atoms.AtomTriggers.OnSpawn, setup.Key, tick: round);

            if (_board is not null && position is { } p)
                _board.Place(setup.Key, p);
        }

        /// <summary>
        /// The per-attacker tail (spec-basic-attack-adoption.md's boundary: everything from the
        /// berserker ramp onward is EngineBehavior trait logic, not the declared basic-attack action
        /// itself) — berserker ramp, essence riders, guardian split, apply, flush, tallies, revive,
        /// kill/death, soul-eater, retreat check. The caller (`Resolve`'s per-attacker loop) still
        /// owns calling `RunBasicAttackStep` and checking its `Continue`/`Break` outcome — only a
        /// `Proceed` step reaches this method.
        /// </summary>
        public void DispatchHit(ActorState attacker, ActorState target, long signedDelta, int round)
        {
            var damage = checked(-signedDelta);

            // Berserker ramp: battle mechanic on resolver OUTPUT, never inside the formula.
            if (attacker.Has("berserker"))
                damage = checked(damage * TraitBattleMath.BerserkerRampMilli(
                    TraitBattleCatalog.Get("berserker"), attacker.Hp, attacker.MaxHp) / 1000);

            // Essence riders (void-touched / chaos-marked): per-landed-hit proc on its own stream.
            long rider = 0;
            foreach (var essenceId in EssenceTraits)
            {
                if (!attacker.Has(essenceId)) continue;
                var def = TraitBattleCatalog.Get(essenceId);
                var essenceRoll = EssenceRng.NextPerMille();
                Trace?.Draw("essence", essenceRoll);
                if (essenceRoll < def.EssenceProcMilli)
                    rider = checked(rider + Math.Max(
                        1L, checked(damage * def.EssenceRiderMilli / 1000L)));
            }

            // Guardian: an adjacent active guardian pulls a share of the hit onto itself.
            // Each slice passes the gate separately — both actors' shields absorb their own
            // portion (spec: guardian two-slice semantics).
            var guardian = FindAdjacentWithTrait(Actors, target, "guardian");
            var share = guardian != null
                ? checked(damage * TraitBattleCatalog.Get("guardian").GuardShareMilli / 1000L)
                : 0L;

            var mainDelta = checked(-(damage - share + rider));
            ApplyHp(target, mainDelta, "battle.attack", attacker.AttackComponents, attacker);
            Trace?.Apply(round, target.Setup.Key, mainDelta);
            if (share > 0)
            {
                ApplyHp(guardian!, -share, "battle.trait.guardian", attacker.AttackComponents, attacker);
                Trace?.Apply(round, guardian!.Setup.Key, -share);
            }

            ApplyOnHitRiders(attacker, target);

            Host.Flush();
            // Resolver output remains the gameplay tally contract; observer/result paths above report
            // the sink-retained amount instead.
            attacker.DamageDealt = checked(attacker.DamageDealt + damage + rider);

            ReviveImmortals();
            var killsThisHit = 0;
            foreach (var victim in guardian == null ? new[] { target } : new[] { target, guardian })
            {
                if (!victim.Alive && RecordedDeaths.Add(victim.Setup.Key))
                {
                    attacker.Kills++;
                    killsThisHit++;
                    Events.Add(new BattleEventRec(
                        round, BattleEventKinds.Die, victim.Setup.Key, victim.Setup.TypeId, victim.Setup.Side,
                        KillerActorKey: attacker.Setup.Key));
                    // Actor is the KILLER here, target the victim — the same actor/target convention
                    // OnDamageDealt uses one frame earlier, so a rider can tell who scored the kill.
                    RaiseTrigger(
                        Effects.Atoms.AtomTriggers.OnDeath, attacker.Setup.Key, victim.Setup.Key, tick: round);
                    Shields.RemoveAll(Contracts.EffectOwnerKeys.Entity(victim.Setup.Key));
                }
            }

            // Soul-eater: on-kill heal through the pipeline.
            if (killsThisHit > 0 && attacker.Has("soul-eater"))
            {
                var def = TraitBattleCatalog.Get("soul-eater");
                ApplyHp(
                    attacker,
                    checked((long)killsThisHit * Math.Max(
                        1L, checked(attacker.MaxHp * def.OnKillHealMilli / 1000L))),
                    "battle.trait.soul-eater");
                Host.Flush();
            }

            CheckRetreats();
        }
    }

    /// <summary>
    /// Test-only seam (matching <c>RpgStore.DiffCommitForTest</c>'s established precedent): constructs
    /// a real <see cref="BattleRunState"/> and returns <c>HeldActionsOf(actorKey)</c>'s action ids
    /// directly. base-defense `combatant-kind` §4's garrison union has no production reader yet —
    /// exactly like the pre-existing loadout-compile mechanism it sits beside
    /// (<c>EquippedActionIdsReportingTests</c>'s own comment: "nothing reads <c>HeldActionsOf</c> for
    /// real behavior") — and <see cref="BattleRunState"/> itself is private/nested per B13's own
    /// deviation note, so this is the only way to prove the mechanism without waiting for
    /// `siege-resolver` to wire a real caller.
    /// </summary>
    internal static IReadOnlyList<string> HeldActionIdsForTest(
        BattleSetup setup, ulong seed, string actorKey, ActionCatalog? actionCatalog = null)
    {
        var state = new BattleRunState(setup, seed, trace: null, onEffectHostReady: null, actionCatalog: actionCatalog);
        return state.HeldActionsOf(actorKey).Select(a => a.ActionId).ToList();
    }

    /// <summary>
    /// Test-only seam, same shape and same reason as <see cref="HeldActionIdsForTest"/>: battle's own
    /// direct HP apply (<c>ApplyHp</c>, the heal/damage entry point the trait and kill paths call)
    /// lives on the private/nested <see cref="BattleRunState"/> and reports only through the Funnel,
    /// so a test cannot observe a heal's finalized amount any other way without threading a whole
    /// battle through <see cref="Resolve"/>.
    /// </summary>
    internal static long ApplyHpForTest(
        BattleSetup setup, ulong seed, string actorKey, long amount, string effectId = "test.apply")
    {
        var state = new BattleRunState(setup, seed, trace: null, onEffectHostReady: null);
        return state.ApplyHp(state.ByKey[actorKey], amount, effectId).AppliedAmount;
    }

    internal static (DamageApplyResult Result, long ObservedAmount, long HpAfterFlush) DamageObservationForTest(
        BattleSetup setup, ulong seed, string targetKey, string attackerKey, long requestedDamage)
    {
        var state = new BattleRunState(setup, seed, trace: null, onEffectHostReady: null);
        long observed = 0;
        state.Host.Bag.OnDamageApplied = (result, _, _, _) => observed = result.AppliedAmount;
        var applied = state.ApplyHp(
            state.ByKey[targetKey], -requestedDamage, "battle.attack",
            state.ByKey[attackerKey].AttackComponents, state.ByKey[attackerKey]);
        state.Host.Flush();
        return (applied, observed, state.ByKey[targetKey].Hp);
    }

    /// <summary>
    /// Test-only seam, same shape and same reason as <see cref="ApplyHpForTest"/>: W17's
    /// <c>AppliedCombat</c> maxHp term lands on the private/nested actor's pool, so this reads it back.
    /// </summary>
    internal static long MaxHpForTest(BattleSetup setup, ulong seed, string actorKey)
    {
        var state = new BattleRunState(setup, seed, trace: null, onEffectHostReady: null);
        return state.ByKey[actorKey].MaxHp;
    }

    /// <summary>
    /// Test-only seam, same shape and same reason as <see cref="ApplyHpForTest"/>: W3's reflect tail
    /// (battle-derived-wire T2) runs on the private/nested <see cref="BattleRunState"/> and its bounce
    /// lands on the ATTACKER through the Funnel, so this returns the attacker's HP after a flush — the
    /// only way to observe the bounce without threading a whole battle through <see cref="Resolve"/>.
    /// </summary>
    internal static long AttackerHpAfterApplyHpForTest(
        BattleSetup setup, ulong seed, string ownerKey, long amount, string attackerKey)
    {
        var state = new BattleRunState(setup, seed, trace: null, onEffectHostReady: null);
        state.ApplyHp(state.ByKey[ownerKey], amount, "test.hit", null, state.ByKey[attackerKey]);
        state.Host.Flush();
        return state.ByKey[attackerKey].Hp;
    }

    /// <summary>
    /// Test-only seam, same shape and same reason as <see cref="HeldActionIdsForTest"/>: the
    /// <c>combat.defense.omni</c> projection (W11, battle-derived-wire T6) runs on the private/nested
    /// <see cref="BattleRunState"/> and reports only through the composed snapshot, so this is the
    /// only way to observe it after the per-round recompose. The three parameters are the three
    /// contribution SOURCES that must share one gate: a status's own <c>defense</c> StatMod, a
    /// <c>stat.modify</c> grant, and an aura contribution through the shipped
    /// <see cref="BattleEffectHost.AddDerivedContribution"/> seam.
    /// </summary>
    internal static double ComposedDefenseForTest(
        BattleSetup setup, ulong seed, string actorKey,
        double statusDefenseFlat = 0, double statModifyDefenseFlat = 0, double auraContribution = 0)
    {
        var state = new BattleRunState(setup, seed, trace: null, onEffectHostReady: host =>
        {
            if (auraContribution != 0)
                host.AddDerivedContribution!(actorKey, DerivedStatChannels.CombatDefenseOmni, "aura:probe", auraContribution);

            if (statModifyDefenseFlat != 0)
            {
                var probe = new Effects.EffectDef
                {
                    EffectId = "test.defense-probe",
                    EffectType = Contracts.EffectTypes.Passive,
                    Name = "defense probe",
                    Actions = new()
                    {
                        new Effects.EffectActionRow
                        {
                            Seq = 1,
                            Action = Contracts.EffectActions.ModifyStat,
                            Params = new Dictionary<string, object?>
                            {
                                ["channel"] = BattleStatModifierLedger.DefenseChannel,
                                ["flat"] = statModifyDefenseFlat,
                            },
                        },
                    },
                };
                host.Bag.Catalog.Upsert(probe);
                host.Bag.Grant(new Contracts.EffectGrantDto
                {
                    GrantId = "probe:defense",
                    EffectId = probe.EffectId,
                    OwnerKind = "entity",
                    OwnerKey = Contracts.EffectOwnerKeys.Entity(actorKey),
                    PluginId = "battle",
                });
            }
        });

        if (statusDefenseFlat != 0)
            state.Status.Apply(new StatusApplyInput(
                "expose", HostPtr: actorKey, AttackerPtr: "wave:0", GrantId: "g-defense-probe",
                BaseMagnitude: 1, BaseDuration: 5000, DurationMs: 5000,
                StatMods: new[] { new StatusStatMod(BattleStatModifierLedger.DefenseChannel, "flat", statusDefenseFlat) }),
                new FixedStatusRng(0.0), state.Host.Clock.UtcNow);

        // Two rounds' worth of the per-round recompose -- idempotence is the property under test.
        state.RecomposeDerivedForAllActors();
        state.RecomposeDerivedForAllActors();
        return state.ByKey[actorKey].Derived.Get(DerivedStatChannels.CombatDefenseOmni);
    }

    /// <summary>
    /// Test-only seam, same shape and same reason as <see cref="ComposedDefenseForTest"/>: W6
    /// (battle-derived-wire T5) routes a status's <c>combat.*</c> StatMod into the derived ledger, whose
    /// per-round <c>Recompose</c> writes it against the frozen <c>BaseDerived</c>. This applies one
    /// status, recomposes twice, optionally withdraws it and recomposes once more, then returns the
    /// channel — the only way to observe it on the private/nested <see cref="BattleRunState"/>.
    /// </summary>
    internal static double ComposedChannelAfterStatusForTest(
        BattleSetup setup, ulong seed, string actorKey, string channel,
        string statusChannel, string op, double value, bool withdraw = false)
    {
        var state = new BattleRunState(setup, seed, trace: null, onEffectHostReady: null);
        state.Status.Apply(new StatusApplyInput(
            "expose", HostPtr: actorKey, AttackerPtr: "wave:0", GrantId: "g-derived-probe",
            BaseMagnitude: 1, BaseDuration: 5000, DurationMs: 5000,
            StatMods: new[] { new StatusStatMod(statusChannel, op, value) }),
            new FixedStatusRng(0.0), state.Host.Clock.UtcNow);

        state.RecomposeDerivedForAllActors();
        state.RecomposeDerivedForAllActors();
        if (withdraw)
        {
            state.Status.ClearGrant("g-derived-probe");
            state.RecomposeDerivedForAllActors();
        }
        return state.ByKey[actorKey].Derived.Get(channel);
    }

    /// <summary>
    /// Test-only seam, same shape and same reason as <see cref="HeldActionIdsForTest"/>: the one
    /// instance rung resolver (<c>AE1.2</c>, `action-base`) lives on the private/nested
    /// <see cref="BattleRunState"/>, and its production reader (the hit site) is a later task, so this
    /// is the only way to prove the non-held fallback reads the swung row without threading a whole
    /// battle through <see cref="Resolve"/>.
    /// </summary>
    internal static int EffectiveRungForTest(
        BattleSetup setup, ulong seed, string actorKey, string actionId, ActionCatalog? actionCatalog = null)
    {
        var state = new BattleRunState(setup, seed, trace: null, onEffectHostReady: null, actionCatalog: actionCatalog);
        return state.EffectiveRungOf(actorKey, actionId);
    }

    /// <summary>
    /// Test-only seam, same shape and same reason as <see cref="HeldActionIdsForTest"/>: base-defense
    /// `siege-positions`'s <c>PositionOf</c>/<c>CombatBoardSnapshot</c> live on the private/nested
    /// <see cref="BattleRunState"/>, so this is the only way to prove them without a production caller
    /// (that is `siege-resolver`'s job, a later module) yet threading a board all the way through
    /// <see cref="Resolve"/>.
    /// </summary>
    internal static (GridPos? Position, Combat.BoardSnapshot? Snapshot) PositionAndSnapshotForTest(
        BattleSetup setup, ulong seed, string actorKey, Board.BoardState? board)
    {
        var state = new BattleRunState(setup, seed, trace: null, onEffectHostReady: null, board: board);
        return (state.PositionOf(actorKey), state.CombatBoardSnapshot);
    }

    /// <summary>
    /// Test-only seam, same shape and same reason as <see cref="PositionAndSnapshotForTest"/>: A9
    /// `movement-actions`' own nearest-enemy orchestration (<see cref="BattleRunState.TryMoveTowardNearestEnemy"/>)
    /// reads <c>Actors</c>/<c>ByKey</c>, which live on the private/nested <see cref="BattleRunState"/>.
    /// </summary>
    internal static (int CellsMoved, GridPos? FinalPosition) TryMoveTowardNearestEnemyForTest(
        BattleSetup setup, ulong seed, string actorKey, int maxCells, Board.BoardState board)
    {
        var state = new BattleRunState(setup, seed, trace: null, onEffectHostReady: null, board: board);
        var moved = state.TryMoveTowardNearestEnemy(actorKey, maxCells);
        return (moved, state.PositionOf(actorKey));
    }
}
