using FusionRpg.Contracts;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// Wave B pollution guards (audit 2026-08-21): webrpg-1 events flow through the same ingest
/// but must leave every pvzrh surface untouched — types catalog, metrics, almanac type XP,
/// capture retention.
///
/// <para><c>DiskSemantics</c>: <c>Closed_web_runs_are_exempt_from_capture_archiving</c> calls the
/// archive entry point <c>CompactAfterRunClosed</c>, which refuses the memory plan, on a
/// <c>DataTestStore.CreateFileBacked()</c> store (docs/contributing/testing-standard.md R2), so the
/// default profile skips the class and it runs at <c>full</c>/nightly/gate.</para>
/// </summary>
[Trait("Category", "DiskSemantics")]
public class WebGameIsolationTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public WebGameIsolationTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose() => _testStore.Dispose();

    EventEnvelope Ev(string game, string kind, string matchKey, object payload) => new()
    {
        T = DateTime.UtcNow.ToString("o"),
        Game = game,
        Kind = kind,
        MatchKey = matchKey,
        Payload = payload
    };

    void PlayMatch(string game, string matchKey, int kills, string result, string ptrPrefix)
        => PlayMatch(_store, game, matchKey, kills, result, ptrPrefix);

    void PlayMatch(RpgStore store, string game, string matchKey, int kills, string result, string ptrPrefix)
    {
        var events = new List<EventEnvelope> { Ev(game, "board.start", matchKey, new { levelName = "iso" }) };
        for (var i = 0; i < kills; i++)
        {
            events.Add(Ev(game, "zombie.spawn", matchKey, new { ptr = $"{ptrPrefix}{i}", type = 0, typeName = "TestZombie", stats = new { hp = 100 } }));
            events.Add(Ev(game, "zombie.die", matchKey, new { ptr = $"{ptrPrefix}{i}", type = 0 }));
        }

        events.Add(Ev(game, "match.result", matchKey, new { result }));
        events.Add(Ev(game, "board.end", matchKey, new { levelName = "iso" }));
        store.InsertEvents(events);
    }

    [Fact]
    public void Runs_are_stamped_with_their_game_profile()
    {
        PlayMatch(RpgConstants.GameId, Guid.NewGuid().ToString("N"), 0, "victory", "pz");
        PlayMatch(RpgConstants.GameIdWebRpg, Guid.NewGuid().ToString("N"), 0, "victory", "wb");
        var runs = _store.ListRuns();
        Assert.Contains(runs, r => r.Game == RpgConstants.GameId);
        Assert.Contains(runs, r => r.Game == RpgConstants.GameIdWebRpg);
    }

    [Fact]
    public void Web_events_leave_types_catalog_and_metrics_untouched()
    {
        // Baseline: one real PvZ match creates the catalog row and bumps metrics.
        PlayMatch(RpgConstants.GameId, Guid.NewGuid().ToString("N"), 2, "victory", "pz");
        var typeRow = _store.ListTypes("zombie").Single(t => t.Type == 0);
        var seenBefore = typeRow.SeenCount;
        var killedBefore = typeRow.KilledCount;
        var metricsBefore = _store.ListMetrics();
        var spawnedBefore = metricsBefore.Single(m => m.Name == "zombies_spawned").Value;
        var killedMetricBefore = metricsBefore.Single(m => m.Name == "zombies_killed").Value;
        var runsStartedBefore = metricsBefore.Single(m => m.Name == "runs_started").Value;

        // A web battle with the same type id: catalog + metrics must be byte-identical after.
        PlayMatch(RpgConstants.GameIdWebRpg, Guid.NewGuid().ToString("N"), 5, "victory", "web");

        var typeAfter = _store.ListTypes("zombie").Single(t => t.Type == 0);
        Assert.Equal(seenBefore, typeAfter.SeenCount);
        Assert.Equal(killedBefore, typeAfter.KilledCount);
        var metricsAfter = _store.ListMetrics();
        Assert.Equal(spawnedBefore, metricsAfter.Single(m => m.Name == "zombies_spawned").Value);
        Assert.Equal(killedMetricBefore, metricsAfter.Single(m => m.Name == "zombies_killed").Value);
        Assert.Equal(runsStartedBefore, metricsAfter.Single(m => m.Name == "runs_started").Value);
    }

    [Fact]
    public void Web_kills_earn_souls_and_player_xp_but_never_almanac_type_xp()
    {
        PlayMatch(RpgConstants.GameIdWebRpg, Guid.NewGuid().ToString("N"), 3, "victory", "web");

        Assert.True(_store.GetSoulBalance(1).Balance > 0); // one economy — web earns Souls
        var playerXp = _store.ListRpgXpLedger(1, kind: "player", typeId: null, reason: null, limit: 100);
        Assert.NotNull(playerXp);
        Assert.NotEmpty(playerXp!.Items);
        var zombieXp = _store.ListRpgXpLedger(1, kind: "zombie", typeId: null, reason: null, limit: 100);
        Assert.True(zombieXp == null || zombieXp.Items.Count == 0,
            "web battles must not level PvZ almanac zombie actors");
    }

    [Fact]
    public void Closed_web_runs_are_exempt_from_capture_archiving()
    {
        // This case's subject is the filesystem archive: it calls CompactAfterRunClosed (an archive
        // entry point that throws on a memory store) and asserts NO capture files appear. So it uses
        // the leak-proof file-backed helper while the rest of the class stays in memory.
        using var file = DataTestStore.CreateFileBacked();
        var store = file.Store;

        // Well past KeepLastN=50 in web runs alone — none may archive, and no files may appear.
        for (var i = 0; i < 55; i++)
            PlayMatch(store, RpgConstants.GameIdWebRpg, Guid.NewGuid().ToString("N"), 1, "victory", $"w{i}-");
        store.CompactAfterRunClosed(null);

        var runs = store.ListRuns();
        Assert.DoesNotContain(runs, r => r.Game == RpgConstants.GameIdWebRpg && !string.IsNullOrEmpty(r.ArchiveUri));
        var archiveDir = Path.Combine(file.DataDir!, "archive");
        var captureFiles = Directory.Exists(archiveDir)
            ? Directory.GetFiles(archiveDir, "capture-run-*.sqlite")
            : Array.Empty<string>();
        Assert.Empty(captureFiles);
    }
}
