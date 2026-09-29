namespace FusionRpg.Core.Effects.Atoms;

/// <summary>What a curve reads to pick its x. Adding one is a reviewed change (E2 boundaries).</summary>
public enum CurveInput
{
    Level = 0,
    Rarity,
    Tier,
}

/// <summary>One authored point: at <paramref name="X"/>, multiply by <paramref name="MultiplierMilli"/>‰.</summary>
public readonly record struct CurvePoint(int X, int MultiplierMilli);

/// <summary>
/// Scaling is a curve reference, never a formula. A formula string is a language, and a language is
/// a parser, a sandbox, and a security surface — so this table holds ordered points and interpolates
/// linearly between them, in integer per-mille.
///
/// <para>The same table serves E9's power reference scale, so a value and its price read one source
/// instead of drifting apart.</para>
/// </summary>
public sealed class CurveTable
{
    readonly CurvePoint[] _points;

    CurveTable(string curveId, CurveInput input, CurvePoint[] points)
    {
        CurveId = curveId;
        Input = input;
        _points = points;
    }

    public string CurveId { get; }
    public CurveInput Input { get; }
    public IReadOnlyList<CurvePoint> Points => _points;

    /// <summary>
    /// Validate and build. "Ordered" is checked, not assumed: a zero-point curve would be a hot-path
    /// divide-by-zero and duplicate or unsorted x would make interpolation depend on insertion order.
    /// </summary>
    public static AtomRejection TryCreate(
        string curveId, CurveInput input, IReadOnlyList<CurvePoint> points, out CurveTable? curve)
    {
        curve = null;

        if (string.IsNullOrWhiteSpace(curveId))
            return AtomRejection.Fail(AtomRejectionReason.BadCurve, "curveId is empty");

        if (points.Count == 0)
            return AtomRejection.Fail(AtomRejectionReason.BadCurve, $"{curveId}: no points");

        for (var i = 1; i < points.Count; i++)
        {
            if (points[i].X == points[i - 1].X)
                return AtomRejection.Fail(AtomRejectionReason.BadCurve,
                    $"{curveId}: duplicate x {points[i].X}");
            if (points[i].X < points[i - 1].X)
                return AtomRejection.Fail(AtomRejectionReason.BadCurve,
                    $"{curveId}: x out of order at index {i} ({points[i - 1].X} then {points[i].X})");
        }

        curve = new CurveTable(curveId, input, points.ToArray());
        return AtomRejection.Ok;
    }

    /// <summary>
    /// The multiplier at <paramref name="x"/>, in per-mille. Clamps at both ends — a curve is never
    /// extrapolated past what an author wrote.
    /// </summary>
    public int MultiplierAt(int x)
    {
        var last = _points.Length - 1;
        if (x <= _points[0].X) return _points[0].MultiplierMilli;
        if (x >= _points[last].X) return _points[last].MultiplierMilli;

        // Linear scan: curves are a handful of points, so this beats a binary search's branching
        // and keeps the resolve path allocation-free and dictionary-free.
        for (var i = 1; i <= last; i++)
        {
            var b = _points[i];
            if (x > b.X) continue;

            var a = _points[i - 1];
            if (x == b.X) return b.MultiplierMilli;

            var span = (long)b.X - a.X;
            var into = (long)x - a.X;
            var rise = (long)b.MultiplierMilli - a.MultiplierMilli;

            // Rounded half away from zero, exactly once, at the end of the interpolation.
            return a.MultiplierMilli + (int)DivRoundHalfAway(rise * into, span);
        }

        return _points[last].MultiplierMilli;
    }

    /// <summary>Scale a magnitude by a per-mille multiplier, rounding half away from zero once.</summary>
    public static int ApplyMilli(int value, int multiplierMilli) =>
        (int)DivRoundHalfAway((long)value * multiplierMilli, 1000);

    /// <summary>
    /// Integer divide rounding half away from zero — symmetric about zero, so a negative magnitude
    /// scales the same distance as its positive twin. C#'s default truncation is not.
    /// </summary>
    internal static long DivRoundHalfAway(long numerator, long denominator)
    {
        if (denominator == 0) throw new DivideByZeroException();

        // Normalise the sign onto the numerator so the half-step is added in the right direction.
        if (denominator < 0) { numerator = -numerator; denominator = -denominator; }

        return numerator >= 0
            ? (numerator + denominator / 2) / denominator
            : -((-numerator + denominator / 2) / denominator);
    }
}
