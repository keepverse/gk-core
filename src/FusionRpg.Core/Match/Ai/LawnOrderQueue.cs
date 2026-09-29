using FusionRpg.Core.Actions;

namespace FusionRpg.Core.Match.Ai;

/// <summary>The result of offering one order to <see cref="LawnOrderQueue"/>. <see cref="Superseded"/>
/// is not a failure: the order was admitted and an earlier one for the same actor was replaced, which
/// the host reports as <c>lawn.order.superseded</c> so the replacement is never silent.</summary>
public readonly record struct LawnOrderOfferResult(bool Accepted, bool Superseded, DirectOrderRefusal Refusal);

/// <summary>
/// combat-ai `commander-direct-orders` (module 20, CAI4.9, spec-commander-direct-orders.md §3/§6) — the
/// lawn's <see cref="IOrderQueue"/>.
///
/// <para><b>Not a plan.</b> An order is a standing preference for one actor's next decision, and an
/// actor has exactly one next decision, so <see cref="MaxLiveOrdersPerActor"/> is 1: a second order
/// REPLACES the first rather than queueing behind it. A queue of orders would be a plan, which this
/// program does not have.</para>
///
/// <para><b>The cap refuses the new order, never evicts a stranger's.</b> Evicting another actor's order
/// to make room for this one is a silent cross-actor failure; <c>InjectorCommandInbox</c>'s drop-oldest
/// is the wrong shape here for exactly that reason — that queue holds interchangeable messages, this one
/// holds per-actor state. Reaching the cap is reported as <see cref="DirectOrderRefusal.QueueFull"/>.</para>
///
/// <para><b>Expiry is not this type's rule.</b> The lifetime comparison belongs to
/// <see cref="FusionRpg.Core.Actions.IntentRouter"/>'s own <c>orderTimeoutTicks</c>, which calls
/// <see cref="Expire"/>; <see cref="TryPeek"/> is the three-line non-blocking read the router's contract
/// specifies, so there is exactly one implementation of the rule and no clock on this path. Nothing here
/// reads a <c>DateTime</c> or a <c>DateTimeOffset</c>.</para>
///
/// <para><b>The queue is not a cache</b> (module 4 already records this against DESIGN-GATE §2.16): it
/// holds orders, not a projection of any other state. Its key set still moves — admission, commit,
/// expiry, a terminal gate refusal, death, spawn and the board edge — and each edge has a named method
/// here (<see cref="Offer"/>, <see cref="Commit"/>, <see cref="Expire"/>,
/// <see cref="HandleGateRefusal"/>, <see cref="Remove"/>, <see cref="Clear"/>).</para>
/// </summary>
public sealed class LawnOrderQueue : IOrderQueue
{
    /// <summary>Structural (tunables-ssot.md T2), NOT a balance number: a memory bound on a per-actor
    /// map, not a progression ceiling and not a magnitude. The orderable population is already bounded by
    /// D6's five-unique-per-side deploy cap, so 16 can only be reached by a defect — and reaching it is
    /// reported rather than absorbed.</summary>
    public const int Cap = 16;

    /// <summary>Structural, the value module 4's own contract names: "a second live order for the same
    /// actor is a queue, and a queue of orders is a plan, which this program does not have." A new order
    /// replaces.</summary>
    public const int MaxLiveOrdersPerActor = 1;

    /// <summary>Structural — an observability RATE bound, not balance. One report per order per DISTINCT
    /// reason: an order living 80 lawn ticks is reconsidered on every edge, and an unbounded report would
    /// put a stream of identical "on cooldown" rows on the wire per second per ordered actor.</summary>
    public const int ReportsPerOrderPerReason = 1;

    readonly Dictionary<string, DirectOrder> _byActor = new(StringComparer.Ordinal);

    /// <summary>Per-actor bitmask of the reasons already reported for the live order. A bitmask over a
    /// CLOSED vocabulary (12 members today) rather than a set: allocation-free, and
    /// <see cref="ShouldReport"/> throws rather than shifting past the word if the enum is ever widened
    /// past 64 — a structural bound, not a cap on a magnitude.</summary>
    readonly Dictionary<string, ulong> _reportedReasons = new(StringComparer.Ordinal);

