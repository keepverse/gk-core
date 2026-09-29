using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Cost;
using FusionRpg.Core.Actions.Unlock;
using FusionRpg.Core.Battle.Siege;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Combat.Shield;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Status;

namespace FusionRpg.Core.Battle;

/// <summary>
/// Pure deterministic battle resolver (spec-match-source-core.md, combat-unification
/// battle-adoption). No I/O, no clock, no ambient state: same setup + seed + platform ⇒
/// byte-identical report. Round order is locked (RulesetVersion): status ticks →
/// initiative-ordered attacks → death cleanup → shield upkeep → round end.
/// v2: attack resolution runs the SSOT resolver (OverlayCombatCalculator — sigmoid hit/crit,
/// ElementHub matchup, battle profile min-chip); every HP delta applies through
/// DamageApplyPipeline (shield gate → battle funnel), so shields work exactly like overlay.
/// Per-system RNG streams (`initiative`, `crit`, `essence`, `status`, `proc`; the v1 `damage`
/// stream name stays reserved — its variance roll retired with the v1 curves).
/// </summary>
public static partial class BattleEngine
{
    sealed class ActorState : IBattleHpTarget, IBattleStatTarget
    {
        public ActorState(BattleActorSetup setup, int sideIndex)
        {
            Setup = setup;
            SideIndex = sideIndex;
            // party-dungeon D2.11: a delve carry-in HP, when the caller set one -- null for every
            // existing setup (expeditions, web matches, sieges, every test), so this is byte-identical
            // to the old `Hp = setup.MaxHp` for all of them. "One hp seat": this IS the actor's HP,
            // never a second value a resource pool could drift out of sync with.
            //
            // W17 (battle-derived-wire T15, BWR1.6): battle now consumes the ActorHub `AppliedCombat`
            // merge, not just its derived half. `RuntimePrimary` is the PRE-merge primary, so
            // `AppliedCombat − RuntimePrimary` IS the merge's own result -- battle maps its maxHp term
            // onto the pool and its defense term onto the channel the battle resolver actually reads
            // (`combat.defense.omni`), never a second fold of `progression.bonus.*`. arm1/arm2 have no
            // battle analogue and are ignored, as they always were here.
            var resolved = BattleHubCompose.Resolve(setup);
            Derived = resolved.Derived;
            var bonusMaxHp = resolved.AppliedCombat.MaxHp - resolved.RuntimePrimary.MaxHp;
            var bonusDefense = resolved.AppliedCombat.DefenseFlat - resolved.RuntimePrimary.DefenseFlat;
            MaxHp = setup.MaxHp + bonusMaxHp;
            BaselineDefense = setup.Defense + bonusDefense;
            if (bonusDefense != 0)
                Derived.Set(DerivedStatChannels.CombatDefenseOmni,
                    Derived.Get(DerivedStatChannels.CombatDefenseOmni) + bonusDefense);
            Hp = (setup.CurrentHp ?? setup.MaxHp) + bonusMaxHp;
            // aura-skill T4: a defensive copy, frozen the instant Derived is born — the one stable
            // baseline BattleDerivedModifierLedger.Recompose adds dynamic contributions on top of.
            // `Derived` itself is mutable (Set, used today by the A18e defense live-read path) so it
            // cannot double as its own baseline once anything else starts writing to it.
            BaseDerived = ActorDerivedSnapshot.FromValues(Derived.Channels);
            ElementTypes = ActorElementTypes.Create(
                setup.ElementPrimary,
                setup.ElementSecondary == setup.ElementPrimary ? null : setup.ElementSecondary);
            // Wave E3: HybridPayload.Build is inert at the shipped weight of 0.
            AttackComponents = HybridPayload.Build(
                setup.ElementPrimary, ElementTypes.Secondary, BattleRuleset.HybridSecondaryWeightMilli);
            foreach (var traitId in setup.TraitIds)
                _traits.Add(TraitBattleCatalog.Get(traitId).TraitId);
            ImmortalCharges = Has("immortal") ? TraitBattleCatalog.Get("immortal").DeathRefusalCharges : 0;
        }

        readonly HashSet<string> _traits = new(StringComparer.Ordinal);

        public BattleActorSetup Setup { get; }
        public int SideIndex { get; }               // position within its own side (adjacency)
        public ActorDerivedSnapshot Derived { get; }

        /// <summary>aura-skill T4: the frozen compose-once result, never mutated. See the
        /// constructor's own comment for why `Derived` cannot serve this role itself.</summary>
        public ActorDerivedSnapshot BaseDerived { get; }
        public ActorElementTypes ElementTypes { get; }
        public ElementPayloadComponent[] AttackComponents { get; }
        public long Hp { get; set; }

        /// <summary>W17 (battle-derived-wire T15, BWR1.6): the pool, INCLUDING the `AppliedCombat`
        /// maxHp term. Set once in the constructor; `Setup.MaxHp` is now only its pre-merge base.</summary>
        public long MaxHp { get; }
        public long DamageDealt;
        public int Kills;
        public long ShieldAbsorbed;
        public bool Retreated;
        public int ImmortalCharges;

        /// <summary>party-dungeon D2.21: sticky for the whole battle, set once by the death-cleanup
        /// site's Downed transition and never cleared even if a later revive takes the actor
        /// `Downed -> Charging` — "the FIRST Downed transition sets downedOnce" reads this, not the
        /// machine's current state, precisely because a revive would otherwise erase the signal.
        /// False for every actor outside `DownedOnDeplete` (no site ever sets it).</summary>
        public bool WentDowned;

        public bool Alive => Hp > 0;

        /// <summary>Still fighting: alive and not retreated.</summary>
        public bool Active => Alive && !Retreated;

        public bool Has(string traitId) => _traits.Contains(traitId);

        /// <summary>A18e: <see cref="IBattleStatTarget"/>'s own baseline — `Setup.Defense` PLUS the
        /// `AppliedCombat` defense term (W17). `BattleBaselineSubsystem` seeded `combat.defense.omni`
        /// from `Setup.Defense`, so the same term is added to that channel in the constructor, keeping
        /// this baseline and the composed value in step.</summary>
        public long BaselineDefense { get; }

