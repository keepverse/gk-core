using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace FusionRpg.E2E.Tests;

/// <summary>
/// L5 acceptance (spec-loam-model.md §Fog): `FractureIntensityMilli` is terrain and reaches anyone
/// who has scouted the sector. `LoamStock` is live state, gated owner-only the same way
/// `StabilityMilli` already is (spec-loam-fe.md's L24 gauge added it to the wire). Same shape as
/// <c>WorldFogE2ETests</c> (W22): one leak, one test, plus a property that catches the leak nobody
/// thought of.
/// </summary>
[Collection("e2e")]
public class WorldLoamFogE2ETests : IAsyncLifetime
{
    readonly HttpClient _http;

    public WorldLoamFogE2ETests(RpgApiFactory factory) => _http = factory.CreateClient();

    public async Task InitializeAsync()
    {
        var created = await _http.PostAsJsonAsync("/api/test/world/create", new
        {
            worldId = "loam-fog", templateId = "first-light", seed = "1"
        });

        Assert.True(
            created.IsSuccessStatusCode || created.StatusCode == HttpStatusCode.Conflict,
            $"unexpected {created.StatusCode} creating the loam-fog world");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    async Task<JsonElement> StateFor(string? faction)
    {
        var path = faction is null ? "/api/world/loam-fog/state" : $"/api/world/loam-fog/state?asFaction={faction}";
        return await _http.GetFromJsonAsync<JsonElement>(path);
    }

    static JsonElement Sector(JsonElement state, string id) =>
        state.GetProperty("sectors").EnumerateArray()
            .Single(s => s.GetProperty("sectorId").GetString() == id);

    [Fact]
    public async Task Loam_stock_reaches_only_the_owner_never_a_non_owner()
    {
        // Superseded by spec-loam-fe.md's gauge, which needs stock alongside income/upkeep/net: the
        // field is on the wire now, gated the same structural way as `StabilityMilli` (read from
        // truth, only for the sector's actual owner) rather than banned outright. `LoamStock` still
        // never reaches belief/`IntelSnapshot` even for the owner — that fog rule is unchanged; this
        // is a separate, later, owner-only projection built for a screen the owner is looking at.
        foreach (var faction in new[] { "dave", "wild", "zomboss" })
        {
            var state = await StateFor(faction);
            foreach (var sector in state.GetProperty("sectors").EnumerateArray())
            {
                var isOwner = sector.GetProperty("ownerFactionId").ValueKind == JsonValueKind.String
                              && sector.GetProperty("ownerFactionId").GetString() == faction;
                if (!isOwner)
                    Assert.Equal(0, sector.GetProperty("loamStock").GetInt64());
            }
        }
    }

    [Fact]
    public async Task Fracture_intensity_is_terrain_so_a_scouted_sector_carries_it()
    {
        var home = Sector(await StateFor("dave"), "homeworld");

        Assert.True(home.TryGetProperty("fractureIntensityMilli", out var intensity));
        Assert.True(intensity.GetInt32() >= 0);
    }

    [Fact]
    public async Task An_unseen_sector_reports_only_the_baseline_intensity()
    {
        // black-gate is authored Unknown to Dave — the same fixture WorldFogE2ETests uses to prove
        // the type id and owner are withheld. `first-light` gives every sector the same (baseline)
        // intensity, so this alone cannot distinguish "withheld" from "coincidentally equal" — that
        // distinction is `LoamFogTests.An_unseen_sector_carries_no_intensity_belief_at_all...` in
        // Core.Tests, which hand-authors a loud non-baseline value on the unseen sector directly.
        // What this test proves at the wire is narrower but still real: the DTO's default for
        // "never seen" is the baseline, not a zero or a crash.
        var dark = Sector(await StateFor("dave"), "black-gate");

        Assert.Equal("Unknown", dark.GetProperty("intel").GetString());
        Assert.Equal(1000, dark.GetProperty("fractureIntensityMilli").GetInt32());
    }
}
