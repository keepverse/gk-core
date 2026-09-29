using System.Net;
using System.Net.Http.Json;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using FusionRpg.Data.Tests;

namespace FusionRpg.Server.Tests;

/// <summary>aura-skill T15: GET/POST /api/loadout against a REAL, minimal in-process host (same
/// pattern as `AptitudeEndpointsTests.cs`), not a mock — the production caller `SetLoadout`/
/// `GetLoadout` never had. The underlying persistence/mid-run/reject-leaves-untouched behavior is
/// already fully proven at the store level (`LoadoutStoreTests.cs`); these tests prove the NEW
/// endpoint surface: real round trip, real 404/400/409 responses, `isHeld` wired to the real action
/// table (not a mock).</summary>
public class LoadoutEndpointsTests : IAsyncLifetime
{
    DataTestStore _testStore = null!;
    RpgStore _store = null!;
    WebApplication _app = null!;
    HttpClient _http = null!;
    long _playerId;

    public async Task InitializeAsync()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _playerId = _store.GetCurrentPlayerId();

        var port = GetFreeTcpPort();
        var baseUrl = $"http://127.0.0.1:{port}";
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(_store);
        builder.WebHost.UseUrls(baseUrl);
        _app = builder.Build();
        _app.UseDeveloperExceptionPage();
        _app.MapLoadout();
        await _app.StartAsync();

        _http = new HttpClient { BaseAddress = new Uri(baseUrl) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        _testStore.Dispose();
    }

    string SeedHeldAction(string actionId)
    {
        var atomId = AtomRow.DeriveId("atom." + actionId.Replace('.', '-'), "", 1);
        var atomResult = _store.UpsertAtom(new AtomRow
        {
            AtomId = atomId,
            KindId = "stat.modify",
            FamilyId = "atom." + actionId.Replace('.', '-'),
            Variant = "",
            Tier = 1,
            Name = actionId,
            ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":1}",
        });
        Assert.True(atomResult.IsOk, atomResult.ToString());

        var containerId = "skill." + actionId.Replace('.', '-') + "-container";
        var containerResult = _store.UpsertContainer(new ContainerRow
        {
            ContainerId = containerId,
            Kind = ContainerKind.Skill,
            Atoms = new[] { new ContainerAtomRow(0, atomId) },
        });
        Assert.True(containerResult.IsOk, containerResult.ToString());

        var write = _store.UpsertAction(new ActionRow
        {
            ActionId = actionId,
            Name = actionId,
            Kind = ActionKind.Skill,
            ContainerId = containerId,
            Grantable = true,
            Tags = new[] { ActionTag.Offensive },
        });
        Assert.True(write.IsOk, write.ToString());
        return actionId;
    }

    static int GetFreeTcpPort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Fact]
    public async Task Get_onAFreshPlayer_returnsAnEmptyLoadout()
    {
        var resp = await _http.GetAsync($"/api/loadout/{_playerId}");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<LoadoutStateDto>();
        Assert.NotNull(body);
        Assert.Empty(body!.ActionIds);
    }

    [Fact]
    public async Task Get_unknownPlayer_returns404()
    {
        var resp = await _http.GetAsync("/api/loadout/999999");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Post_aHeldAction_savesAndRoundTripsThroughGet()
    {
        SeedHeldAction("aura.ember");

        var postResp = await _http.PostAsJsonAsync("/api/loadout",
            new { playerId = _playerId, actionIds = new[] { "aura.ember" } });
        postResp.EnsureSuccessStatusCode();
        var postBody = await postResp.Content.ReadFromJsonAsync<LoadoutStateDto>();
        Assert.Equal(new[] { "aura.ember" }, postBody!.ActionIds);

        var getResp = await _http.GetAsync($"/api/loadout/{_playerId}");
        var getBody = await getResp.Content.ReadFromJsonAsync<LoadoutStateDto>();
        Assert.Equal(new[] { "aura.ember" }, getBody!.ActionIds);
    }

    [Fact]
    public async Task Post_aRealAuraId_savesEvenThoughItIsNeverAnActionRow()
    {
        // aura-skill T18c regression: AuraContentCatalog ids (T16) are never ActionRows -- before the
        // fix, isHeld's `store.GetAction(id) is not null` alone refused every real aura, so no aura
        // could ever be legally equipped. "Might" is a real, shipped AuraContentCatalog id.
        var postResp = await _http.PostAsJsonAsync("/api/loadout",
            new { playerId = _playerId, actionIds = new[] { "Might" } });
        postResp.EnsureSuccessStatusCode();
        var postBody = await postResp.Content.ReadFromJsonAsync<LoadoutStateDto>();
        Assert.Equal(new[] { "Might" }, postBody!.ActionIds);
    }

    [Fact]
    public async Task Post_anUnheldActionId_conflictsAndDoesNotSave()
    {
        var postResp = await _http.PostAsJsonAsync("/api/loadout",
            new { playerId = _playerId, actionIds = new[] { "not-a-real-action" } });

        Assert.Equal(HttpStatusCode.Conflict, postResp.StatusCode);
        var after = await (await _http.GetAsync($"/api/loadout/{_playerId}")).Content.ReadFromJsonAsync<LoadoutStateDto>();
        Assert.Empty(after!.ActionIds); // refused, nothing saved
    }

    [Fact]
    public async Task Post_unknownPlayer_returns404()
    {
        var postResp = await _http.PostAsJsonAsync("/api/loadout",
            new { playerId = 999999L, actionIds = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.NotFound, postResp.StatusCode);
    }

    [Fact]
    public async Task Post_missingActionIds_returns400()
    {
        var postResp = await _http.PostAsJsonAsync("/api/loadout", new { playerId = _playerId });
        Assert.Equal(HttpStatusCode.BadRequest, postResp.StatusCode);
    }

    sealed class LoadoutStateDto
    {
        public long PlayerId { get; set; }
        public List<string> ActionIds { get; set; } = new();
    }
}
