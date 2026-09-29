using FusionRpg.Core.Commanders;
using FusionRpg.Core.Match;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Aptitudes;
using Xunit;

namespace FusionRpg.Core.Tests.Commanders;

[Collection(CommanderDirectoryCollection.Name)]
public class MatchCommanderSnapshotTests
{
    public MatchCommanderSnapshotTests()
    {
        MatchCommanderSessionCache.Configure(ShippedCommanders.Directory);
        MatchCommanderSnapshotHolder.EndMatch();
        MatchCommanderSessionCache.ResetForTests();
    }

    static MatchCommanderSnapshot DaveSnapshot(long revision = 1) =>
        new(
            ShippedCommanders.Dave.StableId,
            ShippedCommanders.Directory.DisplayName(ShippedCommanders.Dave, playerName: null),
            "Might",
            "Might",
            AptitudeAllocation.Single(AllocationScope.Commander, "Might", 30),
            revision,
            revision);

    [Fact]
    public void BeginMatch_sets_Current_EndMatch_clears()
    {
        var snap = DaveSnapshot();
        MatchCommanderSnapshotHolder.BeginMatch(snap);
        Assert.Same(snap, MatchCommanderSnapshotHolder.Current);

        MatchCommanderSnapshotHolder.EndMatch();
        Assert.Null(MatchCommanderSnapshotHolder.Current);
    }

    [Fact]
    public void ResolveAllocation_returns_frozen_copy_during_match()
    {
        var frozen = AptitudeAllocation.Single(AllocationScope.Commander, "Might", 30);
        MatchCommanderSnapshotHolder.BeginMatch(DaveSnapshot() with { Allocation = frozen });

        var live = AptitudeAllocation.Single(AllocationScope.Commander, "Vigor", 99);
        Assert.Equal(30, MatchCommanderSnapshotHolder.ResolveAllocation(live).Total("Might"));
        Assert.Equal(0, MatchCommanderSnapshotHolder.ResolveAllocation(live).Total("Vigor"));

        MatchCommanderSnapshotHolder.EndMatch();
        Assert.Equal(99, MatchCommanderSnapshotHolder.ResolveAllocation(live).Total("Vigor"));
    }

    [Fact]
    public void Allocation_in_snapshot_is_independent_of_cache_mutation_after_freeze()
    {
        var alloc = AptitudeAllocation.Single(AllocationScope.Commander, "Might", 10);
        MatchCommanderSessionCache.Apply(
            ShippedCommanders.Dave.StableId,
            "Crazy Dave",
            null,
            null,
            alloc);
        MatchCommanderSnapshotHolder.BeginMatch(MatchCommanderSessionCache.BuildFromSessionCache());

        MatchCommanderSessionCache.Apply(
            ShippedCommanders.Dave.StableId,
            "Crazy Dave",
            null,
            null,
            AptitudeAllocation.Single(AllocationScope.Commander, "Vigor", 50));

        Assert.Equal(10, MatchCommanderSnapshotHolder.Current!.Allocation.Total("Might"));
        Assert.Equal(0, MatchCommanderSnapshotHolder.Current!.Allocation.Total("Vigor"));
    }

    [Fact]
    public void Cache_miss_builds_implicit_Dave_without_seeded_row()
    {
        MatchCommanderSessionCache.ResetForTests();
        var snap = MatchCommanderSessionCache.BuildFromSessionCache();

        Assert.True(MatchCommanderSessionCache.LastBuildUsedFallback);
        Assert.Equal(ShippedCommanders.Dave.StableId, snap.LeadingCommanderId);
        Assert.Null(snap.ActiveAuraId);
        Assert.Equal(AptitudeAllocation.Empty, snap.Allocation);
    }

    [Fact]
    public void Cache_poll_after_apply_returns_saved_default()
    {
        MatchCommanderSessionCache.Apply(
            ShippedCommanders.Dave.StableId,
            "Crazy Dave",
            "Might",
            "Might",
            AptitudeAllocation.Empty);

        var snap = MatchCommanderSessionCache.BuildFromSessionCache();
        Assert.False(MatchCommanderSessionCache.LastBuildUsedFallback);
        Assert.Equal(ShippedCommanders.Dave.StableId, snap.LeadingCommanderId);
        Assert.Equal("Might", snap.ActiveAuraId);
    }

