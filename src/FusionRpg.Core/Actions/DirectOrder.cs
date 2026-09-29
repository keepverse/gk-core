namespace FusionRpg.Core.Actions;

/// <summary>
/// combat-ai `intent-router` (module 4, CAI1.10, spec-intent-router.md §3): D3's second kind — an
/// order to ONE creature, not a takeover of it. Deterministic by construction: every field is a
/// value, and expiry is a TICK comparison, never a wall clock (`DecisionTrace.cs`'s own "recorded as
/// a DECISION AT A TICK, never re-measured on replay").
///
/// <para><b>No producer exists yet.</b> `commander-direct-orders` (module 20) is the first module
/// that builds a real <see cref="IOrderQueue"/> and issues orders; <see cref="IntentRouter"/> accepts
/// one (constructor/`Compose` parameter `orders`, defaulting to <c>null</c>) and consults it, but
/// every caller this module ships passes `orders: null` — the identity value that keeps this commit
/// byte-identical.</para>
///
/// <para><b><see cref="SubjectId"/>/<see cref="ScopeId"/> are the durable identity the ptr-reuse
/// rules decide on</b> (`DirectOrderAdmission.CheckScope`/`CheckSubject`, spec §2 steps 1-3, rows 3-5).
/// They are <b>trailing and optional</b>, so every existing construction keeps compiling and an order
/// that carries neither is exactly the order this module shipped before — the checks simply have
/// nothing to compare and the caller passes what it resolved. The transport that FILLS them is the FE
/// -> Server -> Injector path (CAI4.9's own remaining files); these two fields are its shape, so the
/// rules no longer need loose arguments threaded beside the order.</para>
/// </summary>
/// <param name="SubjectId">The durable specimen identity the order was issued against — the id
/// `CheckSubject`'s `OrderSubject` resolves from, never a ptr the caller supplied.</param>
/// <param name="ScopeId">The run/match the order belongs to; a mismatch against the live run is
/// <see cref="DirectOrderRefusal.StaleRun"/> rather than an order from the previous match commanding a
/// creature in the next one.</param>
public readonly record struct DirectOrder(
    string ActorKey, string ActionId, string? TargetKey, long IssuedTick,
    string? SubjectId = null, string? ScopeId = null);

/// <summary>
/// combat-ai `intent-router` (module 4, CAI1.10, spec-intent-router.md §3): the order queue seam.
/// `MaxLiveOrdersPerActor` is deliberately a STRUCTURAL code `const`, not a tuning key here — a
/// second live order for the same actor is a queue, and a queue of orders is a plan, which this
/// program does not have; whichever module owns the real queue enforces "a new order replaces the
/// old" at that single point.
/// </summary>
public interface IOrderQueue
{
    /// <summary>Non-blocking, never awaits. Returns false when this actor has no live order.</summary>
    bool TryPeek(string actorKey, out DirectOrder order);

    /// <summary>The order produced an intent that fired.</summary>
    void Commit(string actorKey);

    /// <summary><c>orderTimeoutTicks</c> elapsed with no fire.</summary>
    void Expire(string actorKey);
}
