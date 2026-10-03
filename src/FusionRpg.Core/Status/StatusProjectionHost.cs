using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Status;

/// <summary>
/// How a projection recognises the ONE live instance that belongs to its track. Both shipped
/// projections need this and they disagree, which is why it is a parameter rather than a fixed
/// rule: <c>NervePolicy</c> scans for the <c>nerve.</c> PREFIX (its ladder has three ids, one live at
/// a time) while <c>ExhaustionPolicy</c> scans for one EXACT id (its "track" is one resource, whose
/// debuff has exactly one id). A host that hardcoded either would force the other track to fake ids
/// it does not have.
/// </summary>
public enum StatusProjectionMatch
{
    /// <summary>Any id beginning <c>{trackId}.</c> — the ladder shape, where the track owns a family
    /// of ids and a stage change replaces one with another.</summary>
    Prefix,

    /// <summary>One exact id — the boolean shape, where "projected" and "not projected" are the only
    /// two answers and the track has a single id to write.</summary>
    Exact,
}

/// <summary>
/// One rung of a projection ladder: the status id a stage projects, and the
/// <see cref="StatusStatMod"/> atoms that stage debuffs with. A container of (channel, op, value)
/// triples, exactly like the two shipped payloads — never a hardcoded channel, so a new track's
/// consequence is authored data rather than code.
/// </summary>
/// <param name="StatMods">Never null, and an empty list is the honest way to say "this stage carries
/// no payload" (a real rung with nothing to apply is a content decision, not a missing argument).</param>
public sealed record StatusProjectionRung(string StatusId, IReadOnlyList<StatusStatMod> StatMods);

/// <summary>
/// What one <see cref="StatusProjectionHost.Sync"/> actually did to the runtime. A caller counts
/// transitions with <see cref="Changed"/>, and a caller that needs to know WHY nothing was written
/// reads <see cref="ResistReason"/>.
///
/// <para><b>NoChange is not a synonym for "nothing was wrong".</b> It covers an unchanged projection,
/// a track that was never projected, and an apply the resistance evaluator REFUSED (which reports its
/// reason). The shipped policies collapse all three into a single <c>false</c> because their callers
/// only wanted "did this apply"; a caller that counts transitions, or that has to notice a
/// permanently-resisting track, needs them separated.</para>
/// </summary>
public enum StatusProjectionChange
{
    /// <summary>The live projection already said this — the idempotent no-op that makes "one apply
    /// per transition" countable instead of an accident of how <see cref="StatusStacking.Refresh"/>
    /// happens to collapse duplicates.</summary>
    NoChange,

    /// <summary>A fresh enter transition: the runtime gained this track's instance.</summary>
    Applied,

    /// <summary>A leave transition: the instance that was live has been withdrawn.</summary>
    Withdrew,
}

/// <summary>
/// One projection transition, and the whole truthful answer to "what did this call do". Deliberately
/// not a <c>bool</c>: both shipped <c>Sync</c>s return "true only on a fresh apply", which loses the
/// difference between an unchanged projection and a refused one — and a refused apply on a track the
/// runtime never accepts is a permanent, silent failure that only a caller watching
/// <see cref="ResistReason"/> can notice.
/// </summary>
/// <param name="StatusId">The id the host resolved to; empty for the withdrawn/none state.</param>
/// <param name="GrantId">The grant the runtime wrote (or withdrew), so a caller can address exactly
/// this actor's track later — never a host-wide sweep.</param>
public sealed record StatusProjectionResult(
    StatusProjectionChange Change,
    string StatusId,
    string GrantId,
    StatusResistReason? ResistReason)
{
    /// <summary>True whenever the runtime was actually written to — apply OR withdraw — which is the
    /// count a caller tallies to measure how often a track crossed a rail.
    ///
    /// <para><b>Deliberately broader than what the two shipped policies return.</b> Both collapse to
    /// a single <c>false</c> on a withdrawal, so "how many times did this track change state?" is not
    /// a question their return value can answer. Read <see cref="Change"/> == <see cref="StatusProjectionChange.Applied"/>
    /// instead to reproduce either shipped <c>Sync</c> byte for byte; read this to count transitions in
    /// both directions.</para>
    /// </summary>
    public bool Changed => Change != StatusProjectionChange.NoChange;
}

