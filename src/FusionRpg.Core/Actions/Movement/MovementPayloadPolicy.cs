namespace FusionRpg.Core.Actions.Movement;

/// <summary>
/// A-M1 (spec-movement-payload.md §2): the RPG-layer half of a movement action — a pure, deterministic
/// policy over the published <see cref="MovementPayloadTuning"/> vocabulary. No Unity reference
/// anywhere in this file (asserted by a new <c>FusionRpg.Guard.Tests</c> case that scans this
/// directory directly — <c>scripts/guard-secondary-no-unity.ps1</c> never reaches
/// <c>Actions/Movement/</c>, so a test claiming that guard already covers it would pass for the wrong
/// reason, per §4/AC4). Never reads PvZ state, never makes a model call, never runs on a hot path — it
/// is read by the planner (A-S1, not yet built) to build the pool a movement brief may draw from, and
/// by <see cref="ActionValidator"/> to refuse a bad one.
/// </summary>
public sealed class MovementPayloadPolicy
{
    readonly IReadOnlyDictionary<string, MovementPayloadEntry> _channels;
    readonly IReadOnlyDictionary<string, MovementPayloadEntry> _statuses;
    readonly IReadOnlyDictionary<string, MovementPayloadEntry> _payloadKinds;

    public MovementPayloadPolicy(MovementPayloadTuning tuning)
    {
        ArgumentNullException.ThrowIfNull(tuning);
        _channels = ToMap(tuning.Channels);
        _statuses = ToMap(tuning.Statuses);
        _payloadKinds = ToMap(tuning.PayloadKinds);
    }

    static IReadOnlyDictionary<string, MovementPayloadEntry> ToMap(IReadOnlyList<MovementPayloadEntry> entries)
    {
        var map = new Dictionary<string, MovementPayloadEntry>(entries.Count, StringComparer.Ordinal);
        foreach (var e in entries) map[e.Id] = e;
        return map;
    }

    /// <summary>Membership in the published <c>channels</c> list — the loader has already proven every
    /// member resolves in <c>DerivedStatRegistry</c>, so this is a plain lookup.</summary>
    public bool IsLegalPayloadChannel(string channel) =>
        !string.IsNullOrEmpty(channel) && _channels.ContainsKey(channel);

    /// <summary>Membership in the published <c>statuses</c> list — the loader has already proven every
    /// member resolves in <c>StatusCatalogBootstrap</c> and carries no
    /// <c>StatusPayloadKind.UnityCc</c>, so this is a plain lookup.</summary>
    public bool IsLegalPayloadStatus(string statusId) =>
        !string.IsNullOrEmpty(statusId) && _statuses.ContainsKey(statusId);

    /// <summary>Membership in the published <c>payloadKinds</c> list — exposed for the planner and for
    /// schema tests; not itself part of <see cref="HasStandalonePayload"/>'s check (that check reads
    /// what a compiled action actually carries, not which kind label a brief used to describe it).</summary>
    public bool IsLegalPayloadKind(string kind) =>
        !string.IsNullOrEmpty(kind) && _payloadKinds.ContainsKey(kind);

    /// <summary>
    /// The load-bearing check (§2, AC6/AC7): true when a <c>category = Movement</c> action's compiled
    /// container carries at least one bound effect atom — false only for an action whose sole effect
    /// is the reposition itself.
    ///
    /// <para><b>Why this reads <see cref="CompiledAction.Scopes"/> rather than resolving each atom's
    /// own channel/status id.</b> <see cref="ActionScopeRow"/> carries only an opaque
    /// <c>AtomId</c> — the channel a <c>stat.modify</c> atom writes, or the status id a
    /// <c>status.apply</c> atom names, lives in that atom's own <c>ParamsJson</c>
    /// (<c>AtomRow</c>, resolved through the effect-atom program's own compiler), which is outside
    /// what <see cref="CompiledAction"/> carries and outside what this module's own pure-policy,
    /// never-on-a-hot-path posture (§2, §3 "never become a fourth generation pipeline") can reach
    /// without adding a resolver dependency this module was never specced to carry.</para>
    ///
    /// <para>The real legality gate for a SPECIFIC channel or status id is
    /// <see cref="IsLegalPayloadChannel"/>/<see cref="IsLegalPayloadStatus"/> above, applied at the
    /// point a payload is actually chosen — the planner (A-S1, §6: "does not exist" yet) drawing atoms
    /// into a movement action's container in the first place, the one place that legitimately holds
    /// atom-level detail. By the time an action reaches <see cref="CompiledAction"/> shape, the corpus
    /// has already drawn only from A-M1's own published pool (§3: never a fourth vocabulary), so a
    /// bound atom on a <c>category = Movement</c> container is, by construction, a legal payload — this
    /// method's job is the validator's own backstop, proving the container was not authored empty
    /// ("a movement action must do something with the game closed").</para>
    /// </summary>
    public bool HasStandalonePayload(CompiledAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return action.Scopes.Count > 0;
    }
}