    [Fact]
    public void Second_board_start_picks_up_refreshed_cache()
    {
        MatchCommanderSessionCache.Apply(
            ShippedCommanders.Dave.StableId,
            "Crazy Dave",
            null,
            null,
            AptitudeAllocation.Empty);
        MatchCommanderSnapshotHolder.BeginMatch(MatchCommanderSessionCache.BuildFromSessionCache());
        var firstId = MatchCommanderSnapshotHolder.Current!.LeadingCommanderId;
        MatchCommanderSnapshotHolder.EndMatch();

        MatchCommanderSessionCache.Apply(
            ShippedCommanders.Dave.StableId,
            "Crazy Dave",
            "Might",
            "Might",
            AptitudeAllocation.Single(AllocationScope.Commander, "Might", 5));
        MatchCommanderSnapshotHolder.BeginMatch(MatchCommanderSessionCache.BuildFromSessionCache());

        Assert.Equal(firstId, MatchCommanderSnapshotHolder.Current!.LeadingCommanderId);
        Assert.Equal("Might", MatchCommanderSnapshotHolder.Current!.ActiveAuraId);
        Assert.Equal(5, MatchCommanderSnapshotHolder.Current!.Allocation.Total("Might"));
    }

    [Fact]
    public void Mid_match_cache_change_does_not_alter_Current()
    {
        MatchCommanderSessionCache.Apply(
            ShippedCommanders.Dave.StableId,
            "Crazy Dave",
            null,
            null,
            AptitudeAllocation.Single(AllocationScope.Commander, "Might", 10));
        MatchCommanderSnapshotHolder.BeginMatch(MatchCommanderSessionCache.BuildFromSessionCache());

        var frozenId = MatchCommanderSnapshotHolder.Current!.LeadingCommanderId;
        MatchCommanderSessionCache.Apply(
            ShippedCommanders.Dave.StableId,
            "Different Display",
            "Might",
            "Might",
            AptitudeAllocation.Single(AllocationScope.Commander, "Vigor", 99));

        Assert.Equal(frozenId, MatchCommanderSnapshotHolder.Current!.LeadingCommanderId);
        Assert.Null(MatchCommanderSnapshotHolder.Current!.ActiveAuraId);
        Assert.Equal(10, MatchCommanderSnapshotHolder.Current!.Allocation.Total("Might"));
    }

    [Fact]
    public void Host_mirror_auto_end_before_start_clears_holder()
    {
        var rt = new MatchRuntime();
        HostApplyWithSnapshot(rt, "board.start", new Dictionary<string, object> { ["matchKey"] = "m-a" });
        Assert.NotNull(MatchCommanderSnapshotHolder.Current);

        HostApplyWithSnapshot(rt, "board.start", new Dictionary<string, object> { ["matchKey"] = "m-b" });

        Assert.Equal("m-b", rt.MatchKey);
        Assert.NotNull(MatchCommanderSnapshotHolder.Current);
    }

    [Fact]
    public void Host_mirror_end_paths_clear_holder()
    {
        var rt = new MatchRuntime();
        HostApplyWithSnapshot(rt, "board.start", new Dictionary<string, object> { ["matchKey"] = "m-end" });
        Assert.NotNull(MatchCommanderSnapshotHolder.Current);

        HostApplyWithSnapshot(rt, "board.end");
        Assert.Null(MatchCommanderSnapshotHolder.Current);

        HostApplyWithSnapshot(rt, "board.start", new Dictionary<string, object> { ["matchKey"] = "m-res" });
        Assert.NotNull(MatchCommanderSnapshotHolder.Current);

        HostApplyWithSnapshot(rt, "match.result");
        Assert.Null(MatchCommanderSnapshotHolder.Current);
    }

    [Fact]
    public void Host_mirror_snapshot_exists_before_notify_match_start_ordering()
    {
        var rt = new MatchRuntime();
        var notifyFired = false;
        MatchCommanderSnapshot? atNotify = null;

        void SimulateStart()
        {
            if (rt.Phase is not MatchPhase.Idle)
            {
                MatchCommanderSnapshotHolder.EndMatch();
                rt.Apply("board.end");
            }

            rt.Apply("board.start", new Dictionary<string, object> { ["matchKey"] = "m-order" });
            var snapshot = MatchCommanderSessionCache.BuildFromSessionCache();
            MatchCommanderSnapshotHolder.BeginMatch(snapshot);
            atNotify = MatchCommanderSnapshotHolder.Current;
            notifyFired = true;
        }

        SimulateStart();
        Assert.True(notifyFired);
        Assert.NotNull(atNotify);
        Assert.Equal(ShippedCommanders.Dave.StableId, atNotify!.LeadingCommanderId);
    }

