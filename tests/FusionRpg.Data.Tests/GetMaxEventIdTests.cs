using FusionRpg.Contracts;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>aura-skill live-lawn-quick-start: `RpgStore.GetMaxEventId` is the in-process tip-of-log
/// read a debug-orchestration endpoint needs before triggering new events (mirrors what the live-test
/// scripts approximate externally via a binary search over HTTP paging).</summary>
public class GetMaxEventIdTests : IDisposable
{
    readonly List<DataTestStore> _stores = new();

    public void Dispose()
    {
        foreach (var s in _stores)
            s.Dispose();
    }

    RpgStore NewStore()
    {
        var test = DataTestStore.Create();
        _stores.Add(test);
        return test.Store;
    }

    [Fact]
    public void Empty_log_returns_zero()
    {
        var store = NewStore();
        Assert.Equal(0, store.GetMaxEventId());
    }

    [Fact]
    public void Returns_the_id_of_the_most_recently_inserted_event()
    {
        var store = NewStore();
        var id1 = store.InsertEvent(new EventEnvelope { T = DateTime.UtcNow.ToString("o"), Kind = "test.one" });
        Assert.Equal(id1, store.GetMaxEventId());

        var id2 = store.InsertEvent(new EventEnvelope { T = DateTime.UtcNow.ToString("o"), Kind = "test.two" });
        Assert.True(id2 > id1);
        Assert.Equal(id2, store.GetMaxEventId());
    }

    [Fact]
    public void Matches_ListEvents_own_afterId_convention_for_reading_everything_since()
    {
        var store = NewStore();
        store.InsertEvent(new EventEnvelope { T = DateTime.UtcNow.ToString("o"), Kind = "test.before" });
        var tip = store.GetMaxEventId();
        store.InsertEvent(new EventEnvelope { T = DateTime.UtcNow.ToString("o"), Kind = "test.after" });

        var since = store.ListEvents(500, tip);
        var kinds = since.Select(e => e.Kind).ToList();
        Assert.DoesNotContain("test.before", kinds);
        Assert.Contains("test.after", kinds);
    }
}
