using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// E2's `effect_curve` DDL and DAL. Core owns the interpolation maths and has its own tests; this
/// covers the half that only exists once a database is involved — that the table is actually created
/// by <c>Init()</c>, that rows survive a round trip, and that a malformed row cannot be stored.
/// </summary>
public class CurveStoreTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public CurveStoreTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose() => _testStore.Dispose();

    static CurvePoint[] Points(params (int X, int Milli)[] p) =>
        p.Select(t => new CurvePoint(t.X, t.Milli)).ToArray();

    [Fact]
    public void A_curve_round_trips_with_its_points_in_order()
    {
        var ok = _store.UpsertCurve("curve.atk.level", CurveInput.Level,
            Points((1, 1000), (10, 2000), (20, 3000)));
        Assert.True(ok.Ok, ok.Reason);

        var back = _store.GetCurve("curve.atk.level");

        Assert.NotNull(back);
        Assert.Equal(CurveInput.Level, back!.Input);
        Assert.Equal(3, back.Points.Count);
        Assert.Equal(new CurvePoint(10, 2000), back.Points[1]);

        // And it still interpolates the way Core says it should, through the storage layer.
        // x=5 sits 4/9 along the (1,1000)->(10,2000) segment: 1000 + round(1000*4/9) = 1444.
        Assert.Equal(1444, back.MultiplierAt(5));
        Assert.Equal(3000, back.MultiplierAt(999)); // clamps, never extrapolates
    }

    [Fact]
    public void Upsert_bumps_the_revision_so_E8_can_see_the_edit()
    {
        _store.UpsertCurve("curve.rarity.band", CurveInput.Rarity, Points((1, 1000)));
        var first = _store.GetCurveRevision("curve.rarity.band");

        _store.UpsertCurve("curve.rarity.band", CurveInput.Rarity, Points((1, 1200)));
        var second = _store.GetCurveRevision("curve.rarity.band");

        Assert.True(second > first, $"{second} should exceed {first}");
        Assert.Equal(1200, _store.GetCurve("curve.rarity.band")!.MultiplierAt(1));
    }

    [Theory]
    [InlineData(new int[0])]              // no points -> a hot-path divide by zero
    public void A_curve_with_no_points_is_refused(int[] _)
    {
        var r = _store.UpsertCurve("curve.empty", CurveInput.Level, Array.Empty<CurvePoint>());

        Assert.False(r.Ok);
        Assert.Null(_store.GetCurve("curve.empty"));
    }

    [Fact]
    public void Unsorted_or_duplicate_points_never_reach_the_table()
    {
        Assert.False(_store.UpsertCurve("curve.unsorted", CurveInput.Level, Points((5, 1000), (2, 2000))).Ok);
        Assert.False(_store.UpsertCurve("curve.dup", CurveInput.Level, Points((1, 1000), (1, 2000))).Ok);

        Assert.Null(_store.GetCurve("curve.unsorted"));
        Assert.Null(_store.GetCurve("curve.dup"));
    }

    [Fact]
    public void Curves_list_in_stable_id_order_because_E8_hashes_them()
    {
        foreach (var id in new[] { "curve.z", "curve.a", "curve.m" })
            _store.UpsertCurve(id, CurveInput.Tier, Points((1, 1000)));

        var ids = _store.ListCurves().Select(c => c.CurveId).ToList();

        Assert.Equal(new[] { "curve.a", "curve.m", "curve.z" }, ids);
    }

    [Fact]
    public void An_absent_curve_reads_as_null_not_as_an_empty_curve()
    {
        Assert.Null(_store.GetCurve("curve.nope"));
        Assert.Equal(0, _store.GetCurveRevision("curve.nope"));
    }
}
