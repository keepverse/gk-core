using FusionRpg.Core.Stats.Aptitudes;
using Xunit;

namespace FusionRpg.Core.Tests.Stats;

/// <summary>
/// build-preset BP2.12 (spec-capture-current.md) — <see cref="AptitudePresetCapture.FromAllocation"/>,
/// the pure points-to-shares half of a one-click capture. Goes the OTHER way from
/// <see cref="AptitudePresetMaterialize.Materialize"/> (shares → points); this suite proves the two
/// round-trip at the same budget, within the permille resolution the largest-remainder method itself
/// bounds.
/// </summary>
public class AptitudePresetCaptureTests
{
    static readonly IReadOnlyList<string> AllIds = AptitudeCatalog.All.Select(a => a.Id).ToList();

    static Dictionary<string, long> Allocation(params (string Id, long Points)[] rows) =>
        rows.ToDictionary(r => r.Id, r => r.Points, StringComparer.Ordinal);

    [Fact]
    public void A_single_aptitude_allocation_sums_to_exactly_1000()
    {
        var rows = AptitudePresetCapture.FromAllocation(Allocation((AllIds[0], 500)));

        Assert.Equal(1000L, rows.Sum(r => r.TargetPermille));
        Assert.Equal(1000L, Assert.Single(rows, r => r.AptitudeId == AllIds[0]).TargetPermille);
    }

    [Fact]
    public void A_varied_allocation_sums_to_exactly_1000()
    {
        var rows = AptitudePresetCapture.FromAllocation(
            Allocation((AllIds[0], 7), (AllIds[3], 13), (AllIds[7], 5), (AllIds[11], 1)));

        Assert.Equal(1000L, rows.Sum(r => r.TargetPermille));
    }

    [Fact]
    public void A_perfectly_tied_allocation_across_all_twelve_still_sums_to_1000_with_ties_broken_by_id()
    {
        // 12 aptitudes, 1 point each: floor(1000/12) = 83 for all twelve (996), leftover 4 goes to
        // the four SMALLEST ids ordinally, since every remainder ties.
        var rows = AptitudePresetCapture.FromAllocation(
            Allocation(AllIds.Select(id => (id, 1L)).ToArray()));

        Assert.Equal(1000L, rows.Sum(r => r.TargetPermille));
        var bySmallestId = AllIds.OrderBy(id => id, StringComparer.Ordinal).ToList();
        var byRow = rows.ToDictionary(r => r.AptitudeId, r => r.TargetPermille, StringComparer.Ordinal);
        for (var i = 0; i < bySmallestId.Count; i++)
            Assert.Equal(i < 4 ? 84L : 83L, byRow[bySmallestId[i]]);
    }

    [Fact]
    public void Every_one_of_the_twelve_primaries_gets_a_row_even_when_only_a_few_were_spent()
    {
        var rows = AptitudePresetCapture.FromAllocation(Allocation((AllIds[2], 10)));

        Assert.Equal(AptitudeCatalog.Count, rows.Count);
        Assert.All(rows, r => Assert.Contains(r.AptitudeId, AllIds));
        Assert.All(rows, r => Assert.Null(r.MinAbs));
        Assert.All(rows, r => Assert.Null(r.MaxAbs));
        Assert.All(rows, r => Assert.Null(r.MinPermille));
        Assert.All(rows, r => Assert.Null(r.MaxPermille));
    }

    [Fact]
    public void An_empty_allocation_returns_no_rows()
    {
        Assert.Empty(AptitudePresetCapture.FromAllocation(Allocation()));
    }

    [Fact]
    public void An_all_zero_allocation_returns_no_rows()
    {
        Assert.Empty(AptitudePresetCapture.FromAllocation(Allocation((AllIds[0], 0), (AllIds[1], 0))));
    }

    /// <summary>Every row's `TargetPermille` round-trips through `Materialize` at the SAME budget
    /// (the total spent) within the permille resolution the largest-remainder method itself bounds —
    /// computed from the budget under test, never a literal (spec's own test 2).</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(83)]
    [InlineData(1_000)]
    [InlineData(123_456)]
    public void Round_trip_at_the_same_budget_differs_per_row_by_at_most_the_permille_resolution(long budget)
    {
        // Deliberately non-proportional (budget, budget+1, budget+2) so the permille shares carry a
        // real fractional remainder at most scales, actually exercising the rounding bound rather
        // than dividing evenly every time.
        var allocation = Allocation((AllIds[0], budget), (AllIds[5], budget + 1), (AllIds[10], budget + 2));
        var spent = allocation.Values.Sum();
        var rows = AptitudePresetCapture.FromAllocation(allocation);

        var result = AptitudePresetMaterialize.Materialize(rows, spent);
        Assert.True(result.Ok, result.Reason);

        // ceil(spent / 1000) + 1, computed from THIS test's own spent total -- never a hardcoded bound.
        var bound = (spent + 999) / 1000 + 1;
        foreach (var (id, points) in allocation)
            Assert.True(Math.Abs(result.Shares[id] - points) <= bound,
                $"{id}: materialized {result.Shares[id]} vs allocated {points}, bound {bound}");
    }
}
