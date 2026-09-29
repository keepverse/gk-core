using FusionRpg.Core.Delve.Events;
using FusionRpg.Core.Delve.Roll;
using FusionRpg.Core.Dungeon.Tuning;

namespace FusionRpg.Core.Delve.Domains;

/// <summary>
/// D4.17 row 7's own production bridge (party-dungeon-todo.md, 2026-09-07) — closes THREE of
/// `event-deck` D3.9's own six still-missing preflight rules (spec-event-deck.md §9), the ones that
/// are genuinely domain/room-scoped rather than catalog-wide, matching row 6's own finding that the
/// real `EventDeckPreflight.Run(EventCatalog, int, Func&lt;string,int&gt;)` signature has no
/// domain/room parameter at all — this could not have been added there without changing an
/// already-shipped, already-tested method's own contract.
///
/// <para><b>Rule 1</b> — "every `eventPool` id exists and fits": <see cref="EventFilters.KindFits"/>
/// (D3.2) — a room archetype's own `kind` (e.g. `rest`) may only reference an event of the ONE
/// matching event `kind` (`encounter-event` for `rest`), per `EventFilters.RoomKindToEventKind`'s own
/// closed table. That table's own doc comment says a mismatch is "refused at LOAD (a corpus rule, not
/// this runtime filter)" — but no room-import writer exists anywhere yet (D4.16, still unbuilt) to BE
/// that load-time refusal, so this preflight bridge is, today, the ONLY place this rule is enforced.</para>
///
/// <para><b>Rule 8</b> — "&gt; events.noRepeatRooms distinct cells per archetype pool": "cell" here is
/// NOT row 3's `(kind, climate)` room-palette cell — it is `EventFilters.EventCell(Kind, Theme)`, an
/// EVENT's own identity tuple (`EventFilters.cs:93-101`, the same identity `ByRecentCells`'s own
/// trailing-window filter repeats against). Read directly rather than assumed from the shared English
/// word "cell", which names two different things in this same spec. Skipped for an archetype whose own
/// authored pool is EMPTY — a `fight`/`boss`/`cache` kind structurally never draws an event at all
/// (`EventFilters.RoomKindToEventKind` has no entry for them), so an empty pool there is correct
/// content, not a headroom defect; rule 9 below already catches a `rest` archetype left empty by
/// mistake, so gating rule 8 on non-empty loses no real protection.</para>
///
/// <para><b>Rule 9</b> — "&gt;= 1 `encounter-event` per `rest` archetype": every archetype whose own
/// `kind` is `rest` must resolve at least one pool entry to a real `encounter-event`-kind event.
/// Given rule 1 already runs first in this same loop and `EventFilters.RoomKindToEventKind` maps
/// `rest` ONLY to `encounter-event`, a non-empty, kind-fit-passing `rest` pool is by construction
/// already all `encounter-event` — this rule's real, distinguishable bite is exactly an EMPTY `rest`
/// pool, the one case rule 8 does not itself catch (it is gated on a non-empty pool).</para>
/// </summary>
public static class DomainEventPreflight
{
    public static Func<DomainRow, IReadOnlyList<DomainRefusal>> Build(
        IReadOnlyDictionary<string, IReadOnlyList<string>> roomPaletteByDomainId,
        IReadOnlyDictionary<string, RoomPaletteEntry> roomsById,
        IReadOnlyDictionary<string, IReadOnlyList<string>> roomEventPoolById,
        EventCatalog catalog,
        DungeonTuning tuning)
    {
        if (roomPaletteByDomainId is null) throw new ArgumentNullException(nameof(roomPaletteByDomainId));
        if (roomsById is null) throw new ArgumentNullException(nameof(roomsById));
        if (roomEventPoolById is null) throw new ArgumentNullException(nameof(roomEventPoolById));
        if (catalog is null) throw new ArgumentNullException(nameof(catalog));
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));

        return domain =>
        {
            // Rows 3/4 (run before this one) already refuse an empty palette cell or an unreal room
            // id in the palette; an absent lookup here is unreachable in a real chain.
            if (!roomPaletteByDomainId.TryGetValue(domain.DomainId, out var palette)) return Array.Empty<DomainRefusal>();

            foreach (var roomId in palette)
            {
                if (!roomsById.TryGetValue(roomId, out var room)) continue;
                if (!roomEventPoolById.TryGetValue(roomId, out var poolIds)) continue;

                var resolved = new List<EventRow>(poolIds.Count);
                foreach (var eventId in poolIds)
                {
                    var ev = catalog.Resolve(eventId);
                    if (ev is null)
                        return new[] { new DomainRefusal(domain.DomainId, "domain.event:pool-ref-missing",
                            $"room '{roomId}' eventPool names '{eventId}', which is not a real event") };

                    if (!EventFilters.KindFits(room.Kind, ev.Kind))
                        return new[] { new DomainRefusal(domain.DomainId, "domain.event:kind-mismatch",
                            $"room '{roomId}' (kind={room.Kind}) eventPool names '{eventId}' (kind={ev.Kind}), which does not fit") };

                    resolved.Add(ev);
                }

                if (resolved.Count > 0)
                {
                    var distinctCells = resolved.Select(e => new EventFilters.EventCell(e.Kind, e.Theme)).Distinct().Count();
                    if (distinctCells <= tuning.EventsNoRepeatRooms)
                        return new[] { new DomainRefusal(domain.DomainId, "domain.event:cell-headroom",
                            $"room '{roomId}' (kind={room.Kind}) pool has {distinctCells} distinct (kind,theme) " +
                            $"cell(s), needs more than events.noRepeatRooms ({tuning.EventsNoRepeatRooms})") };
                }

                if (string.Equals(room.Kind, "rest", StringComparison.Ordinal)
                    && !resolved.Any(e => string.Equals(e.Kind, "encounter-event", StringComparison.Ordinal)))
                {
                    return new[] { new DomainRefusal(domain.DomainId, "domain.event:rest-needs-encounter",
                        $"room '{roomId}' is a rest archetype with no encounter-event in its own pool") };
                }
            }

            return Array.Empty<DomainRefusal>();
        };
    }
}
