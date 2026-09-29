using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;

namespace FusionRpg.E2E.Tests;

[Collection("e2e")]
public class AlmanacSeedE2ETests : IAsyncLifetime
{
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    readonly HttpClient _http;
    public AlmanacSeedE2ETests(RpgApiFactory factory) => _http = factory.CreateClient();
    public async Task InitializeAsync()
    {
        (await _http.PostAsJsonAsync("/api/test/reset", new { })).EnsureSuccessStatusCode();
    }
    public Task DisposeAsync() => Task.CompletedTask;

    async Task PutDump(string side, int typeId, object fields)
    {
        using var content = new StringContent(JsonSerializer.Serialize(new { fields }), Encoding.UTF8, "application/json");
        (await _http.PutAsync($"/api/almanac/dump/{side}/{typeId}", content)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Rebuild_then_get_round_trips_through_the_real_server()
    {
        await PutDump("plant", 0, new Dictionary<string, string?>
        {
            ["name"] = "豌豆射手",
            ["enumName"] = "Peashooter",
            ["info"] = "发射豌豆。",
            ["cost"] = "花费：<color=red>100</color>\n冷却时间：<color=red>7.5秒</color>"
        });

        var rebuild = await _http.PostAsync("/api/almanac/seed/rebuild", null);
        rebuild.EnsureSuccessStatusCode();
        var summary = await rebuild.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.True(summary.GetProperty("built").GetInt32() >= 1);

        var dto = await _http.GetFromJsonAsync<JsonElement>("/api/almanac/seed/plant/0", Json);
        Assert.Equal("豌豆射手", dto.GetProperty("displayName").GetString());
        Assert.Equal("Peashooter", dto.GetProperty("typeName").GetString());
        Assert.Equal("parsed", dto.GetProperty("costStatus").GetString());
        Assert.Equal(100, dto.GetProperty("sunCost").GetInt32());
        Assert.Equal(7.5, dto.GetProperty("cooldownSec").GetDouble());

        var list = await _http.GetFromJsonAsync<JsonElement>("/api/almanac/seed?side=plant", Json);
        Assert.Contains(list.GetProperty("items").EnumerateArray(), i => i.GetProperty("typeId").GetInt32() == 0);
    }

    [Fact]
    public async Task Unknown_type_returns_404()
    {
        var resp = await _http.GetAsync("/api/almanac/seed/plant/999999");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Bad_side_rejected()
    {
        var resp = await _http.GetAsync("/api/almanac/seed/notaside/0");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Enrich_imports_checked_in_export_and_reports_unmatched()
    {
        await PutDump("plant", 0, new Dictionary<string, string?> { ["name"] = "Peashooter", ["enumName"] = "Peashooter" });
        (await _http.PostAsync("/api/almanac/seed/rebuild", null)).EnsureSuccessStatusCode();

        var enrich = await _http.PostAsync("/api/almanac/seed/enrich", null);
        enrich.EnsureSuccessStatusCode();
        var body = await enrich.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.True(body.GetProperty("matched").GetInt32() >= 1);
        Assert.True(body.GetProperty("unmatched").GetArrayLength() >= 0);

        var dto = await _http.GetFromJsonAsync<JsonElement>("/api/almanac/seed/plant/0", Json);
        Assert.True(dto.TryGetProperty("enrichment", out var enrichment));
        Assert.Equal("Basic Plant", enrichment.GetProperty("typeClass").GetString());
    }

    [Fact]
    public async Task Reset_clears_almanac_seed()
    {
        await PutDump("plant", 0, new Dictionary<string, string?> { ["name"] = "X" });
        (await _http.PostAsync("/api/almanac/seed/rebuild", null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync("/api/almanac/seed/plant/0")).StatusCode);

        (await _http.PostAsJsonAsync("/api/test/reset", new { })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/api/almanac/seed/plant/0")).StatusCode);
    }
}
