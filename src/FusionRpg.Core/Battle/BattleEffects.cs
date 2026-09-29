using FusionRpg.Contracts;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Battle.Board;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Status;
using FusionRpg.Core.World;
using FusionRpg.Core.World.District;

namespace FusionRpg.Core.Battle;

/// <summary>HP surface the battle sink mutates — implemented by engine actor state (and test fakes).</summary>
public interface IBattleHpTarget
{
    long Hp { get; set; }
    long MaxHp { get; }
}

/// <summary>
/// A18e (spec-battle-live-stat-modifiers.md §4): the narrow surface `BattleEffectSink`'s `ModifyStat`
/// branch needs — `ActorState` is private to `BattleEngine` (a different, unrelated top-level class
/// from this file), the same reason `IBattleHpTarget` above exists rather than naming `ActorState`
/// directly. `Derived` is already mutable (`ActorDerivedSnapshot.Set` is `internal`, reachable
/// anywhere in this assembly) — this interface only needs to expose the reference and the baseline
/// this module recomposes against.
/// </summary>
public interface IBattleStatTarget
{
    ActorDerivedSnapshot Derived { get; }
    long BaselineDefense { get; }

    /// <summary>
    /// Frozen initial Hub output used only by the ledger's isolated compatibility tests. Production
    /// live recomposition calls <c>BattleHubCompose.Resolve</c> with the original setup plus the
    /// current sourced bound atoms, then copies that complete Hub output.
    /// </summary>
    ActorDerivedSnapshot BaseDerived { get; }
}

/// <summary>One FA10 window result: the clamped delta actually applied to an actor.</summary>
public sealed record BattleAppliedHpDelta(string ActorKey, long Amount, int MergedCount);

/// <summary>
/// Battle-local effect stack (spec-match-source-core.md): a private EffectBag + EffectFunnel whose
/// FA10 sink mutates engine-owned battle state instead of Unity. Merge semantics, |amount| caps and
/// mailbox caps are the shipped Funnel's — bypassing it would fork combat numbers between modes.
/// Battle state is memory; the Unity writer paths stay PvZ-only.
/// </summary>
public sealed class BattleEffectHost : IDeclaresExecution
{
    readonly EffectFunnel _funnel;
    readonly BattleEffectSink _sink;
    readonly Func<string, IBattleHpTarget?> _resolve;
    readonly Dictionary<string, long> _pendingHpDeltas = new(StringComparer.Ordinal);
    int _flushDepth;

    public BattleEffectHost(Func<string, IBattleHpTarget?> resolveActor, ulong rngSeed)
    {
        // A18d (spec-battle-status-apply.md §1): Clock built BEFORE _sink now (swapped from the
        // original order) so BattleEffectSink can hold a live reference to it -- BattleRunState's own
        // constructor builds Host (and so this Host's own Clock) two lines before Status/StatusRng
        // even exist (BattleRunState.cs:115 vs 117/103), which is why those two are wired via the
        // settable properties below rather than threaded through this constructor at all. Purely
        // internal reordering: neither Clock nor _sink is read before both complete.
        _resolve = resolveActor ?? throw new ArgumentNullException(nameof(resolveActor));
        Clock = new FakeEffectClock();
        _sink = new BattleEffectSink(resolveActor, Clock);
        var catalog = new InMemoryEffectCatalog();
        catalog.ReplaceAll(EffectAtomCatalog.CreateAll());
        Bag = new EffectBag(catalog, new InMemoryEffectGrantStore(),
            new EffectProcPolicy(Clock, new BattleEffectRandom(rngSeed)), _sink);
        Bag.UtcNow = () => Clock.UtcNow;
        _funnel = new EffectFunnel(Bag);
    }

    public EffectBag Bag { get; }
    public FakeEffectClock Clock { get; }
    public EffectFunnel Funnel => _funnel;

    /// <summary>combat-ai `resolvable-here` (CAI1.12, spec-resolvable-here.md §1): the host's own
    /// declaration, forwarded from the sink that owns the dispatch table. Nothing else in battle
    /// declares an opcode set.</summary>
    public IReadOnlySet<string> ExecutedActions => _sink.ExecutedActions;

