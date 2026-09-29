using FusionRpg.Contracts;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Stats.Derived.Subsystems;
using FusionRpg.Core.Status;

namespace FusionRpg.Core.Effects;

/// <summary>Offline Foundation harness — Secondary/FE/Server Effect tests use this; never opens PVZ.</summary>
public sealed class FoundationHarness
{
    readonly FakeEffectClock _clock;
    readonly SeededEffectRandom _rng;
    readonly RecordingEffectSink _sink;
    readonly RecordingDamageFxSink _fx;
    readonly RecordingUiPresentSink _uiPresent;
    readonly EffectBag _bag;
    readonly ActorDerivedLookup _derived = new();
    readonly ActorElementLookup _elements = new();
    readonly List<OverlayCombatBreakdown> _breakdowns = new();

    public FoundationHarness(int seed = 42)
    {
        _clock = new FakeEffectClock();
        _rng = new SeededEffectRandom(seed);
        _sink = new RecordingEffectSink();
        _fx = new RecordingDamageFxSink();
        _uiPresent = new RecordingUiPresentSink();
        var catalog = new InMemoryEffectCatalog();
        catalog.ReplaceAll(EffectAtomCatalog.CreateAll());
        var grants = new InMemoryEffectGrantStore();
        var proc = new EffectProcPolicy(_clock, _rng);
        _bag = new EffectBag(catalog, grants, proc, _sink);
        _bag.UtcNow = () => _clock.UtcNow;
        _bag.UiPresent = _uiPresent;
        _bag.Status = new StatusRuntime(
            StatusCatalogHub.Current,
            (ptr, attackerLess) => _derived.Resolve(ptr, attackerLess));
        Funnel = new EffectFunnel(_bag, _fx);
    }

    public FoundationHarness WithCatalog(IEnumerable<EffectDef> defs)
    {
        _bag.Catalog.ReplaceAll(defs);
        return this;
    }

    public EffectBag Bag => _bag;
    public EffectFunnel Funnel { get; }
    public RecordingEffectSink Sink => _sink;
    public RecordingDamageFxSink Fx => _fx;
    public RecordingUiPresentSink UiPresent => _uiPresent;
    public FakeEffectClock Clock => _clock;

    public EffectGrant Grant(EffectGrantDto dto)
    {
        _sink.Items.Clear();
        _sink.Fired.Clear();
        _fx.Items.Clear();
        _uiPresent.Clear();
        return _bag.Grant(dto);
    }

    public bool Withdraw(string grantId)
    {
        _sink.Items.Clear();
        _sink.Fired.Clear();
        return _bag.Withdraw(grantId);
    }

    public void AdvanceTime(int ms)
    {
        _clock.AdvanceMs(ms);
        _bag.TickDots();
    }

    public void ClearAll()
    {
        _sink.Items.Clear();
        _sink.Fired.Clear();
        _fx.Items.Clear();
        _uiPresent.Clear();
        _bag.ClearAll();
    }

    public EffectCatalogSnapshotDto Snapshot() => _bag.Snapshot();

    public Combat.BoardSnapshot BoardSnapshot
    {
        get => _bag.BoardSnapshot;
        set => _bag.BoardSnapshot = value ?? Combat.BoardSnapshot.Empty;
    }

    public void SetBoard(IEnumerable<Combat.BoardEntitySnap> entities) =>
        BoardSnapshot = new Combat.BoardSnapshot(entities);

    public void PinDerived(string ptr, ActorDerivedSnapshot snapshot) =>
        _derived.Pin(ptr, snapshot);

    /// <summary>Resolves <paramref name="ptr"/>'s current derived snapshot — the pinned base folded
    /// with any <see cref="ContributeDerived"/> contributions. The read-side complement to
    /// <see cref="PinDerived"/>/<see cref="ContributeDerived"/>, so a caller can observe the fold
    /// directly rather than only through a combat/status side effect.</summary>
    public ActorDerivedSnapshot ResolveDerived(string ptr) => _derived.Resolve(ptr, attackerLess: false);

    /// <summary>
    /// Folds one bound `stat.derived` contribution onto <paramref name="ptr"/>'s pinned snapshot —
    /// mechanism-wiring.md §4.3 step 1/2, the SIM-side analog of
    /// <see cref="Stats.Derived.Subsystems.AtomDerivedSubsystem"/> on the lawn. Delegates entirely to
    /// <see cref="ActorDerivedLookup.AddContribution"/> so this host and <see cref="SimEffectHost"/>
    /// fold through the exact same logic — one function, two hosts. <c>gk-core/tools/CombatSim</c> drives
    /// THIS host, not <see cref="SimEffectHost"/> (Simulator.cs:66), which is why the fold had to
    /// reach both rather than just one.
    /// </summary>
    public void ContributeDerived(string ptr, BoundDerivedAtom atom) =>
        _derived.AddContribution(ptr, atom);

