using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Delve.Events;

/// <summary>
/// `event-deck` D3.2 (spec-event-deck.md §2) — "Pool per room = `archetype.eventPool` ∩ four filters,
/// in order, each a set operation with no draw." Each filter is exposed independently (four PURE set
/// functions) because they are commutative — <see cref="ApplyAll"/> runs them in the spec's own stated
/// order as the one real production path, and the test suite proves a different order lands on the
/// identical surviving set, which is what makes calling them "filters" rather than a pipeline honest.
/// </summary>
public static class EventFilters
{
    /// <summary>Room-archetype kind → the one event kind it may hold (spec §2 line 1). `unknown` is
    /// deliberately absent — it means "any", checked as a special case rather than listed 20+ times.</summary>
    static readonly IReadOnlyDictionary<string, string> RoomKindToEventKind = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["curio"] = "curio",
        ["shrine"] = "shrine",
        ["trap"] = "trap",
        ["merchant"] = "bargain",
        ["wild"] = "story",
        ["rest"] = "encounter-event",
    };

    /// <summary>The one room-archetype kind with no fixed event-kind mapping — "any" (pity already
    /// decided this room resolves to an event, per §4).</summary>
    public const string UnknownRoomKind = "unknown";

    /// <summary>Filter 1 — kind fit (spec §2.1). A pool entry whose kind does not fit its archetype is
    /// refused at LOAD (a corpus rule, not this runtime filter) — this is the room-time mirror of that
    /// same rule, applied to whatever already-valid pool the archetype carries.</summary>
    public static bool KindFits(string roomArchetypeKind, string eventKind)
    {
        if (string.IsNullOrWhiteSpace(roomArchetypeKind)) throw new ArgumentException("roomArchetypeKind required", nameof(roomArchetypeKind));
        if (string.IsNullOrWhiteSpace(eventKind)) throw new ArgumentException("eventKind required", nameof(eventKind));
        if (string.Equals(roomArchetypeKind, UnknownRoomKind, StringComparison.Ordinal)) return true;
        return RoomKindToEventKind.TryGetValue(roomArchetypeKind, out var allowed)
            && string.Equals(allowed, eventKind, StringComparison.Ordinal);
    }

    public static IReadOnlyList<EventRow> ByKindFit(IReadOnlyList<EventRow> pool, string roomArchetypeKind)
    {
        if (pool is null) throw new ArgumentNullException(nameof(pool));
        return pool.Where(e => KindFits(roomArchetypeKind, e.Kind)).ToList();
    }

    /// <summary>Filter 2 — eligibility (spec §2.2). <paramref name="facts"/> is taken BY VALUE, never
    /// `ref`: each candidate evaluates against its own fresh copy (so one candidate's `Reads`
    /// instrumentation never bleeds into the next), the caller's own struct is never mutated by this
    /// call, and `EventCatalog.EligibilityFor` already returns <see cref="PredicateCompiler.Always"/>
    /// for an absent tree (spec: "an absent tree is Always").</summary>
    public static IReadOnlyList<EventRow> ByEligibility(IReadOnlyList<EventRow> pool, EventCatalog catalog, FactReader facts)
    {
        if (pool is null) throw new ArgumentNullException(nameof(pool));
        if (catalog is null) throw new ArgumentNullException(nameof(catalog));

        var result = new List<EventRow>();
        foreach (var e in pool)
        {
            var perCandidate = facts;
            if (catalog.EligibilityFor(e.EventId).Evaluate(ref perCandidate))
                result.Add(e);
        }
        return result;
    }

    /// <summary>
    /// Filter 3 — repeat scope (spec §2.3, §8). "Every scope is at least per-delve, so the invariant
    /// is absolute: no event id twice in one delve" — <paramref name="perDelveSeen"/> is therefore
    /// checked UNCONDITIONALLY, regardless of a row's own declared <see cref="EventRow.RepeatScope"/>;
    /// the domain/player sets are checked only for a row actually declaring that wider scope. Every
    /// seen-set is a plain id set, owned and populated elsewhere (`delve-scope`'s own tables/columns,
    /// none of which exist yet) — this filter only reads them.
    /// </summary>
    public static IReadOnlyList<EventRow> ByRepeatScope(
        IReadOnlyList<EventRow> pool,
        IReadOnlySet<string> perDelveSeen,
        IReadOnlySet<string> perDomainSeen,
        IReadOnlySet<string> oncePerPlayerSeen)
    {
        if (pool is null) throw new ArgumentNullException(nameof(pool));
        if (perDelveSeen is null) throw new ArgumentNullException(nameof(perDelveSeen));
        if (perDomainSeen is null) throw new ArgumentNullException(nameof(perDomainSeen));
        if (oncePerPlayerSeen is null) throw new ArgumentNullException(nameof(oncePerPlayerSeen));

        return pool.Where(e =>
            !perDelveSeen.Contains(e.EventId)
            && (!string.Equals(e.RepeatScope, "per-domain", StringComparison.Ordinal) || !perDomainSeen.Contains(e.EventId))
            && (!string.Equals(e.RepeatScope, "once-per-player", StringComparison.Ordinal) || !oncePerPlayerSeen.Contains(e.EventId))
        ).ToList();
    }

    /// <summary>One drawn cell — an event's own `(Kind, Theme)`, the identity §2.4 repeats against.</summary>
    public readonly record struct EventCell(string Kind, string? Theme);

    /// <summary>Filter 4 — recent cells (spec §2.4). "No event whose `(kind, theme)` cell was drawn in
    /// the last `events.noRepeatRooms` rooms of THIS PARTY'S route" — <paramref name="recentCells"/> is
    /// already that trailing window, computed by the caller from the party's own route; this filter
    /// does no windowing itself, matching the module's own "read model owned elsewhere" shape.</summary>
    public static IReadOnlyList<EventRow> ByRecentCells(IReadOnlyList<EventRow> pool, IReadOnlySet<EventCell> recentCells)
    {
        if (pool is null) throw new ArgumentNullException(nameof(pool));
        if (recentCells is null) throw new ArgumentNullException(nameof(recentCells));
        return pool.Where(e => !recentCells.Contains(new EventCell(e.Kind, e.Theme))).ToList();
    }

    /// <summary>The real production path — all four filters, in the spec's own stated order. Proven
    /// (by test, not by this method) to land on the same surviving set as any other order, since each
    /// filter is an independent set intersection.</summary>
    public static IReadOnlyList<EventRow> ApplyAll(
        IReadOnlyList<EventRow> pool,
        string roomArchetypeKind,
        EventCatalog catalog,
        FactReader facts,
        IReadOnlySet<string> perDelveSeen,
        IReadOnlySet<string> perDomainSeen,
        IReadOnlySet<string> oncePerPlayerSeen,
        IReadOnlySet<EventCell> recentCells)
    {
        var afterKind = ByKindFit(pool, roomArchetypeKind);
        var afterEligibility = ByEligibility(afterKind, catalog, facts);
        var afterRepeat = ByRepeatScope(afterEligibility, perDelveSeen, perDomainSeen, oncePerPlayerSeen);
        return ByRecentCells(afterRepeat, recentCells);
    }
}