    /// <summary>
    /// combat-ai `resolvable-here` (CAI1.12, spec-resolvable-here.md §1): every trigger battle
    /// actually raises. Read from THIS tree, not from a doc — `spec-resolvable-here.md`'s own §1 and §5
    /// say four (`OnActivate`, `OnDamageDealt`, the bag lifecycle pair), but `BattleRunState` and
    /// `BattleEngine` in fact raise all eleven below (`RaiseTrigger`/`RaiseLifecycle` call sites), so
    /// the four-trigger reading is a stale subset and is corrected here (see the finding note on the
    /// task). Declaring the stale subset would make this filter veto real content whose trigger DOES
    /// fire.
    ///
    /// <para>The two absentees are lawn-only: `OnSunCollect` and `OnGridPlace` have no battle raise
    /// site. The source-scan test `Every_trigger_battle_raises_appears_in_its_declared_trigger_set`
    /// scans every `AtomTriggers.X` reference under `gk-core/src/FusionRpg.Core/Battle/**` — the stronger form
    /// of the check the spec names (it also catches `RaiseLifecycle`/`RaiseTrigger` arguments, which a
    /// `Trigger = AtomTriggers.` scan alone would miss) — so a twelfth raise site fails the test rather
    /// than silently falling through this array.</para>
    ///
    /// <para>`OnGranted`/`OnRemoved` are raised by `EffectBag` on the grant/withdraw lifecycle, which
    /// lives outside `Battle/**` and is shared by every runtime, so they are declared here explicitly
    /// rather than found by the scan.</para>
    /// </summary>
    public static readonly string[] RaisedTriggers =
    {
        AtomTriggers.OnSpawn,
        AtomTriggers.OnDamageDealt,
        AtomTriggers.OnDamageTaken,
        AtomTriggers.OnDeath,
        AtomTriggers.OnActivate,
        AtomTriggers.OnTimer,
        AtomTriggers.OnWave,
        AtomTriggers.OnMatchStart,
        AtomTriggers.OnMatchEnd,
        AtomTriggers.OnGranted,
        AtomTriggers.OnRemoved,
    };

    /// <summary>
    /// A25 (battle-runner-path-integration): the Secondary runner (E15), once bindings are installed —
    /// null until <see cref="UseRunner"/>, matching <c>SimEffectHost.Runner</c>'s own "no runner atoms,
    /// no runner state" shape exactly, not a new convention.
    /// </summary>
    public AtomRunner? Runner { get; private set; }

    /// <summary>
    /// Installs runner bindings and builds the trigger index — the same construction
    /// <c>SimEffectHost.UseRunner</c> already uses (proc/apply streams derived from one run seed, so a
    /// gate roll can never shift a magnitude roll, E2's named streams), generalized to battle. Called
    /// from <c>onEffectHostReady</c> (the same seam A24's <c>ActionContainerEffectResolverFactory</c>
    /// already uses to push compiled defs), so a battle with no runner-path atom carries no runner
    /// state at all — byte-identical to every caller that never invokes this.
    ///
    /// <para><paramref name="nowMs"/> is the caller's own <c>state.NowTick</c> reader, deliberately NOT
    /// this host's own <see cref="Clock"/> — verified by reading, not assumed: <see cref="Clock"/>'s
    /// `UtcNow` is set once at construction and never advanced anywhere in `BattleRunState` (both its
    /// own real call sites only READ it), so an ICD keyed off it would see a constant "now" forever —
    /// ready exactly once, at proc time, then never again for the rest of the battle. `NowTick` is the
    /// real, monotonically-advancing, millisecond-scaled clock every other timed battle mechanism
    /// (windup/recovery/cooldown) already reads.</para>
    /// </summary>
    public AtomRunner UseRunner(IEnumerable<RunnerBinding> bindings, ulong runSeed, Func<long> nowMs, string matchKey = "") =>
        Runner = new AtomRunner(
            _funnel, TriggerIndex.Build(bindings),
            new AtomRandom(runSeed, AtomStreams.Proc),
            new AtomRandom(runSeed, AtomStreams.Apply),
            nowMs,
            matchKey);

