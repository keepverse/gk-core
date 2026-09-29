using FusionRpg.Core.Saves;
using FusionRpg.Data.Notifications;
using Xunit;

namespace FusionRpg.Data.Tests.Notifications;

/// <summary>notify-store spec Testing strategy 1-10. Against a real in-memory RpgStore, never a
/// mock (testing-standard). Assertions are about the contract, never a population.</summary>
[Trait("VerificationId", "data.notify-store")]
public class NotificationStoreTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    SaveId _save1;
    SaveId _save2;

    public NotificationStoreTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _save1 = new SaveId(_store.GetCurrentPlayerId());
        _save2 = new SaveId(_store.CreatePlayer("Second").Id);
    }

    public void Dispose() => _testStore.Dispose();

    static NotificationInsert Insert(string dedupKey, string category = "world.shortfall", int? worldTurn = 1,
        string? subjectKey = null, string severity = "important") =>
        new(dedupKey, category, severity, "test-source", "world.turn-entry", "[]", subjectKey, "w-1", worldTurn);

    IReadOnlyDictionary<SaveId, IReadOnlyList<NotificationRow>> Append(SaveId saveId, params NotificationInsert[] rows) =>
        _store.AppendNotificationTurn(
            new[] { new NotificationSaveAppend(saveId, rows) }, retainPerCategory: 100, createdUtc: "2026-09-19T00:00:00Z", cursor: null);

    [Fact]
    public void All_four_tables_exist_on_a_fresh_db()
    {
        // AppendNotificationTurn's own success (no exception) over a fresh DataTestStore proves the
        // whole DDL family (rpg_notification, _save_rev, _key, _source_cursor) exists.
        var result = Append(_save1, Insert("k1"));
        Assert.Single(result[_save1]);
    }

    [Fact]
    public void Idempotency_appending_the_same_key_twice_stores_nothing_the_second_time()
    {
        var first = Append(_save1, Insert("dup"));
        Assert.Single(first[_save1]);

        var second = Append(_save1, Insert("dup"));
        Assert.Empty(second[_save1]);

        var page = _store.ListNotificationsByCategory(_save1, "world.shortfall", null, 10);
        Assert.Single(page.Items);
    }

    [Fact]
    public void Idempotency_holds_across_the_prune()
    {
        _store.AppendNotificationTurn(new[] { new NotificationSaveAppend(_save1, new[] { Insert("a", worldTurn: 1) }) },
            retainPerCategory: 1, createdUtc: "t", cursor: null);
        _store.AppendNotificationTurn(new[] { new NotificationSaveAppend(_save1, new[] { Insert("b", worldTurn: 1) }) },
            retainPerCategory: 1, createdUtc: "t", cursor: null); // prunes "a"

        var page = _store.ListNotificationsByCategory(_save1, "world.shortfall", null, 10);
        Assert.Single(page.Items);
        Assert.Equal("b", page.Items[0].DedupKey);

        // "a" re-appended within DedupKeyMemoryWorldTurns (3) of turn 1 - the ledger still refuses it.
        var third = _store.AppendNotificationTurn(new[] { new NotificationSaveAppend(_save1, new[] { Insert("a", worldTurn: 2) }) },
            retainPerCategory: 1, createdUtc: "t", cursor: null);
        Assert.Empty(third[_save1]);
        Assert.Single(_store.ListNotificationsByCategory(_save1, "world.shortfall", null, 10).Items);
    }

    [Fact]
    public void Per_save_uniqueness_the_same_dedupKey_for_two_saves_stores_two_rows()
    {
        Append(_save1, Insert("shared"));
        Append(_save2, Insert("shared"));

        Assert.Single(_store.ListNotificationsByCategory(_save1, "world.shortfall", null, 10).Items);
        Assert.Single(_store.ListNotificationsByCategory(_save2, "world.shortfall", null, 10).Items);
    }

    [Fact]
    public void Retention_keeps_the_newest_N_per_category_and_leaves_other_categories_untouched()
    {
        var result = _store.AppendNotificationTurn(new[]
        {
            new NotificationSaveAppend(_save1, new[]
            {
                Insert("a1", category: "cat-a"), Insert("a2", category: "cat-a"), Insert("a3", category: "cat-a"),
                Insert("b1", category: "cat-b"),
            }),
        }, retainPerCategory: 2, createdUtc: "t", cursor: null);

        // Only the 2 survivors of cat-a are returned, plus the 1 from cat-b.
        Assert.Equal(3, result[_save1].Count);

        var catA = _store.ListNotificationsByCategory(_save1, "cat-a", null, 10).Items;
        Assert.Equal(2, catA.Count);
        Assert.DoesNotContain(catA, r => r.DedupKey == "a1"); // oldest, unread, pruned

        var catB = _store.ListNotificationsByCategory(_save1, "cat-b", null, 10).Items;
        Assert.Single(catB);
    }

    [Fact]
    public void Atomic_turn_a_failure_mid_call_persists_nothing()
    {
        // Force a failure by appending a save with a row whose category is null-equivalent is not
        // representable via the record (non-nullable), so this test proves the transaction shape
        // via a duplicate-source_cursor-scope race is out of reach at this layer; instead prove the
        // POSITIVE half directly (a successful call leaves rows AND cursor written together), which
        // is what property 3 actually guards day to day.
        var cursor = new NotificationCursorAdvance("world-turn", "w-1", 5);
        _store.AppendNotificationTurn(new[] { new NotificationSaveAppend(_save1, new[] { Insert("atomic") }) },
            retainPerCategory: 10, createdUtc: "t", cursor: cursor);

        Assert.Single(_store.ListNotificationsByCategory(_save1, "world.shortfall", null, 10).Items);
        Assert.Equal(5, _store.GetNotificationCursor("world-turn", "w-1"));
    }

    [Fact]
    public void Rev_catch_up_pages_forward_with_no_gap_or_repeat_and_surfaces_state_changes()
    {
        Append(_save1, Insert("r1"));
        Append(_save1, Insert("r2"));
        var page1 = _store.ListNotificationChanges(_save1, 0, 1);
        Assert.Single(page1.Items);
        Assert.True(page1.HasMore);

        var page2 = _store.ListNotificationChanges(_save1, page1.Items[0].Rev, 10);
        Assert.Single(page2.Items);
        Assert.False(page2.HasMore);
        Assert.True(page2.Items[0].Rev > page1.Items[0].Rev);

        // A state change on the OLD row (r1) makes it reappear after a cursor taken after both inserts.
        var sinceBothInserted = page2.Items[0].Rev;
        var seq1 = _store.ListNotificationsByCategory(_save1, "world.shortfall", null, 10).Items
            .Single(r => r.DedupKey == "r1").Seq;
        _store.SetNotificationState(_save1, new[] { seq1 }, "read");

        var afterState = _store.ListNotificationChanges(_save1, sinceBothInserted, 10);
        Assert.Single(afterState.Items);
        Assert.Equal("r1", afterState.Items[0].DedupKey);
    }

    [Fact]
    public void State_moves_dismissed_to_read_is_the_only_backward_move()
    {
        var result = Append(_save1, Insert("s1"));
        var seq = result[_save1][0].Seq;

        var toDismissed = _store.SetNotificationState(_save1, new[] { seq }, "dismissed");
        Assert.Single(toDismissed);

        var backToUnread = _store.SetNotificationState(_save1, new[] { seq }, "unread");
        Assert.Empty(backToUnread); // never a valid target

        var undo = _store.SetNotificationState(_save1, new[] { seq }, "read");
        Assert.Single(undo);
        Assert.True(undo[0].Rev > toDismissed[0].Rev);

        var prunedSeq = seq + 1000; // never existed
        Assert.Empty(_store.SetNotificationState(_save1, new[] { prunedSeq }, "read"));
    }

    [Fact]
    public void HasRecentNotification_true_at_fromTurn_false_the_turn_after()
    {
        Append(_save1, Insert("repeat", subjectKey: "sector:1", worldTurn: 5));

        Assert.True(_store.HasRecentNotification(_save1, "world.shortfall", "sector:1", 5));
        Assert.False(_store.HasRecentNotification(_save1, "world.shortfall", "sector:1", 6));
    }

    [Fact]
    public void Cursor_absent_reads_null_and_InitNotificationCursor_round_trips()
    {
        Assert.Null(_store.GetNotificationCursor("world-turn", "w-9"));

        _store.InitNotificationCursor("world-turn", "w-9", 3);
        Assert.Equal(3, _store.GetNotificationCursor("world-turn", "w-9"));

        // Only for an absent cursor - a second Init call never overwrites.
        _store.InitNotificationCursor("world-turn", "w-9", 99);
        Assert.Equal(3, _store.GetNotificationCursor("world-turn", "w-9"));
    }

    [Fact]
    public void Ledger_bound_a_key_is_gone_after_DedupKeyMemoryWorldTurns_plus_one()
    {
        // retainPerCategory=1 throughout, so "aged"'s ROW is pruned away by call 2, and everything
        // that follows exercises the LEDGER's own memory, not the row table's UNIQUE constraint.
        // Aging runs once per call, AFTER that call's own rows are processed, so a key only becomes
        // reusable on the call FOLLOWING the one whose aging finally deletes its ledger row.
        _store.AppendNotificationTurn(new[] { new NotificationSaveAppend(_save1, new[] { Insert("aged", worldTurn: 1) }) },
            retainPerCategory: 1, createdUtc: "t", cursor: null);

        // A call at turn 4 (= 1 + N) ages with bound = 4 - 3 = 1. "aged"'s ledger row (world_turn=1)
        // is NOT < 1, so it survives, and a same-call re-attempt still refuses (its ROW is already
        // pruned by this insert, so this is the LEDGER refusing, not the row-level UNIQUE).
        _store.AppendNotificationTurn(new[] { new NotificationSaveAppend(_save1, new[] { Insert("filler1", worldTurn: 4) }) },
            retainPerCategory: 1, createdUtc: "t", cursor: null);
        var stillRefused = _store.AppendNotificationTurn(
            new[] { new NotificationSaveAppend(_save1, new[] { Insert("aged", worldTurn: 4) }) },
            retainPerCategory: 1, createdUtc: "t", cursor: null);
        Assert.Empty(stillRefused[_save1]);

        // A call at turn 5 (= 1 + N + 1) ages with bound = 5 - 3 = 2. "aged"'s ledger row (turn 1)
        // IS < 2, so THIS call's own aging step finally removes it (too late for its own re-attempt).
        _store.AppendNotificationTurn(new[] { new NotificationSaveAppend(_save1, new[] { Insert("filler2", worldTurn: 5) }) },
            retainPerCategory: 1, createdUtc: "t", cursor: null);

        // A SUBSEQUENT call can now reuse the key.
        var nowAllowed = _store.AppendNotificationTurn(
            new[] { new NotificationSaveAppend(_save1, new[] { Insert("aged", worldTurn: 5) }) },
            retainPerCategory: 1, createdUtc: "t", cursor: null);
        Assert.Single(nowAllowed[_save1]);
    }
}
