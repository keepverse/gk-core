using FusionRpg.Core.Status;

namespace FusionRpg.Core.PassiveTree.GateCounters;

/// <summary>
/// spec-gate-counters.md §2.1 — the four sub-decisions, applied over
/// <see cref="StatusRuntime.OnFreshApplication"/> (task G1's P2). Three of the four are already
/// guaranteed by the event this class listens to, and are not re-checked here:
///
/// <list type="bullet">
/// <item><b>(b) Landed, never attempted.</b> <c>StatusRuntime.Apply</c> returns on a resist BEFORE
/// building the instance or firing <c>OnFreshApplication</c> — a resisted apply never reaches
/// <see cref="Handle"/> at all.</item>
/// <item><b>(c) A fresh application, never a refresh.</b> <c>UpsertInstance</c> fires
/// <c>OnFreshApplication</c> only when it added a new instance, never on a `Refresh`/`Replace` that
/// matched an existing one — a reapply loop on one target reaches <see cref="Handle"/> exactly once.</item>
/// </list>
///
/// What THIS class decides is the other two:
///
/// <list type="bullet">
/// <item><b>(a)/(d) Outbound to a distinct host, never inbound or self.</b> No attacker (an
/// attacker-less grant — e.g. an environmental effect) and a self-application (<c>AttackerPtr ==
/// HostPtr</c>) both earn nothing.</item>
/// <item><b>Ownership is decided at spawn, never by current allegiance</b> — see
/// <paramref name="resolveOwner"/>'s own doc below.</item>
/// </list>
/// </summary>
public sealed class StatusAppliedCounter
{
    public const string Quantity = "status_applied";

    readonly GateCounterAccumulator _accumulator;
    readonly Func<StatusInstance, GateOwnerKey?> _resolveOwner;

    /// <param name="accumulator">Where a credit lands — §4.3, no direct store write here.</param>
    /// <param name="resolveOwner">
    /// Answers "which player owns this application's attacker", or <c>null</c> for none. This class
    /// has NO notion of "current side" at all, by construction: it calls this delegate exactly once,
    /// passing only the immutable <see cref="StatusInstance"/> the fresh application is about — the
    /// same shape <see cref="StatusRuntime"/>'s own constructor already uses for
    /// <c>ActorDerivedResolve</c> rather than reaching into an actor-hub dependency itself.
    ///
    /// <para><b>Why that closes the charm/hypno loophole (§2.1's closing rule).</b>
    /// <see cref="StatusInstance.AttackerPtr"/> is set once, at the moment <c>StatusRuntime.Apply</c>
    /// builds the instance, and is never mutated afterward — so it is already "the attacker at spawn
    /// of this application," not "whoever currently controls that entity." The one way this guarantee
    /// could be defeated is a resolver that reaches past the instance to ask a live "who currently
    /// controls this ptr" question instead of a spawn-time roster question — this class's contract is
    /// that the resolver answers from the latter. `hypno`/`charm_pulse` flip which SIDE an actor fights
    /// on for the engagement; they do not rewrite which player's roster that actor was ever a part of,
    /// so a correctly-wired resolver (spawn-time roster ownership) answers <c>null</c> for a charmed
    /// enemy's applications even while it is, for this fight, nominally "on the player's side" —
    /// crediting nobody, never the charmer. Wiring the concrete resolver against whatever roster/actor
    /// store the host maintains is outside this module (task G6's injector wiring), which is why this
    /// class takes it as a dependency rather than owning a lookup.</para>
    /// </param>
    public StatusAppliedCounter(GateCounterAccumulator accumulator, Func<StatusInstance, GateOwnerKey?> resolveOwner)
    {
        _accumulator = accumulator ?? throw new ArgumentNullException(nameof(accumulator));
        _resolveOwner = resolveOwner ?? throw new ArgumentNullException(nameof(resolveOwner));
    }

    /// <summary>Wire directly: <c>statusRuntime.OnFreshApplication += counter.Handle;</c> (mirrors
    /// task G1's own doc comment on <see cref="StatusRuntime.OnFreshApplication"/>).</summary>
    public void Handle(StatusAppliedEvent ev)
    {
        var instance = ev.Instance;

        // (a) No attacker at all -- an attacker-less grant has nobody to credit.
        if (string.IsNullOrWhiteSpace(instance.AttackerPtr))
            return;

        // (d) A distinct host, never yourself -- a self-application (e.g. a self-buff loop) earns
        // nothing (§2.1d). Case-insensitive, matching every other host/attacker comparison in
        // StatusRuntime itself.
        if (string.Equals(instance.AttackerPtr, instance.HostPtr, StringComparison.OrdinalIgnoreCase))
            return;

        // Roster validation (§2.1's closing paragraph): StatusCategoryRegistry.Register can add ids
        // at runtime (the action program's exhaustion debuffs) -- a registered id with no tree still
        // counts harmlessly, but an id the registry has NEVER heard of reaching this counter is a
        // content or programming error, not a normal state, and throws rather than crediting garbage
        // under a subject_id no tree-content stage will ever recognise.
        if (!StatusCategoryRegistry.TryGetCategory(instance.StatusId, out _))
            throw new ArgumentException(
                $"status_applied counter: '{instance.StatusId}' is not a StatusCategoryRegistry id " +
                "-- an unknown status id reaching this counter is a content or programming error.",
                nameof(ev));

        var owner = _resolveOwner(instance);
        if (owner is null)
            return; // no player owns this attacker at spawn -- never laundered via current allegiance.

        _accumulator.Credit(new GateCounterKey(owner.Value, Quantity, instance.StatusId));
    }
}