        /// <summary>A18e (spec-battle-live-stat-modifiers.md §2): the one live-read `Setup.Atk` itself
        /// never composes through — byte-identical to `Setup.Atk` when the ledger holds no `atk` mods
        /// for this actor (every battle today), since `ComposeChannel` over an empty mod list returns
        /// the baseline unchanged.</summary>
        public long LiveAtk(BattleStatModifierLedger ledger) => ledger.Recompose(Setup.Key, "atk", Setup.Atk);
    }

    /// <summary>Owned-PRNG adapter for the L2b apply roll — `status` stream, never System.Random.</summary>
    sealed class BattleStatusRng : IStatusRng
    {
        readonly SeededRng _rng;
        readonly Timeline.BattleTrace? _trace;

        public BattleStatusRng(ulong seed, Timeline.BattleTrace? trace = null)
        {
            _rng = SeededRng.DeriveStream(seed, "status");
            _trace = trace;
        }

        public double NextUnit()
        {
            // Recorded so the parity ladder can see this stream at all. Asserting "the status
            // stream never draws" against an UNinstrumented stream would pass even if it drew a
            // thousand times — the assertion only means something because of this line.
            var raw = _rng.NextInt(1_000_000);
            _trace?.Draw("status", raw);
            return raw / 1_000_000.0;
        }
    }

    /// <summary>
    /// Routes DoT/regen pulses through the shared apply pipeline — shields absorb battle DoTs
    /// exactly like overlay ones (empty components + hitCount 1 is overlay parity: the overlay
    /// pulse sink sends no payload either).
    /// </summary>
    sealed class BattlePulseSink : IStatusPulseSink
    {
        readonly Func<string, string?, long, string, Combat.Element.ElementPayloadComponent[], DamageApplyResult> _apply;
        public BattlePulseSink(Func<string, string?, long, string, Combat.Element.ElementPayloadComponent[], DamageApplyResult> apply) => _apply = apply;

        // Math.Round, NOT a truncating cast — StatusEffectBridge (the overlay sink) rounds, and
        // EffectiveMagnitude is fractional whenever a status power/resist channel is non-zero.
        // Truncating cost battle 1 HP per pulse against the overlay, and turned a −0.6 pulse
        // into 0 — which fails the pipeline's `amount < 0` test and skips the shield gate.
        // Wave E1: the pulse carries the status's own element to the shield gate. Empty for an
        // untyped status, which is every status shipped today -- byte-identical to the pre-E1 call.
        // solid-remediation D1 (T2.5): the ATTACKER now rides along. It was always on the instance
        // and always discarded here, which is why a battle DoT could not be resolved against anybody
        // -- there was no second side to contest. The lawn's own pulse sink (StatusEffectBridge) has
        // always passed it, so this is mode parity, not a new concept.
        public void PulseHp(StatusInstance instance, double amount) =>
            _apply(instance.HostPtr, instance.AttackerPtr, (long)Math.Round(amount),
                   "battle.status." + instance.StatusId,
                   Status.StatusPulsePayload.For(instance));
    }

    /// <param name="trace">
    /// Optional observation for the kernel-adoption parity ladder. Null in production, and every
    /// record site is null-conditional — tracing cannot change an outcome.
    /// </param>
    /// <param name="onEffectHostReady">
    /// T14 (action-todo.md, spec-basic-attack-adoption.md): a test-only seam proving the "grant
    /// path" two atom `D6` comments wait on — <c>resource.delta</c> and <c>shield.grant</c> can now
    /// reach <see cref="BattleEffectHost.Bag"/>'s sink and this method's own <see cref="ShieldGate"/>
    /// once <see cref="EffectBag.ShieldGate"/> is wired (done below, unconditionally). Null in every
    /// production and golden call site — the callback fires once, before round 1, with the fully
    /// wired host handed to it; nothing else about <see cref="Resolve"/> changes when it is null.
    /// </param>
    /// <param name="profile">
    /// B14 (spec-kernel-adoption.md): a 5th optional trailing parameter, not a new overload — every
    /// existing call site (positional 2-arg, or naming <paramref name="trace"/>/
    /// <paramref name="onEffectHostReady"/>) compiles unchanged. <c>null</c> means "content did not
    /// choose," resolving to <see cref="Timeline.BattleModeProfileCatalog.ClassicRound"/>, mirroring
    /// how B12's <see cref="WaveDef.Profile"/> resolution already works. <b>Scope of what this
    /// actually changes today:</b> only the round-boundary tick sequencing runs through the
    /// profile's chosen advance mechanism — <see cref="Resolve"/> is a batch resolver, not a live
    /// per-frame loop, so <see cref="Timeline.AdvancePolicyKind.NextEvent"/> is used regardless of
    /// which profile is passed (a <see cref="Timeline.FixedIncrementAdvance"/> has no meaning
    /// without a caller supplying per-frame ticks, which nothing here does). The profile's other
    /// fields (`W`, `WScope`, `Commitment`, `Economy`) are accepted and available for future
    /// enrichment but are inert here — this gate proves the kernel can carry the round skeleton
    /// byte-identically, not that combat routes through the per-actor turn FSM, which is explicitly
    /// out of scope (`battle-timeline-map.md`: "E2 skills... respec after T5").
    /// </param>
    /// <param name="actionCatalog">
    /// A17 (spec-action-selection-adoption.md §2): a 7th optional trailing parameter, same additive
    /// pattern as <paramref name="profile"/>. Required only if some actor's
    /// <see cref="BattleActorSetup.EquippedActionIds"/> is non-empty — every id in it must resolve
    /// against this catalog, loudly (an `ArgumentException`, not a silent skip) if it does not. An
    /// actor with no loadout needs no catalog at all: it falls back to the single hand-built basic
    /// attack. Synthetic/test-constructed today (`A20`'s own job is a clean harness for that) — this
    /// module does not read a live grant pipeline, per `action-map.md` §12's own explicit scope call.
    /// </param>
    /// <param name="containerResolver">
    /// A18a (spec-action-container-binding.md §2): an 8th optional trailing parameter, same additive
    /// pattern as <paramref name="actionCatalog"/>. Required only if some held action's `ContainerId`
    /// is non-empty — every such container must resolve against it, loudly, if it does not. `null` is
    /// legal exactly when nothing in the loadout carries a real container, which is every caller
    /// today (`A20`'s own job is a clean harness for real content).
    /// </param>
    /// <param name="board">
    /// base-defense `siege-positions` §1: the tactical board, if this battle has one. Optional and
    /// trailing, matching every other collaborator above — `null` for every caller until a siege
    /// wires one in (`siege-resolver`, a later module), which is byte-identical to today's behaviour
    /// for every existing caller. The caller is responsible for having placed actors onto it before
    /// calling <see cref="Resolve"/> — this method does not place them (see
    /// <see cref="Board.Placement"/>'s own doc comment for why).
    /// </param>
    /// <summary>
    /// base-defense `siege-waves` §3: the SAME checks <see cref="Resolve"/> has always applied to every
    /// setup actor, extracted so <c>BattleRunState.AddActor</c> (a mid-battle reinforcement) can run
    /// them too — not a re-implementation. A mid-battle actor that bypassed these would be silently
    /// unhittable at the shield gate (mixed-case key), would shadow an existing actor (duplicate key),
    /// or would spawn a corpse that never gets its own die event (`MaxHp &lt; 1`) — the exact failure
    /// this check has always existed to prevent at setup time.
    /// </summary>
    static void ValidateActorKey(BattleActorSetup a, HashSet<string> seenKeys)
    {
        if (string.IsNullOrWhiteSpace(a.Key) || a.Key != a.Key.Trim().ToLowerInvariant())
            throw new ArgumentException($"Actor key '{a.Key}' must be non-empty lower-case (funnel keys normalize).");
        if (a.Key.StartsWith("entity:", StringComparison.Ordinal) || a.Key.StartsWith("0x", StringComparison.Ordinal))
            throw new ArgumentException($"Actor key '{a.Key}' must not start with 'entity:' or '0x' (ptr-space prefixes).");
        if (!seenKeys.Add(a.Key))
            throw new ArgumentException($"Duplicate actor key '{a.Key}'.");
        if (a.MaxHp < 1)
            throw new ArgumentException($"Actor '{a.Key}' must have MaxHp >= 1.");
    }

