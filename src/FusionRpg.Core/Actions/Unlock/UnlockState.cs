namespace FusionRpg.Core.Actions.Unlock;

public enum UnlockRefusalReason
{
    /// <summary>Holding already equals <see cref="UnlockTuning.HeldCap"/> — a discard must free a slot first.</summary>
    AtCapacity,
    /// <summary>The chance roll missed. Costs nothing: <see cref="UnlockState.EarnCount"/> is untouched.</summary>
    RollMissed,
    /// <summary>ADG-F5: every candidate the roll could have offered was one the grant write path refuses
    /// (a basic, or `grantable = 0`), so the roll offered nothing. Costs nothing — the level-up stands and
    /// <see cref="UnlockState.EarnCount"/> is untouched, exactly like <see cref="RollMissed"/>. The refused
    /// ids and the validator's own reason for each ride on <c>UnlockGrantOutcome.Skipped</c>.</summary>
    NoGrantableCandidate,
}

public readonly record struct UnlockOutcome(bool Accepted, UnlockRefusalReason? Reason)
{
    public static UnlockOutcome Refuse(UnlockRefusalReason reason) => new(false, reason);
    public static readonly UnlockOutcome Success = new(true, null);
}

/// <summary>
/// T20: why a discard did not happen. <see cref="NotHeld"/> is the only reason
/// <see cref="UnlockState.TryDiscard"/> itself can raise — it has no soul ledger and no run-phase
/// reference. <see cref="MidRun"/> and <see cref="InsufficientSoul"/> belong to
/// <see cref="UnlockDiscardService"/>, the layer that actually knows those facts.
/// </summary>
public enum DiscardRefusalReason
{
    /// <summary>The unlock id is not in the held set — nothing to free.</summary>
    NotHeld,
    /// <summary>A15 freezes the action set at run start; discard is refused during a run (spec §3).</summary>
    MidRun,
    /// <summary>The discard tax (soul) could not be paid — nothing was spent, nothing was discarded.</summary>
    InsufficientSoul,
}

public readonly record struct DiscardOutcome(bool Discarded, DiscardRefusalReason? Reason)
{
    public static DiscardOutcome Refuse(DiscardRefusalReason reason) => new(false, reason);
    public static readonly DiscardOutcome Success = new(true, null);
}

/// <summary>
/// One held unlock. <see cref="EarnCountAtAcceptance"/> is the ONLY thing recorded about strength —
/// never a resolved rung (spec's testing strategy: "no column stores a resolved rung value"). The
/// rung this unlock holds today is always <c>UnlockLadder.EffectiveRung(EarnCountAtAcceptance, tuning)</c>,
/// recomputed on every read, so a future change to <c>cap</c> reclassifies every held unlock
/// consistently instead of leaving old ones frozen at a stale stored value.
/// </summary>
public readonly record struct HeldUnlock(string UnlockId, long EarnCountAtAcceptance);

/// <summary>
/// T19 (action-todo.md, spec-unlock-ladder.md §1): <c>earnCount</c> plus the held set — no occupancy
/// math anywhere in this class, per the module's own file-level naming ("NO occupancy math").
/// Discard (T20) adds a method here later; this class does not refuse based on run phase or price
/// anything, because accepting an unlock is free and always available while a slot is open (spec
/// testing strategy: "Accept at holding &lt; cap — succeeds, no tax").
/// </summary>
public sealed class UnlockState
{
    readonly List<HeldUnlock> _held;

    public long EarnCount { get; private set; }
    public IReadOnlyList<HeldUnlock> Held => _held;

    UnlockState(long earnCount, List<HeldUnlock> held)
    {
        EarnCount = earnCount;
        _held = held;
    }

    public static UnlockState Empty() => new(0, new List<HeldUnlock>());

    /// <summary>Rebuilds from persistence. Held-set ORDER carries no meaning (spec: "identical...
    /// across a shuffled held-set order") — callers may pass rows in any order.</summary>
    public static UnlockState FromPersisted(long earnCount, IEnumerable<HeldUnlock> held) =>
        new(earnCount, new List<HeldUnlock>(held));

    /// <summary>
    /// Rolls for one unlock. Capacity is checked BEFORE the roll — a full pool refuses without
    /// consuming a draw, since the outcome ("not an earn, no state change") is identical either way
    /// and this avoids desyncing a shared roll stream's draw count for a check that changes nothing
    /// observable. A miss also changes nothing: "a failed roll costs nothing" (spec §1).
    /// </summary>
    public UnlockOutcome TryAccept(string unlockId, UnlockTuning tuning, AtomRng? rng)
    {
        if (string.IsNullOrWhiteSpace(unlockId)) throw new ArgumentException("unlockId required", nameof(unlockId));
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));

        if (_held.Count >= tuning.HeldCap)
            return UnlockOutcome.Refuse(UnlockRefusalReason.AtCapacity);

        var chance = UnlockLadder.ChanceMilli(EarnCount, tuning);
        var roll = rng?.NextPerMille() ?? 0;
        if (roll >= chance)
            return UnlockOutcome.Refuse(UnlockRefusalReason.RollMissed);

        EarnCount += 1; // only NOW — a successful acquisition into a free slot (spec §1)
        _held.Add(new HeldUnlock(unlockId, EarnCount));
        return UnlockOutcome.Success;
    }

    /// <summary>
    /// Frees one held slot. <see cref="EarnCount"/> is NEVER touched here — that is the entire
    /// anti-farm property (spec §1: "discard moves neither" chance nor rung): a discard changes what
    /// an actor HOLDS, never what an actor has EARNED, so a re-earn right after a discard rolls at
    /// the exact same chance the discard interrupted, and the next successful earn still advances
    /// past whatever <see cref="EarnCount"/> already reached.
    /// </summary>
    public DiscardOutcome TryDiscard(string unlockId)
    {
        var idx = _held.FindIndex(h => h.UnlockId == unlockId);
        if (idx < 0) return DiscardOutcome.Refuse(DiscardRefusalReason.NotHeld);

        _held.RemoveAt(idx);
        return DiscardOutcome.Success;
    }
}
