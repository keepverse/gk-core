using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Expeditions;
using FusionRpg.Data.Sqlite;
using Xunit;

namespace FusionRpg.E2E.Tests;

/// <summary>
/// D4: the full expedition loop in SIM — dispatch → force-due → collect → battles through
/// WebMatchService + rewards through the one economy; collect is exactly-once.
/// </summary>
[Collection("e2e")]
public class ExpeditionE2ETests : IAsyncLifetime
{
    readonly RpgApiFactory _factory;
    readonly HttpClient _http;

    public ExpeditionE2ETests(RpgApiFactory factory)
    {
        _factory = factory;
        _http = factory.CreateClient();
    }

    public async Task InitializeAsync()
    {
        (await _http.PostAsJsonAsync("/api/test/reset", new { })).EnsureSuccessStatusCode();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    async Task<List<string>> SummonSquad(int take)
    {
        (await _http.PostAsJsonAsync("/api/test/seed-souls-demo?amount=2000", new { })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/creatures/summon", new
        {
            count = 10,
            correlationId = "exp-e2e-pull-" + Guid.NewGuid().ToString("N")
        })).EnsureSuccessStatusCode();
        var roster = await _http.GetFromJsonAsync<JsonElement>("/api/creatures/1");
        return roster.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("actor").GetProperty("instanceId").GetString()!)
            .Take(take).ToList();
    }

    async Task<long> Balance() =>
        (await _http.GetFromJsonAsync<JsonElement>("/api/souls/1")).GetProperty("balance").GetInt64();

    void Archive(long playerId)
    {
        using var store = _factory.OpenStore();
        using var db = SqliteConnectionFactory.Open(store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE players SET archived_utc = $t WHERE id = $p;";
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$p", playerId);
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task Full_loop_dispatch_force_due_collect()
    {
        var squad = await SummonSquad(2);
        var balanceBefore = await Balance();

        // Dispatch.
        var dispatch = await _http.PostAsJsonAsync("/api/expeditions/dispatch", new
        {
            correlationId = "exp-loop-1",
            tierId = "scout-30m",
            squad
        });
        dispatch.EnsureSuccessStatusCode();
        var row = await dispatch.Content.ReadFromJsonAsync<JsonElement>();
        var expeditionId = row.GetProperty("expedition").GetProperty("id").GetInt64();
        Assert.Equal("Dispatched", row.GetProperty("expedition").GetProperty("state").GetString());

        // Collect before due refuses.
        var early = await _http.PostAsJsonAsync($"/api/expeditions/{expeditionId}/collect", new { });
        Assert.False(early.IsSuccessStatusCode);

        // Make it due by MOVING THE CLOCK (RS3 increment 5b): the SIM timer rewind route is retired, so the
        // declaration the seam applies here is the same one a scenario's `clock.set` step makes.
        var result = await RpgApiFactory.WithClockAheadAsync(1860, async () =>
        {
            var collect = await _http.PostAsJsonAsync($"/api/expeditions/{expeditionId}/collect", new { });
            collect.EnsureSuccessStatusCode();
            return await collect.Content.ReadFromJsonAsync<JsonElement>();
        });

        Assert.Equal("Collected", result.GetProperty("state").GetString());
        var battles = result.GetProperty("battles").EnumerateArray().ToList();
        Assert.Single(battles); // scout-30m: one battle
        Assert.True(battles[0].GetProperty("runId").GetInt64() > 0);

        // Battles are real webrpg runs through the pipeline.
        var runs = await _http.GetFromJsonAsync<JsonElement>("/api/runs");
        var battleRun = runs.GetProperty("items").EnumerateArray()
            .Single(r => (r.GetProperty("matchKey").GetString() ?? "").StartsWith($"exp-{expeditionId}-"));
        Assert.Equal("webrpg-1", battleRun.GetProperty("game").GetString());

        // Any resolved battle earns through the one economy (victory or defeat both pay).
        Assert.True(await Balance() > balanceBefore);

        // The battle tick always drops a shard — E3a (species-gear-chain T31): the shard follows the
        // PLANNED WAVE's rung, not `isBoss` and not a pinned id. This assert used to pin
        // `shard.chaff` and went red on 2026-09-20 once the plan's rung moved (the shelf held
        // `shard.fused`), so the expectation is now derived through the SAME two calls the resolver
        // makes: `ExpeditionResolver.PlannedRungFor` over the planned wave's roster, then
        // `CreatureYieldTuningHub.ShardFor` against the shipped `creature-yield.v1.json` map.
        // `scout-30m`'s schedule is one non-boss battle on `rift-skirmish` (the resolver's own tier
        // chain) — the wave id is the roster's closed key, never a tuned value; `WaveCatalog` here is
        // the same host-configured instance the resolver read, so this is the exact plan.
        var plannedRung = ExpeditionResolver.PlannedRungFor(WaveCatalog.Get("rift-skirmish").Enemies);
        var expectedShard = CreatureYieldTuningHub.ShardFor(plannedRung);
        var materials = await _http.GetFromJsonAsync<JsonElement>("/api/expeditions/1/materials");
        Assert.Contains(materials.GetProperty("items").EnumerateArray(),
            m => m.GetProperty("materialId").GetString() == expectedShard);

        // Squad released: the expedition list shows Collected and specimens are free again.
        var list = await _http.GetFromJsonAsync<JsonElement>("/api/expeditions/1");
        Assert.Equal("Collected", list.GetProperty("items").EnumerateArray()
            .Single(e => e.GetProperty("id").GetInt64() == expeditionId)
            .GetProperty("state").GetString());
    }

    [Fact]
    public async Task Concurrent_collect_returns_the_same_manifest_and_pays_once()
    {
        var squad = await SummonSquad(2);
        var dispatch = await _http.PostAsJsonAsync("/api/expeditions/dispatch", new
        {
            correlationId = "exp-concurrent-1",
            tierId = "scout-30m",
            squad
        });
        dispatch.EnsureSuccessStatusCode();
        var expeditionId = (await dispatch.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("expedition").GetProperty("id").GetInt64();

        (await _http.PostAsJsonAsync("/api/test/expedition-collect-commit-barrier/2", new { }))
            .EnsureSuccessStatusCode();
        var responses = await RpgApiFactory.WithClockAheadAsync(1860, () => Task.WhenAll(
            _http.PostAsJsonAsync($"/api/expeditions/{expeditionId}/collect", new { }),
            _http.PostAsJsonAsync($"/api/expeditions/{expeditionId}/collect", new { })));

        Assert.All(responses, response => response.EnsureSuccessStatusCode());
        var first = await responses[0].Content.ReadAsStringAsync();
        var second = await responses[1].Content.ReadAsStringAsync();
        Assert.Equal(first, second);

        var balanceAfterCollect = await Balance();
        var rosterAfterCollect = (await _http.GetFromJsonAsync<JsonElement>("/api/creatures/1"))
            .GetProperty("items").EnumerateArray().Count();
        var materialsAfterCollect = await _http.GetFromJsonAsync<JsonElement>("/api/expeditions/1/materials");

        var replay = await _http.PostAsJsonAsync($"/api/expeditions/{expeditionId}/collect", new { });
        replay.EnsureSuccessStatusCode();
        Assert.Equal(first, await replay.Content.ReadAsStringAsync());
        Assert.Equal(balanceAfterCollect, await Balance());
        Assert.Equal(rosterAfterCollect, (await _http.GetFromJsonAsync<JsonElement>("/api/creatures/1"))
            .GetProperty("items").EnumerateArray().Count());
        Assert.Equal(
            materialsAfterCollect.GetRawText(),
            (await _http.GetFromJsonAsync<JsonElement>("/api/expeditions/1/materials")).GetRawText());
    }

    [Fact]
    public async Task Collect_replays_the_same_committed_manifest_after_a_post_commit_fault()
    {
        var squad = await SummonSquad(2);
        var dispatch = await _http.PostAsJsonAsync("/api/expeditions/dispatch", new
        {
            correlationId = "exp-post-commit-fault",
            tierId = "scout-30m",
            squad
        });
        dispatch.EnsureSuccessStatusCode();
        var expeditionId = (await dispatch.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("expedition").GetProperty("id").GetInt64();

        (await _http.PostAsJsonAsync("/api/test/expedition-collect-fault-after-commit", new { }))
            .EnsureSuccessStatusCode();
        var faulted = await RpgApiFactory.WithClockAheadAsync(1860, () =>
            _http.PostAsJsonAsync($"/api/expeditions/{expeditionId}/collect", new { }));
        Assert.Equal(HttpStatusCode.InternalServerError, faulted.StatusCode);

        var firstReplay = await _http.PostAsJsonAsync($"/api/expeditions/{expeditionId}/collect", new { });
        firstReplay.EnsureSuccessStatusCode();
        var first = await firstReplay.Content.ReadAsStringAsync();
        var balanceAfterReplay = await Balance();
        var rosterAfterReplay = (await _http.GetFromJsonAsync<JsonElement>("/api/creatures/1"))
            .GetProperty("items").EnumerateArray().Count();
        var materialsAfterReplay = await _http.GetFromJsonAsync<JsonElement>("/api/expeditions/1/materials");

        var secondReplay = await _http.PostAsJsonAsync($"/api/expeditions/{expeditionId}/collect", new { });
        secondReplay.EnsureSuccessStatusCode();

        Assert.Equal(first, await secondReplay.Content.ReadAsStringAsync());
        Assert.Equal(balanceAfterReplay, await Balance());
        Assert.Equal(rosterAfterReplay, (await _http.GetFromJsonAsync<JsonElement>("/api/creatures/1"))
            .GetProperty("items").EnumerateArray().Count());
        Assert.Equal(
            materialsAfterReplay.GetRawText(),
            (await _http.GetFromJsonAsync<JsonElement>("/api/expeditions/1/materials")).GetRawText());
    }

    [Fact]
    public async Task Instant_recall_closes_with_nothing()
    {
        var squad = await SummonSquad(1);
        var dispatch = await _http.PostAsJsonAsync("/api/expeditions/dispatch", new
        {
            correlationId = "exp-recall-1",
            tierId = "hunt-8h",
            squad
        });
        dispatch.EnsureSuccessStatusCode();
        var expeditionId = (await dispatch.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("expedition").GetProperty("id").GetInt64();
        var balanceBefore = await Balance();

        var recall = await _http.PostAsJsonAsync($"/api/expeditions/{expeditionId}/recall", new { });
        recall.EnsureSuccessStatusCode();
        var responseBody = await recall.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<JsonElement>(responseBody);
        Assert.Equal("Recalled", result.GetProperty("state").GetString());
        Assert.Empty(result.GetProperty("battles").EnumerateArray());
        Assert.Equal(balanceBefore, await Balance());

        var replay = await _http.PostAsJsonAsync($"/api/expeditions/{expeditionId}/recall", new { });
        replay.EnsureSuccessStatusCode();
        Assert.Equal(responseBody, await replay.Content.ReadAsStringAsync());
        Assert.Equal(balanceBefore, await Balance());
    }

    [Fact]
    public async Task Expedition_payloads_never_leak_the_sealed_seed()
    {
        var squad = await SummonSquad(1);
        var dispatch = await _http.PostAsJsonAsync("/api/expeditions/dispatch", new
        {
            correlationId = "exp-seed-leak",
            tierId = "scout-30m",
            squad
        });
        dispatch.EnsureSuccessStatusCode();
        var row = (await dispatch.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("expedition");
        Assert.False(row.TryGetProperty("seed", out _), "the sealed seed lets a client pre-read outcomes");
        Assert.False(row.TryGetProperty("correlationId", out _));
        Assert.False(row.TryGetProperty("squadJson", out _));

        var list = await _http.GetFromJsonAsync<JsonElement>("/api/expeditions/1");
        foreach (var item in list.GetProperty("items").EnumerateArray())
            Assert.False(item.TryGetProperty("seed", out _));
    }

    [Fact]
    public async Task Dispatch_replay_with_a_different_request_is_a_mismatch()
    {
        var squad = await SummonSquad(2);
        var first = await _http.PostAsJsonAsync("/api/expeditions/dispatch", new
        {
            correlationId = "exp-dup-req",
            tierId = "scout-30m",
            squad = new[] { squad[0] }
        });
        first.EnsureSuccessStatusCode();

        var mismatch = await _http.PostAsJsonAsync("/api/expeditions/dispatch", new
        {
            correlationId = "exp-dup-req",
            tierId = "scout-30m",
            squad = new[] { squad[1] } // different squad, same correlation
        });
        Assert.False(mismatch.IsSuccessStatusCode);
        Assert.Equal("correlation.mismatch",
            (await mismatch.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reason").GetString());

        // The true replay still works.
        var replay = await _http.PostAsJsonAsync("/api/expeditions/dispatch", new
        {
            correlationId = "exp-dup-req",
            tierId = "scout-30m",
            squad = new[] { squad[0] }
        });
        replay.EnsureSuccessStatusCode();
        Assert.True((await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("replayed").GetBoolean());
    }

    [Fact]
    public async Task Dispatch_refuses_bad_requests()
    {
        var squad = await SummonSquad(1);
        Assert.False((await _http.PostAsJsonAsync("/api/expeditions/dispatch", new
        {
            correlationId = "exp-bad-1",
            tierId = "no-such-tier",
            squad
        })).IsSuccessStatusCode);
        Assert.False((await _http.PostAsJsonAsync("/api/expeditions/dispatch", new
        {
            correlationId = "exp-bad-2",
            tierId = "scout-30m",
            squad = Array.Empty<string>()
        })).IsSuccessStatusCode);
    }

    [Fact]
    public async Task Archived_save_is_rejected_at_the_expedition_entry_boundary()
    {
        long archivedId;
        using (var store = _factory.OpenStore())
            archivedId = store.CreatePlayer("Archived Expedition Save").Id;
        Archive(archivedId);

        // The deliberately invalid tier/body would normally fail validation. The archived-save
        // contract must win before the request reaches expedition service/store work.
        var response = await _http.PostAsJsonAsync("/api/expeditions/dispatch", new
        {
            playerId = archivedId,
            correlationId = "exp-archived",
            tierId = "no-such-tier",
            squad = Array.Empty<string>()
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("save.archived",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reason").GetString());

        using var readback = _factory.OpenStore();
        Assert.Empty(readback.ListExpeditions(archivedId));
    }
}
