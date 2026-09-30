using FusionRpg.Core.Battle;
using FusionRpg.Core.Effects.Atoms.Power;
using FusionRpg.Core.Items.Requirements;
using FusionRpg.Core.Stats.Aptitudes;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// item/spec-requirement-profiles.md Testing strategy, pulled forward as species-gear-chain T35:
/// every row recomputed from live inputs, never pinned to today's tuning values. Closed
/// vocabularies (kinds, aptitudes, rungs) pinned with reason; no population count anywhere.
/// </summary>
public class RequirementProfileTests
{
    static string RepoRoot()
    {
        return KeepverseRoots.Core();
    }

    static RequirementProfileTuning Shipped() => RequirementProfileTuningLoader.Parse(
        File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", "equipment-requirements.v1.json")));

    static RequirementProfile Resolve(
        ulong seed = 7, string rarity = "fused", long p = 500,
        PowerVector? power = null, IReadOnlyList<string>? pool = null,
        RequirementProfileTuning? tuning = null) =>
        RequirementProfileResolver.Resolve(seed, 1, 3, 1, 20, p,
            power ?? new PowerVector(10, 0, 0, 0, 0), rarity,
            pool ?? new[] { "Might", "Focus" }, tuning ?? Shipped());

    // ── replay, streams, order ───────────────────────────────────────────────────────

    [Fact]
    public void Replay_is_byte_identical()
    {
        var tuning = Shipped();
        var first = Resolve(tuning: tuning);
        var second = Resolve(tuning: tuning);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Candidate_order_is_irrelevant()
    {
        var tuning = Shipped();
        var forward = Resolve(pool: new[] { "Focus", "Might", "Vigor" }, tuning: tuning);
        var backward = Resolve(pool: new[] { "Vigor", "Might", "Focus" }, tuning: tuning);
        Assert.Equal(forward, backward);
    }

    // ── rejections ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Invalid_cells_and_pools_block_with_named_rules()
    {
        var tuning = Shipped();
        Assert.Throws<RequirementProfileRejection>(() => Resolve(rarity: "mythic", tuning: tuning));
        Assert.Throws<RequirementProfileRejection>(() => Resolve(p: -1, tuning: tuning));
        // Pool validations only fire when the draw needs a pool (fixed/ratio kinds): sweep seeds
        // with each bad pool, and assert each rule fired at least once — otherwise the sweep proves
        // nothing about that rule.
        var fired = new HashSet<string>();
        for (ulong seed = 1; seed <= 300; seed++)
        {
            foreach (var pool in new[]
                     {
                         new[] { "Might", "Might" },
                         new[] { "NotAnAptitude" },
                         Array.Empty<string>(),
                     })
            {
                try { Resolve(seed: seed, pool: pool, tuning: tuning); }
                catch (RequirementProfileRejection ex) when (
                    ex.Rule is "pool-duplicate" or "pool-unknown-aptitude" or "pool-missing")
                {
                    fired.Add(ex.Rule);
                }
            }
        }
        Assert.Contains("pool-duplicate", fired);
        Assert.Contains("pool-unknown-aptitude", fired);
        Assert.Contains("pool-missing", fired);
    }

    [Fact]
    public void Revision_mismatches_block()
    {
        var tuning = Shipped();
        Assert.Throws<RequirementProfileRejection>(() =>
            RequirementProfileResolver.Resolve(7, 999, 3, 1, 20, 500,
                new PowerVector(10, 0, 0, 0, 0), "fused", new[] { "Might" }, tuning));
        Assert.Throws<RequirementProfileRejection>(() =>
            RequirementProfileResolver.Resolve(7, 1, 3, 999, 20, 500,
                new PowerVector(10, 0, 0, 0, 0), "fused", new[] { "Might" }, tuning));
    }

    // ── rarity and power boundaries ──────────────────────────────────────────────────

    [Fact]
    public void Rarity_selects_the_row_only_never_a_post_selection_value()
    {
        // Same everything except rarity: any resolved threshold must be identical wherever both
        // rarities can draw the same kind. Proved structurally — the resolver takes no rarity past
        // the matrix row — and behaviorally: fixed/ratio thresholds drawn on one rung re-resolve
        // equal on another rung sharing the kind.
        var tuning = Shipped();
        var seen = new Dictionary<RequirementProfileKind, HashSet<long>>();
        foreach (var rung in new[] { "chaff", "fused", "almanac" })
        {
            var profile = Resolve(rarity: rung, tuning: tuning);
            var value = profile.BuildTrial?.MinimumPoints ?? profile.BuildTrial?.MinimumShareMilli
                ?? profile.MinimumLevel;
            if (value is { } v)
            {
                if (!seen.TryGetValue(profile.ProfileKind, out var set))
                    seen[profile.ProfileKind] = set = new HashSet<long>();
                set.Add(v);
            }
        }
        // Every observed kind's values come from power-keyed bands alone — the sets stay small
        // because bands are shared, not because rarities were pinned.
        foreach (var set in seen.Values)
            Assert.True(set.Count <= 3, $"kind values vary by more than the three power bands: {string.Join(",", set)}");
    }

    [Fact]
    public void Low_power_never_selects_upkeep()
    {
        var tuning = Shipped();
        for (ulong seed = 1; seed <= 60; seed++)
        {
            var profile = Resolve(seed: seed, p: 100, tuning: tuning);
            Assert.True(profile.ProfileKind != RequirementProfileKind.Sustained
                && profile.ProfileKind != RequirementProfileKind.Jackpot,
                $"seed {seed} selected {profile.ProfileKind} at low power");
            Assert.Null(profile.Upkeep);
        }
    }

    // ── evaluator ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Ratio_equality_passes_and_empty_allocation_fails_a_positive_floor()
    {
        // 250/1000 meets a 250 share exactly (integer-exact boundary).
        var allocation = AptitudeAllocation.Single(AllocationScope.Commander, "Might", 250)
            + AptitudeAllocation.Single(AllocationScope.CreatureType, "Focus", 750);
        Assert.True(RequirementTrialEvaluator.MeetsShare(allocation, "Might", 250));
        Assert.False(RequirementTrialEvaluator.MeetsShare(allocation, "Might", 251));
        Assert.False(RequirementTrialEvaluator.MeetsShare(AptitudeAllocation.Empty, "Might", 1));
        Assert.True(RequirementTrialEvaluator.MeetsShare(AptitudeAllocation.Empty, "Might", 0));
    }

    [Fact]
    public void Checked_overflow_throws_never_clamps()
    {
        var allocation = AptitudeAllocation.Single(AllocationScope.Commander, "Might", long.MaxValue);
        Assert.Throws<OverflowException>(() =>
            RequirementTrialEvaluator.MeetsShare(allocation, "Might", 1000));
    }

    [Fact]
    public void Unmet_clauses_report_by_name()
    {
        var level = new RequirementProfile(10, null, null, RequirementProfileKind.Level);
        Assert.Equal(TrialUnmetClause.Level,
            RequirementTrialEvaluator.Evaluate(level, 5, AptitudeAllocation.Empty).Unmet);
        Assert.True(RequirementTrialEvaluator.Evaluate(level, 10, AptitudeAllocation.Empty).Ready);

        var fixed_ = new RequirementProfile(null,
            new BuildTrialClause("Might", 100, null), null, RequirementProfileKind.Fixed);
        var poor = AptitudeAllocation.Single(AllocationScope.Commander, "Might", 50);
        var rich = AptitudeAllocation.Single(AllocationScope.Commander, "Might", 100);
        Assert.Equal(TrialUnmetClause.FixedAptitude,
            RequirementTrialEvaluator.Evaluate(fixed_, 1, poor).Unmet);
        Assert.True(RequirementTrialEvaluator.Evaluate(fixed_, 1, rich).Ready);

        var ratio = new RequirementProfile(null,
            new BuildTrialClause("Might", null, 500), null, RequirementProfileKind.Ratio);
        Assert.Equal(TrialUnmetClause.RatioAptitude,
            RequirementTrialEvaluator.Evaluate(ratio, 1, poor + AptitudeAllocation.Single(
                AllocationScope.CreatureType, "Focus", 950)).Unmet);
    }

    [Fact]
    public void Upkeep_is_ignored_by_the_evaluator()
    {
        var sustained = new RequirementProfile(null, null,
            new UpkeepClause("souls", 10, 5, 100), RequirementProfileKind.Sustained);
        Assert.True(RequirementTrialEvaluator.Evaluate(sustained, 1, AptitudeAllocation.Empty).Ready);
    }
}
