using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Actions.Ai.Lawn;

/// <summary>
/// combat-ai `lawn-actor-view` (CAI4.1, spec-lawn-actor-view.md "The derived memo, and the revision it
/// is keyed on"): one ActorHub resolve per <c>(ptr, revision)</c> per frame. It CONSUMES Hub output — it
/// never composes, never folds, and never caches a number Hub did not return (`AGENTS.md` "One ActorHub
/// compose / one read").
///
/// <para><b>Frame-scoped AND revision-guarded, both, not either.</b> Frame scope bounds staleness to one
/// frame; the revision guard is what honours an invalidation that arrives INSIDE a frame, which is the
/// case `actor-liveness-refresh` rule 3 (a living entity re-composes lazily when its revision moves)
/// exists for. A bump therefore costs exactly one further resolve for that actor and none for others —
/// the row's own acceptance line.</para>
///
/// <para><b>The revision seam is not built yet, and that is a declared wiring dependency.</b>
/// <c>ActorLivenessRevision</c> belongs to `actor-liveness-refresh` (spec, not built), so
/// <paramref name="revisionOf"/> returns a constant today and this memo is frame-scoped only — correct,
/// just colder than it will be. A decision reading a frozen Θ is the defect class that module exists to
/// close, which is why this view must not ship default-on before the seam is wired.</para>
///
/// <para><b>No fog on a lawn</b>, so a miss is always a real actor: this does not need the nullable
/// "hidden" answer `IBattleView.DerivedOf` has to carry elsewhere.</para>
/// </summary>
public sealed class LawnDerivedCache
{
    readonly Func<string, ActorDerivedSnapshot> _resolve;
    readonly Func<string, long> _revisionOf;
    readonly Dictionary<string, (long Revision, ActorDerivedSnapshot Snapshot)> _byPtr =
        new(StringComparer.Ordinal);

    /// <param name="resolve">The Hub's own per-ptr read (<c>InjectorStatusBridge.ResolveDerived</c> in
    /// production). Called at most once per (ptr, revision) per frame.</param>
    /// <param name="revisionOf">The invalidation channel's read. A constant is the honest stand-in until
    /// `actor-liveness-refresh` supplies the real counter.</param>
    public LawnDerivedCache(Func<string, ActorDerivedSnapshot> resolve, Func<string, long> revisionOf)
    {
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        _revisionOf = revisionOf ?? throw new ArgumentNullException(nameof(revisionOf));
    }

    /// <summary>A memo hit, or exactly one <see cref="_resolve"/> call. Returns the instance the
    /// resolver produced — never a copy, never a re-fold, so a caller comparing snapshots is comparing
    /// Hub's own answer.</summary>
    public ActorDerivedSnapshot Get(string ptr)
    {
        if (ptr is null) throw new ArgumentNullException(nameof(ptr));

        var revision = _revisionOf(ptr);
        if (_byPtr.TryGetValue(ptr, out var cached) && cached.Revision == revision) return cached.Snapshot;

        var snapshot = _resolve(ptr);
        _byPtr[ptr] = (revision, snapshot);
        return snapshot;
    }

    /// <summary>Drops the memo. One call per frame, at the decision edge — so a frame with no decision
    /// edge costs nothing at all.</summary>
    public void BeginFrame() => _byPtr.Clear();
}
