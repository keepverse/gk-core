using System.Text.Json;
using FusionRpg.Core.Items;
using Xunit;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// I1 §3.5's overlap invariant, claimed by `rarity-bands` (item module 7) because the only
/// would-be consumer (`spec-uniques.md`) declined to build a second simulator. Re-run against the
/// REAL, shipped `gk-data/packs/fusion/data/seed/rarity/ladder.v1.json` tier windows and count-band floors
/// (E3-corrected), seed 20260822, 2x10^5 rolls per rung.
///
/// <para><b>`chaff` is excluded from every pooled rate.</b> Its count band is 0-0 ("the only rung with
/// no pool", core.v1.json) so it never rolls a magnitude at all -- `U(chaff, sprout)` is trivially 0%
/// by construction, not a measurement of overlap decay, and pooling it in would fail the 5% floor for
/// a reason that has nothing to do with the mechanism being measured. Measured directly below: every
/// individual chaff-anchored pair IS exactly 0%, confirming this is the right, not the convenient,
/// exclusion.</para>
/// </summary>
public class RarityOverlapSimulatorTests
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

    static List<RarityRungWindow> LoadWindows()
    {
        var path = Path.Combine(RepoRoot(), "data", "seed", "rarity", "ladder.v1.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("entries").EnumerateArray()
            .Select(e => (
                Ordinal: e.GetProperty("ordinal").GetInt32(),
                Window: new RarityRungWindow(
                    e.GetProperty("id").GetString()!,
                    e.GetProperty("minTier").GetInt32(),
                    e.GetProperty("maxTier").GetInt32(),
                    e.GetProperty("prefixRolls").GetInt32() + e.GetProperty("suffixRolls").GetInt32())))
            .OrderBy(x => x.Ordinal)
            .Select(x => x.Window)
            .ToList();
    }

    // Rolled once per rung and reused across every distance k -- matches §3.5's "2x10^5 rolls per
    // rung", not per pair.
    static readonly List<RarityRungWindow> Windows = LoadWindows();
    static readonly Dictionary<string, int[]> Rolls = Windows
        .ToDictionary(w => w.RarityId, w => RarityOverlapSimulator.RollMagnitudes(w));

    static double PairUpsetRate(int n, int k)
    {
        var a = Rolls[Windows[n].RarityId];
        var b = Rolls[Windows[n + k].RarityId];
        long wins = 0;
        for (var i = 0; i < a.Length; i++)
            if (a[i] > b[i]) wins++;
        return (double)wins / a.Length;
    }

    /// <summary>Pooled U(n,k) over every valid (n, n+k) pair with a nonzero pool on both sides.</summary>
    static double PooledUpsetRateExcludingChaff(int k)
    {
        long wins = 0, trials = 0;
        for (var n = 0; n + k < Windows.Count; n++)
        {
            if (Windows[n].AffixCount == 0 || Windows[n + k].AffixCount == 0) continue;

            var a = Rolls[Windows[n].RarityId];
            var b = Rolls[Windows[n + k].RarityId];
            for (var i = 0; i < a.Length; i++)
                if (a[i] > b[i]) wins++;
            trials += a.Length;
        }

        return (double)wins / trials;
    }

    [Fact]
    public void Every_chaff_anchored_pair_is_exactly_zero_at_every_distance()
    {
        for (var n = 0; n < Windows.Count; n++)
            if (Windows[n].AffixCount == 0)
                for (var k = 1; n + k < Windows.Count; k++)
                    Assert.Equal(0.0, PairUpsetRate(n, k));
    }

    [Fact]
    public void Adjacent_rung_upset_rate_is_within_band()
    {
        var u1 = PooledUpsetRateExcludingChaff(1);
        Assert.InRange(u1, 0.05, 0.30);
    }

    [Fact]
    public void Every_individual_adjacent_pair_with_a_pool_on_both_sides_is_within_band()
    {
        for (var n = 0; n + 1 < Windows.Count; n++)
        {
            if (Windows[n].AffixCount == 0 || Windows[n + 1].AffixCount == 0) continue;
            var rate = PairUpsetRate(n, 1);
            Assert.InRange(rate, 0.05, 0.30);
        }
    }

    [Fact]
    public void Distance_two_upset_rate_is_at_most_ten_percent()
    {
        var u2 = PooledUpsetRateExcludingChaff(2);
        Assert.True(u2 <= 0.10, $"U(n,2) = {u2:P2}, required <= 10%");
    }

    [Fact]
    public void Distance_three_upset_rate_is_under_two_percent()
    {
        var u3 = PooledUpsetRateExcludingChaff(3);
        Assert.True(u3 <= 0.02, $"U(n,3) = {u3:P2}, required <= 2%");
    }

    [Fact]
    public void Distance_four_upset_rate_is_approximately_zero()
    {
        var u4 = PooledUpsetRateExcludingChaff(4);
        Assert.True(u4 <= 0.01, $"U(n,4) = {u4:P3}, required ~= 0%");
    }

    [Fact]
    public void Rolling_is_deterministic_under_the_pinned_seed()
    {
        var a = RarityOverlapSimulator.RollMagnitudes(Windows[1], RarityOverlapSimulator.Seed, 1000);
        var b = RarityOverlapSimulator.RollMagnitudes(Windows[1], RarityOverlapSimulator.Seed, 1000);
        Assert.Equal(a, b);
    }

    [Fact]
    public void A_rung_with_no_affixes_always_rolls_zero()
    {
        var chaff = Windows.Single(w => w.RarityId == "chaff");
        Assert.Equal(0, chaff.AffixCount);
        Assert.All(RarityOverlapSimulator.RollMagnitudes(chaff, rolls: 1000), m => Assert.Equal(0, m));
    }

    [Fact]
    public void Unique_is_not_a_rarity_rung()
    {
        // I12's R7 was a container flag, not a ladder rung (§3.6, D15) -- pinned as a test so the
        // ladder can never grow an eleventh "unique" entry by mistake.
        Assert.DoesNotContain("unique", RarityLadder.RungIds);
    }
}
