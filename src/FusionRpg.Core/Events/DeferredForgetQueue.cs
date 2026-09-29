namespace FusionRpg.Core.Events;

/// <summary>
/// lawn-hit-entry T9c / lawn-combat-wire L-N16: grant-withdraw callbacks that could not run yet
/// because the death that queued them fired inside an active drain pass
/// (<see cref="EventDrain.FlushForPtr"/> returned <c>-1</c>).
///
/// <para>Contract: a deferred forget runs only from <see cref="RunDue"/>, which is called from the
/// never-nested per-frame tick after the outer drain has returned; each ptr is flushed once more
/// immediately before its callback, so no record for that ptr can drain after its grants withdraw.
/// A callback that defers another forget while running lands in the next <see cref="RunDue"/>, never
/// the current pass. <see cref="Clear"/> at match end drops everything — nothing queued in one match
/// may fire into the next.</para>
/// </summary>
public sealed class DeferredForgetQueue
{
    readonly List<(IntPtr Ptr, Action OnSafeToForget)> _pending = new();

    public int Count => _pending.Count;

    public void Defer(IntPtr ptr, Action onSafeToForget)
    {
        ArgumentNullException.ThrowIfNull(onSafeToForget);
        _pending.Add((ptr, onSafeToForget));
    }

    /// <summary>Flushes then forgets every queued ptr, in queue order.</summary>
    public void RunDue(Action<IntPtr> flushForPtr)
    {
        ArgumentNullException.ThrowIfNull(flushForPtr);
        if (_pending.Count == 0) return;
        var due = _pending.ToArray();
        _pending.Clear();
        foreach (var (ptr, onSafeToForget) in due)
        {
            flushForPtr(ptr);
            onSafeToForget();
        }
    }

    public void Clear() => _pending.Clear();
}
