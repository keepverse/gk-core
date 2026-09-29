using System;
using System.IO;
using System.Linq;
using FusionRpg.Core.World.Loam;
using Xunit;

namespace FusionRpg.Core.Tests.World.Loam;

/// <summary>
/// world-map W49 acceptance: "one test walks every call site of `LoamUpkeep.For` and asserts a
/// single answer... a grep-shaped assertion fails if a new call site appears that does not pass a
/// season." This is that walk, over the real source tree rather than a remembered list — the same
/// scan-`src/`-directly discipline `WorldDeterminismGuardTests`/`RecruitPolicyTests` already use,
/// because a call site added later and never revisited here is exactly the silent failure §8c.6
/// warns about (the AI planning against an upkeep it does not pay, or the forecast disagreeing with
/// the act).
///
/// **Why this is a source scan, not a live cross-module run with a non-identity global config**:
/// `WorldTuningHub`/`RecruitPolicy` are configured once, process-wide, by a module initializer this
/// whole test assembly shares (`ContractTuningTestBootstrap.cs`) — xUnit parallelizes test
/// collections by default, so temporarily reconfiguring that shared static field mid-test to a
/// non-identity value would race every other class reading it concurrently (the same hazard
/// `RecruitPolicyTests` documents avoiding for exactly this reason). Structural agreement across
/// call sites is instead proven the way it actually holds: four of the five sites call the
/// identical `LoamUpkeep.For(WorldState, WorldSector)` overload — the same code, so they cannot
/// independently drift — and the fifth (`FrontierRulesPolicy`) is proven to scale correctly by the
/// second test below, which exercises the exact 6-argument signature it calls with an explicit
/// non-1000 `seasonMilli`, the same arithmetic proof `LoamUpkeepTests.cs` (W48) already established
/// for the row overload in general.
/// </summary>
public class LoamUpkeepSeasonCallSiteTests
{
    /// <summary>
    /// Every file:line in `src/` that calls `LoamUpkeep.For` or `LoamUpkeep.BreakdownFor`, excluding
    /// `LoamUpkeep.cs` itself. A new call site appearing here that is not on this exact, reviewed
    /// list is the failure this test exists to catch — add it to the list only after confirming it
    /// receives a real season (either via the auto-correct `(WorldState, WorldSector)` overload, or
    /// by deriving `seasonMilli` the same way `FrontierRulesPolicy.cs` now does).
    /// </summary>
    static readonly (string File, string Kind)[] KnownCallSites =
    {
        ("LoamBalance.cs", "(WorldState, WorldSector) — auto-correct"),
        ("LoamForecast.cs", "(WorldState, WorldSector) — auto-correct"),
        ("LoamPhases.cs", "(WorldState, WorldSector) — auto-correct"),
        ("WorldEndpoints.cs", "BreakdownFor(WorldState, WorldSector) — auto-correct"),
        ("FrontierRulesPolicy.cs", "6-arg row overload — derives seasonMilli explicitly")
    };

    [Fact]
    public void Every_real_call_site_of_LoamUpkeep_is_on_the_reviewed_list()
    {
        var srcRoot = Path.Combine(FindRepoRoot(), "src");
        var found = Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Replace('\\', '/').EndsWith("World/Loam/LoamUpkeep.cs"))
            .Where(f => System.Text.RegularExpressions.Regex.IsMatch(
                File.ReadAllText(f), @"LoamUpkeep\.(For|BreakdownFor)\("))
            .Select(f => Path.GetFileName(f))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        var known = KnownCallSites.Select(c => c.File).OrderBy(f => f, StringComparer.Ordinal).ToList();

        Assert.Equal(known, found);
    }

    /// <summary>
    /// `FrontierRulesPolicy`'s own call site uses the exact 6-argument row overload — this proves
    /// that signature scales a season factor correctly (the arithmetic risk this whole task exists
    /// to close), at a genuinely non-identity value, without touching any global tuning state.
    /// </summary>
    [Fact]
    public void The_belief_sides_exact_call_shape_scales_a_non_identity_season_correctly()
    {
        const int garrisonMembers = 2, developmentLevel = 1, dangerBand = 1, intensityMilli = 1000, handicapMilli = 1000;

        var atIdentity = LoamUpkeep.For(garrisonMembers, developmentLevel, dangerBand, intensityMilli, handicapMilli, seasonMilli: 1000);
        var atNonIdentity = LoamUpkeep.For(garrisonMembers, developmentLevel, dangerBand, intensityMilli, handicapMilli, seasonMilli: 1500);

        Assert.Equal(atIdentity * 3 / 2, atNonIdentity);
        Assert.NotEqual(atIdentity, atNonIdentity); // the whole point: a season change must move the number
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "FusionRpg.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
