namespace FusionRpg.Core.Balance.Analytic;

/// <summary>
/// class-system-todo.md P4.2 — the renewal first-passage time for depleting a pool of size <c>h</c> at
/// mean rate <c>μ</c> per round with variance <c>σ²</c> per round
/// (class-analytic-balance-2026-08-25.md §2): <c>E[T] = h/μ</c>, <c>Var[T] = h·σ²/μ³</c>.
///
/// <para><b>Naming convention carried into <see cref="Race"/>:</b> the pool being depleted is the
/// SUBJECT's own HP, and <paramref name="mean"/>/<paramref name="variance"/> are the OPPONENT's
/// per-swing damage distribution (from <see cref="StrikeMixture"/>). So "T for actor A" means "the time
/// until A dies", not "the time until A kills its opponent" — a larger T means the actor survives
/// longer, and <see cref="Race"/> compares two of these to ask who outlives whom.</para>
/// </summary>
public static class FirstPassage
{
    public readonly record struct Result(double Mean, double Variance);

    /// <param name="poolSize">The HP being depleted (<c>h</c> — non-negative).</param>
    /// <param name="mean">Mean damage per round against that pool (<c>μ</c> — the opponent's
    /// <see cref="StrikeMixture.Result.Mean"/>).</param>
    /// <param name="variance">Variance of damage per round (<c>σ²</c> — the opponent's
    /// <see cref="StrikeMixture.Result.Variance"/>, non-negative).</param>
    public static Result Compute(double poolSize, double mean, double variance)
    {
        if (double.IsNaN(poolSize) || poolSize < 0)
            throw new ArgumentOutOfRangeException(nameof(poolSize), poolSize, "must be non-negative");
        if (double.IsNaN(mean))
            throw new ArgumentOutOfRangeException(nameof(mean), mean, "must not be NaN");
        if (double.IsNaN(variance) || variance < 0)
            throw new ArgumentOutOfRangeException(nameof(variance), variance, "must be non-negative");

        if (poolSize == 0.0)
            return new Result(0.0, 0.0); // already depleted -- instant, by construction, no division needed.

        if (mean <= 0.0)
            // A non-positive mean per-round damage never depletes the pool -- this side of the race
            // never finishes. Represented as +Infinity, never thrown: the termination invariant
            // (class-system-todo.md P5.1, the HARD criterion) needs to OBSERVE this state through
            // Race's own handling of it, not have it hidden behind an exception at this layer.
            return new Result(double.PositiveInfinity, double.PositiveInfinity);

        var mean3 = mean * mean * mean;
        return new Result(poolSize / mean, poolSize * variance / mean3);
    }
}
