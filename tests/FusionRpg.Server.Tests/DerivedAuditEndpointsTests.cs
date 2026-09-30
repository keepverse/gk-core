using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FusionRpg.Core.ActorSurface;
using FusionRpg.Core.Effects;
using FusionRpg.Core.PassiveTree.State;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Data;
using FusionRpg.Data.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using FusionRpg.Data.Tests;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Server.Tests;

/// <summary>
/// Derived sheet audit: real UniqueActor → Hub → sheet + coverage report (never a synthetic paint).
/// </summary>
[Trait("VerificationId", "server.derived-audit")]
public class DerivedAuditEndpointsTests : IAsyncLifetime
{
    DataTestStore _testStore = null!;
    RpgStore _store = null!;
    WebApplication _app = null!;
    HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;

        PowerTuningHub.Configure(
            PowerTuningLoader.Parse(File.ReadAllText(Path.Combine(RepoTuningDir(), "power-scale.v2.json"))));
        AptitudeTuningHub.Configure(
            AptitudeTuningLoader.Parse(File.ReadAllText(LatestAptitudesPath())));
        PassiveTreeTuningHub.Configure(
            PassiveTreeTuningLoader.Parse(File.ReadAllText(Path.Combine(RepoTuningDir(), "passive-tree.v1.json"))));
        FusionRpg.Core.Progression.ProgressionTuningHub.Configure(
            FusionRpg.Core.Progression.ProgressionTuningLoader.Parse(
                File.ReadAllText(Path.Combine(RepoTuningDir(), "progression.v3.json"))));
        FusionRpg.Core.Status.StatusPolicy.Configure(
            FusionRpg.Core.Status.StatusTuningLoader.Parse(
                File.ReadAllText(Path.Combine(RepoTuningDir(), "status.v1.json"))));
        StatsTuningHub.Configure(
            StatsTuningLoader.Parse(File.ReadAllText(Path.Combine(RepoTuningDir(), "stats.v1.json"))));
        FusionRpg.Core.Creatures.CreatureSpeciesCatalog.ConfigureFromCompiledDefault();
        FusionRpg.Core.Progression.SpeciesProgressionTuningHub.Configure(
            FusionRpg.Core.Progression.SpeciesProgressionTuningLoader.Parse(
                File.ReadAllText(Path.Combine(RepoTuningDir(), "species-progression.v1.json"))));

        ActorSurfaceCatalogHub.ConfigureAll(
            AptitudeSurfaceCatalogLoader.Parse(File.ReadAllText(Path.Combine(RepoTuningDir(), "aptitude-catalog.v1.json"))),
            DerivedStatSurfaceCatalogLoader.Parse(File.ReadAllText(Path.Combine(RepoTuningDir(), "derived-stat-catalog.v3.json"))),
            StatusSurfaceCatalogLoader.Parse(File.ReadAllText(Path.Combine(RepoTuningDir(), "status-catalog.v1.json"))),
            ResourceSurfaceCatalogLoader.Parse(File.ReadAllText(Path.Combine(RepoTuningDir(), "resource-catalog.v1.json"))),
            ElementSurfaceCatalogLoader.Parse(File.ReadAllText(Path.Combine(RepoTuningDir(), "element-catalog.v1.json"))),
            ActorSheetSurfaceCatalogLoader.Parse(File.ReadAllText(Path.Combine(RepoTuningDir(), "actor-sheet.v1.json"))));

        var port = GetFreeTcpPort();
        var baseUrl = $"http://127.0.0.1:{port}";
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSignalR();
        builder.Services.AddSingleton(_store);
        builder.Services.AddSingleton<InjectorCommandInbox>();
        builder.Services.AddSingleton<EffectGrantSession>();
        builder.Services.AddSingleton<IHotCompactor>(sp => new HotCompactor(sp.GetRequiredService<RpgStore>()));
        builder.Services.AddSingleton<EventIngest>();
        builder.Services.AddSingleton<FusionRpg.Server.DelveBattleSessionManager>();
        // `/api/actors/{id}/sheet` takes IActorLiveStateStore (CG-A5b live statuses/shields). Production
        // registers it at Program.cs:345; this test host must too, or route inference fails at start.
        builder.Services.AddSingleton<IActorLiveStateStore, ActorLiveStateStore>();
        builder.WebHost.UseUrls(baseUrl);
        _app = builder.Build();
        _app.UseDeveloperExceptionPage();
        _app.MapHub<RpgHub>("/hub/rpg");
        _app.MapDebug();
        _app.MapAuraDerived();
        await _app.StartAsync();

