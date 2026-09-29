using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace FusionRpg.E2E.Tests;

[Collection("e2e")]
public class PvzStatsE2ETests : IAsyncLifetime
{
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    readonly HttpClient _http;

    public PvzStatsE2ETests(RpgApiFactory factory) => _http = factory.CreateClient();

    public async Task InitializeAsync()
    {
        (await _http.PostAsJsonAsync("/api/test/reset", new { })).EnsureSuccessStatusCode();
        // Drain any leftover injector inbox from prior tests.
        _ = await _http.GetFromJsonAsync<JsonElement>("/api/cheats/commands/pending", Json);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    async Task<long> CurrentPlayerId()
    {
        var players = await _http.GetFromJsonAsync<JsonElement>("/api/players", Json);
        return players.GetProperty("currentPlayerId").GetInt64();
    }

    static bool PendingHasReload(JsonElement pending) =>
        pending.GetProperty("items").EnumerateArray()
            .Any(i => i.GetProperty("name").GetString() == "pvz.stats.reload");

    [Fact]
    public async Task Seed_demo_pairs_hp_and_maxHp_then_withdraw_bumps_once()
    {
        var seed = await _http.PostAsJsonAsync("/api/test/seed-pvz-stats-demo", new { });
        seed.EnsureSuccessStatusCode();
        var sheet = await seed.Content.ReadFromJsonAsync<JsonElement>(Json);
        var rev0 = sheet.GetProperty("revision").GetInt64();
        var channels = sheet.GetProperty("channels").EnumerateArray().ToList();
        var hp = channels.First(c => c.GetProperty("channel").GetString() == "hp");
        var maxHp = channels.First(c => c.GetProperty("channel").GetString() == "maxHp");
        Assert.Equal(5, hp.GetProperty("final").GetDouble());
        Assert.Equal(5, maxHp.GetProperty("final").GetDouble());
        Assert.Equal(2, hp.GetProperty("sourceCount").GetInt32());

        var playerId = sheet.GetProperty("playerId").GetInt64();
        var detail = await _http.GetFromJsonAsync<JsonElement>($"/api/pvz-stats/{playerId}/channels/hp", Json);
        Assert.Equal(2, detail.GetProperty("contributions").GetArrayLength());
        Assert.Equal(rev0, detail.GetProperty("revision").GetInt64());

        var withdraw = await _http.PostAsJsonAsync($"/api/pvz-stats/{playerId}/modifiers/withdraw", new
        {
            sourceKind = "item",
            sourceId = "demo-curse"
        });
        withdraw.EnsureSuccessStatusCode();
        var after = await withdraw.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(rev0 + 1, after.GetProperty("revision").GetInt64());
        var hpAfter = after.GetProperty("channels").EnumerateArray().First(c => c.GetProperty("channel").GetString() == "hp");
        Assert.Equal(10, hpAfter.GetProperty("final").GetDouble());
        Assert.Equal(1, hpAfter.GetProperty("sourceCount").GetInt32());
    }

    [Fact]
    public async Task Seed_enqueues_pvz_stats_reload()
    {
        (await _http.PostAsJsonAsync("/api/test/seed-pvz-stats-demo", new { })).EnsureSuccessStatusCode();
        var pending = await _http.GetFromJsonAsync<JsonElement>("/api/cheats/commands/pending", Json);
        Assert.True(PendingHasReload(pending));
    }

    [Fact]
    public async Task Upsert_withdraw_reset_enqueue_reload()
    {
        var playerId = await CurrentPlayerId();
        (await _http.PostAsJsonAsync($"/api/pvz-stats/{playerId}/modifiers/upsert", new
        {
            pluginId = "rpg.item",
            sourceKind = "item",
            sourceId = "solo",
            channel = "atk",
            op = "Flat",
            value = 3,
            enabled = true
        })).EnsureSuccessStatusCode();
        Assert.True(PendingHasReload(await _http.GetFromJsonAsync<JsonElement>("/api/cheats/commands/pending", Json)));

        (await _http.PostAsJsonAsync($"/api/pvz-stats/{playerId}/modifiers/withdraw", new
        {
            sourceKind = "item",
            sourceId = "solo"
        })).EnsureSuccessStatusCode();
        Assert.True(PendingHasReload(await _http.GetFromJsonAsync<JsonElement>("/api/cheats/commands/pending", Json)));

        (await _http.PostAsJsonAsync($"/api/pvz-stats/{playerId}/modifiers/upsert", new
        {
            pluginId = "rpg.item",
            sourceKind = "item",
            sourceId = "solo2",
            channel = "atk",
            op = "Flat",
            value = 1,
            enabled = true
        })).EnsureSuccessStatusCode();
        _ = await _http.GetFromJsonAsync<JsonElement>("/api/cheats/commands/pending", Json);

        (await _http.PostAsJsonAsync($"/api/pvz-stats/{playerId}/modifiers/reset", new { })).EnsureSuccessStatusCode();
        Assert.True(PendingHasReload(await _http.GetFromJsonAsync<JsonElement>("/api/cheats/commands/pending", Json)));
    }

    [Fact]
    public async Task Get_sheet_and_modifiers_never_bump_revision()
    {
        var playerId = await CurrentPlayerId();
        (await _http.PostAsJsonAsync("/api/test/seed-pvz-stats-demo", new { playerId })).EnsureSuccessStatusCode();
        var a = await _http.GetFromJsonAsync<JsonElement>($"/api/pvz-stats/{playerId}", Json);
        var b = await _http.GetFromJsonAsync<JsonElement>($"/api/pvz-stats/{playerId}", Json);
        Assert.Equal(a.GetProperty("revision").GetInt64(), b.GetProperty("revision").GetInt64());
        var mods = await _http.GetFromJsonAsync<JsonElement>($"/api/pvz-stats/{playerId}/modifiers", Json);
        Assert.Equal(a.GetProperty("revision").GetInt64(), mods.GetProperty("revision").GetInt64());
        var ch = await _http.GetFromJsonAsync<JsonElement>($"/api/pvz-stats/{playerId}/channels/hp", Json);
        Assert.Equal(a.GetProperty("revision").GetInt64(), ch.GetProperty("revision").GetInt64());
    }

    [Fact]
    public async Task After_upsert_sheet_rev_matches_modifiers_rev()
    {
        var playerId = await CurrentPlayerId();
        (await _http.PostAsJsonAsync($"/api/pvz-stats/{playerId}/modifiers/upsert", new
        {
            pluginId = "rpg.item",
            sourceKind = "item",
            sourceId = "sync",
            channel = "defense",
            op = "Flat",
            value = 1,
            enabled = true
        })).EnsureSuccessStatusCode();
        var sheet = await _http.GetFromJsonAsync<JsonElement>($"/api/pvz-stats/{playerId}", Json);
        var mods = await _http.GetFromJsonAsync<JsonElement>($"/api/pvz-stats/{playerId}/modifiers", Json);
        Assert.Equal(sheet.GetProperty("revision").GetInt64(), mods.GetProperty("revision").GetInt64());
    }

    [Fact]
    public async Task Get_missing_player_returns_404()
    {
        Assert.Equal(System.Net.HttpStatusCode.NotFound,
            (await _http.GetAsync("/api/pvz-stats/999999")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.NotFound,
            (await _http.GetAsync("/api/pvz-stats/999999/modifiers")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.NotFound,
            (await _http.GetAsync("/api/pvz-stats/999999/channels/hp")).StatusCode);
    }

    [Fact]
    public async Task Withdraw_empty_body_returns_400()
    {
        var playerId = await CurrentPlayerId();
        var res = await _http.PostAsJsonAsync($"/api/pvz-stats/{playerId}/modifiers/withdraw", new { });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Upsert_unknown_channel_returns_400()
    {
        var playerId = await CurrentPlayerId();
        var res = await _http.PostAsJsonAsync($"/api/pvz-stats/{playerId}/modifiers/upsert", new
        {
            pluginId = "rpg.item",
            sourceKind = "item",
            sourceId = "x",
            channel = "mana",
            op = "Flat",
            value = 1,
            enabled = true
        });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Upsert_enabled_false_omitted_from_sheet_listed_in_modifiers()
    {
        var playerId = await CurrentPlayerId();
        (await _http.PostAsJsonAsync($"/api/pvz-stats/{playerId}/modifiers/upsert", new
        {
            pluginId = "rpg.item",
            sourceKind = "item",
            sourceId = "off",
            channel = "atk",
            op = "Flat",
            value = 9,
            enabled = false
        })).EnsureSuccessStatusCode();
        var sheet = await _http.GetFromJsonAsync<JsonElement>($"/api/pvz-stats/{playerId}", Json);
        Assert.DoesNotContain(sheet.GetProperty("channels").EnumerateArray(),
            c => c.GetProperty("channel").GetString() == "atk");
        var mods = await _http.GetFromJsonAsync<JsonElement>($"/api/pvz-stats/{playerId}/modifiers", Json);
        Assert.Contains(mods.GetProperty("modifiers").EnumerateArray(),
            m => m.GetProperty("sourceId").GetString() == "off" && !m.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task CreatePlayer_starts_at_revision_zero()
    {
        var created = await _http.PostAsJsonAsync("/api/players", new { name = "PvzFresh" });
        created.EnsureSuccessStatusCode();
        var player = await created.Content.ReadFromJsonAsync<JsonElement>(Json);
        var id = player.GetProperty("id").GetInt64();
        var sheet = await _http.GetFromJsonAsync<JsonElement>($"/api/pvz-stats/{id}", Json);
        Assert.Equal(0, sheet.GetProperty("revision").GetInt64());
    }

    [Fact]
    public async Task Reset_all_clears_pvz_tables()
    {
        (await _http.PostAsJsonAsync("/api/test/seed-pvz-stats-demo", new { })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/test/reset", new { })).EnsureSuccessStatusCode();
        var players = await _http.GetFromJsonAsync<JsonElement>("/api/players", Json);
        var playerId = players.GetProperty("currentPlayerId").GetInt64();
        var sheet = await _http.GetFromJsonAsync<JsonElement>($"/api/pvz-stats/{playerId}", Json);
        Assert.Equal(0, sheet.GetProperty("revision").GetInt64());
        Assert.Equal(0, sheet.GetProperty("channels").GetArrayLength());
    }

    [Fact]
    public async Task Upsert_then_reset_clears_channels()
    {
        var playerId = await CurrentPlayerId();
        var upsert = await _http.PostAsJsonAsync($"/api/pvz-stats/{playerId}/modifiers/upsert", new
        {
            pluginId = "rpg.item",
            sourceKind = "item",
            sourceId = "solo",
            channel = "atk",
            op = "Flat",
            value = 3,
            enabled = true,
            detailJson = """{"label":"Solo"}"""
        });
        upsert.EnsureSuccessStatusCode();
        var sheet = await _http.GetFromJsonAsync<JsonElement>($"/api/pvz-stats/{playerId}", Json);
        Assert.Contains(sheet.GetProperty("channels").EnumerateArray(), c => c.GetProperty("channel").GetString() == "atk");

        var reset = await _http.PostAsJsonAsync($"/api/pvz-stats/{playerId}/modifiers/reset", new { });
        reset.EnsureSuccessStatusCode();
        var empty = await reset.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(0, empty.GetProperty("channels").GetArrayLength());
    }

    [Fact]
    public async Task Channel_detail_contributions_match_ssot()
    {
        var playerId = await CurrentPlayerId();
        (await _http.PostAsJsonAsync("/api/test/seed-pvz-stats-demo", new { playerId })).EnsureSuccessStatusCode();
        var detail = await _http.GetFromJsonAsync<JsonElement>($"/api/pvz-stats/{playerId}/channels/HP", Json);
        Assert.Equal("hp", detail.GetProperty("channel").GetString());
        Assert.Equal(5, detail.GetProperty("final").GetDouble());
        var mods = await _http.GetFromJsonAsync<JsonElement>($"/api/pvz-stats/{playerId}/modifiers", Json);
        var hpMods = mods.GetProperty("modifiers").EnumerateArray()
            .Where(m => m.GetProperty("channel").GetString() == "hp" && m.GetProperty("enabled").GetBoolean())
            .ToList();
        Assert.Equal(hpMods.Count, detail.GetProperty("contributions").GetArrayLength());
    }
}
