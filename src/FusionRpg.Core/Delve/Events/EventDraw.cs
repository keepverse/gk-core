using FusionRpg.Core.Actions.Seeding;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Delve.Roll;

namespace FusionRpg.Core.Delve.Events;

/// <summary>Thrown when a room's filtered event pool has nothing left to draw — every candidate
/// refused by D3.2's filters, or weighted to zero (spec-event-deck.md §3: "an empty list throws...
/// rethrown here as `EventDeckRefusal` naming the room"). A content/preflight defect, never expected
/// to reach a real delve — §9's own preflight is what is supposed to prevent this at import.</summary>
public sealed class EventDeckRefusal : Exception
{
    public EventDeckRefusal(string message) : base(message) { }
}

/// <summary>
/// `event-deck` D3.3 (spec-event-deck.md §3) — the low-level, single-purpose draw: given an already
/// D3.2-filtered pool, which event wins. Every stream is `SeededRng.DeriveStream(seed, name)`, one per
/// step, matching `ExpeditionResolver.cs`'s own "the tick shape" discipline; the pick itself goes
/// through the real, shipped `WeightedChoice.Pick` rather than a hand-rolled ceiling comparison, per
/// the spec's own literal citation of it (`WeightedChoice.cs:25`) — `rollSeed` is the ROOT stream's own
/// first `NextULong()`, fed into `Pick`'s own internal re-derivation, never the raw run seed.
/// </summary>
public static class EventDraw
{
    /// <summary>"`match` when `climateAffinity == room.climate`, `none` when climate-blind, `off`
    /// otherwise" (spec §2, verbatim) — `ClimateAffinity: null` is climate-blind, never a string
    /// sentinel. The three milli inputs are `events.climateAffinity.{match,none,off}Milli`
    /// (`DungeonTuning`), taken as plain values rather than the whole tuning object.</summary>
    public static long WeightMilliFor(EventRow row, string? roomClimate, long matchMilli, long noneMilli, long offMilli)
    {
        if (row is null) throw new ArgumentNullException(nameof(row));
        if (row.ClimateAffinity is null) return noneMilli;
        return string.Equals(row.ClimateAffinity, roomClimate, StringComparison.Ordinal) ? matchMilli : offMilli;
    }

    /// <summary>
    /// Which event wins, over the room's already-filtered pool. Weight ‰ is narrowed to
    /// `WeightedOption&lt;T&gt;`'s own `int` (`checked` — every real shipped `*Milli` value here is
    /// small, so this narrowing only ever fires on a genuinely malformed tuning value, never in play).
    /// </summary>
    public static EventRow PickEvent(
        IReadOnlyList<EventRow> pool, int row, int col, string? roomClimate,
        long climateAffinityMatchMilli, long climateAffinityNoneMilli, long climateAffinityOffMilli,
        ulong seed)
    {
        if (pool is null) throw new ArgumentNullException(nameof(pool));

        var options = pool
            .Select(e => new WeightedOption<EventRow>(e, checked((int)WeightMilliFor(
                e, roomClimate, climateAffinityMatchMilli, climateAffinityNoneMilli, climateAffinityOffMilli))))
            .ToList();

        var streamName = DelveStreams.Event(row, col) + ":pick";
        var stream = SeededRng.DeriveStream(seed, streamName);
        var rollSeed = unchecked((long)stream.NextULong());

        try
        {
            return WeightedChoice.Pick(options, rollSeed, streamName);
        }
        catch (NoDrawableWeightedOptionException ex)
        {
            throw new EventDeckRefusal($"room ({row},{col}): {ex.Message}");
        }
    }
}
