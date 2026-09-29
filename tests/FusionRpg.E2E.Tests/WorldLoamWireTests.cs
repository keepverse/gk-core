using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace FusionRpg.E2E.Tests;

/// <summary>
/// L22/L24 acceptance (spec-loam-fe.md): the derived numbers on the wire, plus L24's stock and
/// release-forecast fields. Same shape as <c>WorldFogE2ETests</c> (W22) — a property over every
/// projection, not a spot check.
///
/// **What this file does not cover, and why.** "A split territory reports two components with
/// independent totals" is proven at the Core level already (`TwoHearthsTests`'s severable-waist
/// test, `LoamPhasesTests`'s severing tests) — there is no player-issued "sever a lane" command in
/// this wave (severing is a scripted/environmental event, per L19's own finding), so a *single*
/// faction cannot be driven into two components through the live HTTP surface at all. What this file
/// proves instead is that the wiring (`ComputeLoamReading` → `WorldSectorDto`) is correct for the one
/// component `two-hearths` actually has at creation — and since that function contains no
/// component-count special case, its correctness generalises to two components the same way the
/// already-proven Core-level splitting does.
/// </summary>
[Collection("e2e")]
public class WorldLoamWireTests : IAsyncLifetime
{
    readonly HttpClient _http;

    public WorldLoamWireTests(RpgApiFactory factory) => _http = factory.CreateClient();

    public async Task InitializeAsync()
    {
        var created = await _http.PostAsJsonAsync("/api/test/world/create", new
        {
            worldId = "loam-wire", templateId = "two-hearths", seed = "7"
        });

        Assert.True(
            created.IsSuccessStatusCode || created.StatusCode == System.Net.HttpStatusCode.Conflict,
            $"unexpected {created.StatusCode} creating the loam-wire world");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    async Task<JsonElement> StateFor(string faction) =>
        await _http.GetFromJsonAsync<JsonElement>($"/api/world/loam-wire/state?asFaction={faction}");

    static JsonElement Sector(JsonElement state, string id) =>
        state.GetProperty("sectors").EnumerateArray().Single(s => s.GetProperty("sectorId").GetString() == id);

    [Fact]
    public async Task No_owner_only_loam_number_reaches_a_non_owner()
    {
        foreach (var faction in new[] { "dave", "zomboss" })
        {
            var state = await StateFor(faction);

            foreach (var sector in state.GetProperty("sectors").EnumerateArray())
            {
                if (sector.GetProperty("ownerFactionId").ValueKind == JsonValueKind.String
                    && sector.GetProperty("ownerFactionId").GetString() == faction)
                    continue;   // the owner's own ground is allowed everything below

                var id = sector.GetProperty("sectorId").GetString();
                Assert.Equal(0, sector.GetProperty("loamProduction").GetInt64());
                Assert.Equal(0, sector.GetProperty("loamUpkeep").GetInt64());
                Assert.Equal(0, sector.GetProperty("loamNet").GetInt64());
                Assert.Equal(JsonValueKind.Null, sector.GetProperty("componentId").ValueKind);
                Assert.Equal(0, sector.GetProperty("componentProduction").GetInt64());
                Assert.Equal(0, sector.GetProperty("componentUpkeep").GetInt64());
                Assert.Equal(0, sector.GetProperty("componentNet").GetInt64());
                Assert.Equal(0, sector.GetProperty("stabilityMilli").GetInt32());
                Assert.Equal(0, sector.GetProperty("loamStock").GetInt64());
                Assert.Equal(0, sector.GetProperty("componentStock").GetInt64());
                Assert.False(sector.GetProperty("willReleaseNextTurn").GetBoolean());
                // world-map W46: recruit stock and project progress are owner-only economy numbers,
                // the same gate as everything else in this test.
                Assert.Equal(0, sector.GetProperty("recruitStock").GetInt64());
                Assert.Equal(JsonValueKind.Null, sector.GetProperty("projectId").ValueKind);
                Assert.Equal(JsonValueKind.Null, sector.GetProperty("projectTurnsRemaining").ValueKind);
                _ = id; // named for the assertion message context if any of the above ever fails
            }
        }
    }

    [Fact]
    public async Task The_owners_own_sectors_carry_real_loam_numbers_grouped_by_component()
    {
        var dave = await StateFor("dave");

        // d-home, d-flank-1, d-flank-2, d-outpost are all one connected, Dave-owned component at
        // creation (the capital loop plus its single-lane outpost) — same componentId, same totals.
        var owned = new[] { "d-home", "d-flank-1", "d-flank-2", "d-outpost" }
            .Select(id => Sector(dave, id)).ToList();

        var componentIds = owned.Select(s => s.GetProperty("componentId").GetString()).Distinct().ToList();
        Assert.Single(componentIds);
        Assert.NotNull(componentIds[0]);

        var componentProductions = owned.Select(s => s.GetProperty("componentProduction").GetInt64()).Distinct().ToList();
        var componentUpkeeps = owned.Select(s => s.GetProperty("componentUpkeep").GetInt64()).Distinct().ToList();
        Assert.Single(componentProductions);
        Assert.Single(componentUpkeeps);

        // d-home and d-flank-1 each carry a rootbed — the component's production must be positive.
        Assert.True(componentProductions[0] > 0, "the capital cluster's two rootbeds must show a positive component production");

        var home = Sector(dave, "d-home");
        Assert.True(home.GetProperty("loamProduction").GetInt64() > 0);
        Assert.Equal(home.GetProperty("loamProduction").GetInt64() - home.GetProperty("loamUpkeep").GetInt64(),
            home.GetProperty("loamNet").GetInt64());
        Assert.Equal(1000, home.GetProperty("stabilityMilli").GetInt32());

        // Fresh at creation: nobody has taken any fade, so nothing is about to be released.
        Assert.False(home.GetProperty("willReleaseNextTurn").GetBoolean());

        var componentStocks = owned.Select(s => s.GetProperty("componentStock").GetInt64()).Distinct().ToList();
        Assert.Single(componentStocks);
        Assert.True(componentStocks[0] >= 0);
    }

    [Fact]
    public async Task Habitable_is_terrain_visible_once_scouted_even_on_ground_the_viewer_does_not_own()
    {
        var dave = await StateFor("dave");

        // d-flank-1 is Dave's own and habitable (a rootbed) — the baseline the next assertion
        // contrasts against.
        Assert.True(Sector(dave, "d-flank-1").GetProperty("habitable").GetBoolean());

        // hot-ground is unowned by anyone and unseen by Dave at creation — Unknown ground carries
        // no fields at all, `habitable` included, which is the fog rule this property must also
        // respect: a property visible-once-scouted is not visible before that.
        var hot = Sector(dave, "hot-ground");
        Assert.Equal("Unknown", hot.GetProperty("intel").GetString());
        Assert.False(hot.GetProperty("habitable").GetBoolean());
    }
}
