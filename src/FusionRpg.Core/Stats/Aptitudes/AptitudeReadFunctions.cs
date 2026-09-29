namespace FusionRpg.Core.Stats.Aptitudes;

/// <summary>
/// The one shared read formula (ssot-power-scale.md §4.6, Rule PS-3; class-system-map.md §SS2d) —
/// every consumer that turns an aptitude point share into a channel value calls in here: the real
/// resolver (<c>AptitudeSubsystem</c>, P2.4/P2.5) and the closed-form predictor (Phase 4) share this
/// implementation rather than each carrying its own copy of the arithmetic. `guard-class-system.ps1`
/// G5 fails the build if a second <c>class AptitudeReadFunctions</c> appears anywhere under `src/`.
///
/// <para><b>Contest</b> is Θ-free: <c>k · share^γ · spanPoints</c>. Its result is a bounded contest
/// point value (0..spanPoints), not a game magnitude — <c>double</c> throughout (floating point is
/// allowed for any quantity; no exemption is needed).</para>
///
/// <para><b>Magnitude</b> reads the ladder: <c>k · share^γ · P(Θ)</c>. Its result IS a magnitude, so
/// it never returns anything but a `long`, and only ever throws (never wraps) when the true answer
/// does not fit one. `share^γ` is the floating-point step — a real-exponent power of
/// a bounded [0,1] ratio has no pure-integer form — so it is collapsed to a per-mille `long`
/// immediately (bounded [0,1000], since `x^γ ∈ [0,1]` for `x ∈ [0,1]`, `γ > 0`) and never touched as a
/// `double` again. The widening multiply uses `decimal`, not `checked long`: two independent per-mille
/// factors (`k`, `share^γ`) compound against a `pTheta` that legitimately reaches into the quintillions
/// (docs/architecture/numeric-types.md's long-magnitude ceiling), and a `long*long*long` chain overflows on the *intermediate*
/// product even when the true final answer fits comfortably — the same "multiply first, divide last"
/// trap <see cref="Core.Power.PowerLadder"/>'s `TriangularMilli` already documents for a single
/// per-mille factor. `decimal` (96-bit exact integer precision, ~7.9e28 range) has enough headroom for
/// the full three-way product and throws its own <see cref="OverflowException"/> if it does not — never
/// silently wraps — so this still "throws, never wraps" end to end while never spuriously rejecting an
/// input whose true answer was always representable.</para>
/// </summary>
public static class AptitudeReadFunctions
{
    /// <summary>Contest read: <c>k · share^γ · spanPoints</c>. Θ-free (PS-3) — bounded ratio, exempt
    /// from the long-magnitude rule (PS-8).</summary>
    public static double Contest(long kMilli, double share, long shareExponentMilli, long spanPointsMilli)
    {
        ValidateShare(share);
        if (kMilli < 0) throw new ArgumentOutOfRangeException(nameof(kMilli), kMilli, "kMilli must not be negative");
        if (shareExponentMilli <= 0) throw new ArgumentOutOfRangeException(nameof(shareExponentMilli), shareExponentMilli, "shareExponentMilli must be positive");
        if (spanPointsMilli < 0) throw new ArgumentOutOfRangeException(nameof(spanPointsMilli), spanPointsMilli, "spanPointsMilli must not be negative");

        var gamma = shareExponentMilli / 1000.0;
        var k = kMilli / 1000.0;
        var span = spanPointsMilli / 1000.0;
        return k * Math.Pow(share, gamma) * span;
    }

    /// <summary>Magnitude read: <c>k · share^γ · P(Θ)</c>. Reads the ladder (PS-3) — always `long`,
    /// throws (never wraps) when the true answer would not fit one.
    ///
    /// <para>`species-progression` module 2 (`ladder-scale-parity`, map C6): the widened
    /// `kMicro × pTheta` product now runs through <see cref="Core.Power.LadderScale.Micro"/>, the ONE
    /// shared function `AtomCompiler`'s projected-atom path also calls, so a projected 2b atom
    /// resolved at Θ equals this live resolve at Θ by construction. `kMicro = checked(kMilli ×
    /// sharePowMilli)` stays a plain `checked long` multiply here — both factors are bounded per-mille
    /// quantities, so this product cannot legitimately approach `long`'s ceiling; only the SUBSEQUENT
    /// multiply by `pTheta` (which genuinely reaches into the quintillions) needs `LadderScale.Micro`'s
    /// `decimal` widening. Behaviour-preserving: this is the exact same two-step arithmetic the
    /// previous single-expression `decimal` computation performed, just factored through the one
    /// shared function instead of duplicated inline.</para>
    /// </summary>
    public static long Magnitude(long kMilli, double share, long shareExponentMilli, long pTheta)
    {
        if (kMilli < 0) throw new ArgumentOutOfRangeException(nameof(kMilli), kMilli, "kMilli must not be negative");
        if (pTheta < 0) throw new ArgumentOutOfRangeException(nameof(pTheta), pTheta, "pTheta must not be negative");

        var sharePowMilli = SharePowMilli(share, shareExponentMilli);
        var kMicro = checked(kMilli * sharePowMilli);
        return Core.Power.LadderScale.Micro(kMicro, pTheta);
    }

    /// <summary>
    /// species-progression SP3.4 (spec-species-layer-projector.md rule 1): the `share^gamma` rounding
    /// extracted out of <see cref="Magnitude"/> so <c>SpeciesLayerProjector.ProjectEmpire</c> calls the
    /// SAME function instead of re-typing the exponent + round-to-per-mille arithmetic — the exact
    /// parity claim (species-only allocation vs the live resolver) depends on there being only one
    /// implementation. Bounded [0,1000] per-mille result (`x^gamma` in [0,1] for `x` in [0,1], `gamma`
    /// &gt; 0) — the one float step, rounded once, never touched as a `double` again.
    /// </summary>
    public static long SharePowMilli(double share, long shareExponentMilli)
    {
        ValidateShare(share);
        if (shareExponentMilli <= 0) throw new ArgumentOutOfRangeException(nameof(shareExponentMilli), shareExponentMilli, "shareExponentMilli must be positive");

        var gamma = shareExponentMilli / 1000.0;
        var sharePow = Math.Pow(share, gamma); // in [0,1] for share in [0,1], gamma > 0 — the one float step
        return (long)Math.Round(sharePow * 1000.0, MidpointRounding.AwayFromZero);
    }

    static void ValidateShare(double share)
    {
        if (double.IsNaN(share) || share < 0.0 || share > 1.0)
            throw new ArgumentOutOfRangeException(nameof(share), share, "share must be in [0,1]");
    }
}
