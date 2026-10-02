using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.E2E.Tests;

/// <summary>
/// The FE view contract's adapters (T4, gk-web/web/fusion-rpg-web/src/contract/adapt.ts) bind to real DTO
/// shapes; T5's job is one shared source for those shapes instead of two hand-typed copies drifting
/// apart. Each fixture here is exactly what its endpoint returns for a fixed, reproducible scenario;
/// the FE's Vitest mocks and Playwright e2e both read the same checked-in files
/// (src/test/mocks.ts). A server-side DTO change that isn't re-blessed here fails in whichever
/// project noticed first — same mechanism as WorldFixtureTests.cs, generalised beyond World.
///
/// Set FUSIONRPG_BLESS_CONTRACT_FIXTURES=1 to rewrite fixtures after a deliberate DTO change.
/// </summary>
[Trait("VerificationId", "e2e.contract-fixture")]
[Collection("e2e")]
public class ContractFixtureTests : IAsyncLifetime
{
    const string FixtureDir = "web/fusion-rpg-web/e2e/fixtures";

    readonly HttpClient _http;

    public ContractFixtureTests(RpgApiFactory factory) => _http = factory.CreateClient();

    public async Task InitializeAsync() =>
        (await _http.PostAsJsonAsync("/api/test/reset", new { })).EnsureSuccessStatusCode();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Unique_actor_fixture_still_matches_the_live_dto()
    {
        var create = await _http.PostAsJsonAsync("/api/unique/actors", new { side = "plant", typeId = 1 });
        create.EnsureSuccessStatusCode();
        var actor = await create.Content.ReadFromJsonAsync<JsonNode>();
        Assert.NotNull(actor);

        // instanceId and the two timestamps are fresh every run (a new GUID, wall-clock "now") — the
        // fixture asserts the DTO's *shape*, not one specific instance, so these are normalized to
        // fixed placeholders before comparing. Every other field is a real value from the live server.
        actor!["instanceId"] = "fixture-actor-1";
        actor["createdAt"] = "2026-01-01T00:00:00.0000000Z";
        actor["updatedAt"] = "2026-01-01T00:00:00.0000000Z";

        await AssertFixtureMatches("unique-actor.json", actor);
    }

    [Fact]
    public async Task Commander_list_fixture_matches_live_dto()
    {
        var players = await _http.GetFromJsonAsync<JsonElement>("/api/players");
        var playerId = players.GetProperty("currentPlayerId").GetInt64();

        var resp = await _http.GetAsync($"/api/commanders/{playerId}");
        resp.EnsureSuccessStatusCode();
        var live = await resp.Content.ReadFromJsonAsync<JsonNode>();
        Assert.NotNull(live);

        await AssertFixtureMatches("commander-list.json", live!);
    }

    static async Task AssertFixtureMatches(string fileName, JsonNode live)
    {
        var json = live.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
        var path = Path.Combine(FixtureRoot(), FixtureDir, fileName);

        if (Environment.GetEnvironmentVariable("FUSIONRPG_BLESS_CONTRACT_FIXTURES") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, json);
        }

        Assert.True(File.Exists(path), $"missing fixture {FixtureDir}/{fileName} — run with FUSIONRPG_BLESS_CONTRACT_FIXTURES=1");
        Assert.Equal(json.Replace("\r\n", "\n"), (await File.ReadAllTextAsync(path)).Replace("\r\n", "\n"));
    }

    /// <summary>The repository that OWNS these fixtures, which is gk-web and not this one.
    ///
    /// The fixture paths are still spelled the way they were before the split
    /// (`web/fusion-rpg-web/...`), which resolved correctly only while every repository lived in
    /// one tree. Resolved against `KeepverseRoots.Core()` they name a directory this repository has
    /// never had, so all six of these tests failed on `missing fixture ... run with
    /// FUSIONRPG_BLESS_WORLD_FIXTURE=1` — a remedy that could not have worked, because the blessing
    /// wrote to the same wrong place. `KeepverseRoots.Web()` is the owning repository and the paths
    /// are already relative to it, so the declared spelling did not have to change.
    ///
    /// Named for what it returns rather than kept as `RepoRoot`, because a helper called RepoRoot
    /// that returns gk-web is the kind of small lie that produced this failure in the first place.
    /// </summary>
    static string FixtureRoot() => KeepverseRoots.Web();
}