    /// <summary>A18d: forwards to <see cref="BattleEffectSink"/>'s own settable property, the same
    /// "wire after the dependency exists" shape T14 already used for <c>Bag.ShieldGate</c> and A18c
    /// for <c>Bag.Status</c>/<c>Bag.StatusRng</c> — this constructor's own signature stays
    /// unchanged, so neither existing call site needs to change.</summary>
    public StatusRuntime? Status { set => _sink.Status = value; }

    /// <summary>A18d: see <see cref="Status"/> above — the same shape, one property over.</summary>
    public IStatusRng? StatusRng { set => _sink.StatusRng = value; }

    /// <summary>A18e: the same forwarding shape, one more property — see <see cref="Status"/>'s own
    /// doc comment for why this cannot be a constructor parameter.</summary>
    public BattleStatModifierLedger? Ledger { set => _sink.Ledger = value; }

    /// <summary>W11 (battle-derived-wire T6): the <c>combat.*</c> contribution ledger, forwarded so
    /// <c>ExecModifyStat</c>'s <c>defense</c> branch can push through the SAME single writer a
    /// status's own <c>defense</c> StatMod uses — <see cref="BattleStatModifierLedger.PushDefenseToDerived"/>.
    /// Set-only, like <see cref="Ledger"/> above: only the private sink's own executor reads it back.</summary>
    public BattleDerivedModifierLedger? DerivedLedger { set => _sink.DerivedLedger = value; }

    /// <summary>A18e: <see cref="IBattleStatTarget"/>'s own resolver — `resolveActor` (the ctor
    /// parameter) is insufficient here, since `owner.Derived`/baseline Defense are not on
    /// <see cref="IBattleHpTarget"/>.</summary>
    public Func<string, IBattleStatTarget?>? ResolveStatTarget { set => _sink.ResolveStatTarget = value; }

    /// <summary>
    /// base-defense `siege-construction` (decision 27, 2026-09-06): wired post-construction, the same
    /// shape as <see cref="Status"/> above — `structure.place`'s own executor needs it, and
    /// `BattleEffectSink` is private to this host. Null (the default) for every battle without a board,
    /// which is every existing caller until `DistrictAssaultResolver` sets it — `structure.place` then
    /// refuses quietly rather than throwing, the same posture <see cref="ExecApplyStatus"/>'s own
    /// unwired case already establishes.
    ///
    /// <para><b>Gained a public getter 2026-09-07 (siege-ai), unlike its write-only siblings above.</b>
    /// Every other forward on this host (`Status`/`StatusRng`/`Ledger`/`ResolveStatTarget`) is
    /// deliberately write-only because only `BattleEffectSink`'s own private executor methods ever
    /// need to read them back. This one now has a genuinely different, real caller:
    /// `BasicAttack.DeclareBasicAttack`'s own construction-choice branch needs to READ the board
    /// BEFORE deciding whether to fire anything, not just hand it to an executor that fires
    /// unconditionally — a need that did not exist when this property was first wired.</para>
    /// </summary>
    public ConstructionBoardContext? ConstructionBoard
    {
        get => _sink.ConstructionBoard;
        set => _sink.ConstructionBoard = value;
    }

    /// <summary>passive-tree G2 (spec-mechanism-wiring.md §4.2): the one forward this host needs so a
    /// live mid-battle trigger can add a contribution, matching `BattleDerivedModifierLedger.Add`'s own
    /// signature exactly (actorKey, channel, sourceId, value) — every other trigger this class forwards
    /// (Status/StatusRng/Ledger above) is a full object handed to the private sink; this one is
    /// narrower (a single method, not the whole ledger) because nothing inside `BattleEffectHost` needs
    /// to CONSULT the ledger the way `BattleEffectSink` consults `Ledger` for `stat.modify` — only to
    /// ADD to it, exactly the one operation aura-skill T13's still-unbuilt live toggle will need to
    /// call. Get-set (unlike the set-only forwards above) because a caller needs to INVOKE it, not just
    /// hand it to a private sink.</summary>
    public Action<string, string, string, double>? AddDerivedContribution { get; set; }

