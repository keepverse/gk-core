using FusionRpg.Core.Power;

namespace FusionRpg.Core.Actions.Unlock;

/// <summary>How much freeing one held slot costs, in soul.</summary>
public readonly record struct DiscardPrice(long SoulAmount);

/// <summary>
/// T20 (spec-unlock-ladder.md §3, REVISED 2026-08-28 by direct owner override): discard's price
/// scales with the actor's power (`Θ`) rather than being flat. Mirrors <c>RespecPolicy</c>'s own
/// shape exactly — "this type only ever answers 'what does it cost,' never 'are you allowed'": no
/// run-phase check, no soul-balance check, no state mutation. Those live in
/// <see cref="UnlockDiscardService"/>, which is the thing that IS allowed to refuse.
/// </summary>
public static class DiscardPolicy
{
    /// <summary><c>cost(Θ) = coeffMilli × P(Θ) / 1000</c> — reads the shared power ladder (PS-3),
    /// never a private curve. Widened before multiplying, divided by 1000 exactly once
    /// (docs/architecture/numeric-types.md "Numeric overflow"); <c>PowerLadder.Value</c> itself already throws rather than
    /// wraps on overflow, so this inherits that guarantee for free.</summary>
    public static DiscardPrice PriceOf(long theta, UnlockTuning tuning)
    {
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));
        if (theta < 0 || theta > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(theta), theta, "Θ must fit the power ladder's int index");

        var ladder = new PowerLadder(PowerTuningHub.Tuning);
        var pOfTheta = ladder.Value((int)theta);
        var cost = checked((long)tuning.DiscardTaxCoeffMilli * pOfTheta / 1000);
        return new DiscardPrice(cost);
    }
}
