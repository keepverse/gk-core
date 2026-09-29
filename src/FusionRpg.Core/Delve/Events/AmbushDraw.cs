using FusionRpg.Core.Battle;
using FusionRpg.Core.Delve.Roll;
using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Delve.Events;

/// <summary>
/// Whether an ambush fired this rest room, and if so, which event. `Event` is the drawn `EventRow`
/// only — resolving its own outcome/effects is `OutcomeResolver`'s (D3.5) and `TryInstantiate`'s own
/// job (D3.5's already-named gap), not this draw's. <see cref="EmptyPoolWarning"/> is spec §7's "one
/// designed empty-pool case": a hit whose eligible pool is empty is NOT an ambush and NOT a refusal —
/// preflight is what guarantees the pool is non-empty before eligibility narrows it.
/// </summary>
public sealed record AmbushOutcome(bool Ambushed, EventRow? Event, bool EmptyPoolWarning = false);

/// <summary>
/// `event-deck` D3.9 (spec-event-deck.md §7, "Ambush and curio seams") — PARTIALLY BUILT: the draw
/// itself (this file) is done and proven; `EventDeck.DrawAmbush`'s own thin wrapper (the spec's cited
/// call site, `RestResolver.Resolve` → `EventDeck.DrawAmbush`) is not built, since `EventDeck.cs` itself
/// is D3.3's own already-named gap — nothing in `Delve/Attrition` calls this yet.
/// </summary>
public static class AmbushDraw
{
    /// <summary>Spec §7, verbatim: "every ambush row carrying `Not(HasStatus watch)`" — ASSUMED
    /// `Subject.Self` (the party), matching `EntityFacts.StatusMask`'s own "union over members" shape:
    /// a watch posted by ANY member should suppress the ambush for the whole party. Not explicitly
    /// spelled out in the cited text beyond the leaf name itself.</summary>
    public const string WatchStatusId = "watch";

    public static string RootStream(int row, int col) => DelveStreams.Event(row, col) + ":ambush";

    /// <summary>
    /// Spec §7, verbatim: `NextPerMille() < rest.ambushMilli` on this room's own `:ambush` stream; on a
    /// hit, §2-3's own filter-then-pick over the REST ARCHETYPE's pool restricted to `encounter-event`
    /// (`EventFilters.ByKindFit(pool, "rest")` already resolves this kind mapping, D3.2), each
    /// candidate's own authored eligibility (`EventFilters.ByEligibility`), AND the extra
    /// `Not(HasStatus watch)` gate every ambush row carries. An empty result after that gate is the
    /// ONE designed empty-pool case (`EmptyPoolWarning: true`, never an `EventDeckRefusal`) — spec:
    /// "the pool was non-empty before eligibility (preflight proves it per rest archetype)." The pick
    /// itself reuses `EventDraw.PickEvent`'s own `:pick` sub-stream directly, matching §3's own stream
    /// table row verbatim ("ambush | ...:ambush | one roll, then :pick on the rest pool").
    /// </summary>
    public static AmbushOutcome Draw(
        IReadOnlyList<EventRow> restArchetypePool, int row, int col, string? roomClimate,
        EventCatalog catalog, FactReader facts, Func<string, int> statusBit,
        long ambushMilli,
        long climateAffinityMatchMilli, long climateAffinityNoneMilli, long climateAffinityOffMilli,
        ulong seed)
    {
        if (restArchetypePool is null) throw new ArgumentNullException(nameof(restArchetypePool));
        if (catalog is null) throw new ArgumentNullException(nameof(catalog));
        if (statusBit is null) throw new ArgumentNullException(nameof(statusBit));

        var stream = SeededRng.DeriveStream(seed, RootStream(row, col));
        var roll = stream.NextPerMille();
        if (roll >= ambushMilli) return new AmbushOutcome(Ambushed: false, Event: null);

        var kindFit = EventFilters.ByKindFit(restArchetypePool, "rest");
        var eligible = EventFilters.ByEligibility(kindFit, catalog, facts);
        var afterWatch = WithoutWatch(eligible, statusBit, facts);

        if (afterWatch.Count == 0)
            return new AmbushOutcome(Ambushed: false, Event: null, EmptyPoolWarning: true);

        var picked = EventDraw.PickEvent(afterWatch, row, col, roomClimate,
            climateAffinityMatchMilli, climateAffinityNoneMilli, climateAffinityOffMilli, seed);
        return new AmbushOutcome(Ambushed: true, Event: picked);
    }

    static IReadOnlyList<EventRow> WithoutWatch(
        IReadOnlyList<EventRow> pool, Func<string, int> statusBit, FactReader facts)
    {
        var notWatch = new PredicateNode.Not(
            new PredicateNode.Leaf(LeafId.HasStatus, Subject.Self, Text: WatchStatusId));
        var r = PredicateCompiler.TryCompile(notWatch, statusBit, out var compiled);
        if (!r.IsOk)
            throw new InvalidOperationException($"AmbushDraw's own Not(HasStatus watch) gate failed to compile: {r.Reason}");

        var result = new List<EventRow>();
        foreach (var e in pool)
        {
            var perCandidate = facts;
            if (compiled.Evaluate(ref perCandidate))
                result.Add(e);
        }
        return result;
    }
}