/// <summary>
/// The reusable host for an OUT-OF-COMBAT projection: a scalar living in durable party state, mirrored
/// into a <see cref="StatusRuntime"/> instance so derived-stat modifiers and a VFX token exist while the
/// counter itself stays off the status (spec §4's reason the ladder exists at all).
///
/// <para><b>What this owns, once, for every track.</b> <c>ExhaustionPolicy.Sync</c>
/// (<c>Actions/Cost/ExhaustionPolicy.cs:99-136</c>) and <c>NervePolicy.Sync</c>
/// (<c>Delve/Attrition/NervePolicy.cs:92-127</c>) each hand-write the same dance: pre-read the host's
/// live instances, write only on a transition, apply attacker-less with an inert magnitude,
/// <c>BaseDuration 0</c> so the instance never expires on a clock, a scripted
/// <see cref="FixedStatusRng"/>(0) so a mechanical fact is never a resist roll, and
/// <c>ClearGrant</c> the previous grant before applying the replacement so a ladder stage change never
/// coexists with the stage it replaced. A third track was going to be a third copy of that. It is not:
/// it is a ladder function and a caller.</para>
///
/// <para><b>Idempotence is the pre-read, never the stacking mode.</b> Both shipped policies register
/// their defs as <see cref="StatusStacking.Refresh"/>, which would also collapse a duplicate — but a
/// re-apply under Refresh REPLACES the instance (new <c>AppliedAt</c>, new instance id, a fresh
/// <c>OnApplied</c> for VFX), so "one apply per transition" would hold only in the final state, not in
/// what actually ran. Reading the live instance first is what makes the no-op structural.</para>
///
/// <para><b>NO CLOCK.</b> <c>now</c> is a parameter on every entry point and this type reads nothing
/// else — an out-of-combat need is charged by an event (a room, a rest, a curio), never by elapsed
/// real time, and <c>BaseDuration 0</c> means the instance's <c>ExpiresAt</c> is
/// <see cref="DateTimeOffset.MaxValue"/>: it lives until the next <c>Sync</c> withdraws it, so a
/// projection never decays behind the scalar that drives it.</para>
///
/// <para><b>What this deliberately does NOT own.</b> The catalog. The two shipped precedents
/// DISAGREE about it — <c>ExhaustionPolicy</c> registers its own ids into the catalog and the category
/// registry as a real additive extension point, while <c>NervePolicy</c> only READS ids the
/// bootstrap already locked in — so a host that picked one would be wrong for the other, and a host
/// that validated both would refuse the lawful half. Registering and validating a track's ids stays
/// the ladder's owner's job, at load, exactly as it is today.</para>
/// </summary>
public sealed class StatusProjectionHost
{
    static readonly IStatusRng ScriptedRng = new FixedStatusRng(0.0);

    readonly IReadOnlyList<StatusProjectionRung> _rungs;
    readonly Func<string, string> _grantIdFor;

    /// <summary>The track's own name — the id prefix under <see cref="StatusProjectionMatch.Prefix"/>
    /// and the leading segment of the default grant id.</summary>
    public string TrackId { get; }

    /// <summary>Id prefix this track's family scan matches, <c>"{TrackId}."</c>. Meaningless under
    /// <see cref="StatusProjectionMatch.Exact"/>, where the single rung's id IS the identity.</summary>
    public string StatusIdPrefix { get; }

    public StatusProjectionMatch Match { get; }

    /// <summary>The pool id whose regen channel this track's stages are forbidden from touching, or
    /// null for a track whose source of truth is not one of the six registered pools. Never guessed:
    /// the guard is only as good as the declaration behind it.</summary>
    public string? DrivingPoolResourceId { get; }

