namespace FusionRpg.Core.World.Movement;

/// <summary>
/// Where two legions marching toward each other along one lane actually meet.
///
/// Solved arithmetically rather than sampled: with A at <c>pA</c> closing at <c>sA</c> per turn and
/// B approaching from the far end at <c>sB</c>, they meet when the gap closes —
/// <c>t = (1000 − pA − pB) × 1000 / (sA + sB)</c> in per-mille of the turn. The meeting point is
/// therefore exact and identical whichever legion is processed first, which is the whole reason the
/// turn resolves by events instead of by fixed sub-steps.
///
/// Positions are per-mille along the lane, each measured from that legion's own end.
/// </summary>
public static class LaneCrossing
{
    public const int LaneLengthMilli = 1000;
    public const int TurnLengthMilli = 1000;

    /// <summary>
    /// True when the two close within this turn. <paramref name="timeMilli"/> is when, in turn
    /// fractions; <paramref name="positionMilli"/> is where, measured from A's end.
    /// </summary>
    public static bool TryFind(
        int progressA, int speedA, int progressB, int speedB, out int timeMilli, out int positionMilli)
    {
        timeMilli = 0;
        positionMilli = 0;

        var closingSpeed = (long)speedA + speedB;
        if (closingSpeed <= 0) return false;                 // nobody is moving

        var gap = (long)LaneLengthMilli - progressA - progressB;
        if (gap <= 0) return false;                          // already past each other

        var time = gap * TurnLengthMilli / closingSpeed;
        if (time > TurnLengthMilli) return false;            // the gap outlives the turn

        // Integer division truncates, so computing the point from each side independently can
        // disagree by one — and then two legions would meet at two different places depending on
        // which one the caller passed first. Compute once in a canonical frame (the side that
        // sorts lower) and mirror it for the other, so the two answers are exact complements.
        var flipped = (progressA, speedA).CompareTo((progressB, speedB)) > 0;
        var (nearProgress, nearSpeed) = flipped ? (progressB, speedB) : (progressA, speedA);

        var canonical = nearProgress + (long)nearSpeed * time / TurnLengthMilli;
        var position = flipped ? LaneLengthMilli - canonical : canonical;

        timeMilli = (int)Math.Clamp(time, 1, TurnLengthMilli);
        positionMilli = (int)Math.Clamp(position, 0, LaneLengthMilli);
        return true;
    }
}
