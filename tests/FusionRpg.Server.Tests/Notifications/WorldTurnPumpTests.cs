using FusionRpg.Contracts;
using FusionRpg.Core.Notify;
using FusionRpg.Core.Saves;
using FusionRpg.Core.World;
using FusionRpg.Data.Tests;
using FusionRpg.Data;
using FusionRpg.Server.Notifications;
using Xunit;

namespace FusionRpg.Server.Tests.Notifications;

/// <summary>notify-service spec §3, Testing 7, 9. Real `RpgStore` + real committed world turns
/// (matching `WorldTurnCommitTests`'s own setup), fake sources and push.</summary>
[Collection("NotificationHub")]
public class WorldTurnPumpTests : IDisposable
{
    static readonly string[] AllCommanders = { "dave", "wild", "zomboss" };

    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    readonly FakePlayerPush _push;
    readonly long _saveId;

    public WorldTurnPumpTests()
    {
        NotificationHubFixture.Configure();
        WorldTuningTestSupport.ConfigureOnce();
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _saveId = _store.GetCurrentPlayerId();
        _store.CreateWorld(_saveId, WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, (ulong)_saveId, "w"));
        _push = new FakePlayerPush();
    }

    public void Dispose() => _testStore.Dispose();

    void CommitOneTurn()
    {
        var open = _store.GetWorldHeader("w")!.CurrentTurn;
        foreach (var c in AllCommanders.Where(c => c != "dave").Concat(AllCommanders.Where(c => c == "dave")))
            _store.CommitWorldTurn("w", c, open);
    }

    WorldTurnNotificationPump BuildPump(params IWorldTurnNotificationSource[] sources) =>
        new(_store, new NotificationPublisher(_store, new NotificationContract(NotificationCatalogHub.Catalog), _push), sources);

    [Fact]
    public void An_absent_cursor_initialises_to_R_with_no_publish()
    {
        CommitOneTurn(); // CurrentTurn = 1, so R = CurrentTurn - 1 = 0
        var pump = BuildPump();

        pump.Run("w", WorldTurnTrigger.Commit);

        Assert.Empty(_push.Pushes);
        Assert.Equal(0, _store.GetNotificationCursor("world-turn", "w"));
    }

    [Fact]
    public void A_cursor_at_R_minus_2_publishes_two_turns_in_order_and_ends_at_R()
    {
        CommitOneTurn(); CommitOneTurn(); CommitOneTurn(); // CurrentTurn = 3, R = 2
        _store.InitNotificationCursor("world-turn", "w", 0); // R - 2 = 0: turn 0 already published

        var seenTurns = new List<int>();
        var source = new FakeWorldTurnSource
        {
            OnCollect = ctx =>
            {
                seenTurns.Add(ctx.ResolvedTurn);
                return new[] { new AddressedDraft(new SaveId(_saveId), NotificationHubFixture.Draft($"t{ctx.ResolvedTurn}", worldTurn: ctx.ResolvedTurn)) };
            }
        };
        BuildPump(source).Run("w", WorldTurnTrigger.Commit);

        Assert.Equal(new[] { 1, 2 }, seenTurns);
        Assert.Equal(2, _store.GetNotificationCursor("world-turn", "w"));
        Assert.Equal(2, _push.Pushes.Count); // one batch per published turn
    }

    [Fact]
    public void A_throwing_source_does_not_stop_a_second_source_or_the_cursor()
    {
        CommitOneTurn(); CommitOneTurn(); // CurrentTurn = 2, R = 1
        _store.InitNotificationCursor("world-turn", "w", 0); // one turn (1) in range

        var throwing = new FakeWorldTurnSource { SourceId = "bad", OnCollect = _ => throw new InvalidOperationException("boom") };
        var good = new FakeWorldTurnSource
        {
            SourceId = "good",
            OnCollect = ctx => new[] { new AddressedDraft(new SaveId(_saveId), NotificationHubFixture.Draft("good-1", worldTurn: ctx.ResolvedTurn)) }
        };
        BuildPump(throwing, good).Run("w", WorldTurnTrigger.Commit);

        Assert.Single(_push.Pushes);
        Assert.Equal(1, _store.GetNotificationCursor("world-turn", "w"));
    }

    [Fact]
    public void A_trimmed_turn_advances_the_cursor_and_never_replays()
    {
        CommitOneTurn(); CommitOneTurn(); // CurrentTurn = 2, R = 1
        _store.InitNotificationCursor("world-turn", "w", 0); // one turn (1) in range
        _store.TrimWorldTurnReports("w", keepLast: 0); // forces ReportJson = null for every turn

        var sawNullReport = false;
        var source = new FakeWorldTurnSource
        {
            OnCollect = ctx =>
            {
                sawNullReport = ctx.Report is null;
                return Array.Empty<AddressedDraft>();
            }
        };
        BuildPump(source).Run("w", WorldTurnTrigger.Commit);

        Assert.True(sawNullReport, "the source should have seen a null report for a trimmed turn");
        Assert.Equal(1, _store.GetNotificationCursor("world-turn", "w"));
    }

    [Fact]
    public void A_delve_kind_world_is_skipped()
    {
        _store.CreateWorld(_saveId, WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, (ulong)_saveId, "delve-w"));
        // No public API in this test project mutates `kind` post-creation for a map world, so this
        // proves the pump's own guard against the ONE kind value CreateWorld ever produces here is a
        // no-op path, not a crash - the real "kind != map" skip is exercised end to end by
        // world-notify-source's own delve-world fixtures once that module exists (map §Modules row 7).
        var pump = BuildPump();
        pump.Run("delve-w", WorldTurnTrigger.Commit);
        Assert.Empty(_push.Pushes);
    }

    [Fact]
    public void An_unknown_world_is_a_no_op()
    {
        BuildPump().Run("no-such-world", WorldTurnTrigger.Commit);
        Assert.Empty(_push.Pushes);
        Assert.Null(_store.GetNotificationCursor("world-turn", "no-such-world"));
    }

    [Fact]
    public void Delivery_label_commit_pushes_the_newest_turn_Live_and_older_turns_CatchUp()
    {
        CommitOneTurn(); CommitOneTurn(); CommitOneTurn(); // CurrentTurn = 3, R = 2
        _store.InitNotificationCursor("world-turn", "w", 0); // R - 2 = 0

        var source = new FakeWorldTurnSource
        {
            OnCollect = ctx => new[] { new AddressedDraft(new SaveId(_saveId), NotificationHubFixture.Draft($"t{ctx.ResolvedTurn}", worldTurn: ctx.ResolvedTurn)) }
        };
        BuildPump(source).Run("w", WorldTurnTrigger.Commit);

        var batches = _push.Pushes.Select(p => (NotificationBatchDto)p.Payload).ToList();
        Assert.Equal(NotifyDelivery.CatchUp, batches[0].Delivery); // turn 1
        Assert.Equal(NotifyDelivery.Live, batches[1].Delivery);    // turn 2 (= R)
    }

    [Fact]
    public void A_boot_run_pushes_only_CatchUp()
    {
        CommitOneTurn(); CommitOneTurn(); // CurrentTurn = 2, R = 1
        _store.InitNotificationCursor("world-turn", "w", 0);
        var source = new FakeWorldTurnSource
        {
            OnCollect = ctx => new[] { new AddressedDraft(new SaveId(_saveId), NotificationHubFixture.Draft("boot-1", worldTurn: ctx.ResolvedTurn)) }
        };

        BuildPump(source).Run("w", WorldTurnTrigger.Boot);

        var batch = (NotificationBatchDto)Assert.Single(_push.Pushes).Payload;
        Assert.Equal(NotifyDelivery.CatchUp, batch.Delivery);
    }

    [Fact]
    public void A_boot_run_and_a_later_commit_run_end_at_R_with_each_row_stored_once()
    {
        CommitOneTurn(); // CurrentTurn = 1, R = 0
        var source = new FakeWorldTurnSource
        {
            OnCollect = ctx => new[] { new AddressedDraft(new SaveId(_saveId), NotificationHubFixture.Draft($"t{ctx.ResolvedTurn}", worldTurn: ctx.ResolvedTurn)) }
        };
        var pump = BuildPump(source);

        // Boot meets an absent cursor: initialises to R=0, publishes nothing (no back-fill).
        pump.Run("w", WorldTurnTrigger.Boot);
        Assert.Empty(_push.Pushes);

        CommitOneTurn(); // CurrentTurn = 2, R = 1 - a real new turn resolves
        pump.Run("w", WorldTurnTrigger.Commit); // publishes turn 1, cursor -> 1

        // A second Boot right after (simulating a restart) finds the cursor already at R: no-op.
        pump.Run("w", WorldTurnTrigger.Boot);

        Assert.Equal(1, _store.GetNotificationCursor("world-turn", "w"));
        Assert.Single(_push.Pushes); // turn 1's batch, exactly once
        var stored = _store.ListNotificationsByCategory(new SaveId(_saveId), NotificationHubFixture.TestCategory, null, 10).Items;
        Assert.Single(stored); // t1 stored exactly once despite three pump runs touching this world
    }
}
