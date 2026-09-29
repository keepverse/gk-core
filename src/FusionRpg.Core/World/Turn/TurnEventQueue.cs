namespace FusionRpg.Core.World.Turn;

public static class TurnEventKinds
{
    public const string Arrival = "arrival";
    public const string Contact = "contact";
    public const string Crossing = "crossing";

    /// <summary>A march cut short by a hostile force standing where it was headed.</summary>
    public const string Halt = "halt";
}

/// <summary>
/// One thing happening at an instant inside a turn. Time is an integer fraction of the turn
/// (0–1000 per-mille), so a crossing lands where it actually crosses instead of on the nearest
/// sample.
/// </summary>
public readonly record struct TurnEvent(int TimeMilli, string EntityId, string Kind, string Detail);

/// <summary>
/// The turn's event list: what makes movement resolution discrete-event rather than sampled.
///
/// Ordering is <c>(TimeMilli, EntityId)</c> — deliberately the entity id and not an insertion
/// sequence, so re-ordering a seeding loop can never silently change a turn's outcome. That is the
/// one difference from <c>Battle.Timeline.EventQueue</c>, which serves the combat clock and ties by
/// insertion because it also supports cancel and reschedule. Two small queues with explicit,
/// different contracts beat one shared queue that quietly means different things to each caller.
///
/// The queue is monotonic: nothing may be scheduled before the moment being processed. An out-of-
/// order insert would reorder a turn without any test noticing, so it throws instead.
/// </summary>
public sealed class TurnEventQueue
{
    // structural, not tunable — tunables-ssot.md T2: these are the definitional bounds of "a turn's
    // own timeline" (0..1000 per-mille), not a balance number a rebalance would move independently
    // of the per-mille convention every other fraction in this repo already uses.
    public const int TurnStartMilli = 0;
    public const int TurnEndMilli = 1000;

    readonly List<TurnEvent> _pending = new();
    int _now = TurnStartMilli;
    bool _sorted = true;

    public int Count => _pending.Count;

    /// <summary>The instant last dequeued — nothing may be scheduled before it.</summary>
    public int NowMilli => _now;

    public void Schedule(int timeMilli, string entityId, string kind, string detail)
    {
        if (timeMilli is < TurnStartMilli or > TurnEndMilli)
            throw new ArgumentOutOfRangeException(nameof(timeMilli), timeMilli,
                $"Turn time must be within {TurnStartMilli}..{TurnEndMilli} per-mille.");
        if (timeMilli < _now)
            throw new InvalidOperationException(
                $"Cannot schedule at {timeMilli} while processing {_now} — an event in the past would reorder the turn.");

        _pending.Add(new TurnEvent(timeMilli, entityId, kind, detail));
        _sorted = false;
    }

    public bool TryDequeue(out TurnEvent next)
    {
        if (_pending.Count == 0)
        {
            next = default;
            return false;
        }

        if (!_sorted)
        {
            // Stable total order: time, then entity id, then kind — never list position.
            _pending.Sort(static (a, b) =>
            {
                var byTime = a.TimeMilli.CompareTo(b.TimeMilli);
                if (byTime != 0) return byTime;
                var byEntity = string.CompareOrdinal(a.EntityId, b.EntityId);
                return byEntity != 0 ? byEntity : string.CompareOrdinal(a.Kind, b.Kind);
            });
            _sorted = true;
        }

        next = _pending[0];
        _pending.RemoveAt(0);
        _now = next.TimeMilli;
        return true;
    }
}
