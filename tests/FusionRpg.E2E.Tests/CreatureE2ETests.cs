using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace FusionRpg.E2E.Tests;

[Collection("e2e")]
public class CreatureE2ETests : IAsyncLifetime
{
    readonly HttpClient _http;

    public CreatureE2ETests(RpgApiFactory factory)
    {
        _http = factory.CreateClient();
    }

    public async Task InitializeAsync()
    {
        var r = await _http.PostAsJsonAsync("/api/test/reset", new { });
        r.EnsureSuccessStatusCode();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Catalog_serves_generated_species_and_traits()
    {
        var doc = await _http.GetFromJsonAsync<JsonElement>("/api/creatures/catalog");
        var species = doc.GetProperty("species").EnumerateArray().ToList();
        Assert.True(species.Count >= 16, $"expected ≥16 species, got {species.Count}");
        Assert.Contains(species, s => s.GetProperty("elementPrimary").GetString() == "light");
        Assert.Contains(species, s => s.GetProperty("elementPrimary").GetString() == "dark");
        Assert.Contains(species, s => s.GetProperty("deployMode").GetString() == "hypno-ally");
        Assert.Contains(species, s => s.GetProperty("captureOnly").GetBoolean());
        var traits = doc.GetProperty("traits").EnumerateArray().ToList();
        Assert.True(traits.Count >= 10);
    }

    [Fact]
    public async Task Roster_and_codex_start_empty_and_404_for_unknown_player()
    {
        var roster = await _http.GetFromJsonAsync<JsonElement>("/api/creatures/1");
        Assert.Empty(roster.GetProperty("items").EnumerateArray());
        var codex = await _http.GetFromJsonAsync<JsonElement>("/api/creatures/1/codex");
        Assert.Empty(codex.GetProperty("entries").EnumerateArray());
        var missing = await _http.GetAsync("/api/creatures/424242");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Specimen_edit_endpoints_404_on_unknown_instance()
    {
        var nick = await _http.PostAsJsonAsync("/api/creatures/specimen/nope/nickname", new { nickname = "X" });
        Assert.Equal(HttpStatusCode.NotFound, nick.StatusCode);
        var locked = await _http.PostAsJsonAsync("/api/creatures/specimen/nope/lock", new { locked = true });
        Assert.Equal(HttpStatusCode.NotFound, locked.StatusCode);
    }
}