    public static BattleReport Resolve(BattleSetup setup, ulong seed, Timeline.BattleTrace? trace = null,
        Action<BattleEffectHost>? onEffectHostReady = null, Timeline.BattleModeProfile? profile = null,
        ActionCatalog? actionCatalog = null, IContainerEffectResolver? containerResolver = null,
        Timeline.IIntentSource? intentSource = null, Board.BoardState? board = null,
        Func<string, UnlockState>? unlockStateFor = null, UnlockTuning? unlockTuning = null,
        IReadOnlyList<RunnerBinding>? runnerBindings = null,
        IReadOnlySet<string>? containersWithRunnerCoverage = null,
        Func<string, IReadOnlyList<string>>? equipEffectIdsFor = null,
        AiTuning? aiTuning = null, IStockLedger? stockLedger = null, IStanceCheck? stance = null)
    {
        if (setup.Squad.Count == 0) throw new ArgumentException("Squad is empty.");
        if (setup.Wave.Count == 0) throw new ArgumentException("Wave is empty.");

        // Loud validation over silent corruption (2026-08-21 review): the funnel lower-cases
        // target keys while the actor map is case-sensitive — a mixed-case key would make its
        // actor silently unhittable; a duplicate key would shadow an actor; MaxHp < 1 spawns a
        // corpse that never gets its die event. battle-adoption adds the prefix ban: keys
        // starting "entity:" or "0x" would be mangled by CombatPtr.Normalize at the shield
        // gate while the actor map keeps the original.
        //
        // base-defense `siege-waves` §3: extracted into ValidateActorKey (below) so a mid-battle
        // reinforcement (BattleRunState.AddActor) runs the identical checks — not a re-implementation.
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in setup.Squad.Concat(setup.Wave))
            ValidateActorKey(a, seenKeys);

        // B37: the profile is now READ. `null` means "content did not choose" and resolves to
        // classic-round, mirroring WaveDef.Profile's own resolution. Resolved BEFORE `state` below
        // (moved 2026-09-07, base-defense siege-ai) so its own `RoundDurationMs` can build a real
        // `roundOf` for `SiegeAiIntentSource` — a safe, purely-local reorder: nothing between here and
        // the old call site read anything `state`'s own construction depends on.
        var activeProfile = profile ?? Timeline.BattleModeProfileCatalog.ClassicRound;

        // B13 (spec-kernel-adoption.md): every local this method used to hold — actors, byKey,
        // host, shields, gate, sink, events, RNG streams — plus the eight closures over them, now
        // live on BattleRunState (BattleRunState.cs, nested in this partial class). Zero behavior
        // change: every line below is the same statement sequence as before extraction, reading
        // through `state.` instead of a captured local.
        // base-defense siege-ai: `roundOf` derives from THIS battle's own real round duration, not a
        // guess — `weightRound` is XCOM's own smallest-weighted term, so precision here is a nicety,
        // not a correctness requirement, but deriving it costs nothing once `activeProfile` is resolved
        // (immediately above) before this call. `aiTuning` null (every existing caller) leaves both
        // arguments inert — `BattleRunState`'s own constructor never reads `roundOf` unless `aiTuning`
        // is also non-null.
        var state = new BattleRunState(setup, seed, trace, onEffectHostReady, actionCatalog, containerResolver, board,
            unlockStateFor, unlockTuning, runnerBindings, containersWithRunnerCoverage, equipEffectIdsFor,
            aiTuning, stockLedger, roundOf: tick => (int)(tick / Math.Max(1, activeProfile.RoundDurationMs)),
            stance: stance);

