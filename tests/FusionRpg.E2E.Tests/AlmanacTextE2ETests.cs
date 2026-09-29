using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;

namespace FusionRpg.E2E.Tests;

[Collection("e2e")]
public class AlmanacTextE2ETests : IAsyncLifetime
{
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    readonly HttpClient _http;
    public AlmanacTextE2ETests(RpgApiFactory factory) => _http = factory.CreateClient();
    public async Task InitializeAsync()
    {
        (await _http.PostAsJsonAsync("/api/test/reset", new { })).EnsureSuccessStatusCode();
    }
    public Task DisposeAsync() => Task.CompletedTask;
    async Task FlushIngest() => (await _http.GetAsync("/api/test/snapshot")).EnsureSuccessStatusCode();
    async Task PutDump(string side, int typeId, object payload)
    {
        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        (await _http.PutAsync($"/api/almanac/dump/{side}/{typeId}", content)).EnsureSuccessStatusCode();
    }
    [Fact]
    public async Task Put_then_get_almanac_text_dump()
    {
        var payload = new
        {
            fields = new Dictionary<string, string?>
            {
                ["name"] = "Peashooter",
                ["info"] = "Shoots peas",
                ["cost"] = "100",
                ["introduce"] = "Classic plant"
            },
            sources = new Dictionary<string, string>
            {
                ["name"] = "PlantInfo.name",
                ["info"] = "PlantInfo.info"
            }
        };
        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var put = await _http.PutAsync("/api/almanac/dump/plant/0", content);
        put.EnsureSuccessStatusCode();
        var body = await put.Content.ReadFromJsonAsync<PutBody>(Json);
        Assert.True(body!.Created);
        Assert.Equal(4, body.FieldCount);
        var dump = await _http.GetFromJsonAsync<DumpBody>("/api/almanac/dump/plant/0", Json);
        Assert.Equal("Peashooter", dump!.Fields["name"]);
        Assert.Equal("Shoots peas", dump.Fields["info"]);
        var list = await _http.GetFromJsonAsync<ListBody>("/api/almanac/dump?side=plant", Json);
        Assert.Contains(list!.Items, i => i.TypeId == 0);
        using var content2 = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var again = await _http.PutAsync("/api/almanac/dump/plant/0", content2);
        var body2 = await again.Content.ReadFromJsonAsync<PutBody>(Json);
        Assert.False(body2!.Created);
    }
    [Fact]
    public async Task Bad_side_rejected()
    {
        using var content = new StringContent("""{"fields":{"a":"b"}}""", Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.PutAsync("/api/almanac/dump/player/1", content)).StatusCode);
    }
    [Fact]
    public async Task Almanac_fields_promote_onto_progression_actor()
    {
        var playerId = await CurrentPlayerId();
        (await _http.PostAsJsonAsync("/api/test/seed-rpg-progression-demo", new { playerId })).EnsureSuccessStatusCode();
        await PutDump("plant", 0, new
        {
            fields = new Dictionary<string, string?>
            {
                ["name"] = "豌豆射手",
                ["displayName"] = "豌豆射手",
                ["enumName"] = "Peashooter",
                ["info"] = "发射豌豆。<color=red>20</color>",
                ["cost"] = "花费：<color=red>100</color>"
            },
            sources = new Dictionary<string, string>
            {
                ["name"] = "PlantInfo.name",
                ["info"] = "PlantInfo.info",
                ["cost"] = "PlantInfo.cost"
            }
        });
        var plant = await _http.GetFromJsonAsync<JsonElement>($"/api/rpg/progression/{playerId}/plant/0", Json);
        Assert.Equal("豌豆射手", plant.GetProperty("displayName").GetString());
        Assert.Equal("Peashooter", plant.GetProperty("typeName").GetString());
        Assert.Equal("发射豌豆。<color=red>20</color>", plant.GetProperty("almanacInfo").GetString());
        Assert.Equal("花费：<color=red>100</color>", plant.GetProperty("almanacCost").GetString());
    }
    [Fact]
    public async Task Case_mismatched_field_keys_still_promote()
    {
        var playerId = await CurrentPlayerId();
        (await _http.PostAsJsonAsync("/api/test/seed-rpg-progression-demo", new { playerId })).EnsureSuccessStatusCode();
        // Bypass ignore-case client dicts: serialize PascalCase keys literally.
        const string raw = """
            {"fields":{"Name":"豌豆射手","Info":"伤害。<color=red>20</color>","EnumName":"Peashooter"},"sources":{"Name":"PlantInfo.name"}}
            """;
        using var content = new StringContent(raw, Encoding.UTF8, "application/json");
        (await _http.PutAsync("/api/almanac/dump/plant/0", content)).EnsureSuccessStatusCode();
        var plant = await _http.GetFromJsonAsync<JsonElement>($"/api/rpg/progression/{playerId}/plant/0", Json);
        Assert.Equal("豌豆射手", plant.GetProperty("displayName").GetString());
        Assert.Equal("Peashooter", plant.GetProperty("typeName").GetString());
        Assert.Equal("伤害。<color=red>20</color>", plant.GetProperty("almanacInfo").GetString());
    }
    [Fact]
    public async Task Zombie_introduce_promotes_onto_actor()
    {
        var playerId = await CurrentPlayerId();
        (await _http.PostAsJsonAsync("/api/test/seed-rpg-progression-demo", new { playerId })).EnsureSuccessStatusCode();
        await PutDump("zombie", 1, new
        {
            fields = new Dictionary<string, string?>
            {
                ["name"] = "旗帜僵尸",
                ["enumName"] = "FlagZombie",
                ["info"] = "摇旗。",
                ["introduce"] = "<color=#3D1400>他迷恋旗帜。</color>"
            }
        });
        var zombie = await _http.GetFromJsonAsync<JsonElement>($"/api/rpg/progression/{playerId}/zombie/1", Json);
        Assert.Equal("旗帜僵尸", zombie.GetProperty("displayName").GetString());
        Assert.Equal("FlagZombie", zombie.GetProperty("typeName").GetString());
        Assert.Equal("摇旗。", zombie.GetProperty("almanacInfo").GetString());
        Assert.Equal("<color=#3D1400>他迷恋旗帜。</color>", zombie.GetProperty("almanacIntroduce").GetString());
    }
    [Fact]
    public async Task Summary_topPlants_include_promoted_almanac()
    {
        var playerId = await CurrentPlayerId();
        (await _http.PostAsJsonAsync("/api/test/seed-rpg-progression-demo", new { playerId })).EnsureSuccessStatusCode();
        await PutDump("plant", 0, new
        {
            fields = new Dictionary<string, string?>
            {
                ["name"] = "豌豆射手",
                ["enumName"] = "Peashooter",
                ["info"] = "第一道防线。"
            }
        });
        var summary = await _http.GetFromJsonAsync<JsonElement>($"/api/rpg/progression/{playerId}/summary", Json);
        var tops = summary.GetProperty("topPlants").EnumerateArray().ToList();
        Assert.Contains(tops, p =>
            p.GetProperty("typeId").GetInt32() == 0 &&
            p.GetProperty("displayName").GetString() == "豌豆射手" &&
            p.GetProperty("almanacInfo").GetString() == "第一道防线。");
    }
    [Fact]
    public async Task Almanac_names_survive_later_english_spawn()
    {
        await PutDump("plant", 0, new
        {
            fields = new Dictionary<string, string?>
            {
                ["name"] = "豌豆射手",
                ["enumName"] = "Peashooter",
                ["info"] = "info"
            }
        });
        var types1 = await _http.GetFromJsonAsync<JsonElement>("/api/types?side=plant", Json);
        var pea1 = types1.GetProperty("items").EnumerateArray()
            .First(t => t.GetProperty("type").GetInt32() == 0);
        Assert.Equal("豌豆射手", pea1.GetProperty("displayName").GetString());
        Assert.Equal("Peashooter", pea1.GetProperty("typeName").GetString());
        var matchKey = Guid.NewGuid().ToString("N");
        (await _http.PostAsJsonAsync("/api/events", new
        {
            events = new object[]
            {
                new { kind = "board.start", matchKey, t = DateTime.UtcNow.ToString("o"), payload = new { } },
                new
                {
                    kind = "plant.spawn",
                    matchKey,
                    t = DateTime.UtcNow.AddMilliseconds(1).ToString("o"),
                    payload = new
                    {
                        ptr = "PX",
                        type = 0,
                        typeName = "PeashooterEN",
                        displayName = "PeashooterEN",
                        hpBase = 300,
                        maxHpBase = 300
                    }
                }
            }
        })).EnsureSuccessStatusCode();
        await FlushIngest();
        var types2 = await _http.GetFromJsonAsync<JsonElement>("/api/types?side=plant", Json);
        var pea2 = types2.GetProperty("items").EnumerateArray()
            .First(t => t.GetProperty("type").GetInt32() == 0);
        Assert.Equal("豌豆射手", pea2.GetProperty("displayName").GetString());
        Assert.Equal("Peashooter", pea2.GetProperty("typeName").GetString());
        var playerId = await CurrentPlayerId();
        (await _http.PostAsJsonAsync("/api/test/seed-rpg-progression-demo", new { playerId })).EnsureSuccessStatusCode();
        var plant = await _http.GetFromJsonAsync<JsonElement>($"/api/rpg/progression/{playerId}/plant/0", Json);
        Assert.Equal("豌豆射手", plant.GetProperty("displayName").GetString());
    }
    [Fact]
    public async Task Reset_clears_almanac_dump()
    {
        await PutDump("plant", 0, new
        {
            fields = new Dictionary<string, string?> { ["name"] = "豌豆射手", ["info"] = "x" }
        });
        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync("/api/almanac/dump/plant/0")).StatusCode);
        (await _http.PostAsJsonAsync("/api/test/reset", new { })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/api/almanac/dump/plant/0")).StatusCode);
    }
    async Task<long> CurrentPlayerId()
    {
        var players = await _http.GetFromJsonAsync<JsonElement>("/api/players", Json);
        return players.GetProperty("currentPlayerId").GetInt64();
    }
    sealed class PutBody
    {
        public bool Created { get; set; }
        public int FieldCount { get; set; }
    }
    sealed class DumpBody
    {
        public Dictionary<string, string?> Fields { get; set; } = new();
    }
    sealed class ListBody
    {
        public List<Item> Items { get; set; } = new();
        public sealed class Item
        {
            public int TypeId { get; set; }
        }
    }
}
