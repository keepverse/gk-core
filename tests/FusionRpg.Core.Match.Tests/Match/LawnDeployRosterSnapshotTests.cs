using FusionRpg.Core.Match;
using Xunit;

namespace FusionRpg.Core.Tests.Match;

/// <summary>
/// creature-lawn-deploy T2.1 (spec-lawn-deploy-events.md Correction 2) — the Hot/Cold-safe roster
/// snapshot, tested the same way <c>MatchCommanderSnapshotTests.cs</c> already proves the identical
/// pattern for the commander snapshot: <see cref="HostApplyWithSnapshot"/> mirrors
/// <c>MatchHost.Apply</c>'s own board.start/board.end logic directly (the Injector project targets
/// net6.0 against real game interop and cannot be unit tested from here).
/// </summary>
public class LawnDeployRosterSnapshotTests
{
    public LawnDeployRosterSnapshotTests()
    {
        LawnDeployRosterSnapshotHolder.EndMatch();
        LawnDeployRosterSessionCache.ResetForTests();
    }

    static LawnDeployRosterEntry Entry(string instanceId, string speciesId = "abyssswordstar") =>
        new(instanceId, speciesId);

    [Fact]
    public void BeginMatch_sets_Current_EndMatch_clears()
    {
        var snap = new LawnDeployRosterSnapshot(new[] { Entry("a") }, 1);
        LawnDeployRosterSnapshotHolder.BeginMatch(snap);
        Assert.Same(snap, LawnDeployRosterSnapshotHolder.Current);

        LawnDeployRosterSnapshotHolder.EndMatch();
        Assert.Null(LawnDeployRosterSnapshotHolder.Current);
    }

    [Fact]
    public void ResolveOrEmpty_returns_empty_outside_a_match()
    {
        Assert.Same(LawnDeployRosterSnapshot.Empty, LawnDeployRosterSnapshotHolder.ResolveOrEmpty());

        var snap = new LawnDeployRosterSnapshot(new[] { Entry("a") }, 1);
        LawnDeployRosterSnapshotHolder.BeginMatch(snap);
        Assert.Same(snap, LawnDeployRosterSnapshotHolder.ResolveOrEmpty());
    }

    [Fact]
    public void Cache_miss_builds_Empty_without_a_seeded_roster()
    {
        var snap = LawnDeployRosterSessionCache.BuildFromSessionCache();
        Assert.Same(LawnDeployRosterSnapshot.Empty, snap);
        Assert.True(LawnDeployRosterSessionCache.LastBuildWasCacheMiss);
    }

    [Fact]
    public void Cache_poll_after_apply_returns_the_saved_eligible_list()
    {
        LawnDeployRosterSessionCache.Apply(new[] { Entry("a"), Entry("b", "legionzombie") });

        var snap = LawnDeployRosterSessionCache.BuildFromSessionCache();
        Assert.Equal(2, snap.Eligible.Count);
        Assert.Contains(snap.Eligible, e => e.InstanceId == "a" && e.SpeciesId == "abyssswordstar");
        Assert.Contains(snap.Eligible, e => e.InstanceId == "b" && e.SpeciesId == "legionzombie");
        Assert.False(LawnDeployRosterSessionCache.LastBuildWasCacheMiss);
    }

    /// <summary>A real player with zero eligible creatures is NOT a cache miss — the flag distinguishes
    /// "never fetched yet" from "fetched, and it's genuinely empty" (e.g. everything owned is the
    /// active Patron). Mirrors why the analogous commander-snapshot flag exists.</summary>
    [Fact]
    public void Cache_poll_after_apply_with_zero_eligible_is_not_a_cache_miss()
    {
        LawnDeployRosterSessionCache.Apply(Array.Empty<LawnDeployRosterEntry>());

        var snap = LawnDeployRosterSessionCache.BuildFromSessionCache();
        Assert.Empty(snap.Eligible);
        Assert.False(LawnDeployRosterSessionCache.LastBuildWasCacheMiss);
    }

    /// <summary>The active Patron never appears in <see cref="LawnDeployRosterEntry"/> — this is
    /// proven at the INJECTOR-SIDE caller (RpgClient.RefreshLawnDeployRosterCacheAsync's own
    /// `Where(!= patronInstanceId)` filter), not inside the cache/holder, which only stores whatever
    /// the caller resolved. This test documents that split so a future reader does not look for the
    /// exclusion here and conclude it is missing.</summary>
    [Fact]
    public void Cache_stores_exactly_what_the_caller_resolved_no_independent_filtering()
    {
        LawnDeployRosterSessionCache.Apply(new[] { Entry("patron-designated-elsewhere") });
        var snap = LawnDeployRosterSessionCache.BuildFromSessionCache();
        Assert.Single(snap.Eligible);
    }