        // B14: the round boundary runs on the kernel's own EventQueue/SimulationClock — the same
        // primitives every other Timeline module uses — instead of a raw integer counter. `Resolve`
        // is a batch resolver, so `NextEventAdvance` drives it regardless of the chosen profile's
        // `AdvancePolicy` (see the `profile` parameter's own doc comment for why).
        //
        // B16 (spec-kernel-adoption.md hazard 5, the deliberate fix): status pulses now share this
        // SAME queue as their own event kind, scheduled at each instance's true `NextPulse` tick
        // instead of being folded into the once-per-1000ms round-open call. The round-open call to
        // `Status.Tick` is GONE — round-open now only runs the regenerator trait pulse (step 1's
        // OTHER half); status delivery is fully event-driven. Exactly one event of EACH kind is ever
        // pending at a time, recomputed and rescheduled after it fires — the same "does the queue
        // still hold a scheduled X" pattern B14 already established for rounds, now applied twice.
        // One economy per BATTLE, never the profile's own — profiles are cached singletons and an
        // economy holds mutable per-key budget state, so sharing one across concurrent battles
        // starves actors of turns. See BattleModeProfile.NewEconomy for the reproduction.
        var battleEconomy = activeProfile.NewEconomy();
        // `battle-tempo` `reaction-lane` RL2: one lane per BATTLE, same reasoning as `battleEconomy` —
        // its `Depth`/slot state must persist across the whole battle's reactions, not reset per round.
        // `WReact = 0` (every shipped profile) makes this byte-identical to no lane at all
        // (ReactionLane's own doc), so constructing it unconditionally here is inert until a caller
        // exists — `RunTimelineActionPhase` is that caller, reached only when `UsesTimelineDispatch`.
        var reactionLane = new Timeline.ReactionLane(activeProfile.WReact, activeProfile.WScope);
        // `battle-tempo` `timeline-dispatch` (found via `LAND1`'s own staged sweep against real
        // content, 2026-09-05): one clock per BATTLE, matching `Cooldowns`/`ResourcePools`/
        // `reactionLane`'s own "persists across rounds" lifetime -- both of those assume monotonically
        // increasing ticks, and a per-round-fresh clock fed them a tick sequence that goes backwards
        // every round boundary. Unused (and harmless to construct) unless `UsesTimelineDispatch`.
        var actionClock = new Timeline.SimulationClock();

        var roundQueue = new Timeline.EventQueue(expectedEvents: 4);
        var roundClock = new Timeline.SimulationClock();
        var roundAdvance = new Timeline.NextEventAdvance();
        var roundEventBuffer = new List<Timeline.ScheduledEvent>(2);
        const int RoundEventKind = 0;
        const int StatusPulseEventKind = 1;
        const string RoundEventOwnerKey = "round";
        const string StatusPulseEventOwnerKey = "status-pulse";

        // The battle's own absolute horizon — base-defense F2: read from the PROFILE, not the
        // engine-global BattleRuleset, so a siege can run a longer horizon than a squad fight without
        // moving every other battle's. `classic-round` inherits BattleRuleset.MaxRounds/RoundDurationMs
        // exactly (TimelineProfileTuning's null-means-inherit resolution in
        // BattleModeProfileCatalog.Build), so this is byte-identical to the pre-F2 read for every
        // existing battle. The pre-B16 loop was ALWAYS bounded by `rounds < MaxRounds`; status pulses
        // now live on the same timeline and need the identical ceiling, or a long/unbounded-duration
        // status against two sides that never wipe each other schedules forever once round events
        // stop — a real infinite loop this exact scenario hit and was caught by, not a hypothetical.
        var maxBattleTick = (long)activeProfile.MaxRounds * activeProfile.RoundDurationMs;

        void ScheduleNextStatusPulse()
        {
            if (state.NextStatusPulseAt() is not { } at) return;
            var tick = (long)Math.Round((at - state.T0).TotalMilliseconds);
            if (tick > maxBattleTick) return;
            roundQueue.Schedule(Math.Max(tick, roundClock.Now), StatusPulseEventOwnerKey, StatusPulseEventKind, 0);
        }

        // base-defense `siege-waves` §1-2,4-5: batches from BOTH sides flow through ONE flattened,
        // pre-sorted queue -- `Side` is data on each entry, never a second code path. Sorted by
        // (AtTick, actor key ordinal) so same-tick arrivals -- including two different sides' batches
        // sharing a tick, decision 6's "both sides enter together" -- land in a replay-stable order.
        // Empty for every existing caller (`BattleSetup.Reinforcements` defaults empty), so this list
        // is empty, `ScheduleNextReinforcement` below is never called, and the reinforcement event
        // kind is NEVER scheduled -- a never-scheduled event kind cannot change a tick sequence, the
        // structural half of this module's byte-identity argument.
        const int ReinforcementEventKind = 2;
        const string ReinforcementEventOwnerKey = "reinforcement";
        var reinforcementQueue = setup.Reinforcements
            .SelectMany(b => b.Actors.Select(a => (b.AtTick, b.Side, b.Edge, Actor: a)))
            .OrderBy(x => x.AtTick).ThenBy(x => x.Actor.Key, StringComparer.Ordinal)
            .ToList();
        var reinforcementCursor = 0;
        Timeline.EventHandle? reinforcementHandle = null;

        // F8's hybrid trigger, the actual verdict (not a pure clock, the first draft's mistake):
        // `due = Math.Min(nextScheduledTick, fieldClearedTick ?? long.MaxValue)`. Null means "clock
        // only so far" -- set the instant the field actually clears, which is what lets that side of
        // the race win when it comes first.
        long? fieldClearedTick = null;

        void ScheduleNextReinforcement(long earliestTick)
        {
            if (reinforcementCursor >= reinforcementQueue.Count) { reinforcementHandle = null; return; }
            var nextScheduledTick = reinforcementQueue[reinforcementCursor].AtTick;
            var due = Math.Max(earliestTick, Math.Min(nextScheduledTick, fieldClearedTick ?? long.MaxValue));
            // Bounded by `maxBattleTick`, like every other scheduled event -- the same
            // `if (tick > maxBattleTick) return;` guard `ScheduleNextStatusPulse` already applies.
            if (due > maxBattleTick) { reinforcementHandle = null; return; }
            reinforcementHandle = roundQueue.Schedule(due, ReinforcementEventOwnerKey, ReinforcementEventKind, 0);
        }

        // F8's state half: whenever the currently-arrived field (everyone already in the roster, not
        // future reinforcements) drops to the configured threshold, pull the pending batch forward to
        // right now instead of waiting for its clock tick. Checked once per round boundary (below) —
        // matching `SiegeObjective`'s own "evaluated at round boundaries, never per action" discipline.
        void CheckFieldCleared()
        {
            if (reinforcementCursor >= reinforcementQueue.Count || fieldClearedTick is not null) return;
            var livingAnimate = state.Actors.Count(a => a.Active && a.Setup.Kind == CombatantKind.Animate);
            if (livingAnimate > Board.SiegeTuningPolicy.Waves.FieldClearedThreshold) return;
            fieldClearedTick = roundClock.Now;
            if (reinforcementHandle is { } h) roundQueue.Cancel(h);
            ScheduleNextReinforcement(roundClock.Now);
        }

