using FusionRpg.Core.Stats.Aptitudes;
using Xunit;

namespace FusionRpg.Core.Tests.Stats.Aptitudes;

/// <summary>`species-build` T2.1 (module 5, `creature-type-allocation`) — the pure baseline math.
/// Uses the real shipped `aptitudes.v10.json` (same convention as `SpeciesCatalogDiffTests`' own
/// `RepoRoot()` helper) rather than constructing the whole `AptitudeTuning` record inline — only
/// `PointEconomy.AptitudePointsPerThetaMilliByScope[CreatureType]` is actually read by this code path
/// (the sibling table D55 did not touch). v5 -> v6 (passive-tree C6, 2026-09-06) tracks
/// RpgHost.cs/Program.cs so "the real shipped tuning" stays true; v6 -> v7 (D55, 2026-09-06) same.</summary>
public class SpeciesAllocationTests
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

    static long CreatureTypeRate => RealTuning.PointEconomy.AptitudePointsPerThetaMilliByScope[AllocationScope.CreatureType];

    static readonly Dictionary<string, long> ThreeWaySplit = new(StringComparer.Ordinal)
    {
        ["Might"] = 500, ["Vigor"] = 300, ["Fortitude"] = 200
    };

    [Fact]
    public void Baseline_at_level_one_is_empty_never_a_ceiling()
    {
        // budget-source's own zero-at-level-1 rule (T0.4): CreatureTypeSourceFromLevel(1) = 0.
        var result = SpeciesAllocation.Baseline(ThreeWaySplit, speciesLevel: 1, RealTuning);
        Assert.Same(AptitudeAllocation.Empty, result);
    }

    [Fact]
    public void Baseline_at_level_zero_is_also_empty()
    {
        var result = SpeciesAllocation.Baseline(ThreeWaySplit, speciesLevel: 0, RealTuning);
        Assert.Same(AptitudeAllocation.Empty, result);
    }

    [Fact]
    public void Baseline_scales_the_plans_shares_by_the_creatureType_budget()
    {
        const long level = 21; // CreatureTypeSourceFromLevel(21) = 20
        var result = SpeciesAllocation.Baseline(ThreeWaySplit, level, RealTuning);
        var budget = 20 * CreatureTypeRate;

        Assert.Equal(budget, result.TotalForScope(AllocationScope.CreatureType));
        // Largest-remainder rounding, but the ORDER of shares (500:300:200) must still hold at this scale.
        var might = result.PointsAt(AllocationScope.CreatureType, "Might");
        var vigor = result.PointsAt(AllocationScope.CreatureType, "Vigor");
        var fortitude = result.PointsAt(AllocationScope.CreatureType, "Fortitude");
        Assert.True(might > vigor && vigor > fortitude, $"expected Might({might}) > Vigor({vigor}) > Fortitude({fortitude})");
    }

    [Fact]
    public void Baseline_sums_to_exactly_the_budget_including_awkward_remainders()
    {
        // A level chosen so 1000-permille shares against the real rate force a non-round division.
        const long level = 8; // source = 7
        var result = SpeciesAllocation.Baseline(ThreeWaySplit, level, RealTuning);
        var budget = 7 * CreatureTypeRate;
        Assert.Equal(budget, result.TotalForScope(AllocationScope.CreatureType));
    }

    [Fact]
    public void Baseline_with_no_plan_entry_for_the_species_is_empty()
    {
        var result = SpeciesAllocation.Baseline(new Dictionary<string, long>(), speciesLevel: 50, RealTuning);
        Assert.Same(AptitudeAllocation.Empty, result);
    }

    [Fact]
    public void Baseline_rejects_a_plan_share_naming_an_unknown_aptitude()
    {
        var bad = new Dictionary<string, long>(StringComparer.Ordinal) { ["NotAnAptitude"] = 1000 };
        Assert.Throws<ArgumentException>(() => SpeciesAllocation.Baseline(bad, speciesLevel: 10, RealTuning));
    }

    /// <summary>
    /// solid-remediation T4.1 (S1/S3) restated this from "per player and per species". That was the
    /// incomplete contract the defect lived in: with no empire dimension, every species lookup resolved
    /// to whoever asked, so a lawn zombie read the human player's progression and Zomboss's empire had
    /// no key of its own for anything to credit.
    /// </summary>
    [Fact]
    public void ScopeKey_is_per_player_per_empire_and_per_species()
    {
        var dave = FusionRpg.Core.Commanders.EmpireId.Dave;
        var zomboss = FusionRpg.Core.Commanders.EmpireId.Zomboss;

        Assert.NotEqual(
            SpeciesAllocation.ScopeKey(1, dave, "fumeshroom"),
            SpeciesAllocation.ScopeKey(2, dave, "fumeshroom"));
        Assert.NotEqual(
            SpeciesAllocation.ScopeKey(1, dave, "fumeshroom"),
            SpeciesAllocation.ScopeKey(1, dave, "wallnut"));

        // The dimension this task added: the same player and the same species, two empires, two keys.
        Assert.NotEqual(
            SpeciesAllocation.ScopeKey(1, dave, "fumeshroom"),
            SpeciesAllocation.ScopeKey(1, zomboss, "fumeshroom"));
    }

    /// <summary>
    /// <b>Why this change needs no migration, asserted rather than asserted-in-a-comment.</b> Every row
    /// ever written under the old two-argument key was written for the player's own empire, so Dave maps
    /// onto the unchanged string. A live database keeps resolving every override it already holds; only
    /// a non-player empire mints a new key.
    ///
    /// <para>⚠️ This is a persisted key format. Changing what Dave produces silently orphans every
    /// existing CreatureType override, which is why it is pinned here rather than left to reviewer
    /// memory.</para>
    /// </summary>
    [Fact]
    public void The_players_own_empire_keeps_the_exact_pre_empire_key_so_no_row_is_orphaned()
    {
        Assert.Equal(
            "player:1:species:fumeshroom",
            SpeciesAllocation.ScopeKey(1, FusionRpg.Core.Commanders.EmpireId.Dave, "fumeshroom"));
    }

    /// <summary>
    /// The lawn's two sides ARE two empires. This is S1 stated as a contract: an actor resolves its own
    /// empire, never whoever happened to ask.
    /// </summary>
    [Fact]
    public void A_zombie_resolves_Zombosss_empire_and_a_plant_the_players()
    {
        Assert.Equal(
            FusionRpg.Core.Commanders.EmpireId.Zomboss,
            SpeciesAllocation.EmpireForSide(FusionRpg.Core.Stats.StatSide.Zombie));
        Assert.Equal(
            FusionRpg.Core.Commanders.EmpireId.Dave,
            SpeciesAllocation.EmpireForSide(FusionRpg.Core.Stats.StatSide.Plant));
    }

    [Fact]
    public void Overflow_an_extreme_level_throws_rather_than_wraps()
    {
        Assert.Throws<OverflowException>(() =>
            SpeciesAllocation.Baseline(ThreeWaySplit, speciesLevel: long.MaxValue - 1, RealTuning));
    }
}
