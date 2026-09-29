using FusionRpg.Contracts;

namespace FusionRpg.Core.Effects.Atoms;

/// <summary>One event, already reduced to the facts a predicate can read.</summary>
/// <param name="TriggerOrdinal">Index into <see cref="AtomTriggers.All"/>; -1 fires nothing.</param>
public readonly record struct RunnerEvent(
    int TriggerOrdinal,
    string ActorKey,
    string TargetKey,
    EntityFacts Self,
    EntityFacts Target);

/// <summary>A binding that has stopped dispatching for this match. Emitted once, never per attempt.</summary>
public readonly record struct CapNotice(string BindingId, string AtomId, string MatchKey, int Cap);

/// <summary>
/// The Secondary effect runner (spec-atom-runner.md, E15) — the runtime half of the compile/run
/// split. E7 compiles what Foundation can already express; this runs what it cannot: per-binding
/// state, predicate trees, and <c>capPerMatch</c>.
///
/// <para><b>It dispatches; it does not apply.</b> The Funnel is the only Secondary path to the bag.
/// Nothing here calls the bag directly, touches the Writer, or reaches Unity.</para>
///
/// <para><b>It never awaits.</b> No SignalR, no HTTP, no SQLite on this path. Not because a frame
/// deadline demands it — the pipeline is record-then-drain and delay is the designed degradation —
/// but because the hook fires thousands of times a second, pointers die while a round trip is in
/// flight, and the layer has to keep working with the server unreachable. Determinism comes from the
/// server owning the seed, not from where the dice are thrown.</para>
/// </summary>
public sealed class AtomRunner
{
    public const string SkipIcd = "icd";
    public const string SkipCharges = "charges";
    public const string SkipMeter = "everyHits";
    public const string SkipPredicate = "predicate";
    public const string SkipChance = "chance";
    public const string SkipCap = "cap";
    public const string SkipReentry = "reentry";

    readonly Func<EffectGrantDto, bool> _dispatch;
    readonly IAtomRandom _proc;
    readonly IAtomRandom _apply;
    readonly Func<long> _nowMs;

    readonly List<string> _skipped;
    readonly List<CapNotice> _capNotices;
    readonly KeyValuePair<string, int>[] _rolled;

    bool _dispatching;

    /// <param name="dispatch">
    /// Where a passing proc goes. Defaults to <see cref="EffectFunnel.EnqueueModifier"/>, which is
    /// the only Secondary path to the bag — a different sink is how E19 delivers on the injector,
    /// not a licence to bypass the Funnel.
    /// </param>
    public AtomRunner(
        EffectFunnel funnel,
        TriggerIndex index,
        IAtomRandom procRandom,
        IAtomRandom applyRandom,
        Func<long> nowMs,
        string matchKey = "",
        Func<EffectGrantDto, bool>? dispatch = null)
    {
        if (funnel is null && dispatch is null) throw new ArgumentNullException(nameof(funnel));
        _dispatch = dispatch ?? funnel!.EnqueueModifier;
        Index = index ?? throw new ArgumentNullException(nameof(index));
        _proc = procRandom ?? throw new ArgumentNullException(nameof(procRandom));
        _apply = applyRandom ?? throw new ArgumentNullException(nameof(applyRandom));
        _nowMs = nowMs ?? throw new ArgumentNullException(nameof(nowMs));

        State = new RunnerState(index);
        State.BeginMatch(index, matchKey);

        var widest = 0;
        foreach (var b in index.Bindings)
            if (b.Entry.Values.Count > widest) widest = b.Entry.Values.Count;
        _rolled = new KeyValuePair<string, int>[widest];

        // Sized up front, not grown on first use. One event can skip at most once per binding, so
        // this is the ceiling — and a list that grows lazily allocates its backing array the first
        // time a gate fails, which is exactly the event the zero-allocation budget cares about.
        _skipped = new List<string>(Math.Max(1, index.Count));
        _capNotices = new List<CapNotice>(Math.Max(1, index.Count));
    }

    public TriggerIndex Index { get; }
    public RunnerState State { get; }

    /// <summary>Skip reasons from the last event. Test instrumentation — only `cap` is telemetry.</summary>
    public IReadOnlyList<string> LastSkipped => _skipped;

    /// <summary>The one skip that is recorded for an operator, at most once per binding per match.</summary>
    public IReadOnlyList<CapNotice> CapNotices => _capNotices;

    public void BeginMatch(string matchKey)
    {
        State.BeginMatch(Index, matchKey);
        _capNotices.Clear();
    }

