namespace FusionRpg.Core.Actions;

/// <summary>Board coordinates. Independent of any one board representation — callers adapt their own
/// entity-snapshot fields into this.</summary>
public readonly record struct GridPos(int Row, int Col);

/// <summary>
/// One Chebyshev implementation, two callers (spec-targeting.md §6c): `A4`'s usability gate asks
/// "is any target in range"; `A2`'s targeting gate asks "which targets qualify." Both use this.
///
/// <para><b>Chebyshev, not Manhattan.</b> The shipped `Square` area shape of size <c>n</c> already
/// IS a Chebyshev ball of radius <c>(n-1)/2</c> — this is the metric the existing code implies, not
/// an arbitrary choice.</para>
///
/// <para><b>With no board, every range check passes</b> — not an error, not empty
/// (spec-targeting.md §4, spec-action-model.md §8). That is what keeps `A5` byte-identical: with no
/// coordinates, range excludes nobody and targeting behaves exactly as it does today.</para>
/// </summary>
public static class GridDistance
{
    public static int Chebyshev(GridPos a, GridPos b) =>
        Math.Max(Math.Abs(a.Row - b.Row), Math.Abs(a.Col - b.Col));

    /// <summary>
    /// Whether <paramref name="target"/> qualifies against a range window centred on
    /// <paramref name="caster"/>. Either side absent means no board exists yet — passes, always.
    /// </summary>
    public static bool InRange(GridPos? caster, GridPos? target, int minRange, int maxRange)
    {
        if (caster is null || target is null) return true;
        var d = Chebyshev(caster.Value, target.Value);
        return d >= minRange && d <= maxRange;
    }

    /// <summary>
    /// Every cell in a `Square` of the given size, centred on <paramref name="center"/> — exactly the
    /// cells within Chebyshev radius <c>(size-1)/2</c>. Used both to enumerate an `Area` action's
    /// targets and, in tests, to prove the metric matches the shipped area shape.
    /// </summary>
    public static IReadOnlyList<GridPos> Square(GridPos center, int size)
    {
        var radius = (size - 1) / 2;
        var cells = new List<GridPos>();
        for (var dr = -radius; dr <= radius; dr++)
            for (var dc = -radius; dc <= radius; dc++)
                cells.Add(new GridPos(center.Row + dr, center.Col + dc));
        return cells;
    }
}
