using FusionRpg.Core.Delve.Events;

namespace FusionRpg.Core.Delve.Quests;

/// <summary>
/// D4.13's own real remaining bridge, closed 2026-09-07 (citation corrected the same day):
/// `QuestOffer.Satisfiable`'s own `archetypeEventPoolHasKind(archetypeId, targetRef)` delegate, which
/// had ZERO implementations anywhere in the tree (confirmed via `grep`, zero hits outside its own
/// declaration in `QuestOffer.cs`).
///
/// <para><b>"archetypeId" IS a real room id, not a separate concept.</b> A rolled room's
/// <see cref="Roll.DelveRoomFact.ArchetypeId"/> is set, at roll time, straight from
/// <c>domain.RoomPalette</c>'s own <c>RoomId</c> (`Roll/DelveGraphRoll.cs`, step 6: <c>archetype[node]
/// = WeightedChoice.Pick(options, ...)</c> over <c>palette.Select(rp =&gt; new
/// WeightedOption&lt;string&gt;(rp.RoomId, 1))</c>) — the SAME key <see cref="Domains.DomainEventPreflight"/>
/// already reads a room's own <c>eventPool</c> by (<see cref="RoomEventPoolSeedFile"/>, keyed by
/// `roomId`). This is not two unrelated lookups bridged together; it is one lookup reached from a
/// differently-named starting field.</para>
///
/// <para><b>"targetRef" IS an event kind, not a curio-specific vocabulary.</b> Spec-delve-quests.md §1's
/// own table names `gather-curio-kind`'s `targetKind` as "`curio-kind` (an event `kind`)" — the exact
/// same <c>.Kind</c> field a resolved <see cref="EventRow"/> already carries (e.g. `"curio-event"`),
/// read the identical way <see cref="Domains.DomainEventPreflight"/> already reads it off <c>ev.Kind</c>
/// for its own rule 1 (`EventFilters.KindFits`).</para>
/// </summary>
public static class QuestArchetypeEventBridge
{
    /// <summary>Does room <paramref name="archetypeId"/>'s own `eventPool` hold at least one event
    /// whose real, resolved <see cref="EventRow.Kind"/> equals <paramref name="targetRef"/>? An
    /// archetype absent from <paramref name="roomEventPoolById"/>, or an event id the pool names that
    /// does not resolve, both read as "no" here — referential integrity for a room's own `eventPool`
    /// is `domain-catalog` row 7's own job (<see cref="Domains.DomainEventPreflight"/>), not this
    /// bridge's; a bad reference should never masquerade as "this quest happens to be satisfiable."</summary>
    public static Func<string, string, bool> Build(
        IReadOnlyDictionary<string, IReadOnlyList<string>> roomEventPoolById, EventCatalog catalog)
    {
        if (roomEventPoolById is null) throw new ArgumentNullException(nameof(roomEventPoolById));
        if (catalog is null) throw new ArgumentNullException(nameof(catalog));

        return (archetypeId, targetRef) =>
            roomEventPoolById.TryGetValue(archetypeId, out var pool)
            && pool.Any(eventId => catalog.Resolve(eventId) is { } ev && string.Equals(ev.Kind, targetRef, StringComparison.Ordinal));
    }
}