        var rounds = 0;
        if (rounds < activeProfile.MaxRounds && state.AnyActive("squad") && state.AnyActive("wave"))
            roundQueue.Schedule(activeProfile.RoundDurationMs, RoundEventOwnerKey, RoundEventKind, 0);
        ScheduleNextStatusPulse();   // initial statuses may already carry a due pulse in flight
        if (reinforcementQueue.Count > 0) ScheduleNextReinforcement(0);

        // Belt-and-suspenders on top of `maxBattleTick`, the same shape the kernel's own test rigs
        // already use (e.g. TurnFsmActionEnvelopeTests.Rig.Pump's `guard < 10_000`): `maxBattleTick`
        // is the domain-correct fix, but a hard iteration cap means a bug this shape ever produces
        // again throws loudly and fast instead of spinning to an OOM crash — which is exactly what
        // happened here once already, from status-pulse events not being bounded by MaxRounds.
        //
        // base-defense F2: scaled to THIS battle's horizon via BattleRuleset.LoopGuardRoundMultiple
        // (tuning: ruleset.loopGuardRoundMultiple, structural not balance) rather than hard-coded to
        // classic-round's 50 rounds — a siege with a larger horizon would otherwise throw on a legal
        // battle, which is worse than the runaway this guards against. `checked`: an overflow here
        // means a profile asked for a horizon the guard cannot express, and that must throw rather
        // than silently wrap into a cap too small to hold a legal battle.
        // Reproduces exactly 200,000 at classic-round's shipped 50 rounds (50 * 4000).
        var maxLoopIterations = checked(activeProfile.MaxRounds * BattleRuleset.LoopGuardRoundMultiple);
        var loopGuard = 0;

        // D3 (solid-remediation T3.4): the match-lifecycle triggers. Raised through
        // BattleRunState.RaiseLifecycle, which gates on HasGrantWithTrigger first -- so with no content
        // bound to them (every battle today) nothing is allocated and no golden can move.
        //
        // OnWave rides the same moment as OnMatchStart rather than getting a loop of its own: a battle
        // IS one wave (`BattleSetup.WaveId`), so a separate per-wave boundary would be inventing a
        // concept the engine does not have.
        state.RaiseLifecycle(Effects.Atoms.AtomTriggers.OnMatchStart);
        state.RaiseLifecycle(Effects.Atoms.AtomTriggers.OnWave);

