using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// E2 acceptance for the curve half. Scaling is a reference, never a formula — so the rules that
/// matter are validation at load, clamping at the ends, and rounding exactly once.
/// </summary>
public class CurveTableTests
{
    static CurveTable Build(params (int X, int Milli)[] points)
    {
        var r = CurveTable.TryCreate("curve.test", CurveInput.Level,
            points.Select(p => new CurvePoint(p.X, p.Milli)).ToArray(), out var curve);

        Assert.True(r.IsOk, r.ToString());
        return curve!;
    }

    [Fact]
    public void A_single_point_curve_is_a_constant_multiplier()
    {
        var curve = Build((5, 1500));

        Assert.Equal(1500, curve.MultiplierAt(1));
        Assert.Equal(1500, curve.MultiplierAt(5));
        Assert.Equal(1500, curve.MultiplierAt(99));
    }

    [Fact]
    public void Zero_points_is_rejected_at_load()
    {
        // A hot-path divide-by-zero must be impossible by construction, not caught later.
        var r = CurveTable.TryCreate("curve.empty", CurveInput.Level, Array.Empty<CurvePoint>(), out var curve);

        Assert.Equal(AtomRejectionReason.BadCurve, r.Reason);
        Assert.Null(curve);
    }

    [Fact]
    public void Duplicate_x_is_rejected()
    {
        var r = CurveTable.TryCreate("curve.dup", CurveInput.Level,
            new[] { new CurvePoint(1, 1000), new CurvePoint(1, 2000) }, out _);

        Assert.Equal(AtomRejectionReason.BadCurve, r.Reason);
    }

    [Fact]
    public void Unsorted_x_is_rejected_rather_than_sorted_for_the_author()
    {
        // "Ordered" is validated, not assumed - silently sorting would hide an authoring mistake.
        var r = CurveTable.TryCreate("curve.unsorted", CurveInput.Level,
            new[] { new CurvePoint(5, 1000), new CurvePoint(2, 2000) }, out _);

        Assert.Equal(AtomRejectionReason.BadCurve, r.Reason);
    }

    [Fact]
    public void An_empty_curve_id_is_rejected()
    {
        Assert.Equal(AtomRejectionReason.BadCurve,
            CurveTable.TryCreate("", CurveInput.Level, new[] { new CurvePoint(1, 1000) }, out _).Reason);
    }

    [Fact]
    public void Below_the_first_and_above_the_last_point_clamps_and_never_extrapolates()
    {
        var curve = Build((10, 1000), (20, 2000));

        Assert.Equal(1000, curve.MultiplierAt(0));
        Assert.Equal(1000, curve.MultiplierAt(-500));
        Assert.Equal(2000, curve.MultiplierAt(21));
        Assert.Equal(2000, curve.MultiplierAt(10_000));
    }

    [Fact]
    public void Interpolation_is_linear_between_points()
    {
        var curve = Build((0, 1000), (10, 2000));

        Assert.Equal(1000, curve.MultiplierAt(0));
        Assert.Equal(1500, curve.MultiplierAt(5));
        Assert.Equal(2000, curve.MultiplierAt(10));
        Assert.Equal(1300, curve.MultiplierAt(3));
    }

    [Fact]
    public void Interpolation_rounds_half_away_from_zero()
    {
        // x = 1 of 2 across a 1000-milli rise is exactly 500 - the .5 case, rounded up not truncated.
        var curve = Build((0, 0), (2, 1000));

        Assert.Equal(500, curve.MultiplierAt(1));
    }

    [Fact]
    public void Multi_segment_curves_pick_the_right_segment()
    {
        var curve = Build((1, 1000), (5, 2000), (10, 2500));

        Assert.Equal(1000, curve.MultiplierAt(1));
        Assert.Equal(1500, curve.MultiplierAt(3));
        Assert.Equal(2000, curve.MultiplierAt(5));
        Assert.Equal(2200, curve.MultiplierAt(7)); // (5..10) rises 500 over 5, so +100/step
        Assert.Equal(2500, curve.MultiplierAt(10));
    }

    [Theory]
    [InlineData(100, 1000, 100)]   // identity
    [InlineData(100, 1500, 150)]
    [InlineData(100, 0, 0)]
    [InlineData(3, 1500, 5)]       // 4.5 rounds away from zero -> 5
    [InlineData(-3, 1500, -5)]     // and symmetrically for negatives
    [InlineData(-100, 1500, -150)]
    public void ApplyMilli_scales_and_rounds_half_away_from_zero(int value, int milli, int expected)
    {
        Assert.Equal(expected, CurveTable.ApplyMilli(value, milli));
    }

    [Fact]
    public void MultiplierAt_allocates_nothing()
    {
        var curve = Build((1, 1000), (10, 2000), (20, 3000));

        for (var i = 0; i < 1000; i++) curve.MultiplierAt(i % 25);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100_000; i++) curve.MultiplierAt(i % 25);
        var after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, after - before);
    }
}
