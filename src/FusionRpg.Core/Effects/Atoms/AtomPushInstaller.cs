namespace FusionRpg.Core.Effects.Atoms;

/// <summary>
/// The per-match seed, derived from the match key.
///
/// <para><b>Both ends compute it, neither invents it.</b> The server has no lawn match key at Hello —
/// that key is born in the injector's <c>board.start</c> capture — so shipping a seed at connect time
/// would either be empty or a guess. A pure function of the match key gives the same guarantee D5
/// actually wants: the rolls are reproducible, and the match key is already in every event and in
/// <c>runs</c>, so a replay can recover it.</para>
///
/// <para>FNV-1a, not <c>String.GetHashCode</c>, which is randomised per process — "same match key,
/// same rolls" would be false after every restart, silently.</para>
/// </summary>
public static class MatchSeed
{
    public static ulong For(string? matchKey)
    {
        if (string.IsNullOrEmpty(matchKey)) return 0UL;

        var hash = 14695981039346656037UL;
        foreach (var ch in matchKey)
        {
            hash ^= ch;
            hash *= 1099511628211UL;
        }
        return hash;
    }
}

/// <summary>
/// What a delivered push does to the receiver's state (spec-compiled-push.md, E19).
///
/// <para><b>The logic lives here, in Core, so it can be tested.</b> The injector half is a shim over
/// this: static holder, funnel, clock, error sink. Everything worth getting wrong — revision
/// negotiation, keeping what you hold on an up-to-date reply, rebuilding the runner with a new
/// match's seed, dropping compiled output at <c>board.end</c> — is here, where a test can reach it.</para>
/// </summary>
public sealed class AtomPushInstaller
{
    readonly Func<long> _nowMs;
    readonly Func<Contracts.EffectGrantDto, bool> _dispatch;

    IReadOnlyList<RunnerBinding> _bindings = Array.Empty<RunnerBinding>();
    TriggerIndex _index = TriggerIndex.Empty;

    /// <param name="dispatch">
    /// Where a passing proc goes — the Funnel, and nothing else. <b>Required, not defaulted:</b> a
    /// settable sink with a "refuse everything" default means a host that forgets to wire it gets a
    /// runner that silently swallows every proc, which looks exactly like content that never fires.
    /// </param>
    public AtomPushInstaller(Func<long> nowMs, Func<Contracts.EffectGrantDto, bool> dispatch)
    {
        _nowMs = nowMs ?? throw new ArgumentNullException(nameof(nowMs));
        _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
    }

    /// <summary>Null until a push with bindings arrives AND a match has started.</summary>
    public AtomRunner? Runner { get; private set; }

    /// <summary>-1 when nothing is held — which is what makes the next Hello ask for the full set.</summary>
    public long CatalogRevision { get; private set; } = -1;

    public string? ContentHash { get; private set; }

    public int BindingCount => _bindings.Count;

    /// <summary>
    /// Take a delivered payload's runner half. Returns how many bindings are now held.
    ///
    /// <para>An <c>upToDate</c> reply installs nothing and keeps what is already here — that is the
    /// point of the negotiation. The content hash is still recorded, because a reconnect that
    /// delivers no content must still make a mismatch visible.</para>
    /// </summary>
    public int Install(Contracts.AtomPushDto payload)
    {
        if (payload is null) throw new ArgumentNullException(nameof(payload));

        ContentHash = payload.ContentHash;

        if (payload.UpToDate)
        {
            CatalogRevision = payload.CatalogRevision;
            return _bindings.Count;
        }

        _bindings = AtomPushCodec.DecodeBindings(payload);
        CatalogRevision = payload.CatalogRevision;
        _index = _bindings.Count == 0 ? TriggerIndex.Empty : TriggerIndex.Build(_bindings);

        // A push that arrives mid-match re-arms the runner immediately; one that arrives at connect
        // time waits for board.start, because that is when the seed exists.
        Runner = Runner is null && _bindings.Count == 0
            ? null
            : Rebuild(Runner?.State.MatchKey ?? payload.MatchKey ?? "");

        return _bindings.Count;
    }

    /// <summary>
    /// Match start. Rebuilds the runner against this match's seed — a new match is new dice and
    /// fresh counters, and rebuilding is how both happen at once without a re-push.
    /// </summary>
    public void BeginMatch(string matchKey)
    {
        Runner = _bindings.Count == 0 ? null : Rebuild(matchKey ?? "");
    }

    /// <summary>
    /// <c>board.end</c>: compiled output is match-scoped, like the grant session it arrived with. The
    /// revision goes with it, so the next Hello asks for the full set rather than claiming to hold
    /// bindings it has just dropped.
    /// </summary>
    public void Clear()
    {
        Runner = null;
        _bindings = Array.Empty<RunnerBinding>();
        _index = TriggerIndex.Empty;
        CatalogRevision = -1;
        ContentHash = null;
    }

    public Contracts.AtomPushHelloDto Hello() =>
        new() { CatalogRevision = CatalogRevision, ContentHash = ContentHash };

    AtomRunner Rebuild(string matchKey)
    {
        var seed = MatchSeed.For(matchKey);
        return new AtomRunner(
            null!, _index,
            new AtomRandom(seed, AtomStreams.Proc),
            new AtomRandom(seed, AtomStreams.Apply),
            _nowMs,
            matchKey,
            dispatch: _dispatch);
    }
}
