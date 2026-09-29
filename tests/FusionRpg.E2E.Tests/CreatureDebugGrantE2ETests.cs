using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace FusionRpg.E2E.Tests;

/// <summary>Combat-unification F3.2's own live-lawn proof needs a specific dual-typed species on a
/// real roster without gambling real souls on gacha odds (best case ~1/45 within a rarity tier).
/// `/api/creatures/debug/grant` reuses the same `RpgStore.MintCreature` atomic path every other mint source
/// (summon, fusion, capture, delve) already goes through — this is a test-coverage seam, not a second
/// mint implementation.</summary>
[Collection("e2e")]
public class CreatureDebugGrantE2ETests : IAsyncLifetime
{
    readonly HttpClient _http;

    public CreatureDebugGrantE2ETests(RpgApiFactory factory)
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
    public async Task Grants_the_named_species_with_its_real_catalog_elements_no_soul_cost()
    {
        var before = await _http.GetFromJsonAsync<JsonElement>("/api/creatures/1/summon-state");
        var balanceBefore = before.GetProperty("balance").GetProperty("balance").GetInt64();

        var grant = await _http.PostAsJsonAsync("/api/creatures/debug/grant",
            new { playerId = 1, speciesId = "potatomine" });
        grant.EnsureSuccessStatusCode();
        var outcome = await grant.Content.ReadFromJsonAsync<JsonElement>();
        var profile = outcome.GetProperty("specimen").GetProperty("profile");
        Assert.Equal("potatomine", profile.GetProperty("speciesId").GetString());
        Assert.Equal("earth", profile.GetProperty("elementPrimary").GetString());
        Assert.Equal("fire", profile.GetProperty("elementSecondary").GetString());

        var roster = await _http.GetFromJsonAsync<JsonElement>("/api/creatures/1");
        Assert.Contains(roster.GetProperty("items").EnumerateArray(),
            it => it.GetProperty("profile").GetProperty("speciesId").GetString() == "potatomine");

        var after = await _http.GetFromJsonAsync<JsonElement>("/api/creatures/1/summon-state");
        Assert.Equal(balanceBefore, after.GetProperty("balance").GetProperty("balance").GetInt64());
    }

    [Fact]
    public async Task Unknown_species_is_rejected()
    {
        var grant = await _http.PostAsJsonAsync("/api/creatures/debug/grant",
            new { playerId = 1, speciesId = "NotARealSpecies" });
        Assert.Equal(HttpStatusCode.BadRequest, grant.StatusCode);
    }

    [Fact]
    public async Task Atoms_preview_compiles_for_a_real_instance_id()
    {
        var grant = await _http.PostAsJsonAsync("/api/creatures/debug/grant",
            new { playerId = 1, speciesId = "potatomine" });
        grant.EnsureSuccessStatusCode();
        var outcome = await grant.Content.ReadFromJsonAsync<JsonElement>();
        var instanceId = outcome.GetProperty("specimen").GetProperty("actor").GetProperty("instanceId").GetString();

        var preview = await _http.GetFromJsonAsync<JsonElement>($"/api/creatures/debug/atoms-preview/{instanceId}");
        Assert.Equal(instanceId, preview.GetProperty("instanceId").GetString());
        Assert.True(preview.GetProperty("grantCount").GetInt32() >= 0);
    }

    [Fact]
    public async Task Atoms_preview_404s_for_an_unknown_instance()
    {
        var preview = await _http.GetAsync("/api/creatures/debug/atoms-preview/not-a-real-instance");
        Assert.Equal(HttpStatusCode.NotFound, preview.StatusCode);
    }

    [Fact]
    public async Task A_dual_typed_deployed_specimen_with_a_bound_test_atom_compiles_a_real_two_component_elementPayload()
    {
        // gameTypeId 4 == potatomine (earth/fire) in the real catalog. A bare UniqueActor (no creature
        // profile) sidesteps the creature-contracts deploy gate entirely, matching
        // AtomPushServiceInstanceOwnerRewriteTests's own CreateUniqueActor + deploy + ack shape.
        var spawn = await _http.PostAsJsonAsync("/api/creatures/debug/spawn-unique-actor",
            new { playerId = 1, side = "plant", gameTypeId = 4 });
        spawn.EnsureSuccessStatusCode();
        var spawned = await spawn.Content.ReadFromJsonAsync<JsonElement>();
        var instanceId = spawned.GetProperty("instanceId").GetString();

        var bind = await _http.PostAsJsonAsync($"/api/creatures/debug/grant-test-atom/{instanceId}", new { });
        bind.EnsureSuccessStatusCode();

        var preview = await _http.GetFromJsonAsync<JsonElement>($"/api/creatures/debug/atoms-preview/{instanceId}");
        Assert.True(preview.GetProperty("grantCount").GetInt32() >= 1);
        var payload = preview.GetProperty("grants").EnumerateArray()
            .Select(g => g.GetProperty("elementPayload"))
            .First(p => p.ValueKind != JsonValueKind.Null);
        var components = payload.EnumerateArray().ToList();
        Assert.Equal(2, components.Count);
        Assert.Contains(components, c => c.GetProperty("element").GetString() == "earth");
        Assert.Contains(components, c => c.GetProperty("element").GetString() == "fire");
    }
}