    /// <summary>
    /// base-defense `siege-ai` R3 (2026-09-07): which board edge the attacker entered from, for
    /// `BattleRunState.ObjectivePositionOf` alone — no `BattleEffectSink` executor ever reads this, so
    /// it lives directly on the host rather than forwarded through `_sink` the way `ConstructionBoard`
    /// is (that one's own executor DOES need it). `null` (the default) for every battle before
    /// `DistrictAssaultResolver` sets it — every non-siege battle, byte-identical.
    /// </summary>
    public BoardEdge? AttackerEdge { get; set; }

    /// <summary>Deltas actually applied in the last flush window (clamped to [0, MaxHp]).</summary>
    public IReadOnlyList<BattleAppliedHpDelta> LastApplied => _sink.Applied;

    public bool QueueHpDelta(string actorKey, long amount, string? effectId = null, string? grantId = null)
    {
        var ownerKey = EffectOwnerKeys.Entity(actorKey);
        var accepted = _funnel.EnqueueMutation(ownerKey, amount,
            pluginId: "battle",
            effectId: string.IsNullOrWhiteSpace(effectId) ? "battle.hp_delta" : effectId,
            grantId: grantId);
        if (accepted) ProjectRetainedDamage(ownerKey, amount);
        return accepted;
    }

    /// <summary>
    /// Reports how much of an accepted damage delta the battle target can actually retain in the
    /// current Funnel window. Pending deltas are included because opposite-sign mutations coalesce
    /// before the sink runs. This changes observation only: the requested signed amount still goes
    /// through the Funnel unchanged.
    /// </summary>
    public long ProjectRetainedDamage(string ownerKey, long amount)
    {
        var actorKey = ActorKeyFromOwner(ownerKey);
        var target = _resolve(actorKey);
        if (target == null) return 0;

        var pending = _pendingHpDeltas.GetValueOrDefault(actorKey);
        var projectedBefore = ClampHp(checked(target.Hp + pending), target.MaxHp);
        var projectedAfter = ClampHp(checked(projectedBefore + amount), target.MaxHp);
        _pendingHpDeltas[actorKey] = checked(pending + amount);
        return projectedAfter - projectedBefore;
    }

    public void Flush()
    {
        if (_flushDepth > 0) return;
        _flushDepth++;
        try
        {
            _sink.Applied.Clear();
            _funnel.Flush();
            _funnel.AcknowledgeWindow();
        }
        finally
        {
            _flushDepth--;
            if (_flushDepth == 0) _pendingHpDeltas.Clear();
        }
    }

    static string ActorKeyFromOwner(string ownerKey) =>
        ownerKey.StartsWith("entity:", StringComparison.OrdinalIgnoreCase)
            ? ownerKey["entity:".Length..]
            : ownerKey;

    static long ClampHp(long value, long maxHp) => Math.Min(maxHp, Math.Max(0L, value));

    sealed class BattleEffectSink : IEffectActionSink, IDeclaresExecution
    {
        readonly Func<string, IBattleHpTarget?> _resolve;
        readonly FakeEffectClock _clock;

        /// <summary>
        /// combat-ai `resolvable-here` (CAI1.12, spec-resolvable-here.md §1): THE dispatch table — the
        /// single object that both routes a plan item and answers <see cref="ExecutedActions"/>. The
        /// allowlist cannot drift from the executor because it IS the executor; there is no second list
        /// beside it. Keyed <see cref="StringComparer.OrdinalIgnoreCase"/>, exactly the comparison the
        /// four if-branches this replaces used.
        /// </summary>
        readonly Dictionary<string, Func<EffectExecuteContext, EffectActionPlanItem, bool>> _handlers;

        /// <summary>Derived from <see cref="_handlers"/>'s own keys, never a second declaration.</summary>
        HashSet<string>? _executed;

        public BattleEffectSink(Func<string, IBattleHpTarget?> resolve, FakeEffectClock clock)
        {
            _resolve = resolve;
            _clock = clock;
            _handlers = new Dictionary<string, Func<EffectExecuteContext, EffectActionPlanItem, bool>>(StringComparer.OrdinalIgnoreCase)
            {
                // A18d (spec-battle-status-apply.md §1): FA2, distinct from resource.delta's own
                // DoT/contagion piggyback (A18c, a different branch of FireGrant entirely -- this action
                // is a standalone plan item, not a byproduct of ApplyResourceDelta).
                [EffectActions.ApplyStatus] = ExecApplyStatus,
                // A18e (spec-battle-live-stat-modifiers.md §4): FA1, a third standalone plan item action.
                [EffectActions.ModifyStat] = ExecModifyStat,
                // base-defense `siege-construction` (decision 27, 2026-09-06): a fourth standalone plan
                // item action -- `structure.place` is Battle-only by construction (AttachPoint.Siege), so
                // it belongs on the SAME allowlist as the other three rather than a separate mechanism.
                [EffectActions.PlaceStructure] = ExecPlaceStructure,
                // A18c/FA10 -- the branch that used to be inlined at the bottom of Execute.
                [EffectActions.ApplyResourceDelta] = ExecApplyResourceDelta,
            };
        }

