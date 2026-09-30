using System.Net.Http.Json;
using FusionRpg.Contracts;
using FusionRpg.Core.ActorSurface;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Server.Tests;

[Trait("VerificationId", "server.derived-surface")]
public sealed class DerivedSurfaceEndpointsTests : IAsyncLifetime
{
    WebApplication _app = null!;
    HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        ActorSurfaceCatalogHub.ConfigureAll(
            AptitudeSurfaceCatalogLoader.Parse(ReadTuning("aptitude-catalog.v1.json")),
            DerivedStatSurfaceCatalogLoader.Parse(ReadTuning("derived-stat-catalog.v3.json")),
            StatusSurfaceCatalogLoader.Parse(ReadTuning("status-catalog.v1.json")),
            ResourceSurfaceCatalogLoader.Parse(ReadTuning("resource-catalog.v1.json")),
            ElementSurfaceCatalogLoader.Parse(ReadTuning("element-catalog.v1.json")),
            ActorSheetSurfaceCatalogLoader.Parse(ReadTuning("actor-sheet.v1.json")));

        var port = GetFreeTcpPort();
        var baseUrl = $"http://127.0.0.1:{port}";
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(baseUrl);
        _app = builder.Build();
        _app.MapDerivedSurface();
        await _app.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri(baseUrl) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
    }

    [Fact]
    public async Task Get_derived_surface_returns_four_tabs()
    {
        var resp = await _http.GetAsync("/api/catalogs/derived-surface?lang=en&side=plant");
        if (!resp.IsSuccessStatusCode) throw new Exception(await resp.Content.ReadAsStringAsync());
        var body = await resp.Content.ReadFromJsonAsync<DerivedSurfaceDto>();
        Assert.NotNull(body);
        Assert.Equal("en", body!.Lang);
        Assert.Equal("plant", body.Side);
        Assert.Equal(2, body.SchemaVersion);
        Assert.Equal(new[] { "elements", "status", "resources", "other" }, body.Tabs.Select(t => t.Id));
        Assert.Contains(body.Tabs.Single(t => t.Id == "elements").Variants, v => v.Id == "omni" && v.PresentationOnly);
        var statusTab = body.Tabs.Single(t => t.Id == "status");
        // D1 (spec-derived-cook-ia.md): the Status rail is Omni + `statusCategoryVariants`
        // (omni/dot/cc/contagion), NOT the per-status-id chips this test used to expect (25). The
        // catalog's own `statusCategoryVariants` is the source, so assert against it rather than a
        // literal — a category added to the catalog moves the rail by construction.
        var expectedStatusIds = new[] { "omni" }
            .Concat(ReadTuningDerivedStatusCategories())
            .ToArray();
        Assert.Equal(expectedStatusIds, statusTab.Variants.Select(v => v.Id).ToArray());
        Assert.Equal("omni", statusTab.Variants[0].Id);
        Assert.True(statusTab.Variants[0].PresentationOnly);
        // D3 (spec-derived-cook-ia.md): OTHER Shared is a first-class BE-emitted variant, so the
        // `other` tab is never empty — asserting `Empty` predated D3 and was masked by a host-start
        // error. The contract is that `shared` is present and the action-category rail is populated
        // from the catalog (asserted against the catalog, not a literal).
        var other = body.Tabs.Single(t => t.Id == "other");
        Assert.Equal(new[] { "shared" }, other.Variants.Select(v => v.Id).ToArray());
        Assert.NotNull(other.ActionCategoryVariants);
        Assert.NotEmpty(other.ActionCategoryVariants!);
    }

    [Fact]
    public async Task Get_derived_surface_zombie_flips_hunger_label()
    {
        var resp = await _http.GetAsync("/api/catalogs/derived-surface?lang=en&side=zombie");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<DerivedSurfaceDto>();
        Assert.NotNull(body);
        Assert.Equal("Hunger", body!.Tabs.Single(t => t.Id == "resources").Variants.Single(v => v.Id == "hunger").DisplayName);
    }

    [Fact]
    public async Task Get_derived_surface_unknown_lang_falls_back_to_en()
    {
        var resp = await _http.GetAsync("/api/catalogs/derived-surface?lang=xx");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<DerivedSurfaceDto>();
        Assert.NotNull(body);
        Assert.Equal("xx", body!.Lang);
        Assert.Equal("Elements", body.Tabs.Single(t => t.Id == "elements").DisplayName);
    }

    [Fact]
    public async Task Get_derived_surface_defaults_and_family_floors()
    {
        var resp = await _http.GetAsync("/api/catalogs/derived-surface");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<DerivedSurfaceDto>();
        Assert.NotNull(body);
        Assert.Equal("en", body!.Lang);
        Assert.Equal("plant", body.Side);
        Assert.Contains("derived-stat-catalog.v2", body.VersionStamp, StringComparison.Ordinal);
        Assert.Equal(28, body.Tabs.Single(t => t.Id == "elements").Categories.SelectMany(c => c.Families).Count());
        Assert.Equal(6, body.Tabs.Single(t => t.Id == "status").Categories.SelectMany(c => c.Families).Count());
        Assert.Equal(
            new[] { "hp", "stamina", "hunger", "spirit", "qi", "poise" },
            body.Tabs.Single(t => t.Id == "resources").Variants.Select(v => v.Id));
    }

    static string ReadTuning(string fileName)
    {
        var path = Path.Combine(FindRepoRoot(), "data", "tuning", fileName);
        Assert.True(File.Exists(path), "missing " + path);
        return File.ReadAllText(path);
    }

    /// <summary>The Status rail's category ids from the catalog's own `statusCategoryVariants`,
    /// excluding Omni (which the rail carries first). Read from the shipped tuning so the assertion
    /// tracks a catalog edit rather than pinning a literal.</summary>
    static IReadOnlyList<string> ReadTuningDerivedStatusCategories()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(
            ReadTuning("derived-stat-catalog.v3.json"));
        return doc.RootElement.GetProperty("statusCategoryVariants").EnumerateArray()
            .Select(v => v.GetProperty("id").GetString()!)
            .Where(id => id != "omni")
            .ToList();
    }

    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }

    static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