        while (roundQueue.Count > 0)
        {
            if (++loopGuard > maxLoopIterations)
                throw new InvalidOperationException(
                    $"BattleEngine.Resolve exceeded {maxLoopIterations} scheduling iterations — a runaway event loop, not a long battle. WaveId '{setup.WaveId}', seed {seed}.");

            if (!state.AnyActive("squad") || !state.AnyActive("wave")) break;   // decided by an earlier sub-round pulse; a stale scheduled event is never acted on

            roundClock.TryAdvance(roundAdvance, roundQueue);
            // D3: the round clock IS battle's timer. Same gate as the rest -- with nothing bound this
            // costs one dictionary probe per round and allocates nothing.
            state.RaiseLifecycle(Effects.Atoms.AtomTriggers.OnTimer, tick: roundClock.Now);
            roundEventBuffer.Clear();
            roundQueue.PopDue(roundClock.Now, roundEventBuffer);

            foreach (var ev in roundEventBuffer)
            {
                if (!state.AnyActive("squad") || !state.AnyActive("wave")) break;   // decided by an earlier event in THIS same batch
                var now = state.T0.AddMilliseconds((double)roundClock.Now);
                state.Host.Clock.UtcNow = now;

                if (ev.Kind == StatusPulseEventKind)
                {
                    trace?.Phase(rounds, "status-pulse");
                    // base-defense `siege-positions` §3: the board is an optional trailing parameter,
                    // exactly like `trace`/`actionCatalog`/`containerResolver`/`intentSource` above —
                    // null for every existing caller (no `_board` supplied), which is byte-identical to
                    // this line's own prior literal `board: null`.
                    // status-rail C2: refresh snap so contagion sees live positions / Active filter.
                    state.RefreshCombatBoardSnapshot();
                    state.Status.Tick(now, state.PulseSink, board: state.CombatBoardSnapshot, spreadRng: state.StatusRng);
                    state.Host.Flush();
                    state.PostFlush(rounds);
                    if (state.AnyActive("squad") && state.AnyActive("wave"))
                        ScheduleNextStatusPulse();
                    continue;
                }

                if (ev.Kind == ReinforcementEventKind)
                {
                    // base-defense `siege-waves` §4 (F9/C7): bounded, resumable drain -- at most
                    // MaxArrivalsPerRound actors join per firing. Arrivals beyond the cap are NOT
                    // dropped: `reinforcementCursor` keeps its place and the loop below re-schedules
                    // the rest one full round later, so a large batch lands over several rounds rather
                    // than spiking one. FIFO order with ordinal tie-break was already established when
                    // `reinforcementQueue` was built (sorted once, up front).
                    trace?.Phase(rounds, "reinforcement");
                    reinforcementHandle = null; // it just fired; PopDue already removed it from roundQueue
                    var arrivalsThisFiring = 0;
                    while (reinforcementCursor < reinforcementQueue.Count
                           && arrivalsThisFiring < Board.SiegeTuningPolicy.Waves.MaxArrivalsPerRound
                           && reinforcementQueue[reinforcementCursor].AtTick <= roundClock.Now)
                    {
                        var next = reinforcementQueue[reinforcementCursor];
                        state.AddActor(next.Actor, position: null, rounds);
                        reinforcementCursor++;
                        arrivalsThisFiring++;
                    }

                    // A clearing that happened to land exactly on this same firing already consumed
                    // its own pulled-forward batch above -- reset so the NEXT clearing (for whatever
                    // remains queued) can pull forward again too, matching decision 6's "the field
                    // resolves, THEN the next batch enters" as a repeating cycle, not a one-time event.
                    fieldClearedTick = null;

                    if (reinforcementCursor < reinforcementQueue.Count)
                    {
                        // Carry-over continuation: one full round later, never immediately, so a
                        // large batch visibly lands over several rounds. If the NEXT queued arrival's
                        // own clock tick is later still, ScheduleNextReinforcement's own Math.Max/Min
                        // resolves the real due tick from there -- this floor only matters for the
                        // over-cap-at-one-tick case.
                        ScheduleNextReinforcement(roundClock.Now + activeProfile.RoundDurationMs);
                    }

                    state.Host.Flush();
                    state.PostFlush(rounds);
                    continue;
                }

                // RoundEventKind — steps 1 (regen only — status delivery moved off this call in
                // B16), 2 (initiative + attacks), 3/4 (death cleanup + shield upkeep).
                rounds++;
                // passive-tree G2 (spec-mechanism-wiring.md §4.2): recompose Derived from DerivedLedger
                // once per actor, at the very start of the round, before regen/initiative/attacks read
                // it. The only other call site is construction-time (the ActiveAuras loop); this is the
                // per-round half, so a mechanism that changed the ledger after construction actually
                // takes effect. Provably a no-op for every battle whose ledger hasn't changed since the
                // last call (BattleRunState.RecomposeDerivedForAllActors's own doc comment).
                state.RecomposeDerivedForAllActors();
                state.RunRegeneratorPulses();
                state.Host.Flush();
                trace?.Phase(rounds, "post-flush");
                state.PostFlush(rounds);
                if (!state.AnyActive("squad") || !state.AnyActive("wave"))
                    break;

                // 2) Initiative-ordered attacks: stable order, per-round jitter from the initiative
                //    stream; swift subtracts a full band so it always acts before non-swift kin.
                trace?.Phase(rounds, "initiative");
                // The initiative draw is hoisted out of the sort key so that BOTH orderings consume
                // the RNG identically: one draw per Active actor, in source order — T5 hazard 1, and
                // note that CC-locked actors are Active and therefore DO draw (hazard 4). That is not
                // tidying. If the speed-ordered path drew a different number of values, or drew them
                // in a different sequence, every downstream roll in the battle would shift and the
                // delta would no longer be attributable to turn ORDER alone.
                var jittered = new List<(ActorState Actor, int Jitter)>();
                foreach (var a in state.Actors)
                {
                    // base-defense `combatant-kind` §3: a structure never enters initiative — filtered
                    // at selection, before it can draw from the initiative stream or occupy a queue
                    // slot, rather than special-cased inside the turn machine. Byte-identical for every
                    // existing battle (every actor there is Animate, so this clause is always true).
                    if (!a.Active || a.Setup.Kind != CombatantKind.Animate) continue;
                    var roll = state.InitiativeRng.NextInt(1000);
                    trace?.Draw("initiative", roll);
                    jittered.Add((a, roll - (a.Has("swift") ? TraitBattleCatalog.Get("swift").InitiativeBonusMilli : 0)));
                }

                // B39 — turn order by readiness, when the PROFILE's own declared row says so
                // (`OrdersBySpeed`, never a branch on AdvancePolicyKind: adding a mode adds a row).
                //
                // `classic-round` takes the `else` and is byte-identical by construction, not by
                // luck: same draws, same key, same comparer as before this change. That is what keeps
                // every existing golden blessed.
                //
                // Fewer ticks-to-ready acts first. The initiative jitter stays as the TIE-BREAK rather
                // than being discarded — actors of equal speed are the common case, and dropping the
                // jitter there would replace a fair random order with setup-list order.
                var order = (activeProfile.OrdersBySpeed
                        ? jittered.OrderBy(x => (ReadyTicks(x.Actor), x.Jitter))
                        : jittered.OrderBy(x => (0L, x.Jitter)))
                    .Select(x => x.Actor)
                    .ToList();

                // B37 (spec-fsm-routing.md): the action phase is gated by the PROFILE's own turn
                // economy and action slots, so `Economy` and `W` stop being inert fields.
                //
                // `classic-round` is byte-identical BY CONSTRUCTION, not by luck:
                // OneActionPerTurnEconomy.TryAcquire is `_spent.Add(key)`, so every actor succeeds
                // exactly once on pass 1 and every actor fails on pass 2 — one action each, in
                // initiative order, which is precisely the loop this replaced. W=1/Global acquires and
                // releases around each sequential action and can never refuse, because with atomic
                // resolution a battle is already serialised regardless of W (ActionSlots' own doc).
                var economy = battleEconomy;
                // party-dungeon D2.11: a raid's own parties each get an INDEPENDENT PerSide budget
                // rather than sharing one across the whole "squad" side -- PartyIndex is null for
                // every existing caller, so this is byte-identical to "side:" + Side for all of them.
                string EconomyKey(ActorState a) =>
                    economy.Scope != Timeline.TurnEconomyScope.PerSide ? a.Setup.Key
                    : a.Setup.PartyIndex is { } partyIndex ? $"side:{a.Setup.Side}:p{partyIndex}"
                    : "side:" + a.Setup.Side;

                foreach (var a in order) economy.ResetForNewTurn(EconomyKey(a), roundClock.Now);

                // `battle-tempo` `timeline-dispatch` (D14, spec-timeline-dispatch.md §2.5): a declared
                // capability, never a branch on ProfileId/AdvancePolicyKind (the same discipline
                // OrdersBySpeed already established). False for every catalog row today, including
                // hybrid-atb -- this branch is reached only by a synthetic, never-shipped test profile.
                if (activeProfile.UsesTimelineDispatch)
                {
                    RunTimelineActionPhase(state, activeProfile, order, economy, EconomyKey, now, rounds, trace, intentSource, reactionLane, actionClock);
                }
                else
                {
                var slots = new Timeline.ActionSlots(activeProfile.W, activeProfile.WScope);
                var phaseBroken = false;
                bool anyActed;
                do
                {
                    anyActed = false;

                    // B38: readiness is offered at the START OF EVERY PASS, not once per round. That
                    // is what keeps the ECONOMY the thing deciding how many actions an actor gets: a
                    // one-action economy refuses the second acquire, while a points economy grants it
                    // and the actor is Ready to take it. Offering readiness only once would have
                    // silently capped every economy at one action — which it did, and the staged
                    // sweep caught it immediately.
                    // `classic-round` pins readiness to a constant (battle-turn-ideal.md §10), so all
                    // actors arrive together at the round tick rather than at staggered speed times.
                    foreach (var a in order)
                    {
                        var m = state.MachineFor(a.Setup.Key);
                        if (m.State != Timeline.TurnState.Charging) continue;
                        m.TransitionTo(Timeline.TurnState.Ready);
                        trace?.Turn(rounds, a.Setup.Key, Timeline.TurnState.Charging, Timeline.TurnState.Ready);
                    }

                    foreach (var attacker in order)
                    {
                        if (!attacker.Active) continue;
                        // B38: the turn-state gate comes FIRST, before any resource is taken. Checking
                        // it after `slots.TryAcquire` leaked a slot on every rejection — with W=1 that
                        // starves every later actor, which is a real bug this ordering removes rather
                        // than a style preference.
                        var machine = state.MachineFor(attacker.Setup.Key);
                        if (machine.State != Timeline.TurnState.Ready) continue;

                        if (!economy.TryAcquire(EconomyKey(attacker), 1, roundClock.Now)) continue;
                        if (!slots.TryAcquire(attacker.Setup.Key, attacker.Setup.Side)) continue;

                        // Ready -> Committed, the transition an interactive dwell would gate on.
                        machine.TransitionTo(Timeline.TurnState.Committed);
                        trace?.Turn(rounds, attacker.Setup.Key, Timeline.TurnState.Ready, Timeline.TurnState.Committed);
                        machine.TransitionTo(Timeline.TurnState.Resolving);
                        trace?.Turn(rounds, attacker.Setup.Key, Timeline.TurnState.Committed, Timeline.TurnState.Resolving);

                        AttackStep step;
                        try
                        {
                            // action-todo.md T13 (spec-basic-attack-adoption.md §1): the first four steps —
                            // active check, CC-lock, target, calculator.Compute — are the declared action
                            // `act.attack` (BasicAttack.cs). Everything below this call is EngineBehavior
                            // trait tail (E12), extracted to BattleRunState.DispatchHit (B13) but otherwise
                            // unchanged.
                            step = RunBasicAttackStep(attacker, state, now, roundClock.Now, state.Calculator, state.CritRng, trace, rounds, intentSource);
                        }
                        finally
                        {
                            // Always released: atomic resolution holds no slot across time, and a leaked
                            // slot would deadlock the first profile that ever gains wind-up.
                            slots.Release(attacker.Setup.Key);
                        }

                        // B38: the action is over either way — the cycle closes back to Charging so the
                        // actor is eligible again next round. Zero recovery ticks under classic-round,
                        // so Recovering is instantaneous rather than absent.
                        machine.TransitionTo(Timeline.TurnState.Recovering);
                        trace?.Turn(rounds, attacker.Setup.Key, Timeline.TurnState.Resolving, Timeline.TurnState.Recovering);
                        machine.TransitionTo(Timeline.TurnState.Charging);
                        trace?.Turn(rounds, attacker.Setup.Key, Timeline.TurnState.Recovering, Timeline.TurnState.Charging);

                        if (step.Outcome == AttackStepOutcome.Continue) continue;
                        // `Break` ends the whole action phase (hazard 3), exactly as before — it must
                        // escape BOTH loops, or a round that should end early would keep going.
                        if (step.Outcome == AttackStepOutcome.Break) { phaseBroken = true; break; }
                        // A builder that found no live target instead placed a structure (15.3d) — the
                        // round keeps going for the other actors, exactly like a normal completed action,
                        // but there is no target to dispatch a hit against.
                        if (step.Outcome == AttackStepOutcome.ActedWithNoTarget) { anyActed = true; continue; }

                        state.DispatchHit(attacker, step.Target!, step.SignedDelta, rounds);
                        economy.OnActionResolved(EconomyKey(attacker), Timeline.ActionResolutionOutcome.Normal);
                        anyActed = true;
                    }
                }
                while (anyActed && !phaseBroken);
                }

                // B38: anyone still Ready never got a turn this round (no budget, no slot, or the phase
                // broke). `Ready -> Charging` is the kernel's own "passed turn" edge — it must be taken
                // rather than left dangling, or the actor would be stuck Ready and skipped forever.
                foreach (var a in order)
                {
                    var machine = state.MachineFor(a.Setup.Key);
                    if (machine.State != Timeline.TurnState.Ready) continue;
                    machine.TransitionTo(Timeline.TurnState.Charging);
                    trace?.Turn(rounds, a.Setup.Key, Timeline.TurnState.Ready, Timeline.TurnState.Charging);
                }

                // 3) Death cleanup happens inline (Hp gate); 4) shield upkeep AFTER dispatch —
                // an expiring shield still absorbed this round's damage (shield spec order).
                //
                // party-dungeon D2.21: behind DownedOnDeplete, a PartyIndex-carrying actor at Hp <= 0
                // goes to TurnState.Downed instead of the withdraw-entity/clear-shields path below —
                // "still present, targetable, and revivable" (spec-delve-attrition.md §6), so nothing
                // here wipes its status bag or shields while it is only downed, not dead. Every wave
                // actor, and every actor under every other profile (DownedOnDeplete false on all of
                // them), keeps today's exact path — the branch below is unreachable for them.
                foreach (var a in state.Actors)
                {
                    if (a.Alive) continue;

                    if (activeProfile.DownedOnDeplete && a.Setup.PartyIndex is not null)
                    {
                        var machine = state.MachineFor(a.Setup.Key);
                        var fromState = machine.State;
                        if (fromState != Timeline.TurnState.Downed)
                        {
                            machine.TransitionTo(Timeline.TurnState.Downed);
                            trace?.Turn(rounds, a.Setup.Key, fromState, Timeline.TurnState.Downed);
                            a.WentDowned = true;
                        }
                        continue;
                    }

                    state.WithdrawStatusHost(a.Setup.Key);
                    state.Shields.RemoveAll(Contracts.EffectOwnerKeys.Entity(a.Setup.Key));
                }

                trace?.Phase(rounds, "shield-upkeep");
                // B17: true ms tick, not the round counter — matches DurationTicks now carrying
                // true ms (BattleRunState.cs), so an innate shield expires at its authored duration
                // instead of being silently extended to the next whole round boundary.
                state.Shields.Tick(roundClock.Now, activeProfile.RoundDurationMs, ownerKey =>
                {
                    var key = ownerKey.StartsWith("entity:", StringComparison.Ordinal)
                        ? ownerKey.Substring("entity:".Length)
                        : ownerKey;
                    return state.ByKey.TryGetValue(key, out var a) ? a.Derived : ActorDerivedSnapshot.AttackerLess();
                });
                state.DrainShieldEvents(rounds);
                if (trace != null)
                    foreach (var a in state.Actors)
                        trace.State(rounds, a.Setup.Key, a.Hp, a.ShieldAbsorbed);

                // Schedule the NEXT round only now — mirrors the original while-header's
                // re-evaluated `rounds < Max && bothActive` exactly, just checked once per round
                // instead of once per loop entry, since `rounds` here already holds the round that
                // just finished.
                if (rounds < activeProfile.MaxRounds && state.AnyActive("squad") && state.AnyActive("wave"))
                    roundQueue.Schedule(roundClock.Now + activeProfile.RoundDurationMs, RoundEventOwnerKey, RoundEventKind, 0);

                // base-defense `siege-waves` §1 (F8's state half): checked once per round boundary,
                // the same cadence `SiegeObjective.Evaluate` is specced to use and for the same
                // reason -- checking mid-round would let the order of two simultaneous deaths decide
                // whether a clearing pulled a batch forward. No-op (returns immediately) whenever no
                // reinforcement is queued, which is every existing battle.
                CheckFieldCleared();
            }
        }

