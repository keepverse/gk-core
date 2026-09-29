using FusionRpg.Contracts;
using FusionRpg.Core.Activity;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// live-probe Task 20: <see cref="RpgStore.ListPvzActivityFacts"/> gains a descending <c>afterId</c> cursor (same shape
/// as the soul ledger). Without it a run with more facts than the page cap could never be read in full, and the
/// probe's soul-provenance step reported those kills as FactNotFound.
/// </summary>
public class PvzActivityFactsPagingTests : IDisposable
{
    readonly DataTestStore _testStore = DataTestStore.Create();
    RpgStore Store => _testStore.Store;

    public void Dispose() => _testStore.Dispose();

    [Fact]
    public void AfterId_pages_every_fact_newest_first_with_no_gap_or_overlap()
    {
        var player = Store.CreatePlayer("FactsPaging");
        for (var i = 0; i < 7; i++)
            Store.AppendPvzActivityFact(player.Id, new PvzActivityAppendRequest
            {
                Kind = PvzActivityKinds.ZombieKilled,
                PayloadJson = $$"""{"type":0,"i":{{i}}}""",
                DedupeKey = "page-zk-" + i
            });

        var seen = new List<long>();
        var after = 0L;
        for (var guard = 0; guard < 10; guard++)
        {
            var page = Store.ListPvzActivityFacts(player.Id, PvzActivityKinds.ZombieKilled, runId: null, limit: 3, afterId: after)!;
            if (page.Items.Count == 0) break;
            seen.AddRange(page.Items.Select(f => f.Id));
            if (page.Items.Count < 3) break;
            after = page.Items.Min(f => f.Id);
        }

        var all = Store.ListPvzActivityFacts(player.Id, PvzActivityKinds.ZombieKilled, runId: null, limit: 500)!.Items.Select(f => f.Id).ToList();
        Assert.Equal(all, seen);                       // same ids, same newest-first order
        Assert.Equal(seen.Count, seen.Distinct().Count()); // no overlap between pages
        Assert.True(seen.Count > 3, "the fixture must span more than one page");
    }

    [Fact]
    public void AfterId_zero_is_the_newest_page()
    {
        var player = Store.CreatePlayer("FactsPagingZero");
        for (var i = 0; i < 4; i++)
            Store.AppendPvzActivityFact(player.Id, new PvzActivityAppendRequest
            {
                Kind = PvzActivityKinds.ZombieKilled,
                PayloadJson = """{"type":0}""",
                DedupeKey = "zero-zk-" + i
            });

        var newest = Store.ListPvzActivityFacts(player.Id, PvzActivityKinds.ZombieKilled, runId: null, limit: 2)!.Items;
        var cursorZero = Store.ListPvzActivityFacts(player.Id, PvzActivityKinds.ZombieKilled, runId: null, limit: 2, afterId: 0)!.Items;
        Assert.Equal(newest.Select(f => f.Id), cursorZero.Select(f => f.Id));
    }
}