        /// <summary>A18d: wired post-construction via <see cref="BattleEffectHost.Status"/> — see
        /// that property's own doc comment for why this cannot be a constructor parameter.</summary>
        public StatusRuntime? Status { get; set; }
        public IStatusRng? StatusRng { get; set; }

        /// <summary>A18e: wired post-construction via <see cref="BattleEffectHost.Ledger"/>/
        /// <see cref="BattleEffectHost.ResolveStatTarget"/> — same shape.</summary>
        public BattleStatModifierLedger? Ledger { get; set; }

        /// <summary>W11 (battle-derived-wire T6): see <see cref="BattleEffectHost.DerivedLedger"/>.</summary>
        public BattleDerivedModifierLedger? DerivedLedger { get; set; }
        public Func<string, IBattleStatTarget?>? ResolveStatTarget { get; set; }

        /// <summary>base-defense `siege-construction`: wired post-construction via
        /// <see cref="BattleEffectHost.ConstructionBoard"/> — same shape.</summary>
        public ConstructionBoardContext? ConstructionBoard { get; set; }

        public List<BattleAppliedHpDelta> Applied { get; } = new();

        /// <summary>
        /// combat-ai `resolvable-here` (CAI1.12, spec-resolvable-here.md §1): ONE table lookup. An
        /// opcode the table does not name is INERT — `return true`, never a failure — the identical
        /// fall-through the if-chain this replaces had: "battle mode consumes ApplyResourceDelta (FA10) /
        /// ApplyStatus (FA2) / ModifyStat (FA1) / PlaceStructure (Siege) only; every other action is
        /// inert here". Ordinal-ignore-case comes from the table's own comparer.
        /// </summary>
        public bool Execute(EffectExecuteContext ctx, EffectActionPlanItem item) =>
            _handlers.TryGetValue(item.Action, out var handler) ? handler(ctx, item) : true;

        /// <summary>combat-ai `resolvable-here` (CAI1.12, spec-resolvable-here.md §1): this place's
        /// opcode allowlist, read off the same table <see cref="Execute"/> dispatches through.</summary>
        public IReadOnlySet<string> ExecutedActions =>
            _executed ??= new HashSet<string>(_handlers.Keys, StringComparer.OrdinalIgnoreCase);

        /// <summary>A18c/FA10's own body, moved verbatim out of `Execute`'s former inline last branch
        /// so the dispatch table can name it. No statement changed.</summary>
        bool ExecApplyResourceDelta(EffectExecuteContext ctx, EffectActionPlanItem item)
        {
            var ptr = item.Params.TryGetValue("targetPtr", out var p) ? p as string : null;
            if (string.IsNullOrWhiteSpace(ptr))
                return true;
            var target = _resolve(ptr!);
            if (target == null)
                return true;

            var amount = item.Params.TryGetValue("amount", out var a) ? Convert.ToInt64(a) : 0L;
            var mergedCount = item.Params.TryGetValue("mergedCount", out var m) ? Convert.ToInt32(m) : 1;

            var before = target.Hp;
            var after = ClampHp(checked(before + amount), target.MaxHp);
            target.Hp = after;
            Applied.Add(new BattleAppliedHpDelta(ptr!, after - before, mergedCount));
            return true;
        }