    /// <summary>Orders currently live — a reading, never a pinned population.</summary>
    public int Count => _byActor.Count;

    /// <summary>
    /// Admits an order for <paramref name="actorKey"/>, replacing any order already live for that actor.
    /// Refuses with <see cref="DirectOrderRefusal.QueueFull"/> only when a NEW actor would push the map
    /// past <see cref="Cap"/>.
    /// </summary>
    public LawnOrderOfferResult Offer(string actorKey, in DirectOrder order)
    {
        if (string.IsNullOrEmpty(actorKey))
            throw new ArgumentException("actorKey must not be empty", nameof(actorKey));
        if (!string.Equals(order.ActorKey, actorKey, StringComparison.Ordinal))
            throw new ArgumentException(
                $"order names actor '{order.ActorKey}' but was offered for '{actorKey}'", nameof(order));

        var replaced = _byActor.ContainsKey(actorKey);
        if (!replaced && _byActor.Count >= Cap)
            return new LawnOrderOfferResult(Accepted: false, Superseded: false, DirectOrderRefusal.QueueFull);

        _byActor[actorKey] = order;
        // A NEW order starts its own report budget: the same reason it carried before must be reported
        // again, because it is a different order.
        _reportedReasons.Remove(actorKey);
        return new LawnOrderOfferResult(Accepted: true, Superseded: replaced, DirectOrderRefusal.None);
    }

    /// <summary>Non-blocking, never awaits, never expires — the router owns the lifetime comparison.
    /// False when this actor has no live order.</summary>
    public bool TryPeek(string actorKey, out DirectOrder order) => _byActor.TryGetValue(actorKey, out order);

    /// <summary>The order produced an intent that paid and fired. Idempotent: a second call is a no-op,
    /// so a double report of the same commit cannot resurrect or double-remove anything.</summary>
    public void Commit(string actorKey) => Drop(actorKey);

    /// <summary><c>orderTimeoutTicks</c> elapsed with no fire.</summary>
    public void Expire(string actorKey) => Drop(actorKey);

    /// <summary>The death edge (Hot rule 4), the spawn edge (a reused ptr address must find nothing), and
    /// the module-19 kill switch. Returns whether an order was actually removed.</summary>
    public bool Remove(string actorKey) => Drop(actorKey);

    /// <summary>The board edge and the kill switch turning off mid-match: every order goes, so no
    /// half-armed queue survives into a match nothing will decide in.</summary>
    public void Clear()
    {
        _byActor.Clear();
        _reportedReasons.Clear();
    }

    /// <summary>
    /// A gate refused the order's action at decision time (spec §7). A <b>retryable</b> reason leaves the
    /// order live — it is reconsidered on every later edge, and the AI decides underneath meanwhile —
    /// while a <b>terminal</b> one removes it at once, because retrying cannot help. Returns whether the
    /// order was removed.
    /// </summary>
    public bool HandleGateRefusal(string actorKey, UsabilityResult result)
    {
        if (DirectOrderAdmission.Classify(result) != DirectOrderRefusalClass.Terminal) return false;
        return Drop(actorKey);
    }

    /// <summary>
    /// Whether this refusal deserves a report. True once per order per DISTINCT reason
    /// (<see cref="ReportsPerOrderPerReason"/>); the emission itself is the host's, through the
    /// observe-only <c>lawn.order.*</c> fork — nothing on the decision path reads it back.
    /// </summary>
    public bool ShouldReport(string actorKey, UsabilityResult result)
    {
        var index = (int)result.Reason;
        if (index is < 0 or >= 64)
            throw new ArgumentOutOfRangeException(
                nameof(result), result.Reason,
                "the report bitmask is one machine word; a UsabilityReason past index 63 needs a wider mask");

        var bit = 1UL << index;
        _reportedReasons.TryGetValue(actorKey, out var reported);
        if ((reported & bit) != 0) return false;

        _reportedReasons[actorKey] = reported | bit;
        return true;
    }

    bool Drop(string actorKey)
    {
        _reportedReasons.Remove(actorKey);
        return _byActor.Remove(actorKey);
    }
}
