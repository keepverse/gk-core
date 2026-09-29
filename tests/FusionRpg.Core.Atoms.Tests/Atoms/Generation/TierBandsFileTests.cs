using FusionRpg.Core.Effects.Atoms.Generation;
using Xunit;
using FusionRpg.TestSupport;

namespace FusionRpg.Core.Tests.Atoms.Generation;

/// <summary>
/// Real bug found 2026-09-08 (atom-family-expansion program, `tier-bands-coverage` module): both
/// `gk-forge/tools/FamilyExpandGen/Program.cs` and (until this fix) every consumer of `TierBandsFile` resolved
/// the tuning file as a hardcoded literal `"tier-bands.v1.json"` — never the latest published version.
/// `seedsmith numerics rebalance --publish` already writes `tier-bands.v{n+1}.json` (and has done so
/// twice: `v2.json`/`v3.json` exist on disk, dated before this fix, with 109/112 real
/// `channelWeightPermille` entries each) but the real C# generator never read either — the entire
/// versioned-rebalance mechanism was silently disconnected from the thing it exists to feed.
/// `TierBandsFile.FindLatestPath` mirrors `seedsmith.numerics.tier_bands_io.load("latest")`'s own
/// glob-and-pick-highest-version logic exactly, so both languages resolve "latest" the same way.
/// </summary>
public class TierBandsFileTests
{
    static string MakeTuningDir(params string[] fileNames)
    {
        var dir = Path.Combine(Path.GetTempPath(), "tier-bands-file-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var name in fileNames)
            File.WriteAllText(Path.Combine(dir, name), "{}");
        return dir;
    }

    [Fact]
    public void Picks_the_highest_version_number_present()
    {
        var dir = MakeTuningDir("tier-bands.v1.json", "tier-bands.v2.json", "tier-bands.v3.json");
        try
        {
            var path = TierBandsFile.FindLatestPath(dir);
            Assert.Equal(Path.Combine(dir, "tier-bands.v3.json"), path);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Picks_the_highest_version_even_out_of_lexicographic_order()
    {
        // v10 must beat v2 numerically, not lose to it lexicographically ("v10" < "v2" as strings).
        var dir = MakeTuningDir("tier-bands.v2.json", "tier-bands.v10.json", "tier-bands.v9.json");
        try
        {
            var path = TierBandsFile.FindLatestPath(dir);
            Assert.Equal(Path.Combine(dir, "tier-bands.v10.json"), path);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_single_version_resolves_to_itself()
    {
        var dir = MakeTuningDir("tier-bands.v1.json");
        try
        {
            Assert.Equal(Path.Combine(dir, "tier-bands.v1.json"), TierBandsFile.FindLatestPath(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void No_tier_bands_file_at_all_throws_rather_than_silently_returning_null()
    {
        var dir = MakeTuningDir(); // empty dir
        try
        {
            Assert.Throws<FileNotFoundException>(() => TierBandsFile.FindLatestPath(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Ignores_files_that_do_not_match_the_versioned_filename_pattern()
    {
        var dir = MakeTuningDir("tier-bands.v1.json", "tier-bands.v2.json", "some-other-file.json",
            "tier-bands.json" /* no version at all */);
        try
        {
            Assert.Equal(Path.Combine(dir, "tier-bands.v2.json"), TierBandsFile.FindLatestPath(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void The_real_repo_tuning_dir_resolves_to_a_version_at_least_3_today()
    {
        // Regression proof against the real bug: v2/v3 already existed on disk before this fix
        // shipped, dated before it, and were never read by anything. This test pins that the real
        // directory resolves to v3 or higher, not the old hardcoded v1.
        var dir = FindRealTuningDir();
        var path = TierBandsFile.FindLatestPath(dir);
        var name = Path.GetFileName(path);
        Assert.Matches(@"^tier-bands\.v(\d+)\.json$", name);
        var version = int.Parse(System.Text.RegularExpressions.Regex.Match(name, @"\d+").Value);
        Assert.True(version >= 3, $"expected the real tuning dir to resolve to v3+, got {name}");
    }

    static string FindRealTuningDir() => Path.Combine(ContentRoot.Path, "data", "seed", "items", "_tuning");
}
