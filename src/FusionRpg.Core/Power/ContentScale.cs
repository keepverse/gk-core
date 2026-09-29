namespace FusionRpg.Core.Power;

/// <summary>
/// content-scale (T3.4, spec-content-scale.md §2.1): the ratio that makes the same authored item
/// worth more when it drops deeper. Applied exactly once, inside <c>Instantiator</c> — nowhere else
/// (§2.2; <c>power-guard</c>, wave 4, scans for a second multiplication).
///
/// <para><c>contentScale(Θc) = PowerLadder.Value(Θc) / pinValue</c> — <c>pinValue</c> read from
/// <see cref="PowerTuning"/>, never a literal (audit F5: an earlier draft hardcoded <c>680</c> and
/// even listed changing it as ask-first, violating PS-7 in the program that declares it).</para>
/// </summary>
public static class ContentScale
{
    /// <summary>contentScale in per-mille — 1000 means ×1.000 (the pin, Θc=20).</summary>
    public static long Milli(int thetaContent, PowerTuning tuning)
    {
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));
        var valueWhole = new PowerLadder(tuning).Value(thetaContent);
        return checked(valueWhole * 1000L / tuning.Curve.PinValue);
    }

    /// <summary>Applies a per-mille content scale to one rolled magnitude — round half away from
    /// zero, once, matching every other milli→whole conversion in this module.
    ///
    /// <para><c>long</c> in and out since 2026-09-05 (effort-power reconciliation P3). This is the one
    /// funnel every content-scaled magnitude passes through, and <c>P(Θ)</c> is quadratic, so an
    /// <c>int</c> result stops being able to hold what the curve produces long before the ladder
    /// itself runs out — docs/architecture/numeric-types.md's rule is <c>long</c> for any magnitude <c>contentScale</c> can
    /// touch. The arithmetic was already correct (widen before multiplying, divide by 1000 once,
    /// overflow throws); only the width was wrong.</para></summary>
    public static long Apply(long rolledValue, long contentScaleMilli)
    {
        long numerator = checked(rolledValue * contentScaleMilli);
        long q = numerator / 1000;
        long r = numerator % 1000;
        if (r == 0) return q;
        long twiceR = checked(Math.Abs(r) * 2);
        if (twiceR < 1000) return q;
        return checked(numerator < 0 ? q - 1 : q + 1);
    }
}
