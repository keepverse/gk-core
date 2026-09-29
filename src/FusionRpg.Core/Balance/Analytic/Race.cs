namespace FusionRpg.Core.Balance.Analytic;

/// <summary>
/// class-system-todo.md P4.2 — the normal race between two <see cref="FirstPassage"/> times
/// (class-analytic-balance-2026-08-25.md §2):
/// <c>P(A wins) = Φ((E[T_A] − E[T_B]) / sqrt(Var[T_A] + Var[T_B] − 2·ρ·SD_A·SD_B))</c>.
///
/// <para><b>Naming convention (from <see cref="FirstPassage"/>):</b> <c>T_A</c> is the time until actor
/// A DIES (A's own HP pool, depleted by the opponent's damage) — a larger T_A means A survives longer.
/// "A wins" means A outlives B, i.e. <c>T_A &gt; T_B</c>. With that reading, <c>P(T_A − T_B &gt; 0)</c>
/// for a normally-distributed difference is exactly <c>Φ</c> of the standardized mean, which is the
/// formula as published — no sign flip needed once T is read as "time to die," not "time to kill."</para>
///
/// <para><b>The ρ term is not optional.</b> When one actor's own swing damages both combatants at once
/// (reflection), the two kill-times move together and the variance of their difference shrinks —
/// dropping ρ costs 5 points of win rate on a reflect matchup (class-analytic-balance-2026-08-25.md §2).
/// </para>
/// </summary>
public static class Race
{
    /// <param name="rho">Correlation between the two kill-times, clamped to <c>[-1, 1]</c> by contract
    /// (throws outside it). 0 when neither side's swing damages both actors at once (no reflection);
    /// nonzero when it does.</param>
    /// <returns>P(A outlives B), in <c>[0, 1]</c>, or <c>NaN</c> when neither side ever dies (both
    /// <see cref="FirstPassage.Result.Mean"/> are +Infinity) — the termination invariant's own failure
    /// state (class-system-todo.md P5.1); <see cref="Race"/> reports it honestly rather than guessing
    /// 0.5, and it is the termination guard (P5.1)'s job (not this one) to decide what to do about it.</returns>
    public static double PWinsA(FirstPassage.Result a, FirstPassage.Result b, double rho = 0.0)
    {
        if (double.IsNaN(rho) || rho < -1.0 || rho > 1.0)
            throw new ArgumentOutOfRangeException(nameof(rho), rho, "correlation must be in [-1, 1]");
        if (double.IsNaN(a.Mean) || double.IsNaN(a.Variance) || a.Variance < 0)
            throw new ArgumentOutOfRangeException(nameof(a), a, "invalid first-passage result");
        if (double.IsNaN(b.Mean) || double.IsNaN(b.Variance) || b.Variance < 0)
            throw new ArgumentOutOfRangeException(nameof(b), b, "invalid first-passage result");

        var aInf = double.IsPositiveInfinity(a.Mean);
        var bInf = double.IsPositiveInfinity(b.Mean);
        if (aInf && bInf) return double.NaN;   // neither side ever dies -- undefined, not a coin flip.
        if (aInf) return 1.0;                  // A never dies, B does (finite) -> A outlives B -> A wins.
        if (bInf) return 0.0;                  // B never dies, A does (finite) -> B outlives A -> A loses.

        var sdA = Math.Sqrt(a.Variance);
        var sdB = Math.Sqrt(b.Variance);
        var combinedVariance = a.Variance + b.Variance - 2.0 * rho * sdA * sdB;

        if (combinedVariance <= 0.0)
            // Degenerate: both times are effectively deterministic (or numerically indistinguishable).
            // Whichever mean is smaller dies first with certainty; an exact tie is a coin flip.
            return a.Mean < b.Mean ? 0.0 : a.Mean > b.Mean ? 1.0 : 0.5;

        var z = (a.Mean - b.Mean) / Math.Sqrt(combinedVariance);
        return Phi(z);
    }

    /// <summary>The standard normal CDF, via the Abramowitz &amp; Stegun 7.1.26 rational approximation
    /// of erf (max absolute error ~1.5e-7) — pure, deterministic, no external dependency. Public because
    /// spec-deterministic-core.md §4 names it as its own testable unit, not just an internal of
    /// <see cref="PWinsA"/>.</summary>
    public static double Phi(double x)
    {
        if (double.IsNaN(x)) throw new ArgumentOutOfRangeException(nameof(x), x, "must not be NaN");
        if (double.IsPositiveInfinity(x)) return 1.0;
        if (double.IsNegativeInfinity(x)) return 0.0;
        return 0.5 * (1.0 + Erf(x / Sqrt2));
    }

    // Not a balance dial: sqrt(2), for the erf/Phi change of variable. Structural.
    const double Sqrt2 = 1.4142135623730951;

    static double Erf(double x)
    {
        // Abramowitz & Stegun, Handbook of Mathematical Functions, formula 7.1.26.
        var sign = x < 0.0 ? -1.0 : 1.0;
        x = Math.Abs(x);

        // The six constants below are the fixed coefficients of that formula (max abs error
        // ~1.5e-7) -- not balance dials. Changing any one breaks the approximation rather than
        // changing how the game feels, so each stays a structural const, commented per-line so
        // the reason travels with the line even if these get reordered.
        // A&S 7.1.26 coefficient, not a balance dial.
        const double a1 = 0.254829592;
        // A&S 7.1.26 coefficient, not a balance dial.
        const double a2 = -0.284496736;
        // A&S 7.1.26 coefficient, not a balance dial.
        const double a3 = 1.421413741;
        // A&S 7.1.26 coefficient, not a balance dial.
        const double a4 = -1.453152027;
        // A&S 7.1.26 coefficient, not a balance dial.
        const double a5 = 1.061405429;
        // A&S 7.1.26 coefficient, not a balance dial.
        const double p = 0.3275911;

        var t = 1.0 / (1.0 + p * x);
        var poly = ((((a5 * t + a4) * t + a3) * t + a2) * t + a1) * t;
        var y = 1.0 - poly * Math.Exp(-x * x);
        return sign * y;
    }
}
