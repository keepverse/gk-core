using FusionRpg.Contracts;
using FusionRpg.Core.Creatures;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests;

public class SummonStoreTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public SummonStoreTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _store.AwardSouls(1, 10_000, SoulEarnPolicy.Reasons.Seed, "test-bankroll");
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    [Fact]
    public void Ten_pull_mints_spends_and_logs_atomically()
    {
        const int pullCount = 10;
        var (ok, _, outcome) = _store.ExecuteSummon(1, SummonBannerCatalog.StandardRift, pullCount, "c-ten", 42, null);
        Assert.True(ok);
        // Self-referential (population-pin SE3.4, 2026-09-20): this test asked for pullCount pulls, so
        // it proves none were lost or duplicated, not a fact about shipped content.
        Assert.Equal(pullCount, outcome!.Specimens.Count);
        Assert.Equal(pullCount, _store.ListCreatureRoster(1).Items.Count);
        Assert.True(_store.ListCreatureCodex(1).Entries.Count >= 1);
        // Spent 900; discovery rewards may add back — assert via totals.
        Assert.Equal(900, _store.GetSoulBalance(1).SpentTotal);
        Assert.True(outcome.Pity.PullsSinceSunwoven <= pullCount);
        Assert.Equal(outcome.Pity, _store.GetSummonPity(1));
    }

    [Fact]
    public void Replay_returns_stored_results_without_spending()
    {
        var (_, _, first) = _store.ExecuteSummon(1, SummonBannerCatalog.StandardRift, 10, "c-replay", 7, null);
        var balanceAfter = _store.GetSoulBalance(1).Balance;
        var (ok, reason, replay) = _store.ExecuteSummon(1, SummonBannerCatalog.StandardRift, 10, "c-replay", 999, null);
        Assert.True(ok);
        Assert.Equal("replay", reason);
        Assert.True(replay!.Replayed);
        Assert.Equal(first!.Specimens.Select(s => s.Profile.InstanceId), replay.Specimens.Select(s => s.Profile.InstanceId));
        Assert.Equal(balanceAfter, _store.GetSoulBalance(1).Balance);
        Assert.Equal(10, _store.ListCreatureRoster(1).Items.Count); // no extra mints
    }

    [Fact]
    public void Replay_with_mismatched_request_is_refused()
    {
        _store.ExecuteSummon(1, SummonBannerCatalog.StandardRift, 10, "c-mismatch", 7, null);
        var (ok, reason, _) = _store.ExecuteSummon(1, SummonBannerCatalog.StandardRift, 1, "c-mismatch", 7, null);
        Assert.False(ok);
        Assert.Equal("correlation.mismatch", reason);
    }

    [Fact]
    public void Overdraft_refusal_writes_nothing()
    {
        _store.TrySpendSouls(1, 9_950, SoulEarnPolicy.Reasons.Summon, "drain"); // leave 50
        var (ok, reason, _) = _store.ExecuteSummon(1, SummonBannerCatalog.StandardRift, 1, "c-broke", 7, null);
        Assert.False(ok);
        Assert.Equal("souls.insufficient", reason);
        Assert.Empty(_store.ListCreatureRoster(1).Items);
        Assert.Equal(PityState.Fresh, _store.GetSummonPity(1));
        Assert.Equal(50, _store.GetSoulBalance(1).Balance);
    }

    [Fact]
    public void Mid_pull_failure_rolls_back_everything()
    {
        _store.SummonMidPullTestHook = () => throw new InvalidOperationException("boom");
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                _store.ExecuteSummon(1, SummonBannerCatalog.StandardRift, 10, "c-crash", 42, null));
        }
        finally
        {
            _store.SummonMidPullTestHook = null;
        }

        // Atomic-or-nothing: no specimens, no codex, no pity, no log, spend rolled back.
        Assert.Empty(_store.ListCreatureRoster(1).Items);
        Assert.Empty(_store.ListCreatureCodex(1).Entries);
        Assert.Equal(PityState.Fresh, _store.GetSummonPity(1));
        Assert.Equal(10_000, _store.GetSoulBalance(1).Balance);
        var (ok, _, outcome) = _store.ExecuteSummon(1, SummonBannerCatalog.StandardRift, 10, "c-crash", 42, null);
        Assert.True(ok); // correlation was never logged, so the retry runs fresh
        Assert.False(outcome!.Replayed);
    }

    [Fact]
    public void Discovery_rewards_pay_once_per_species()
    {
        var (_, _, a) = _store.ExecuteSummon(1, SummonBannerCatalog.StandardRift, 10, "c-d1", 42, null);
        var (_, _, b) = _store.ExecuteSummon(1, SummonBannerCatalog.StandardRift, 10, "c-d2", 42, null);
        // Identical seed + pity differences aside: any species discovered in pull 1 pays nothing in pull 2.
        var speciesInBoth = a!.Specimens.Select(s => s.Profile.SpeciesId)
            .Intersect(b!.Specimens.Select(s => s.Profile.SpeciesId)).ToList();
        Assert.NotEmpty(speciesInBoth);
        var ledger = _store.ListSoulLedger(1, 200);
        var discoveryRows = ledger.Items.Where(i => i.Reason == SoulEarnPolicy.Reasons.Discovery && i.RefKind == "species").ToList();
        Assert.Equal(discoveryRows.Select(d => d.RefId).Distinct().Count(), discoveryRows.Count);
    }

    [Fact]
    public void Pity_counters_accumulate_across_pulls_and_banners()
    {
        _store.ExecuteSummon(1, SummonBannerCatalog.StandardRift, 10, "c-p1", 1, null);
        var afterTen = _store.GetSummonPity(1);
        _store.ExecuteSummon(1, SummonBannerCatalog.ElementFocus, 1, "c-p2", 2, "fire");
        var afterEleven = _store.GetSummonPity(1);
        // Counter either advanced by one or reset on a hit — never unchanged-and-nonzero drifting.
        Assert.True(afterEleven.PullsSinceSunwoven == afterTen.PullsSinceSunwoven + 1
                    || afterEleven.PullsSinceSunwoven == 0);
    }

    [Fact]
    public void Focus_banner_requires_a_valid_focus_element()
    {
        var (ok, reason, _) = _store.ExecuteSummon(1, SummonBannerCatalog.ElementFocus, 1, "c-f", 7, "42");
        Assert.False(ok);
        Assert.Equal("focus.unknown", reason);
    }

    [Fact]
    public void Codex_milestones_pay_at_50_and_90_percent_once()
    {
        var all = FusionRpg.Core.Creatures.CreatureSpeciesCatalog.All;
        var half = (all.Count + 1) / 2;            // ceil 50 %
        var ninety = (all.Count * 9 + 9) / 10;     // ceil 90 %

        // Pre-discover exactly the half threshold, then one pull triggers the half milestone.
        foreach (var s in all.Take(half))
            _store.UpsertCreatureCodex(1, s.SpeciesId, CreatureCodexStates.Discovered);
        _store.ExecuteSummon(1, SummonBannerCatalog.StandardRift, 1, "c-m1", 3, null);
        var ledger = _store.ListSoulLedger(1, 300);
        Assert.Single(ledger.Items, i => i.Reason == SoulEarnPolicy.Reasons.Milestone && i.RefId == "half");

        // Cross the 90 % line: full milestone lands; a further pull re-awards neither.
        foreach (var s in all.Take(ninety))
            _store.UpsertCreatureCodex(1, s.SpeciesId, CreatureCodexStates.Discovered);
        _store.ExecuteSummon(1, SummonBannerCatalog.StandardRift, 1, "c-m2", 4, null);
        _store.ExecuteSummon(1, SummonBannerCatalog.StandardRift, 1, "c-m3", 5, null);
        ledger = _store.ListSoulLedger(1, 300);
        Assert.Single(ledger.Items, i => i.Reason == SoulEarnPolicy.Reasons.Milestone && i.RefId == "half");
        Assert.Single(ledger.Items, i => i.Reason == SoulEarnPolicy.Reasons.Milestone && i.RefId == "full");
    }

    [Fact]
    public void Spend_collision_outside_the_summon_log_throws_instead_of_free_pull()
    {
        // A summon-reason spend row without a summon-log row (only reachable by misusing the
        // public spend API) must abort the pull, not mint for free (review S1).
        _store.TrySpendSouls(1, 100, SoulEarnPolicy.Reasons.Summon, "c-collide");
        Assert.Throws<InvalidOperationException>(() =>
            _store.ExecuteSummon(1, SummonBannerCatalog.StandardRift, 1, "c-collide", 7, null));
        Assert.Empty(_store.ListCreatureRoster(1).Items);
    }
}