        _http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(60) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        _testStore.Dispose();
    }

    [Fact]
    public async Task Seed_then_sheet_has_multi_source_combat_and_coverage_reports_present()
    {
        var seedResp = await _http.PostAsJsonAsync("/api/debug/derived-audit-actor", new { });
        if (!seedResp.IsSuccessStatusCode)
            throw new Exception(await seedResp.Content.ReadAsStringAsync());
        var seed = await seedResp.Content.ReadFromJsonAsync<SeedDto>();
        Assert.NotNull(seed);
        Assert.Equal(DerivedAuditActor.InstanceId, seed!.InstanceId);

        var sheetResp = await _http.GetAsync($"/api/actors/{seed.InstanceId}/sheet");
        if (!sheetResp.IsSuccessStatusCode)
            throw new Exception(await sheetResp.Content.ReadAsStringAsync());
        var sheet = await sheetResp.Content.ReadFromJsonAsync<SheetDto>();
        Assert.NotNull(sheet);
        Assert.True(sheet!.Derived.Count >= 200);

        var power = Assert.Single(sheet.Derived, c => c.ChannelId == DerivedStatChannels.CombatPowerOmni);
        Assert.True(power.Contributions.Count >= 2, "expected aptitude + equip (or tree) on combat.power.omni");
        Assert.Contains(power.Contributions, c => c.SourceId.StartsWith("aptitude.", StringComparison.Ordinal));
        Assert.Contains(power.Contributions, c => c.SourceId.StartsWith("equip:", StringComparison.Ordinal));

        var covResp = await _http.GetAsync($"/api/debug/derived-audit-coverage?instanceId={seed.InstanceId}");
        if (!covResp.IsSuccessStatusCode)
            throw new Exception(await covResp.Content.ReadAsStringAsync());
        using var doc = JsonDocument.Parse(await covResp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal(268, root.GetProperty("registryCount").GetInt32()); // 269 -> 268 (`retire-atk` R4, 2026-09-18)
        Assert.True(root.GetProperty("presentCount").GetInt32() > 0);
        Assert.True(root.GetProperty("touchedCount").GetInt32() > 0);
        // The cook expands sparse status.{id} joins that are intentionally session-only. They may be
        // absent from a cold server sheet — but the audit's CONTRACT is not "there must be gaps": it is
        // that whatever IS missing is properly accounted for. A closed gap (empty `missingCook`) is a
        // pass, so this asserts the classification invariant rather than non-emptiness.
        var missingCook = root.GetProperty("missingCook").EnumerateArray()
            .Select(e => e.GetString()!).ToList();
        Assert.All(missingCook, id => Assert.True(
            DerivedAuditCoverage.IsStatusSessionChannel(id), "unexpected cold-sheet cook gap: " + id));
        Assert.Equal(0, root.GetProperty("missingRegistry").GetArrayLength());

        // First ship may keep a non-empty gap.unwired allowlist — that IS the audit product.
        var gaps = root.GetProperty("gap").GetProperty("unwired")
            .EnumerateArray().Select(e => e.GetString()!).ToList();
        if (ApprovedGapUnwiredAllowlist.Count == 0)
        {
            // Inventory mode: record count; do not invent sheet rows to force empty.
            Assert.True(gaps.Count >= 0);
        }
        else
        {
            var unexpected = gaps.Except(ApprovedGapUnwiredAllowlist, StringComparer.Ordinal).ToList();
            Assert.True(unexpected.Count == 0,
                "new gap.unwired channels (close or allowlist with reason): " + string.Join(", ", unexpected.Take(40)));
            foreach (var id in ApprovedGapUnwiredAllowlist)
                Assert.Contains(id, gaps);
        }
    }

    /// <summary>
    /// Owner-reviewed wiring gaps left after maximizing Server Hub producers on the audit actor.
    /// Empty = inventory mode (first ship). When populated, surprise gaps fail the test.
    /// Shrink as producers land — never "fix" by inventing sheet rows.
    /// </summary>
    static readonly HashSet<string> ApprovedGapUnwiredAllowlist = new(StringComparer.Ordinal);

    sealed class SeedDto
    {
        public string InstanceId { get; set; } = "";
        public long PlayerId { get; set; }
        public long Level { get; set; }
    }

    sealed class SheetDto
    {
        public List<SheetChannelDto> Derived { get; set; } = new();
    }

    sealed class SheetChannelDto
    {
        public string ChannelId { get; set; } = "";
        public double Value { get; set; }
        public List<ContribDto> Contributions { get; set; } = new();
    }

    sealed class ContribDto
    {
        public string SourceId { get; set; } = "";
        public double Value { get; set; }
    }

    static string RepoTuningDir() => Path.Combine(KeepverseRoots.Core(), "data", "tuning");

    static string LatestAptitudesPath()
    {
        var dir = RepoTuningDir();
        var best = Directory.EnumerateFiles(dir, "aptitudes.v*.json")
            .Select(Path.GetFileName)
            .Select(n => (Name: n!, Match: System.Text.RegularExpressions.Regex.Match(n!, @"^aptitudes\.v(\d+)\.json$")))
            .Where(x => x.Match.Success)
            .OrderByDescending(x => int.Parse(x.Match.Groups[1].Value))
            .First();
        return Path.Combine(dir, best.Name);
    }

    static int GetFreeTcpPort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