    /// <summary>
    /// Attempts a real `stat.derived` bind with <c>BindContext(RuntimeId.Sim)</c> — §4.3 step 3. See
    /// <see cref="ActorDerivedLookup.TryBind"/> for why this refuses every row until E5 flips the Sim
    /// cell off <see cref="RuntimeState.None"/>.
    /// </summary>
    public AtomRejection TryBindDerivedAtoms(
        IReadOnlyList<AtomRow> atoms, OwnerScope owner, IReadOnlyCollection<string>? overlayKeys = null) =>
        _derived.TryBind(atoms, owner, overlayKeys);

    public void PinElementTypes(string ptr, ActorElementTypes types) =>
        _elements.Pin(ptr, types);

    public IReadOnlyList<OverlayCombatBreakdown> CombatBreakdowns => _breakdowns;

    public FoundationHarness WithOverlayCombatMath(int combatSeed = 42)
    {
        _breakdowns.Clear();
        _bag.CombatRng = new SeededCombatRng(combatSeed);
        _bag.CombatMath = OverlayCombatMath.Create(
            ResolveCombatActor,
            ElementHub.Default,
            _bag.CombatRng,
            (breakdown, _, _) => _breakdowns.Add(breakdown));
        // aura-skill T20: same resolve as combat/shields, wired into the bag itself (not just
        // exposed via Resolve for a manual DispatchInstant call) -- so a test that drives reflect
        // through EffectBag's own internal grant processing, not a hand-built DispatchInstant call,
        // exercises the exact same production wiring EffectRuntime.WireCombatMath sets up.
        _bag.ActorResolve = ResolveCombatActor;
        return this;
    }

    /// <summary>Shield runtime when <see cref="WithShieldGate"/> was called; null otherwise.</summary>
    public Combat.Shield.ShieldRuntime? ShieldRuntime { get; private set; }

    public FoundationHarness WithShieldGate()
    {
        ShieldRuntime = new Combat.Shield.ShieldRuntime();
        _bag.ShieldGate = new Combat.Shield.ShieldGate(ShieldRuntime, ResolveCombatActor);
        return this;
    }

    /// <summary>Grant a shield to a board ptr (normalized like dispatcher targets are).</summary>
    public Combat.Shield.ShieldApplyResult GrantShield(
        string ptr, long baseHp, ElementTypeId? element = null,
        // Not `= ShieldPolicy.PrioritySkill`: PrioritySkill is config-loaded now (tunables-ssot.md
        // T1), and a default parameter value must be a compile-time constant. Null resolves to it
        // below instead, so the six existing callers relying on the default need no change.
        int? priority = null,
        string sourceId = "test-shield", long? durationTicks = null, bool refillOnMerge = true)
    {
        if (ShieldRuntime == null)
            throw new InvalidOperationException("Call WithShieldGate() first.");
        var ownerKey = EffectOwnerKeys.Entity(CombatPtr.Normalize(ptr));
        return ShieldRuntime.Apply(new Combat.Shield.ShieldGrant
        {
            OwnerKey = ownerKey,
            SourceId = sourceId,
            Element = element,
            BaseHp = baseHp,
            Priority = priority ?? Combat.Shield.ShieldPolicy.PrioritySkill,
            DurationTicks = durationTicks,
            RefillOnMerge = refillOnMerge
        }, _derived.Resolve(CombatPtr.Normalize(ptr), attackerLess: false), nowTick: 0);
    }

    /// <summary>The same actor-resolution function <see cref="WithOverlayCombatMath"/> wires into
    /// the combat calculator — exposed so a caller can ALSO pass it as
    /// <see cref="Combat.CombatDamageDispatcher.DispatchInstant"/>'s <c>actorResolve</c> parameter
    /// (T5.4 reflection), reading the SAME pinned actors rather than a second, divergent lookup.</summary>
    public CombatActorResolve Resolve => ResolveCombatActor;

    CombatActorSnapshot ResolveCombatActor(string? ptr, bool attackerLess)
    {
        if (attackerLess)
            return CombatActorSnapshot.AttackerLess();
        return new CombatActorSnapshot(
            _derived.Resolve(ptr, attackerLess: false),
            _elements.Resolve(ptr));
    }

    public IntentPlanDto OnEvent(EffectEventDto ev)
    {
        _sink.Items.Clear();
        _sink.Fired.Clear();
        _fx.Items.Clear();
        _uiPresent.Clear();
        _breakdowns.Clear();
        return _bag.OnEvent(ev);
    }

    public (IntentPlanDto Plan, IReadOnlyList<EffectFiredDto> Fired) Run(EffectEventDto ev)
    {
        var plan = OnEvent(ev);
        return (plan, _sink.Fired.ToList());
    }
}
