using FusionRpg.Core.Stats.Aptitudes;
using Xunit;

namespace FusionRpg.Core.Tests.Stats.Aptitudes;

/// <summary>passive-tree G7 (spec-species-tree.md §8.1 point 2) — the `UniqueCreature`-scope twin of
/// <see cref="SpeciesAllocationTests"/>, one test per test there, same shared-tuning convention
/// (`SpeciesAllocationTests`'s own `RepoRoot()`/real-`aptitudes.v10.json` precedent) — only
/// `PointEconomy.AptitudePointsPerThetaMilliByScope[UniqueCreature]` is actually read by this code
/// path (the sibling table D55 did not touch).</summary>
public class UniqueCreatureAllocationTests
{
    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Injector"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("repo root");
    }

    static readonly AptitudeTuning RealTuning = AptitudeTuningLoader.Parse(
        File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", "aptitudes.v10.json")));

    static long UniqueCreatureRate => RealTuning.PointEconomy.AptitudePointsPerThetaMilliByScope[AllocationScope.UniqueCreature];

    static readonly Dictionary<string, long> ThreeWaySplit = new(StringComparer.Ordinal)
    {
        ["Might"] = 500, ["Vigor"] = 300, ["Fortitude"] = 200
    };

    [Fact]
    public void Baseline_at_level_one_is_empty_never_a_ceiling()
    {
        // A freshly-created specimen (RpgStore.CreateUniqueActor starts every row at level 1) must
        // read exactly zero, or every roster entry that has never fought would own free points.
        var result = UniqueCreatureAllocation.Baseline(ThreeWaySplit, specimenLevel: 1, RealTuning);
        Assert.Same(AptitudeAllocation.Empty, result);
    }

    [Fact]
    public void Baseline_at_level_zero_is_also_empty()
    {
        var result = UniqueCreatureAllocation.Baseline(ThreeWaySplit, specimenLevel: 0, RealTuning);
        Assert.Same(AptitudeAllocation.Empty, result);
    }

    [Fact]
    public void Baseline_scales_the_plans_shares_by_the_uniqueCreature_budget()
    {
        const long level = 21; // UniqueCreatureSourceFromLevel(21) = 20
        var result = UniqueCreatureAllocation.Baseline(ThreeWaySplit, level, RealTuning);
        var budget = 20 * UniqueCreatureRate;

        Assert.Equal(budget, result.TotalForScope(AllocationScope.UniqueCreature));
        // Largest-remainder rounding, but the ORDER of shares (500:300:200) must still hold at this scale.
        var might = result.PointsAt(AllocationScope.UniqueCreature, "Might");
        var vigor = result.PointsAt(AllocationScope.UniqueCreature, "Vigor");
        var fortitude = result.PointsAt(AllocationScope.UniqueCreature, "Fortitude");
        Assert.True(might > vigor && vigor > fortitude, $"expected Might({might}) > Vigor({vigor}) > Fortitude({fortitude})");
    }

    [Fact]
    public void Baseline_sums_to_exactly_the_budget_including_awkward_remainders()
    {
        // A level chosen so 1000-permille shares against the real rate force a non-round division.
        const long level = 8; // source = 7
        var result = UniqueCreatureAllocation.Baseline(ThreeWaySplit, level, RealTuning);
        var budget = 7 * UniqueCreatureRate;
        Assert.Equal(budget, result.TotalForScope(AllocationScope.UniqueCreature));
    }

    [Fact]
    public void Baseline_with_no_plan_entry_for_the_specimen_is_empty()
    {
        var result = UniqueCreatureAllocation.Baseline(new Dictionary<string, long>(), specimenLevel: 50, RealTuning);
        Assert.Same(AptitudeAllocation.Empty, result);
    }

    [Fact]
    public void Baseline_rejects_a_plan_share_naming_an_unknown_aptitude()
    {
        var bad = new Dictionary<string, long>(StringComparer.Ordinal) { ["NotAnAptitude"] = 1000 };
        Assert.Throws<ArgumentException>(() => UniqueCreatureAllocation.Baseline(bad, specimenLevel: 10, RealTuning));
    }

    [Fact]
    public void Overflow_an_extreme_level_throws_rather_than_wraps()
    {
        Assert.Throws<OverflowException>(() =>
            UniqueCreatureAllocation.Baseline(ThreeWaySplit, specimenLevel: long.MaxValue - 1, RealTuning));
    }
}
