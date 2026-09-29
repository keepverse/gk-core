using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FusionRpg.Contracts;
using Xunit;

namespace FusionRpg.E2E.Tests;

/// <summary>
/// creature-lawn-deploy T1.6: the real HTTP/Server-layer proof, extending
/// <see cref="StorageE2ETests"/>'s own established `deploy → ack → assert ActiveBound` pattern
/// (`StorageE2ETests.cs:90-120`) from a bare unique actor to a real creature specimen — the case that
/// pipeline had never been driven through end to end before Phase 1 of this module.
///
/// <para>Stat/trait-effect correctness on a real board is deliberately NOT re-proven here — it is
/// already covered exhaustively at the `RpgStore` layer
/// (`FusionRpg.Data.Tests/CreatureLawnDeployTests.cs`, `CreatureLawnDeployMagnitudeTests.cs`), and this
/// module's own spec names the REMAINING half (a live board entity's combat stats reflecting the
/// specimen) as needing a real game board, not the in-memory `SimService` this factory runs — that is
/// the separate, owner-observable live-lawn check this task's own acceptance also names.</para>
/// </summary>
[Collection("e2e")]
public class CreatureLawnDeployE2ETests : IAsyncLifetime
{
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    readonly HttpClient _http;

    public CreatureLawnDeployE2ETests(RpgApiFactory factory)
    {
        _http = factory.CreateClient();
    }

    public async Task InitializeAsync()
    {
        var r = await _http.PostAsJsonAsync("/api/test/reset", new { });
        r.EnsureSuccessStatusCode();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    async Task FlushIngest() => (await _http.GetAsync("/api/test/snapshot")).EnsureSuccessStatusCode();

    /// <summary>Picks a real catalog species by property rather than a hardcoded id — this factory's
    /// own configured roster (compiled-default or store-backed) is an environment detail this test
    /// must stay robust to, the same lesson `CreatureLawnDeployHypnoRefusalTests.cs` already learned.</summary>
    async Task<string> PickSpecies(bool hypnoAlly)
    {
        var catalog = await _http.GetFromJsonAsync<JsonElement>("/api/creatures/catalog", Json);
        var wantedMode = hypnoAlly ? "hypno-ally" : "plant-avatar";
        var species = catalog.GetProperty("species").EnumerateArray()
            .First(s => s.GetProperty("deployMode").GetString() == wantedMode &&
                        !s.GetProperty("captureOnly").GetBoolean());
        return species.GetProperty("speciesId").GetString()!;
    }

    async Task<CreatureSpecimenDto> MintCreature(string speciesId)
    {
        var r = await _http.PostAsync($"/api/test/mint-creature?speciesId={speciesId}", content: null);
        r.EnsureSuccessStatusCode();
        var specimen = await r.Content.ReadFromJsonAsync<CreatureSpecimenDto>(Json);
        Assert.NotNull(specimen);
        return specimen!;
    }

    [Fact]
    public async Task A_real_creature_specimen_deploys_through_the_full_http_path_and_reaches_ActiveBound()
    {
        var speciesId = await PickSpecies(hypnoAlly: false);
        var specimen = await MintCreature(speciesId);
        var instanceId = specimen.Actor.InstanceId;

        var corr = "e2e-creature-deploy-" + Guid.NewGuid().ToString("N")[..8];
        var matchKey = "m-creature-" + Guid.NewGuid().ToString("N")[..8];
        var deploy = await _http.PostAsJsonAsync($"/api/unique/actors/{instanceId}/deploy",
            new { correlationId = corr, matchKey });
        deploy.EnsureSuccessStatusCode();

        var t = DateTime.UtcNow.ToString("o");
        (await _http.PostAsJsonAsync("/api/events", new
        {
            events = new[]
            {
                new
                {
                    t,
                    game = RpgConstants.GameId,
                    kind = "pvz.spawn.extra.ack",
                    matchKey,
                    payload = new { correlationId = corr, ptr = "0xCREATUREE2E", side = specimen.Actor.Side, typeId = specimen.Actor.TypeId }
                }
            }
        })).EnsureSuccessStatusCode();
        await FlushIngest();

        var get = await _http.GetFromJsonAsync<UniqueActorDto>($"/api/unique/actors/{instanceId}", Json);
        Assert.Equal(UniqueActorPhases.ActiveBound, get!.Phase);
    }

    [Fact]
    public async Task A_hypno_ally_species_refuses_deploy_through_the_full_http_path_too()
    {
        // T1.4's own refusal, proven one layer up: RpgStore.TryBeginUniqueDeploy's own refusal already
        // has direct coverage (CreatureLawnDeployHypnoRefusalTests.cs); this proves the Server's own HTTP
        // wrapper (UniqueActorEndpoints.cs's /deploy handler) actually surfaces it as a real 409, not a
        // silently-swallowed or mis-mapped response.
        var speciesId = await PickSpecies(hypnoAlly: true);
        var specimen = await MintCreature(speciesId);

        var deploy = await _http.PostAsJsonAsync($"/api/unique/actors/{specimen.Actor.InstanceId}/deploy",
            new { correlationId = "e2e-hypno-" + Guid.NewGuid().ToString("N")[..8], matchKey = "m-hypno-e2e" });

        Assert.Equal(HttpStatusCode.Conflict, deploy.StatusCode);
        var body = await deploy.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("deploy.hypno-ally-not-implemented", body.GetProperty("reason").GetString());
    }
}
