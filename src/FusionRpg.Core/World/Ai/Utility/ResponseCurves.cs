namespace FusionRpg.Core.World.Ai.Utility;

/// <summary>The shape of "how much do I care as this input rises".</summary>
public enum ResponseCurve
{
    /// <summary>More is proportionally better.</summary>
    Linear,

    /// <summary>Less is better. The same curve read backwards.</summary>
    Inverse,

    /// <summary>Indifferent until it is not — slow at the bottom, steep at the top.</summary>
    Quadratic,

    /// <summary>Urgent immediately, then flattening. The shape of "any at all is what matters".</summary>
    InverseQuadratic,

    /// <summary>Flat, steep, flat. A soft threshold with no cliff in it.</summary>
    Smoothstep,

    /// <summary>All or nothing at the given point.</summary>
    Threshold
}

/// <summary>
/// Integer response curves, `0..1000 → 0..1000` (spec-ai-commander.md §The consideration arithmetic).
///
/// Built now although nothing scores yet: pure arithmetic with no world knowledge is provable in
/// isolation, so wave 3 inherits a tested scorer and only has to decide *which* considerations to
/// write — which is the part that needs an economy to argue with.
///
/// No logistic curve. It cannot be done in integers without an approximation nobody would trust, and
/// a curve that is 2‰ off on one machine is a replay that disagrees with itself.
/// </summary>
public static class ResponseCurves
{
    public const int Max = 1000;

    /// <param name="input">Clamped to 0..1000; a caller normalising badly gets a sane answer.</param>
    /// <param name="threshold">Only read by <see cref="ResponseCurve.Threshold"/>.</param>
    public static int Evaluate(ResponseCurve curve, int input, int threshold = Max / 2)
    {
        var x = Math.Clamp(input, 0, Max);

        return curve switch
        {
            ResponseCurve.Linear => x,
            ResponseCurve.Inverse => Max - x,
            ResponseCurve.Quadratic => x * x / Max,
            ResponseCurve.InverseQuadratic => Max - (Max - x) * (Max - x) / Max,
            ResponseCurve.Smoothstep => Smooth(x),
            ResponseCurve.Threshold => x >= threshold ? Max : 0,
            _ => throw new ArgumentOutOfRangeException(nameof(curve), curve, "No such response curve.")
        };
    }

    /// <summary>
    /// `3x² − 2x³` in per-mille, ordered so the intermediate never leaves `long` range and the
    /// division happens once. Writing it as three separate per-mille steps would round three times
    /// and drift away from the curve it is named after.
    /// </summary>
    static int Smooth(int x)
    {
        long t = x;
        return (int)((3 * t * t * Max - 2 * t * t * t) / ((long)Max * Max));
    }
}