    /// <param name="trackId">The track's name. Names the grant id so <c>ClearGrant</c> can never reach
    /// another track's instance, and prefixes the id family under
    /// <paramref name="match"/> = <see cref="StatusProjectionMatch.Prefix"/>.</param>
    /// <param name="match">How the ONE live instance is found — prefix for a ladder, exact for a
    /// boolean. See the enum's own doc for why it is not one rule.</param>
    /// <param name="rungs">The ladder in threshold order. At least one: a track with no stage has
    /// nothing to project and is a declaration bug, not a track that is always off.</param>
    /// <param name="drivingPoolResourceId">The pool whose depletion or level this track reads — the
    /// one whose own regen channel a stage may not touch. See the ST1.5 refusal below.</param>
    /// <param name="grantIdFor">Optional grant-id shape, taking the host ptr and returning the grant
    /// id. The default is <c>"{trackId}:{hostPtr}"</c>, which is exactly
    /// <c>NerveStatusIds.GrantIdFor</c>'s shape — host-scoped and track-named, so withdrawing one
    /// actor's track cannot touch another's. The override exists because the two shipped precedents
    /// disagree about where a per-resource scope sits: <c>NerveStatusIds.GrantIdFor</c> has no
    /// secondary scope at all, while <c>ExhaustionStatusIds.GrantIdFor</c> interleaves the resource id
    /// AFTER the host (<c>exhaustion:{host}:{resource}</c>) so one policy manages many resources under
    /// one id shape. A host that invented a single string would silently re-scope one of them, so the
    /// shape is the caller's, and the HOST-SCOPED, TRACK-NAMED property is asserted at every call
    /// instead of assumed here.</param>
    public StatusProjectionHost(
        string trackId,
        StatusProjectionMatch match,
        IReadOnlyList<StatusProjectionRung> rungs,
        string? drivingPoolResourceId = null,
        Func<string, string>? grantIdFor = null)
    {
        if (string.IsNullOrWhiteSpace(trackId)) throw new ArgumentException("trackId required", nameof(trackId));
        if (rungs is null) throw new ArgumentNullException(nameof(rungs));
        if (rungs.Count == 0) throw new ArgumentException("a projection needs at least one stage", nameof(rungs));

        var ownRegenChannel = drivingPoolResourceId is null
            ? null
            : DerivedStatChannels.ResourceRegen(drivingPoolResourceId);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rung in rungs)
        {
            if (rung is null) throw new ArgumentNullException(nameof(rungs));
            if (string.IsNullOrWhiteSpace(rung.StatusId))
                throw new ArgumentException("every stage needs a status id", nameof(rungs));
            if (rung.StatMods is null)
                throw new ArgumentException($"stage '{rung.StatusId}' needs an explicit StatMods list", nameof(rungs));
            // A ladder repeating an id makes every stage between the two copies unreachable: entering
            // stage N would read as already-projected and write nothing, so the sequence [a, b, a]
            // silently collapses to [a, a]. Refused here rather than discovered as a track that
            // skips a middle stage.
            if (!seen.Add(rung.StatusId))
                throw new ArgumentException(
                    $"track '{trackId}' lists '{rung.StatusId}' twice: two stages on one id is unobservable", nameof(rungs));

            // ST1.5, the one true spiral: a stage that debuffs the regen of the pool whose depletion
            // or level drives it can never be climbed back out of — the mirror of the state's own
            // cause closes the loop and the value is stuck for the rest of the run. Both shipped
            // precedents refuse this at construction (ExhaustionPolicy.cs:59-66,
            // NervePolicy.cs:68-74); StatusRuntime enforces NOTHING here, so a host that dropped the
            // check would not lose a warning, it would lose the only guard the loop has.
            if (ownRegenChannel is not null && rung.StatMods.Any(m => m.ChannelId == ownRegenChannel))
                throw new ArgumentException(
                    $"self-regen cycle: track '{trackId}' stage '{rung.StatusId}' must not touch " +
                    $"'{drivingPoolResourceId}'s own regen channel '{ownRegenChannel}'", nameof(rungs));
        }

