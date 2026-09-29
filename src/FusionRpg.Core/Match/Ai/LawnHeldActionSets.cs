using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Grants;

namespace FusionRpg.Core.Match.Ai;

/// <summary>
/// combat-ai `lawn-held-actions` (module 16, CAI4.2) — the per-match store of a lawn actor's compiled
/// action list, keyed by **species key** and, where one exists, by **Bound instance key**.
///
/// <para>Why this exists: on the lawn nothing ever asks "what can this creature cast". The injector's
/// whole <see cref="CompiledAction"/> surface is one hand-built row
/// (<c>LawnBasicAttackRow.TryGet()</c>), while battle compiles each actor's loadout once at setup
/// (<c>BattleRunState</c>) and hands it out through its view. This type is the lawn's equivalent feed,
/// and it reuses battle's own two mechanisms rather than re-deriving them: <see cref="ActionSetAssembler"/>
/// for the assembly and <see cref="FrozenActionSet"/> for the one snapshot moment.</para>
///
/// <para><b>The snapshot moment is a match.</b> On the lawn "a run" is one match — the same lifecycle
/// the lawn's resource pools already state ("match-scoped and full at spawn"). So a key freezes on its
/// first push in a match, a creature spawning mid-match references the already-frozen set for its key
/// (never a fresh assembly), and a grant arriving mid-match applies at the next match. The match
/// boundary is <see cref="BeginMatch"/>, which drops every freeze so the next push re-assembles for
/// real — the lawn's <see cref="FrozenActionSet.RefreshAtNextRunStart"/>, called at <c>board.start</c>
/// and nowhere else.</para>
///
/// <para><b>The basic attack is never in a set.</b> An actor whose species has no pushed set gets an
/// <b>empty</b> held list, never the vanilla swing: the swing is what the rider observes and charges,
/// not a candidate the AI chooses between, so falling back to it would double-charge a path that
/// already charges. That boundary is what modules 17 and 18 sit on.</para>
///
/// <para><b>Unity-free and ptr-free.</b> The server owns the inputs (a species' basics and its live
/// grant rows) and never sees a ptr; binding a ptr to a set is the injector's job
/// (<c>LawnHeldActionRegistry</c>, CAI4.3). Nothing here reads a clock, a board or a file.</para>
/// </summary>
public sealed class LawnHeldActionSets
{
    /// <summary>One frozen key. <see cref="Held"/> is empty exactly when the compile was refused, so a
    /// reader never has to re-check <see cref="Refused"/> to know what to run with.</summary>
    sealed class Entry
    {
        public Entry(FrozenActionSet set) => Set = set;

        public FrozenActionSet Set { get; }
        public IReadOnlyList<CompiledAction> Held { get; set; } = Array.Empty<CompiledAction>();
        public bool Refused { get; set; }
        public string RefusalReason { get; set; } = "";
    }

    readonly ActionCatalog _catalog;
    readonly Action<string>? _report;
    readonly Dictionary<string, Entry> _bySpeciesKey = new(StringComparer.Ordinal);
    readonly Dictionary<string, Entry> _byInstanceKey = new(StringComparer.Ordinal);

    /// <param name="catalog">The live compiled-action catalog. An id it does not know is a refusal,
    /// never a silent drop — the posture <c>BattleRunState</c> already takes for an equipped id it
    /// cannot resolve.</param>
    /// <param name="report">Where a refusal is reported, once. Optional so Core stays host-free; the
    /// injector passes its log seam. Never called twice for the same key in the same match.</param>
    public LawnHeldActionSets(ActionCatalog catalog, Action<string>? report = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _report = report;
    }

    /// <summary>Assemblies actually performed this match — a reading for diagnostics and for the
    /// "one assembly per key, not per ptr" contract, never a pinned population.</summary>
    public int AssemblyCount { get; private set; }

    /// <summary>Keys (species + bound instance) currently frozen this match.</summary>
    public int KeyCount => _bySpeciesKey.Count + _byInstanceKey.Count;

    /// <summary>The lawn's match boundary. Drops every freeze, so the next push for a key re-assembles
    /// from whatever grants are live at that moment. This is the only place a set is ever rebuilt —
    /// a mid-match push does not reassemble, which is the freeze rule, not an omission.</summary>
    public void BeginMatch()
    {
        _bySpeciesKey.Clear();
        _byInstanceKey.Clear();
        AssemblyCount = 0;
    }

    /// <summary>Cold push for a species key. Freezes once per match: a second push of the same key
    /// returns the already-frozen set and assembles nothing.</summary>
    public LawnHeldActionPush PushSpecies(
        string speciesKey,
        SpeciesBasicsRow basics,
        IReadOnlyList<ActionGrantRow> liveGrants,
        Func<string, bool> isDefaultAttackEligible) =>
        Push(_bySpeciesKey, "species", speciesKey, basics, liveGrants, isDefaultAttackEligible);

    /// <summary>Cold push for a Bound specimen's own key. A built specimen's own loadout is its own;
    /// resolution prefers this key over the species key (<see cref="HeldFor"/>).</summary>
    public LawnHeldActionPush PushBound(
        string instanceKey,
        SpeciesBasicsRow basics,
        IReadOnlyList<ActionGrantRow> liveGrants,
        Func<string, bool> isDefaultAttackEligible) =>
        Push(_byInstanceKey, "instance", instanceKey, basics, liveGrants, isDefaultAttackEligible);

