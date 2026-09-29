using System.Net.Http.Json;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Aura;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using FusionRpg.Data.Tests;

namespace FusionRpg.Server.Tests;

/// <summary>aura-skill T18c: GET /api/auras exposes the real, shipped AuraContentCatalog (T16) so the
/// web surface can render every equipped-or-gated slot, not just the per-player subset -- including
/// each aura's real upkeep cost, read live from `RpgStore.ListCosts` (never fabricated).</summary>
[Trait("VerificationId", "server.aura-catalog")]
public class AuraCatalogEndpointsTests : IAsyncLifetime
{
    DataTestStore _testStore = null!;
    RpgStore _store = null!;
    WebApplication _app = null!;
    HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;

        var port = GetFreeTcpPort();
        var baseUrl = $"http://127.0.0.1:{port}";
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(_store);
        builder.WebHost.UseUrls(baseUrl);
        _app = builder.Build();
        _app.UseDeveloperExceptionPage();
        _app.MapAuraCatalog();
        await _app.StartAsync();

        _http = new HttpClient { BaseAddress = new Uri(baseUrl) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        _testStore.Dispose();
    }

    [Fact]
    public async Task Get_returnsEveryRealCatalogAura()
    {
        var resp = await _http.GetAsync("/api/auras");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<CatalogDto>();
        Assert.NotNull(body);
        Assert.Equal(AuraContentCatalog.All.Count, body!.Items.Count);
        Assert.Contains(body.Items, i => i.AuraId == "Might" && i.AptitudeId == "Might");
    }

    [Fact]
    public async Task Get_todaysRealState_everyAuraHasNoUpkeepAuthoredYet()
    {
        // aura-skill T18c: confirmed by `grep -rn PerTick data/` finding zero hits -- no aura has a
        // real, shipped upkeep cost anywhere yet. This asserts the endpoint reports that honestly
        // (an empty array) rather than fabricating a number.
        var resp = await _http.GetAsync("/api/auras");
        var body = await resp.Content.ReadFromJsonAsync<CatalogDto>();
        Assert.NotNull(body);
        Assert.NotEmpty(body!.Items);
        Assert.All(body.Items, i => Assert.Empty(i.Upkeep));
    }

    [Fact]
    public async Task Get_onceABalancePassAuthorsARealCost_reportsItWithNoCodeChange()
    {
        // `RpgStore.ListCosts` has no foreign-key requirement on a real ActionRow -- an aura id is a
        // legal cost key today even though auras are never authored as ActionRows (T16's own
        // deliberate separation). Proves the read path is real, not a stub that would need editing
        // once content actually exists.
        var upsert = _store.UpsertCost(new ActionCostRow("Might", "stamina", ValueSpec.Of(5), ActionCostTiming.PerTick));
        Assert.True(upsert.IsOk, upsert.ToString());

        var resp = await _http.GetAsync("/api/auras");
        var body = await resp.Content.ReadFromJsonAsync<CatalogDto>();
        var might = Assert.Single(body!.Items, i => i.AuraId == "Might");
        var cost = Assert.Single(might.Upkeep);
        Assert.Equal("stamina", cost.ResourceId);
        Assert.Equal(5, cost.AmountMin);
        Assert.Equal(5, cost.AmountMax);
        Assert.Equal("PerTick", cost.When);

        // A sibling aura with no authored cost stays honestly empty.
        var fortitude = Assert.Single(body.Items, i => i.AuraId == "Fortitude");
        Assert.Empty(fortitude.Upkeep);
    }

    sealed class CatalogDto
    {
        public List<AuraCatalogItemDto> Items { get; set; } = new();
    }

    sealed class AuraCatalogItemDto
    {
        public string AuraId { get; set; } = "";
        public string AptitudeId { get; set; } = "";
        public List<UpkeepDto> Upkeep { get; set; } = new();
    }

    sealed class UpkeepDto
    {
        public string ResourceId { get; set; } = "";
        public int AmountMin { get; set; }
        public int AmountMax { get; set; }
        public string When { get; set; } = "";
    }

    static int GetFreeTcpPort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