    [Fact]
    public void ObserveCommanderFold_maps_current_snapshot_fields()
    {
        MatchCommanderSnapshotHolder.BeginMatch(DaveSnapshot());
        var fold = MatchCommanderSnapshotHolder.ObserveCommanderFold();

        Assert.NotNull(fold);
        Assert.Equal(ShippedCommanders.Dave.StableId, fold!["leadingCommanderId"]);
        Assert.Equal(ShippedCommanders.Directory.DisplayName(ShippedCommanders.Dave, playerName: null), fold["leadingCommanderDisplayName"]);
        Assert.Equal("Might", fold["activeAuraId"]);
        Assert.Equal("Might", fold["activeAuraDisplayName"]);
    }

    [Fact]
    public void ObserveCommanderFold_null_aura_fields()
    {
        MatchCommanderSnapshotHolder.BeginMatch(DaveSnapshot() with { ActiveAuraId = null, ActiveAuraDisplayName = null });
        var fold = MatchCommanderSnapshotHolder.ObserveCommanderFold();

        Assert.NotNull(fold);
        Assert.Null(fold!["activeAuraId"]);
        Assert.Null(fold["activeAuraDisplayName"]);
    }

    [Fact]
    public void Apply_increments_cache_revision_captured_in_snapshot()
    {
        MatchCommanderSessionCache.Apply(
            ShippedCommanders.Dave.StableId,
            "Crazy Dave",
            null,
            null,
            AptitudeAllocation.Empty);
        var first = MatchCommanderSessionCache.BuildFromSessionCache();

        MatchCommanderSessionCache.Apply(
            ShippedCommanders.Dave.StableId,
            "Crazy Dave",
            "Might",
            "Might",
            AptitudeAllocation.Single(AllocationScope.Commander, "Might", 5));

        var second = MatchCommanderSessionCache.BuildFromSessionCache();
        Assert.Equal(1, first.AllocationRevision);
        Assert.Equal(2, second.AllocationRevision);
        Assert.Equal(2, second.SnapshotRevision);
    }

    [Fact]
    public void Apply_rejects_invalid_commander_id_without_corrupting_cache()
    {
        MatchCommanderSessionCache.Apply(
            ShippedCommanders.Dave.StableId,
            "Crazy Dave",
            "Might",
            "Might",
            AptitudeAllocation.Empty);
        var before = MatchCommanderSessionCache.BuildFromSessionCache();

        MatchCommanderSessionCache.Apply(
            "not-a-commander",
            "Bad",
            null,
            null,
            AptitudeAllocation.Single(AllocationScope.Commander, "Vigor", 99));

        var after = MatchCommanderSessionCache.BuildFromSessionCache();
        Assert.Equal(before.LeadingCommanderId, after.LeadingCommanderId);
        Assert.Equal(before.ActiveAuraId, after.ActiveAuraId);
        Assert.Equal(before.AllocationRevision, after.AllocationRevision);
    }

    [Fact]
    public void CommanderAllocationSource_syncs_at_match_edges()
    {
        var live = AptitudeAllocation.Single(AllocationScope.Commander, "Vigor", 99);
        var frozen = AptitudeAllocation.Single(AllocationScope.Commander, "Might", 30);
        var source = new CommanderAllocationSource(() =>
            MatchCommanderSnapshotHolder.ResolveAllocation(live));

        source.Refresh();
        Assert.Equal(99, source.Resolve(new StatContext()).Total("Vigor"));

        MatchCommanderSnapshotHolder.BeginMatch(DaveSnapshot() with { Allocation = frozen });
        source.Refresh();
        Assert.Equal(30, source.Resolve(new StatContext()).Total("Might"));
        Assert.Equal(0, source.Resolve(new StatContext()).Total("Vigor"));

        MatchCommanderSnapshotHolder.EndMatch();
        source.Refresh();
        Assert.Equal(99, source.Resolve(new StatContext()).Total("Vigor"));
    }

    static void HostApplyWithSnapshot(MatchRuntime rt, string kind, IReadOnlyDictionary<string, object>? payload = null)
    {
        var isStart = string.Equals(kind, "board.start", StringComparison.OrdinalIgnoreCase);
        var isEnd = string.Equals(kind, "board.end", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "match.result", StringComparison.OrdinalIgnoreCase);

        if (isStart && rt.Phase is not MatchPhase.Idle)
        {
            MatchCommanderSnapshotHolder.EndMatch();
            rt.Apply("board.end");
        }

        rt.Apply(kind, payload);

        if (isEnd)
            MatchCommanderSnapshotHolder.EndMatch();

        if (isStart)
        {
            var snapshot = MatchCommanderSessionCache.BuildFromSessionCache();
            MatchCommanderSnapshotHolder.BeginMatch(snapshot);
        }
    }
}