    /// <summary>
    /// The compiled, preference-ordered list a ptr runs with. The bound instance key wins over the
    /// species key; an unknown pair returns <b>empty</b> — never the basic-attack row, and never a
    /// synthesised set.
    /// </summary>
    public IReadOnlyList<CompiledAction> HeldFor(string speciesKey, string? boundInstanceKey = null)
    {
        if (boundInstanceKey is not null && _byInstanceKey.TryGetValue(boundInstanceKey, out var bound))
            return bound.Held;
        if (!string.IsNullOrEmpty(speciesKey) && _bySpeciesKey.TryGetValue(speciesKey, out var species))
            return species.Held;
        return Array.Empty<CompiledAction>();
    }

    /// <summary>The frozen set itself, so the ptr-binding registry can hold a <b>reference</b> to the
    /// shared per-key set rather than a copy. Null when the key has not been pushed this match.</summary>
    public FrozenActionSet? SetFor(string speciesKey, string? boundInstanceKey = null)
    {
        if (boundInstanceKey is not null && _byInstanceKey.TryGetValue(boundInstanceKey, out var bound))
            return bound.Set;
        return !string.IsNullOrEmpty(speciesKey) && _bySpeciesKey.TryGetValue(speciesKey, out var species)
            ? species.Set
            : null;
    }

    /// <summary>Why a key's held list is empty, or <c>null</c> when it is not refused. Named so a host
    /// can report a stale refusal without re-deriving it.</summary>
    public string? RefusalReasonFor(string speciesKey, string? boundInstanceKey = null)
    {
        if (boundInstanceKey is not null && _byInstanceKey.TryGetValue(boundInstanceKey, out var bound))
            return bound.Refused ? bound.RefusalReason : null;
        if (!string.IsNullOrEmpty(speciesKey) && _bySpeciesKey.TryGetValue(speciesKey, out var species))
            return species.Refused ? species.RefusalReason : null;
        return null;
    }

    LawnHeldActionPush Push(
        Dictionary<string, Entry> sets,
        string scope,
        string key,
        SpeciesBasicsRow basics,
        IReadOnlyList<ActionGrantRow> liveGrants,
        Func<string, bool> isDefaultAttackEligible)
    {
        if (string.IsNullOrEmpty(key)) throw new ArgumentException("key must not be empty", nameof(key));
        ArgumentNullException.ThrowIfNull(basics);
        ArgumentNullException.ThrowIfNull(liveGrants);
        ArgumentNullException.ThrowIfNull(isDefaultAttackEligible);

        // Freeze first. A key already frozen this match keeps whatever it froze — including a refusal,
        // because re-compiling on every push would be the same "re-assemble" defect a second time.
        if (sets.TryGetValue(key, out var existing))
            return new LawnHeldActionPush(key, scope, FrozenNow: false, existing.Refused, existing.RefusalReason, existing.Held);

        var set = FrozenActionSet.FreezeAtRunStart(basics, liveGrants, isDefaultAttackEligible);
        checked { AssemblyCount++; }

        var entry = new Entry(set);
        var (held, unknownIds) = Compile(set.Snapshotted());
        if (unknownIds.Count > 0)
        {
            // Loud, once, then refused on every later call — never a per-frame throw and never a
            // silent skip, which would be indistinguishable from a creature that has no kit.
            entry.Refused = true;
            entry.RefusalReason =
                $"{scope} key '{key}' was pushed a set naming action id(s) [{string.Join(", ", unknownIds)}] " +
                "that the supplied ActionCatalog does not know; this key runs with no held actions.";
            entry.Held = Array.Empty<CompiledAction>();
            _report?.Invoke(entry.RefusalReason);
        }
        else
        {
            entry.Held = held;
        }

        sets[key] = entry;
        return new LawnHeldActionPush(key, scope, FrozenNow: true, entry.Refused, entry.RefusalReason, entry.Held);
    }

    /// <summary>Compile every assembled id, then order once by <see cref="ActionTagPreference"/> —
    /// the same comparison battle's held-action sort uses, so the ladder reaches the lawn without a
    /// second ordering rule. Returns the unknown ids rather than dropping them, so the caller can
    /// refuse the whole set loudly. Never called per decision: exactly once per key per match.</summary>
    (IReadOnlyList<CompiledAction> Held, IReadOnlyList<string> UnknownIds) Compile(AssemblyResult snapshot)
    {
        var unknown = new List<string>();
        var list = new List<CompiledAction>(snapshot.Actions.Count);
        foreach (var assembled in snapshot.Actions)
        {
            var compiled = _catalog.Get(assembled.ActionId);
            if (compiled is null) unknown.Add(assembled.ActionId);
            else list.Add(compiled);
        }

        if (unknown.Count > 0) return (Array.Empty<CompiledAction>(), unknown);

        list.Sort(ActionTagPreference.Compare);
        return (list, unknown);
    }
}

/// <summary>The result of one Cold push: what the key now holds, and whether this push was the one
/// that froze it. <see cref="FrozenNow"/> false means an earlier push in this match already decided
/// the set — which is the freeze contract, not a failure.</summary>
public readonly record struct LawnHeldActionPush(
    string Key,
    string Scope,
    bool FrozenNow,
    bool Refused,
    string RefusalReason,
    IReadOnlyList<CompiledAction> Held);
