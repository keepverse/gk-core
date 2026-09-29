using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace FusionRpg.E2E.Tests;

/// <summary>
/// world-stage W17: `GET /api/world/catalog` — rules, not state. No world id, no viewer, no fog:
/// this must answer without ever creating a world.
/// </summary>
[Collection("e2e")]
public class WorldCatalogE2ETests
{
    readonly HttpClient _http;

    public WorldCatalogE2ETests(RpgApiFactory factory)
    {
        _http = factory.CreateClient();
    }

    [Fact]
    public async Task Answers_with_no_world_and_no_viewer()
    {
        var response = await _http.GetAsync("/api/world/catalog");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var catalog = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(catalog.GetProperty("structures").GetArrayLength() > 0);
        Assert.True(catalog.GetProperty("slotTypes").GetArrayLength() > 0);
        Assert.True(catalog.GetProperty("strengthBands").GetArrayLength() > 0);
        Assert.True(catalog.GetProperty("laneTypes").GetArrayLength() > 0);
    }

    [Fact]
    public async Task The_structure_cost_field_is_named_cost_not_costmilli_and_holds_whole_units()
    {
        var catalog = await _http.GetFromJsonAsync<JsonElement>("/api/world/catalog");
        var structure = catalog.GetProperty("structures").EnumerateArray().First();

        // The field is named `cost`, never `costMilli` — GG-46 is a Tier-1 gate, and a renderer
        // trusting a `Milli`-suffixed name here would be wrong by 1000x.
        Assert.True(structure.TryGetProperty("cost", out var cost));
        Assert.False(structure.TryGetProperty("costMilli", out _));
        Assert.True(cost.GetInt64() >= 0);
    }

    [Fact]
    public async Task A_strength_band_carries_its_full_shape()
    {
        var catalog = await _http.GetFromJsonAsync<JsonElement>("/api/world/catalog");
        var band = catalog.GetProperty("strengthBands").EnumerateArray().First();

        Assert.True(band.TryGetProperty("index", out _));
        Assert.True(band.TryGetProperty("name", out _));
        Assert.True(band.TryGetProperty("floor", out _));
        Assert.True(band.TryGetProperty("ceiling", out _));
        Assert.True(band.TryGetProperty("midpoint", out _));
    }

    /// <summary>
    /// world-stage W69: a waystation's build range read from the tuning row
    /// (`gk-core/data/tuning/loam.v4.json:39`, `waystationRangeHops: 3` — world-map W55 bumped the pinned
    /// file v1 to v2 for a `development` block, W56 bumped it again to v3 for three new
    /// structure rows, and W57 bumped it to v4 to rename the `*CostMilli` keys, all byte-identical
    /// to v1 otherwise except the key names), not hard-coded a second time on either side of the wire.
    /// </summary>
    [Fact]
    public async Task Waystation_range_hops_is_read_from_the_tuning_row()
    {
        var catalog = await _http.GetFromJsonAsync<JsonElement>("/api/world/catalog");
        Assert.True(catalog.TryGetProperty("waystationRangeHops", out var hops));
        Assert.Equal(3, hops.GetInt32());
    }
}