    /// <summary>
    /// Evaluate every binding listening to this trigger and dispatch the ones that pass. Returns how
    /// many dispatched.
    ///
    /// <para>Gates run cheapest-first on purpose: an ICD check is an integer compare and a predicate
    /// walk is not. <b>A pre-proc gate consumes nothing when it fails</b> — no ICD stamped, no roll
    /// drawn. <b>The cap is post-proc</b>: it fires after the proc already succeeded, so a capped
    /// atom and an uncapped one sit at the same position in the RNG stream and a replay holds.</para>
    /// </summary>
    public int OnEvent(in RunnerEvent ev)
    {
        _skipped.Clear();

        // Foundation dispatches at depth 0 and drains anything a death adds inside the same window.
        // The runner must not re-enter its own dispatch, or one proc becomes a chain nobody bounded.
        if (_dispatching)
        {
            _skipped.Add(SkipReentry);
            return 0;
        }

        var slots = Index.SlotsFor(ev.TriggerOrdinal);
        if (slots.Length == 0) return 0;

        var now = _nowMs();
        var dispatched = 0;

        foreach (var slot in slots)
        {
            var binding = Index.Bindings[slot];
            var entry = binding.Entry;
            var limits = entry.Limits;

            // 2. cheap gates: integer compares, in ascending cost.
            if (!State.IcdReady(slot, now)) { _skipped.Add(SkipIcd); continue; }
            if (limits.HasCharges && !State.HasChargeLeft(slot)) { _skipped.Add(SkipCharges); continue; }
            if (limits.HasEveryHits && !State.AdvanceMeter(slot, limits.EveryHits))
            {
                _skipped.Add(SkipMeter);
                continue;
            }

            // 3. the compiled predicate. Allocation-free, and it short-circuits.
            var facts = new FactReader(ev.Self, ev.Target);
            if (!entry.Predicate.Evaluate(ref facts)) { _skipped.Add(SkipPredicate); continue; }

            // 4. the chance gate. A certainty draws nothing — the same short-circuit Foundation
            //    already makes, so the two paths stay in step on a shared stream.
            if (entry.ChanceMilli < 1000 && _proc.NextPerMille() >= entry.ChanceMilli)
            {
                _skipped.Add(SkipChance);
                continue;
            }

            // 5. resolve OnApply values. Drawn BEFORE the cap check, deliberately.
            var rolledCount = RollValues(entry);

            // 6. the proc succeeded: the clock and the charge are spent now, cap or no cap.
            State.StampIcd(slot, now, entry.IcdMs);
            if (limits.HasCharges) State.SpendCharge(slot);

            // 7. the cap suppresses the dispatch and nothing else.
            if (limits.HasCap && State.CapReached(slot, limits.CapPerMatch))
            {
                _skipped.Add(SkipCap);
                if (State.ClaimCapNotice(slot))
                    _capNotices.Add(new CapNotice(
                        binding.BindingId, entry.AtomId, State.MatchKey, limits.CapPerMatch));
                continue;
            }

            // 8. the Funnel, and nothing else.
            if (Dispatch(binding, entry, rolledCount, in ev))
            {
                if (limits.HasCap) State.CountDispatch(slot);
                dispatched++;
            }
        }

        return dispatched;
    }

    /// <summary>
    /// Resolve each value into the reusable buffer. A fixed spec is its own value; an
    /// <c>OnApply</c> range is the roll this module exists to make.
    /// </summary>
    int RollValues(RunnerEntry entry)
    {
        var i = 0;
        foreach (var (name, bounds) in entry.Values)
        {
            var value = bounds.IsFixed ? bounds.Min : _apply.NextInclusive(bounds.Min, bounds.Max);
            _rolled[i++] = new KeyValuePair<string, int>(name, value);
        }
        return i;
    }

    bool Dispatch(RunnerBinding binding, RunnerEntry entry, int rolledCount, in RunnerEvent ev)
    {
        // The overlay carries ONLY what varies per proc — the rolled values. Static params belong on
        // the def's action rows, which is where E7 already puts them on the compiled path; pushing
        // them through the overlay instead means keys like `element` hit an allowlist that has no
        // slot for them, and the grant is refused mid-flush, far from the enqueue that caused it.
        //
        // The def for a runner atom is not emitted by anything yet — E7 emits defs only for the
        // compiled path. Until E19 ships one per runner entry (from RunnerEntry.Params), a dispatch
        // needs a def already in the catalog under the atom id.
        var overlay = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var i = 0; i < rolledCount; i++) overlay[_rolled[i].Key] = _rolled[i].Value;

        var grant = new EffectGrantDto
        {
            GrantId = binding.BindingId + "#" + entry.AtomId,
            EffectId = entry.AtomId,
            OwnerKey = string.IsNullOrWhiteSpace(binding.OwnerKey) ? ev.ActorKey : binding.OwnerKey,
            PluginId = "atom-runner",
            Overlay = overlay,
        };

        _dispatching = true;
        try
        {
            return _dispatch(grant);
        }
        finally
        {
            _dispatching = false;
        }
    }
}
