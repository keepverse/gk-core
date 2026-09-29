using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace FusionRpg.E2E.Tests;

/// <summary>
/// W22 (spec-world-intel.md §On the wire): the state endpoint is the only place fog can leak, so
/// this is where the tests point.
///
/// One test per leak, plus a property test that catches the leaks nobody thought of: no payload may
/// name a sector its viewer has never seen.
/// </summary>
[Collection("e2e")]
public class WorldFogE2ETests : IAsyncLifetime
{
    readonly HttpClient _http;

    public WorldFogE2ETests(RpgApiFactory factory) => _http = factory.CreateClient();

    public async Task InitializeAsync()
    {
        // Every test in this class reads the same read-only world. `reset` does not drop worlds, so
        // the first test creates it and the rest are told it already exists — which is fine, and
        // better than a world per test when none of them writes.
        var created = await _http.PostAsJsonAsync("/api/test/world/create", new
        {
            worldId = "fog", templateId = "first-light", seed = "1"
        });

        Assert.True(
            created.IsSuccessStatusCode || created.StatusCode == HttpStatusCode.Conflict,
            $"unexpected {created.StatusCode} creating the fog world");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    async Task<JsonElement> StateFor(string? faction)
    {
        var path = faction is null ? "/api/world/fog/state" : $"/api/world/fog/state?asFaction={faction}";
        return await _http.GetFromJsonAsync<JsonElement>(path);
    }

    static JsonElement Sector(JsonElement state, string id) =>
        state.GetProperty("sectors").EnumerateArray()
            .Single(s => s.GetProperty("sectorId").GetString() == id);

    // ---- the leaks, one test each -------------------------------------------------------

    [Fact]
    public async Task A_sector_you_have_never_seen_carries_its_position_and_nothing_else()
    {
        // black-gate is authored Unknown and three lanes from anything Dave holds.
        var dark = Sector(await StateFor("dave"), "black-gate");

        Assert.Equal("Unknown", dark.GetProperty("intel").GetString());
        Assert.Equal("", dark.GetProperty("typeId").GetString());
        Assert.Equal(JsonValueKind.Null, dark.GetProperty("climate").ValueKind);
        Assert.Equal(0, dark.GetProperty("dangerBand").GetInt32());
        Assert.Empty(dark.GetProperty("slots").EnumerateArray());
        Assert.Empty(dark.GetProperty("forces").EnumerateArray());

        // …but the map still knows where to draw it. The graph's shape is public.
        Assert.Equal(6, dark.GetProperty("layoutX").GetInt32());
        Assert.Equal(1, dark.GetProperty("layoutY").GetInt32());
    }

    [Fact]
    public async Task A_sector_you_have_only_glimpsed_carries_no_slot_detail()
    {
        // ash-waste is authored Rumored — heard of, never walked.
        var rumoured = Sector(await StateFor("dave"), "ash-waste");

        Assert.NotEqual("Unknown", rumoured.GetProperty("intel").GetString());
        Assert.Empty(rumoured.GetProperty("slots").EnumerateArray());

        // The homeworld, which Dave is standing in, does carry them.
        Assert.NotEmpty(Sector(await StateFor("dave"), "homeworld").GetProperty("slots").EnumerateArray());
    }

    [Fact]
    public async Task A_force_you_cannot_see_is_not_in_the_payload_at_all()
    {
        // Re-anchored 2026-08-22: Zomboss has a warband now, so "sees nothing" is no longer his to
        // demonstrate. What still holds — and is the actual claim — is that he sees *his own* corner
        // and nothing of Dave's, and Dave sees the reverse.
        var zomboss = await StateFor("zomboss");

        var seenByZomboss = zomboss.GetProperty("sectors").EnumerateArray()
            .SelectMany(s => s.GetProperty("forces").EnumerateArray())
            .Select(f => f.GetProperty("entityId").GetString())
            .ToList();

        Assert.Contains("e-zomboss-band-1", seenByZomboss);
        Assert.DoesNotContain("e-dave-legion-1", seenByZomboss);

        // `entities` is the viewer's own forces only — never anybody else's, at any distance.
        Assert.All(zomboss.GetProperty("entities").EnumerateArray(),
            e => Assert.Equal("zomboss", e.GetProperty("ownerFactionId").GetString()));
    }

    [Fact]
    public async Task A_glimpsed_force_is_banded_and_a_surveyed_one_is_counted()
    {
        var dave = await StateFor("dave");

        var mine = Sector(dave, "homeworld").GetProperty("forces").EnumerateArray().Single();
        Assert.True(mine.GetProperty("exact").GetBoolean());
        Assert.True(mine.GetProperty("strength").GetInt64() > 0);

        var theirs = Sector(dave, "ash-waste").GetProperty("forces").EnumerateArray().Single();
        Assert.False(theirs.GetProperty("exact").GetBoolean());
        Assert.Equal(0, theirs.GetProperty("strength").GetInt64());
        Assert.False(string.IsNullOrWhiteSpace(theirs.GetProperty("bandName").GetString()));
    }

    [Fact]
    public async Task You_always_see_your_own_forces_in_full()
    {
        var entities = (await StateFor("dave")).GetProperty("entities").EnumerateArray().ToList();

        var legion = Assert.Single(entities);
        Assert.Equal("e-dave-legion-1", legion.GetProperty("entityId").GetString());
        Assert.Equal(3, legion.GetProperty("members").GetArrayLength());
        Assert.All(entities, e => Assert.Equal("dave", e.GetProperty("ownerFactionId").GetString()));
    }

    [Fact]
    public async Task The_seed_never_reaches_the_wire()
    {
        var raw = await _http.GetStringAsync("/api/world/fog/state?asFaction=dave");
        Assert.DoesNotContain("\"seed\"", raw, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The catch-all. Any future field that quietly carries truth into an unseen sector fails here
    /// even if nobody wrote a test for that particular field.
    /// </summary>
    [Fact]
    public async Task No_viewer_learns_anything_about_ground_it_has_never_seen()
    {
        foreach (var faction in new[] { "dave", "wild", "zomboss" })
        {
            var state = await StateFor(faction);

            foreach (var sector in state.GetProperty("sectors").EnumerateArray())
            {
                if (sector.GetProperty("intel").GetString() != "Unknown") continue;

                var id = sector.GetProperty("sectorId").GetString();
                Assert.True(string.IsNullOrEmpty(sector.GetProperty("typeId").GetString()),
                    $"{faction} learned the type of unseen {id}");
                Assert.Equal(JsonValueKind.Null, sector.GetProperty("ownerFactionId").ValueKind);
                Assert.Equal(0, sector.GetProperty("slots").GetArrayLength());
                Assert.Equal(0, sector.GetProperty("forces").GetArrayLength());
                Assert.Equal(0, sector.GetProperty("developmentLevel").GetInt32());
            }
        }
    }

    // ---- who is asking ------------------------------------------------------------------

    [Fact]
    public async Task Different_factions_get_genuinely_different_maps()
    {
        var dave = await StateFor("dave");
        var wild = await StateFor("wild");

        Assert.Equal("Watched", Sector(dave, "homeworld").GetProperty("intel").GetString());
        Assert.Equal("Unknown", Sector(wild, "homeworld").GetProperty("intel").GetString());

        Assert.Equal("Watched", Sector(wild, "ash-waste").GetProperty("intel").GetString());
        Assert.NotEqual("Watched", Sector(dave, "ash-waste").GetProperty("intel").GetString());
    }

    [Fact]
    public async Task Asking_as_nobody_in_particular_means_asking_as_the_player()
    {
        var implicitly_ = await StateFor(null);
        var explicitly = await StateFor("dave");

        Assert.Equal(
            explicitly.GetProperty("sectors").EnumerateArray().Select(s => s.GetProperty("intel").GetString()),
            implicitly_.GetProperty("sectors").EnumerateArray().Select(s => s.GetProperty("intel").GetString()));
    }

    [Fact]
    public async Task Asking_as_a_faction_that_does_not_exist_is_refused_rather_than_answered()
    {
        // Falling back to omniscience here would be the worst possible default.
        var res = await _http.GetAsync("/api/world/fog/state?asFaction=nobody");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task The_graph_itself_is_public_knowledge()
    {
        var blind = await StateFor("zomboss");

        Assert.Equal(6, blind.GetProperty("sectors").GetArrayLength());
        Assert.Equal(6, blind.GetProperty("lanes").GetArrayLength());
        Assert.All(blind.GetProperty("lanes").EnumerateArray(),
            l => Assert.False(string.IsNullOrEmpty(l.GetProperty("fromSectorId").GetString())));
    }

    [Fact]
    public async Task A_remembered_sector_says_how_old_the_memory_is()
    {
        var ash = Sector(await StateFor("dave"), "ash-waste");

        Assert.True(ash.TryGetProperty("intelAge", out var age));
        Assert.True(age.GetInt32() >= 0);
    }
}
