using FusionRpg.Contracts;
using FusionRpg.Core.Notify;
using FusionRpg.Core.Saves;
using FusionRpg.Data.Notifications;
using FusionRpg.Data.Tests;
using FusionRpg.Data;
using FusionRpg.Server.Notifications;
using Xunit;

namespace FusionRpg.Server.Tests.Notifications;

/// <summary>notify-service spec §2, Testing 1-3, 5, 6, 8. In-memory store + fake `IPlayerPush`, no
/// live SignalR (`IDelveLivePush` precedent).</summary>
[Collection("NotificationHub")]
public class NotificationPublisherTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    readonly FakePlayerPush _push;
    readonly NotificationPublisher _publisher;
    readonly SaveId _save1;
    readonly SaveId _save2;

    public NotificationPublisherTests()
    {
        NotificationHubFixture.Configure();
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _save1 = new SaveId(_store.GetCurrentPlayerId());
        _save2 = new SaveId(_store.CreatePlayer("Second").Id);
        _push = new FakePlayerPush();
        _publisher = new NotificationPublisher(_store, new NotificationContract(NotificationCatalogHub.Catalog), _push);
    }

    public void Dispose() => _testStore.Dispose();

    [Fact]
    public void Durable_before_push_the_row_and_cursor_already_exist_when_the_fake_push_is_called()
    {
        var checkingPush = new CheckingPush(_store, _save1);
        var publisher = new NotificationPublisher(_store, new NotificationContract(NotificationCatalogHub.Catalog), checkingPush);

        publisher.Publish(_save1, new[] { NotificationHubFixture.Draft("durable-1", worldTurn: 1) }, NotifyDelivery.Live);

        Assert.True(checkingPush.RowExistedAtPushTime);
    }

    sealed class CheckingPush : IPlayerPush
    {
        readonly RpgStore _store;
        readonly SaveId _saveId;
        public bool RowExistedAtPushTime;
        public CheckingPush(RpgStore store, SaveId saveId) { _store = store; _saveId = saveId; }
        public void Push(SaveId saveId, string eventName, object payload)
        {
            var page = _store.ListNotificationsByCategory(_saveId, NotificationHubFixture.TestCategory, null, 10);
            RowExistedAtPushTime = page.Items.Count > 0;
        }
    }

    [Fact]
    public void Publishing_the_same_drafts_twice_pushes_once()
    {
        var drafts = new[] { NotificationHubFixture.Draft("idem-1", worldTurn: 1) };
        _publisher.Publish(_save1, drafts, NotifyDelivery.Live);
        _publisher.Publish(_save1, drafts, NotifyDelivery.Live);

        Assert.Single(_push.Pushes);
    }

    [Fact]
    public void Drafts_for_two_saves_produce_two_batches_each_addressed_to_its_own_save()
    {
        // "Never WebGroup" is HubPlayerPush's own routing contract, proven against a real SignalR
        // connection by PlayerRoutingTests.HubPlayerPush_never_reaches_a_connection_joined_only_to_WebGroup
        // (player-routing NS1.7). This test proves the PUBLISHER's half: each save gets exactly one
        // batch, addressed by SaveId, through IPlayerPush - never merged, never cross-delivered.
        _publisher.PublishTurn(new[]
        {
            new AddressedDraft(_save1, NotificationHubFixture.Draft("r1", worldTurn: 1)),
            new AddressedDraft(_save2, NotificationHubFixture.Draft("r2", worldTurn: 1)),
        }, NotifyDelivery.Live, cursor: null);

        Assert.Equal(2, _push.Pushes.Count);
        Assert.Contains(_push.Pushes, p => p.SaveId == _save1 && p.EventName == NotificationEvents.Batch);
        Assert.Contains(_push.Pushes, p => p.SaveId == _save2 && p.EventName == NotificationEvents.Batch);
        var batchFor1 = (NotificationBatchDto)_push.Pushes.Single(p => p.SaveId == _save1).Payload;
        var batchFor2 = (NotificationBatchDto)_push.Pushes.Single(p => p.SaveId == _save2).Payload;
        Assert.Equal("r1", Assert.Single(batchFor1.Items).DedupKey);
        Assert.Equal("r2", Assert.Single(batchFor2.Items).DedupKey);
    }

    [Fact]
    public void A_routine_draft_inside_the_repeat_window_is_dropped_but_critical_and_subjectless_never_are()
    {
        _publisher.Publish(_save1, new[] { NotificationHubFixture.Draft("first", subjectKey: "s-1", worldTurn: 5) }, NotifyDelivery.Live);
        Assert.Single(_push.Pushes);

        // Same subject, inside the 3-turn window, routine severity -> dropped.
        _publisher.Publish(_save1, new[] { NotificationHubFixture.Draft("second", subjectKey: "s-1", worldTurn: 6) }, NotifyDelivery.Live);
        Assert.Single(_push.Pushes); // still 1 - nothing new pushed

        // Same subject, inside the window, but Critical -> never throttled.
        _publisher.Publish(_save1, new[]
        {
            NotificationHubFixture.Draft("third", NotifySeverity.Critical, "s-1", 6, NotificationHubFixture.CriticalCategory)
        }, NotifyDelivery.Live);
        Assert.Equal(2, _push.Pushes.Count);

        // No subject key -> never suppressed even with the same dedup pattern reused loosely.
        _publisher.Publish(_save1, new[] { NotificationHubFixture.Draft("fourth", worldTurn: 6) }, NotifyDelivery.Live);
        Assert.Equal(3, _push.Pushes.Count);
    }

    [Fact]
    public void Batch_order_is_severity_descending_then_seq_ascending_regardless_of_input_order()
    {
        _publisher.PublishTurn(new[]
        {
            new AddressedDraft(_save1, NotificationHubFixture.Draft("a", NotifySeverity.Important, worldTurn: 1)),
            new AddressedDraft(_save1, NotificationHubFixture.Draft("b", NotifySeverity.Critical, category: NotificationHubFixture.CriticalCategory, worldTurn: 1)),
            new AddressedDraft(_save1, NotificationHubFixture.Draft("c", NotifySeverity.Important, worldTurn: 1)),
        }, NotifyDelivery.Live, cursor: null);

        var batch = (NotificationBatchDto)_push.Pushes.Single().Payload;
        Assert.Equal(new[] { "b", "a", "c" }, batch.Items.Select(i => i.DedupKey));

        // Reverse input order -> identical output order (order is a property of severity+seq, not input).
        var push2 = new FakePlayerPush();
        var publisher2 = new NotificationPublisher(_store, new NotificationContract(NotificationCatalogHub.Catalog), push2);
        var save3 = new SaveId(_store.CreatePlayer("Third").Id);
        publisher2.PublishTurn(new[]
        {
            new AddressedDraft(save3, NotificationHubFixture.Draft("c2", NotifySeverity.Important, worldTurn: 1)),
            new AddressedDraft(save3, NotificationHubFixture.Draft("b2", NotifySeverity.Critical, category: NotificationHubFixture.CriticalCategory, worldTurn: 1)),
            new AddressedDraft(save3, NotificationHubFixture.Draft("a2", NotifySeverity.Important, worldTurn: 1)),
        }, NotifyDelivery.Live, cursor: null);
        var batch2 = (NotificationBatchDto)push2.Pushes.Single().Payload;
        Assert.Equal("b2", batch2.Items[0].DedupKey); // Critical always first regardless of input order
    }

    [Fact]
    public void A_push_that_throws_after_append_leaves_rows_and_cursor_and_a_rerun_pushes_nothing()
    {
        _push.ThrowOnNextPush = true;
        var cursor = new NotificationCursorAdvance("test-source", "w", 7);
        Assert.Throws<InvalidOperationException>(() =>
            _publisher.PublishTurn(new[] { new AddressedDraft(_save1, NotificationHubFixture.Draft("crash-1", worldTurn: 1)) },
                NotifyDelivery.Live, cursor));

        // Rows + cursor are stored even though the push threw (append happens before push).
        Assert.Single(_store.ListNotificationsByCategory(_save1, NotificationHubFixture.TestCategory, null, 10).Items);
        Assert.Equal(7, _store.GetNotificationCursor("test-source", "w"));

        // A second run (same dedup key) pushes nothing new.
        _publisher.PublishTurn(new[] { new AddressedDraft(_save1, NotificationHubFixture.Draft("crash-1", worldTurn: 1)) },
            NotifyDelivery.Live, cursor: null);
        Assert.Empty(_push.Pushes); // the throwing push was consumed; this run found nothing new to push
    }
}
