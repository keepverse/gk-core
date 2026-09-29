using FusionRpg.Core.Delve.Attrition;
using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Delve.Events;

/// <summary>
/// `event-deck` D3.8 (spec-event-deck.md §6): "Facts for an event, built once by the host." Two
/// independent builders, matching the spec's own two-subject split — `Self` = the party, `Target` =
/// the room — rather than one combined function hiding which inputs feed which subject.
/// </summary>
public static class EventFacts
{
    /// <summary>
    /// `Self` = the party (spec §6, verbatim): `HpMilli` the LOWEST STANDING member's hp‰, `StatusMask`
    /// the UNION over every member (standing or not — the spec says "over members", not "over standing
    /// members"), `Stock0..3Qty` interned supply quantities. `DownedCount` (D3.7's own new fact) rides
    /// along here since it is the same party-level aggregation. Throws if every member is downed — "the
    /// lowest STANDING member's" has no answer for a wiped party, and an event never resolves against
    /// one; this is a caller misuse to surface loudly, not a case to paper over with a fallback.
    /// </summary>
    /// <param name="maxHpByInstanceId">Each member's own current max hp — deliberately narrower than a
    /// full `ActorDerivedSnapshot` per member, since `hp`'s own max is the only derived value this
    /// aggregation needs.</param>
    /// <param name="statusBit">The SAME interning function a caller already has for
    /// <see cref="PredicateCompiler.TryCompile"/> — status ids are interned once, not re-derived here.</param>
    /// <param name="stock0Qty">Party-level supply quantities, already interned to the caller's own
    /// slots 0-3 (mirroring <see cref="LeafId.HoldsStock"/>'s own convention) — `loot-pack`'s future
    /// job to resolve for real; 0 is the honest "no pack yet" default, never a sentinel.</param>
    public static EntityFacts BuildSelf(
        IReadOnlyList<DelveMemberState> members,
        IReadOnlyDictionary<string, long> maxHpByInstanceId,
        Func<string, int> statusBit,
        int stock0Qty = 0, int stock1Qty = 0, int stock2Qty = 0, int stock3Qty = 0)
    {
        if (members is null) throw new ArgumentNullException(nameof(members));
        if (members.Count == 0) throw new ArgumentException("a party has at least one member", nameof(members));
        if (maxHpByInstanceId is null) throw new ArgumentNullException(nameof(maxHpByInstanceId));
        if (statusBit is null) throw new ArgumentNullException(nameof(statusBit));

        var standing = members.Where(m => !m.Downed).ToList();
        if (standing.Count == 0)
            throw new InvalidOperationException(
                "EventFacts.BuildSelf: every member is downed -- an event never resolves against a wiped party");

        var lowestHpMilli = standing.Min(m => HpMilliOf(m, maxHpByInstanceId));

        ulong statusMask = 0;
        foreach (var m in members)
            foreach (var status in m.Statuses)
            {
                var bit = statusBit(status.StatusId);
                if (bit is >= 0 and < 64) statusMask |= 1UL << bit;
            }

        return new EntityFacts(
            Side: 0, TypeId: 0, HpMilli: lowestHpMilli, ElementId: -1, Row: -1, Col: -1,
            IsMindControlled: false, IsKiller: false, StatusMask: statusMask,
            Stock0Qty: stock0Qty, Stock1Qty: stock1Qty, Stock2Qty: stock2Qty, Stock3Qty: stock3Qty,
            DownedCount: members.Count - standing.Count);
    }

    // Returns a bounded [0, 1000] per-mille ratio, never a magnitude -- the same `int` shape
    // `EntityFacts.HpMilli` and `HpBelowMilli`/`HpAboveMilli` already commit to; `hp`/`maxHp`
    // themselves stay `long` throughout and the multiply is `checked`, so overflow is caught before
    // the final narrowing, never silently wrapped.
    static int HpMilliOf(DelveMemberState m, IReadOnlyDictionary<string, long> maxHpByInstanceId)  // overflow-bounded: [0, 1000] per-mille ratio, see the comment above
    {
        if (!maxHpByInstanceId.TryGetValue(m.InstanceId, out var maxHp) || maxHp <= 0)
            throw new ArgumentException(
                $"missing or non-positive max hp for '{m.InstanceId}'", nameof(maxHpByInstanceId));
        if (!m.Pools.TryGetValue("hp", out var hp))
            throw new ArgumentException($"'{m.InstanceId}' has no 'hp' pool", nameof(m));
        return checked((int)(hp * 1000 / maxHp));
    }

    /// <summary>
    /// `Target` = the room (spec §6, verbatim): `ElementId` climate, `Row`/`Col`, `Band`, `RoomKind` —
    /// every one a plain, already-resolved ordinal (`-1` for a climate-neutral room, matching
    /// <see cref="EntityFacts.ElementId"/>'s own existing "-1 when the entity has none" convention).
    /// </summary>
    public static EntityFacts BuildTarget(int elementId, int row, int col, int band, int roomKind) => new(
        Side: 0, TypeId: 0, HpMilli: 1000, ElementId: elementId, Row: row, Col: col,
        IsMindControlled: false, IsKiller: false, StatusMask: 0, Band: band, RoomKind: roomKind);
}
