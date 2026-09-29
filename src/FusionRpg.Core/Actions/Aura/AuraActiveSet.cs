namespace FusionRpg.Core.Actions.Aura;

/// <summary>
/// aura-skill T13 (`spec-aura-action-shape.md` §5.1): one commander's ordered set of currently-active
/// aura ids. `1` active by default; `maxActiveAuras` is a tunable ceiling — activating past it evicts
/// the OLDEST active aura (FIFO), never the newest, and never with a refund
/// (`spec-aura-action-shape.md` §5.2's own owner call: *"the aura you have run longest"* is the first
/// casualty, preserving the player's latest intent over an established setup).
///
/// <para>Order is insertion order of ACTIVATION, not loadout order — "oldest active," not "first
/// equipped." Re-activating an aura that is already active is the caller's job to refuse
/// (<see cref="AuraRuntime"/>) — this type has no notion of "already active" as a distinct outcome,
/// only membership.</para>
/// </summary>
public sealed class AuraActiveSet
{
    readonly List<string> _order = new();

    public AuraActiveSet(int maxActive)
    {
        if (maxActive < 1)
            throw new ArgumentOutOfRangeException(nameof(maxActive), maxActive, "maxActive must be at least 1");
        MaxActive = maxActive;
    }

    public int MaxActive { get; }

    /// <summary>Oldest first — index 0 is the next eviction candidate.</summary>
    public IReadOnlyList<string> Active => _order;

    public bool IsActive(string auraId) => _order.Contains(auraId);

    /// <summary>Records <paramref name="auraId"/> as newly active. Returns the evicted aura's id if
    /// activating this one pushed the set past <see cref="MaxActive"/>, or null if nothing was
    /// evicted. The caller is responsible for checking <see cref="IsActive"/> first — this method does
    /// not itself refuse a duplicate activation, it would simply record a second entry.</summary>
    public string? Activate(string auraId)
    {
        _order.Add(auraId);
        if (_order.Count <= MaxActive) return null;

        var evicted = _order[0];
        _order.RemoveAt(0);
        return evicted;
    }

    /// <summary>Returns true if <paramref name="auraId"/> was active and is now removed; false if it
    /// was not active at all (a safe no-op, matching `EffectBag.WithdrawForOwner`'s own contract for
    /// an owner with nothing granted).</summary>
    public bool Deactivate(string auraId) => _order.Remove(auraId);
}