    [Fact]
    public void Apply_increments_cache_revision_captured_in_snapshot()
    {
        LawnDeployRosterSessionCache.Apply(new[] { Entry("a") });
        var first = LawnDeployRosterSessionCache.BuildFromSessionCache();

        LawnDeployRosterSessionCache.Apply(new[] { Entry("a"), Entry("b") });
        var second = LawnDeployRosterSessionCache.BuildFromSessionCache();

        Assert.Equal(1, first.SnapshotRevision);
        Assert.Equal(2, second.SnapshotRevision);
    }

    [Fact]
    public void Mid_match_cache_change_does_not_alter_Current()
    {
        LawnDeployRosterSessionCache.Apply(new[] { Entry("a") });
        LawnDeployRosterSnapshotHolder.BeginMatch(LawnDeployRosterSessionCache.BuildFromSessionCache());

        var frozen = LawnDeployRosterSnapshotHolder.Current!;
        Assert.Single(frozen.Eligible);

        // A fusion completing mid-match adds a brand-new specimen to the cache.
        LawnDeployRosterSessionCache.Apply(new[] { Entry("a"), Entry("new-from-fusion") });

        Assert.Same(frozen, LawnDeployRosterSnapshotHolder.Current);
        Assert.Single(LawnDeployRosterSnapshotHolder.Current!.Eligible);
    }

    [Fact]
    public void Second_board_start_picks_up_the_refreshed_cache_the_first_did_not_see()
    {
        LawnDeployRosterSessionCache.Apply(new[] { Entry("a") });
        LawnDeployRosterSnapshotHolder.BeginMatch(LawnDeployRosterSessionCache.BuildFromSessionCache());
        Assert.Single(LawnDeployRosterSnapshotHolder.Current!.Eligible);
        LawnDeployRosterSnapshotHolder.EndMatch();

        // The "fusion completing mid-match" specimen from the test above is now visible — but only
        // because a NEW match started, never retroactively on the run that was already in progress.
        LawnDeployRosterSessionCache.Apply(new[] { Entry("a"), Entry("new-from-fusion") });
        LawnDeployRosterSnapshotHolder.BeginMatch(LawnDeployRosterSessionCache.BuildFromSessionCache());

        Assert.Equal(2, LawnDeployRosterSnapshotHolder.Current!.Eligible.Count);
    }

    [Fact]
    public void ObserveRosterFold_maps_current_snapshot_fields()
    {
        LawnDeployRosterSnapshotHolder.BeginMatch(new LawnDeployRosterSnapshot(new[] { Entry("a"), Entry("b") }, 7));
        var fold = LawnDeployRosterSnapshotHolder.ObserveRosterFold();

        Assert.NotNull(fold);
        Assert.Equal(2, fold!["eligibleCount"]);
        Assert.Equal(new[] { "a", "b" }, fold["eligibleInstanceIds"]);
        Assert.Equal(7L, fold["snapshotRevision"]);
    }

    [Fact]
    public void ObserveRosterFold_null_outside_a_match()
    {
        Assert.Null(LawnDeployRosterSnapshotHolder.ObserveRosterFold());
    }

    /// <summary>Mirrors MatchCommanderSnapshotTests.cs's own Host_mirror_* shape — reproduces
    /// MatchHost.Apply's board.start/board.end sequencing for the roster holder specifically, since the
    /// real MatchHost lives in the Injector project and cannot be constructed here.</summary>
    static void HostApplyWithSnapshot(bool isStart, bool isEnd)
    {
        if (isEnd)
            LawnDeployRosterSnapshotHolder.EndMatch();

        if (isStart)
        {
            var snapshot = LawnDeployRosterSessionCache.BuildFromSessionCache();
            LawnDeployRosterSnapshotHolder.BeginMatch(snapshot);
        }
    }

    [Fact]
    public void Host_mirror_end_paths_clear_holder()
    {
        LawnDeployRosterSessionCache.Apply(new[] { Entry("a") });
        HostApplyWithSnapshot(isStart: true, isEnd: false);
        Assert.NotNull(LawnDeployRosterSnapshotHolder.Current);

        HostApplyWithSnapshot(isStart: false, isEnd: true);
        Assert.Null(LawnDeployRosterSnapshotHolder.Current);
    }
}