        bool ExecApplyStatus(EffectExecuteContext ctx, EffectActionPlanItem item)
        {
            if (Status is null || StatusRng is null) return true; // not wired (e.g. a bare test harness) -- refuse quietly, not a NullReferenceException

            var statusId = item.Params.TryGetValue("status", out var s) ? s as string : null;
            if (string.IsNullOrWhiteSpace(statusId)) return true; // malformed content, refused upstream at bind

            var durationSec = item.Params.TryGetValue("duration", out var d) ? Convert.ToDouble(d) : 4.0;
            var durationMs = (int)Math.Round(durationSec * 1000);
            var targetPtr = item.Params.TryGetValue("targetPtr", out var p) ? p as string : ctx.Event.TargetPtr;
            if (string.IsNullOrWhiteSpace(targetPtr)) return true;

            // BaseDuration and DurationMs are the SAME unit (ms) -- found empirically: StatusRuntime.Apply
            // uses eval.EffectiveDuration (derived FROM BaseDuration) whenever BaseDuration > 0, so
            // passing durationSec (seconds) here produced a 5ms status for an authored 5-SECOND
            // duration. Verified against the existing scripted-InitialStatuses call
            // (BattleRunState.cs), which already passes the identical ms value to both fields.
            Status.Apply(new StatusApplyInput(
                StatusId: statusId!,
                HostPtr: targetPtr!,
                AttackerPtr: ctx.Event.ActorPtr,
                GrantId: item.GrantId,
                BaseMagnitude: 0, // FA2 never pulses HP -- that payload lives on FA10 resource.delta (A18c)
                BaseDuration: durationMs,
                DurationMs: durationMs,
                GrantChance: 1.0, // the atom's own bind-time chance gate already decided whether this plan item exists
                EffectId: item.EffectId,
                PluginId: "battle",
                AttackerLess: ctx.Event.ActorPtr is null), StatusRng, _clock.UtcNow);
            return true;
        }

        bool ExecModifyStat(EffectExecuteContext ctx, EffectActionPlanItem item)
        {
            if (Ledger is null || ResolveStatTarget is null) return true; // not wired -- refuse quietly, same posture as Status/StatusRng above

            var channel = item.Params.TryGetValue("channel", out var c) ? c as string : null;
            if (string.IsNullOrWhiteSpace(channel)) return true; // malformed content, refused upstream at bind

            // Found against real shipped content (fx.passive_atk_flat), not the atom kind's own
            // authoring-time schema: EffectOverlayMerge's own ModifyStat allowlist ("channel", "flat",
            // "increased", "more", ...) uses THREE SEPARATE, independently-optional keys, never a
            // combined "op"+"amount" pair. A first draft assumed op+amount (matching the ATOM
            // schema's own authoring-time param names, AtomKindRegistry.cs) and silently no-oped on
            // every real grant -- AtomCompiler translates op+amount into whichever of these three keys
            // at compile time; by the time a plan item reaches this sink, it is already in this shape.
            // One action may carry more than one key at once (e.g. a flat AND an increased together).
            var ownerKey = ctx.Grant.OwnerKey.StartsWith("entity:", StringComparison.Ordinal)
                ? ctx.Grant.OwnerKey["entity:".Length..] : ctx.Grant.OwnerKey;
            var owner = ResolveStatTarget(ownerKey);
            if (owner is null) return true; // no live actor under this key (e.g. already dead)

            var pluginId = item.Tags.GetValueOrDefault("plugin", "battle");
            var any = false;
            void AddIfPresent(string key, Stats.ModifierOp op)
            {
                if (!item.Params.TryGetValue(key, out var raw) || raw is null) return;
                Ledger!.Add(ownerKey, channel!, item.GrantId, new Stats.StatModifier
                {
                    Channel = channel!, Op = op, Value = Convert.ToDouble(raw), SourceId = item.GrantId, PluginId = pluginId,
                });
                any = true;
            }
            // "override" is refused at bind (AtomKindRegistry.Validate) -- no case for it here.
            AddIfPresent("flat", Stats.ModifierOp.Flat);
            AddIfPresent("increased", Stats.ModifierOp.Increased);
            AddIfPresent("more", Stats.ModifierOp.More);
            if (!any) return true; // malformed content: channel present, no recognised op key at all

            // "atk" needs no push here -- ActorState.LiveAtk recomposes on every read (A18e §2).
            // W11 (battle-derived-wire T6): defense goes through the ONE writer
            // (`BattleStatModifierLedger.PushDefenseToDerived`), the same one a status's own
            // `defense` StatMod uses, so there is one gate on `combat.defense.omni` -- and so the
            // value survives the per-round `BattleDerivedModifierLedger.Recompose` instead of being
            // overwritten by it.
            if (string.Equals(channel, BattleStatModifierLedger.DefenseChannel, StringComparison.Ordinal)
                && DerivedLedger is not null)
                BattleStatModifierLedger.PushDefenseToDerived(ownerKey, Ledger!, DerivedLedger, owner);

            return true;
        }

