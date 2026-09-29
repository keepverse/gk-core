namespace FusionRpg.Core.Power;

/// <summary>
/// `species-progression` module 2, `ladder-scale-parity` (map C6) — the ONE <c>k · P(Θ)</c> product
/// for a per-million coefficient. Before this type, two sites computed it with DIFFERENT arithmetic:
/// `AptitudeReadFunctions.Magnitude` (decimal, rounds away from zero, throws past `long`) and
/// `AtomCompiler`'s `kMicro` branch (`checked long`, truncates, and the multiply overflows before the
/// divide can bring it back into range). `species-layer-projector` (module 3) projects a 2b allocation
/// into atoms whose coefficient is exactly `kMilli × sharePowMilli` — with one shared scale function, a
/// projected atom resolved at Θ equals the live resolve at Θ **by construction**, which is what makes
/// the ideal's *"the math does not change; where it runs does"* exactly true rather than approximately
/// true.
///
/// <para><b>`decimal` is the widening type</b>, not `checked long`: `kMicro × pTheta` overflows `long`
/// long before the TRUE quotient does (`pTheta` legitimately reaches into the quintillions — docs/architecture/numeric-types.md's
/// long-magnitude ceiling), so a `long*long` chain would spuriously reject an input whose true answer
/// was always representable. `decimal` (96-bit exact integer precision, ~7.9e28 range) has enough
/// headroom for the product and throws its own <see cref="OverflowException"/> if it does not — never
/// silently wraps. Rounding happens once, last, away from zero — the rule
/// <see cref="Stats.Aptitudes.AptitudeReadFunctions.Magnitude"/> already shipped for the live aptitude
/// read, now shared rather than duplicated.</para>
/// </summary>
public static class LadderScale
{
    /// <summary>`kMicro · pTheta / 1,000,000`, rounded once away from zero. Throws (never wraps or
    /// clamps) on a negative input, or when the true rounded answer does not fit a `long`.</summary>
    public static long Micro(long kMicro, long pTheta)
    {
        if (kMicro < 0) throw new ArgumentOutOfRangeException(nameof(kMicro), kMicro, "kMicro must not be negative");
        if (pTheta < 0) throw new ArgumentOutOfRangeException(nameof(pTheta), pTheta, "pTheta must not be negative");
        var rounded = Math.Round((decimal)kMicro * pTheta / 1_000_000m, MidpointRounding.AwayFromZero);
        if (rounded > long.MaxValue)
            throw new OverflowException($"ladder magnitude overflow: kMicro={kMicro} pTheta={pTheta}");
        return (long)rounded;
    }
}