        state.DrainShieldEvents(rounds);   // trailing grants/absorbs when the loop exits mid-round

        var outcome = !state.AnyActive("wave") ? BattleOutcome.Victory
            : !state.AnyActive("squad") ? BattleOutcome.Defeat
            : BattleOutcome.Stalemate;

        var greedyDef = TraitBattleCatalog.Get("greedy");
        var geniusDef = TraitBattleCatalog.Get("genius");
        var greedySurvivors = state.Actors.Count(a =>
            a.Setup.Side == "squad" && a.Alive && a.Has("greedy"));

        state.RaiseLifecycle(Effects.Atoms.AtomTriggers.OnMatchEnd);

        return new BattleReport
        {
            Seed = seed,
            WaveId = setup.WaveId,
            Outcome = outcome,
            Rounds = rounds,
            ZombossPatternId = setup.ZombossPatternId,
            SoulLootMilli = 1000 + greedyDef.SoulLootBonusMilli * greedySurvivors,
            Warnings = state.Warnings.Count > 0 ? state.Warnings : null,
            Events = state.Events,
            Actors = state.Actors.Select(a => new BattleActorResult(
                a.Setup.Key, a.Setup.Side, a.Setup.SpeciesId, a.Setup.TypeId,
                a.Hp, a.DamageDealt, a.Kills, a.Alive, a.Retreated,
                a.Has("genius") ? 1000 + geniusDef.SpecimenXpBonusMilli : 1000,
                a.ShieldAbsorbed)
            { EquippedActionIds = a.Setup.EquippedActionIds, WentDowned = a.WentDowned }).ToList()
        };
    }

    static readonly string[] EssenceTraits = { "void-touched", "chaos-marked" };

    static bool IsCcLocked(StatusRuntime status, string actorKey, DateTimeOffset now)
    {
        foreach (var inst in status.ForHost(actorKey))
        {
            // Asks what the status DOES, not how it is delivered (E17). This tested `Kind`, and
            // `Kind` conflates semantic role with execution path — so every `UnityCc` status locked
            // the actor out of its turn, including `poison`, which is damage over time. Of the nine
            // statuses the old check caught, eight are categorised `cc` and exactly one is not.
            if (inst.IsCrowdControl && inst.ExpiresAt >= now)
                return true;
        }

        return false;
    }

    /// <summary>
    /// base-defense `combatant-kind` §2: structures do not count. Otherwise an indestructible fence on
    /// the defender's side keeps every siege alive to `MaxRounds` and turns every victory into a
    /// stalemate — a wall is a fact of the ground, not an enemy that must be beaten. Byte-identical for
    /// every existing battle: every actor built before this module defaults to
    /// <see cref="CombatantKind.Animate"/>, so the added clause is always true there.
    /// </summary>
    static bool AnyActive(List<ActorState> actors, string side) =>
        actors.Any(a => a.Active && a.Setup.Side == side && a.Setup.Kind == CombatantKind.Animate);

    /// <summary>
    /// **B39** — how many ticks this actor needs to be ready for one turn: the readiness kernel's own
    /// math, `TicksFor(OneTurnWork, EffectiveRate(speed, haste))`. Lower acts sooner.
    ///
    /// <para><b>Both channels are clamped here, and the clamp is required rather than defensive.</b>
    /// <c>EffectiveRate</c> throws on a non-positive speed or haste — the readiness spec's "speed
    /// clamped before division" rule — and an actor with no authored <c>turn.speed</c> reads 0 from the
    /// snapshot, which is every actor today. So an unclamped call would throw on the first ordinary
    /// battle. The fallbacks are the declared defaults, not invented numbers:
    /// <see cref="Stats.Derived.DerivedStatPolicy.TurnDefaultSpeed"/> (config) and
    /// <see cref="Timeline.DerivedTurnChannels.NominalHasteMilli"/> (structural, 1000 = unity).</para>
    ///
    /// <para><b>`long`, and rounded rather than truncated.</b> `turn.speed` is a magnitude the power
    /// ladder can drive, so it follows the repo's magnitude rule (`AGENTS.md`: `long`, never `float`);
    /// the snapshot stores doubles, so the narrowing happens once, here, at the boundary — and
    /// truncation would round a speed of 99.9 down to 99, making a faster actor read as slower.</para>
    /// </summary>
    static long ReadyTicks(ActorState a)
    {
        var speed = (long)Math.Round(a.Derived.Get(Timeline.DerivedTurnChannels.Speed));
        if (speed <= 0) speed = Stats.Derived.DerivedStatPolicy.TurnDefaultSpeed;

        var haste = (long)Math.Round(a.Derived.Get(Timeline.DerivedTurnChannels.Haste));
        if (haste <= 0) haste = Timeline.DerivedTurnChannels.NominalHasteMilli;

        return Timeline.TurnReadiness.TicksFor(
            Timeline.TurnReadiness.OneTurnWork,
            Timeline.TurnReadiness.EffectiveRate(speed, haste));
    }

    /// <summary>First active same-side setup-order neighbor (index ±1) carrying the trait.</summary>
    static ActorState? FindAdjacentWithTrait(List<ActorState> actors, ActorState around, string traitId)
    {
        foreach (var a in actors)
        {
            if (!a.Active || ReferenceEquals(a, around)) continue;
            if (a.Setup.Side != around.Setup.Side) continue;
            if (Math.Abs(a.SideIndex - around.SideIndex) != 1) continue;
            if (a.Has(traitId))
                return a;
        }

        return null;
    }
}