        /// <summary>
        /// base-defense `siege-construction` (decision 27, 2026-09-06): `structure.place`'s executor.
        /// Validates through the SAME <see cref="ConstructionPlacement.CanPlace"/> gate every one of the
        /// four acquisition paths shares (§6), then occupies the cell for the REST of this battle
        /// (blocking movement/pathing immediately) and records the placement for
        /// <see cref="DistrictAssaultResolver"/> to read back once <c>BattleEngine.Resolve</c> returns.
        ///
        /// <para><b>Named, deliberate simplification</b>: the newly-placed structure does NOT become a
        /// fightable <c>CombatantKind.Structure</c> actor within THIS SAME battle — the actor list is
        /// built once, up front, from the setup's own Squad/Wave, and is not designed to grow mid-fight.
        /// It occupies its cell (so pathing/adjacency
        /// for the rest of THIS engagement already sees it as real ground), and becomes a real,
        /// fightable structure with correct HP from the NEXT engagement onward, once the world layer
        /// has persisted it and <c>DistrictAssaultResolver.PlaceStructures</c> rebuilds the board fresh
        /// — the same "board rebuilt from world truth every engagement" model decision 24's own
        /// siege-spans-turns fix already established.</para>
        /// </summary>
        bool ExecPlaceStructure(EffectExecuteContext ctx, EffectActionPlanItem item)
        {
            if (ConstructionBoard is null) return true; // not wired (e.g. a bare test harness) -- refuse quietly

            var structureId = item.Params.TryGetValue("structureId", out var s) ? s as string : null;
            if (string.IsNullOrWhiteSpace(structureId) || !StructureCatalog.IsKnown(structureId))
                return true; // malformed content, refused upstream at bind

            var instant = item.Params.TryGetValue("instant", out var i) && Convert.ToBoolean(i);

            var builderPtr = ctx.Event.ActorPtr;
            if (string.IsNullOrWhiteSpace(builderPtr)
                || !ConstructionBoard.Board.Positions.TryGetValue(builderPtr, out var builderPos))
                return true; // no live builder position -- cannot validate adjacency

            if (ctx.Event.TargetRow is not { } targetRow || ctx.Event.TargetCol is not { } targetCol)
                return true; // no target cell named

            var targetCell = new GridPos(targetRow, targetCol);
            if (!ConstructionBoard.SlotByCell.TryGetValue(targetCell, out var slot))
                return true; // not a world slot's own cell -- nothing can ever be built here (§6: every legal target is a WorldSlot cell)

            var def = StructureCatalog.Get(structureId!);
            var slotKindSatisfied = def.RequiredSlotKind == slot.Kind;

            if (!ConstructionPlacement.CanPlace(
                    ConstructionBoard.Board, ConstructionBoard.Board.Spec, targetCell, builderPos,
                    ConstructionBoard.BoardSide, ConstructionBoard.CoreSideMilli, ConstructionBoard.RampartThickness,
                    slotKindSatisfied))
                return true; // refused by the shared gate -- a legal "cannot build here", not a bug

            ConstructionBoard.Board.Place($"slot:{slot.SlotIndex}", targetCell);
            ConstructionBoard.Placed.Add(new StructurePlacementRecord(slot.SlotIndex, structureId!, instant));
            return true;
        }
    }

    /// <summary>Owned-PRNG adapter for EffectProcPolicy — never System.Random on a replayable path.</summary>
    sealed class BattleEffectRandom : IEffectRandom
    {
        readonly SeededRng _rng;
        public BattleEffectRandom(ulong seed) => _rng = SeededRng.DeriveStream(seed, "proc");
        public double NextDouble() => (_rng.NextULong() >> 11) * (1.0 / (1UL << 53));
    }
}
