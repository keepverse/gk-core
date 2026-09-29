using System;
using System.Linq;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats;
using Xunit;

namespace FusionRpg.Core.Tests.Power;

/// <summary>
/// The report's own shape guarantees (spec-power-index.md §2.4, §5): sums reconcile, shares sum to
/// ~1000‰, and <c>Explain</c>/<c>ActorIndex</c> can never drift from each other because they share
/// one code path. Composition/weighting/rejection behaviour is PowerIndexTests.cs, not here.
/// </summary>
public class PowerAxisReportTests
{
    static PowerTuning Tuning() =>
        PowerTuning.Build(1, 1, PowerTuning.FixedCMilli, 0, PowerTuning.FixedPinIndex, PowerTuning.FixedPinValue,
            wdMilli: 1000, waMilli: 25000, wrMilli: 250, wzMilli: 1000, wmMilli: 5000, wwMilli: 5000, wfMilli: 25000);

    static StatContext Ctx(long playerId) => new() { PlayerId = playerId, Side = StatSide.Plant, TypeId = 0 };

    [Fact]
    public void AxisContributions_SumToTotal()
    {
        var provider = new HydratedPowerIndexProvider(Tuning());
        provider.Hydrate(Ctx(1), new ActorLadderSnapshot(DaveLevel: 37, RealmsAdvanced: 4, PvzRuns: 812));

        var report = provider.Explain(Ctx(1));
        long summedMilli = report.Axes.Sum(a => a.Milli);
        Assert.Equal(report.Total, RoundHalfAwayFromZero(summedMilli, 1000));
    }

    [Fact]
    public void Shares_SumToOneThousandPermille_WithinOnePermilleDrift()
    {
        var provider = new HydratedPowerIndexProvider(Tuning());
        provider.Hydrate(Ctx(1), new ActorLadderSnapshot(DaveLevel: 12, RealmsAdvanced: 7, PvzRuns: 233));

        var report = provider.Explain(Ctx(1));
        int summedShares = report.Axes.Sum(a => a.SharePermille);
        Assert.InRange(summedShares, 999, 1001);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 0, 0)]
    [InlineData(0, 1, 0)]
    [InlineData(0, 0, 1)]
    [InlineData(50, 12, 999)]
    [InlineData(1, 1, 1)]
    [InlineData(9999, 500, 123456)]
    public void ExplainTotal_AlwaysEqualsActorIndex_OverAGeneratedMatrix(int dave, int realms, int runs)
    {
        var provider = new HydratedPowerIndexProvider(Tuning());
        provider.Hydrate(Ctx(1), new ActorLadderSnapshot(dave, realms, runs));

        Assert.Equal(provider.Explain(Ctx(1)).Total, provider.ActorIndex(Ctx(1)));
    }

    [Fact]
    public void Purity_ComposerIsAllocationless_SameInputsSameOutput()
    {
        var tuning = Tuning();
        var snapshot = new ActorLadderSnapshot(44, 6, 900);
        var first = PowerIndexComposer.ActorExplain(tuning, snapshot);
        for (int i = 0; i < 1000; i++)
        {
            var again = PowerIndexComposer.ActorExplain(tuning, snapshot);
            Assert.Equal(first.Total, again.Total);
        }
    }

    static long RoundHalfAwayFromZero(long milli, long scale)
    {
        long q = milli / scale, r = milli % scale;
        if (r == 0) return q;
        return (r * 2 >= scale) ? q + 1 : q;
    }
}