        TrackId = trackId;
        Match = match;
        StatusIdPrefix = $"{trackId}.";
        DrivingPoolResourceId = drivingPoolResourceId;
        _rungs = rungs;
        _grantIdFor = grantIdFor ?? (hostPtr => $"{trackId}:{hostPtr}");
    }

    /// <summary>The id <paramref name="stage"/> projects, or empty for the withdrawn state
    /// (<c>-1</c>). Exposed so a caller can name the id it is about to drive without duplicating the
    /// ladder's own ordering.</summary>
    public string StatusIdFor(int stage)
    {
        if (stage < -1 || stage >= _rungs.Count)
            throw new ArgumentOutOfRangeException(nameof(stage), stage, $"stage must be -1..{_rungs.Count - 1}");
        return stage >= 0 ? _rungs[stage].StatusId : "";
    }

    /// <summary>The grant id this host writes on <paramref name="hostPtr"/> — one actor's one track,
    /// never another actor's and never another track's.</summary>
    public string GrantIdFor(string hostPtr)
    {
        if (string.IsNullOrWhiteSpace(hostPtr)) throw new ArgumentException("hostPtr required", nameof(hostPtr));
        return _grantIdFor(hostPtr);
    }

    /// <summary>
    /// Makes the runtime's live projection match <paramref name="stage"/>: withdraws whatever the
    /// track had live, then applies this stage — attacker-less, inert magnitude, <c>BaseDuration 0</c>,
    /// scripted <see cref="FixedStatusRng"/>(0). Writes ONLY on a transition, and never leaves two
    /// instances of one track live on one host.
    ///
    /// <para><c>stage == -1</c> is the withdrawn state (a ladder that has climbed back to nothing),
    /// which is what makes a two-sided ladder — the shape <c>NerveLadder</c> does not have — expressible
    /// without a second mechanism.</para>
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="runtime"/> is null. A missing runtime is
    /// a CALLER's choice to defer (see the deferred-projection note on the type), never something the
    /// host silently absorbs as a no-op.</exception>
    public StatusProjectionResult Sync(StatusRuntime runtime, string hostPtr, int stage, DateTimeOffset now)
    {
        if (runtime is null) throw new ArgumentNullException(nameof(runtime));
        if (string.IsNullOrWhiteSpace(hostPtr)) throw new ArgumentException("hostPtr required", nameof(hostPtr));
        if (stage < -1 || stage >= _rungs.Count)
            throw new ArgumentOutOfRangeException(nameof(stage), stage, $"stage must be -1..{_rungs.Count - 1}");

        var grantId = _grantIdFor(hostPtr);
        var wantStatusId = stage >= 0 ? _rungs[stage].StatusId : null;
        var live = FindLive(runtime, hostPtr);

        // The idempotence read, and the whole reason this host exists: an unchanged answer is decided
        // from live state BEFORE any write, so N calls at an unchanged value produce exactly one apply.
        if (live != null && live.StatusId == wantStatusId)
            return new StatusProjectionResult(StatusProjectionChange.NoChange, wantStatusId ?? "", grantId, null);

        if (live != null)
            runtime.ClearGrant(grantId); // explicit withdraw: this instance never expires on its own

        if (wantStatusId is null)
            // Only a withdrawal if something WAS live. "Nothing live, nothing wanted" is the same
            // no-op as "nothing live, already what was asked for", and reporting it as a transition
            // would make a track that never projected look like it churned.
            return live != null
                ? new StatusProjectionResult(StatusProjectionChange.Withdrew, "", grantId, null)
                : new StatusProjectionResult(StatusProjectionChange.NoChange, "", grantId, null);

        var outcome = runtime.Apply(
            new StatusApplyInput(
                StatusId: wantStatusId,
                HostPtr: hostPtr,
                AttackerPtr: null,
                GrantId: grantId,
                BaseMagnitude: 1.0, // inert -- ModifyStat reads StatMods directly, never EffectiveMagnitude
                BaseDuration: 0,    // 0 -> ExpiresAt = DateTimeOffset.MaxValue: persists until the next Sync
                PeriodMs: 0,
                DurationMs: 0,
                AttackerLess: true,
                StatMods: _rungs[stage].StatMods),
            ScriptedRng,
            now);

        return new StatusProjectionResult(
            outcome.Applied ? StatusProjectionChange.Applied : StatusProjectionChange.NoChange,
            wantStatusId, grantId, outcome.ResistReason);
    }

    /// <summary>
    /// The boolean/edge shape: <c>active == true</c> projects rung 0, <c>false</c> withdraws. The
    /// exhaustion case verbatim — a pool that hit zero is a mechanical fact, not something an actor's
    /// stats can contest, so it is read as a threshold and written as a one-rung ladder.
    /// </summary>
    public StatusProjectionResult Sync(StatusRuntime runtime, string hostPtr, bool active, DateTimeOffset now) =>
        Sync(runtime, hostPtr, active ? 0 : -1, now);

    StatusInstance? FindLive(StatusRuntime runtime, string hostPtr)
    {
        foreach (var instance in runtime.ForHost(hostPtr))
        {
            var owned = Match == StatusProjectionMatch.Exact
                ? instance.StatusId == _rungs[0].StatusId
                : instance.StatusId.StartsWith(StatusIdPrefix, StringComparison.Ordinal);
            if (owned)
                return instance;
        }
        return null;
    }
}
