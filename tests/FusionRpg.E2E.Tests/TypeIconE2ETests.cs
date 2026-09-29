using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;

namespace FusionRpg.E2E.Tests;

// The file plan IS this class's subject (the two sqlite files and the absent icons mirror), so it runs
// on the file-bound host and is excluded from the default profile by the tag below.
[Collection("e2e-file")]
[Trait("Category", "DiskSemantics")]
public class TypeIconE2ETests : IAsyncLifetime
{
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    readonly HttpClient _http;
    readonly FileBackedRpgApiFactory _factory;

    static readonly byte[] TinyPng =
    {
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x02, 0x00, 0x00, 0x00, 0x90, 0x77, 0x53,
        0xDE, 0x00, 0x00, 0x00, 0x0C, 0x49, 0x44, 0x41, 0x54, 0x08, 0xD7, 0x63, 0xF8, 0xCF, 0xC0, 0x00,
        0x00, 0x00, 0x03, 0x00, 0x01, 0x00, 0x05, 0xFE, 0x02, 0xFE, 0xDC, 0xCC, 0x59, 0xE7, 0x00, 0x00,
        0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82
    };

    public TypeIconE2ETests(FileBackedRpgApiFactory factory)
    {
        _factory = factory;
        _http = factory.CreateClient();
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Put_dump_layers_then_list_and_get()
    {
        Assert.True(File.Exists(Path.Combine(_factory.DataDir, "rpg-hot.sqlite")));
        Assert.True(File.Exists(Path.Combine(_factory.DataDir, "rpg-media.sqlite")));

        var payload = new
        {
            layers = new object[]
            {
                new
                {
                    name = "image",
                    source = "Image:image",
                    width = 604,
                    height = 603,
                    pngBase64 = Convert.ToBase64String(TinyPng)
                },
                new
                {
                    name = "originalSprite",
                    source = "AlmanacCardUI.originalSprite",
                    width = 1,
                    height = 1,
                    pngBase64 = Convert.ToBase64String(TinyPng)
                }
            }
        };
        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var put = await _http.PutAsync("/api/icons/dump/plant/77", content);
        put.EnsureSuccessStatusCode();
        var body = await put.Content.ReadFromJsonAsync<DumpPut>(Json);
        Assert.NotNull(body);
        Assert.True(body!.Created);
        Assert.Equal(2, body.LayerCount);
        Assert.True(body.PortraitSet);

        var composed = await _http.GetAsync("/api/icons/plant/77.png");
        Assert.Equal(HttpStatusCode.OK, composed.StatusCode);

        var dump = await _http.GetFromJsonAsync<DumpGet>("/api/icons/dump/plant/77", Json);
        Assert.Equal(2, dump!.Layers.Count);

        var list = await _http.GetFromJsonAsync<DumpList>("/api/icons/dump?side=plant", Json);
        Assert.Contains(list!.Items, i => i.TypeId == 77);

        var layer = await _http.GetAsync("/api/icons/dump/plant/77/layer/originalSprite");
        Assert.Equal(HttpStatusCode.OK, layer.StatusCode);
        var bytes = await layer.Content.ReadAsByteArrayAsync();
        Assert.Equal(0x89, bytes[0]);

        // Icons live only in SQLite (no disk mirror under data/icons).
        Assert.False(Directory.Exists(Path.Combine(_factory.DataDir, "icons")));

        // Second put is not "created" but still upserts
        using var content2 = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var again = await _http.PutAsync("/api/icons/dump/plant/77", content2);
        again.EnsureSuccessStatusCode();
        var body2 = await again.Content.ReadFromJsonAsync<DumpPut>(Json);
        Assert.False(body2!.Created);
    }

    [Fact]
    public async Task Bad_side_rejected()
    {
        using var content = new StringContent("""{"layers":[]}""", Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.PutAsync("/api/icons/dump/player/1", content)).StatusCode);
    }

    sealed class DumpPut
    {
        public bool Created { get; set; }
        public int LayerCount { get; set; }
        public bool PortraitSet { get; set; }
        public string? Url { get; set; }
    }

    sealed class DumpGet
    {
        public List<Layer> Layers { get; set; } = new();
        public sealed class Layer
        {
            public string Name { get; set; } = "";
        }
    }

    sealed class DumpList
    {
        public List<Item> Items { get; set; } = new();
        public sealed class Item
        {
            public int TypeId { get; set; }
        }
    }
}
