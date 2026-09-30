using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FusionRpg.Core.ActorSurface;
using FusionRpg.Core.Hud;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Server.Tests;

/// <summary>
/// Contract tests for <c>GET /api/catalogs/actor-surface</c> — the route the web FE has requested
/// since it landed and that no host had ever mapped.
/// </summary>
/// <remarks>
/// <para>
/// The failure this file exists to prevent is not a crash. On a deployed Server the SPA fallback
/// (<c>Program.cs</c>'s <c>MapFallbackToFile("index.html")</c>) answered the unmapped path with
/// <c>200 text/html</c>; <c>tryGetJson</c> saw <c>r.ok</c>, called <c>r.json()</c> on HTML, threw, and
/// the client's catch returned its fixture. On a Server with no published SPA the same path is a
/// <c>404</c>, which <c>tryGetJson</c> converts to <c>null</c> and the caller converts to the same
/// fixture. Both were green, so <b>a bare "200 OK" assertion would have passed against HTML for the
/// route's entire missing life</b> — hence <see cref="Get_answers_json_not_the_spa_fallback"/>.
/// </para>
/// <para>
/// No test here pins a catalog entry count, glyph count, or any other population
/// (<c>docs/architecture/validation-ssot.md</c>): these catalogs grow whenever content ships, and a
/// pinned total fails on the normal case while catching nothing. What is asserted is the mapping
/// contract, the response shape, the property-name vocabulary the FE reads, closure of
/// <c>defaultOpen</c> into the served tab kinds, uniqueness of the ids the FE iterates, and
/// determinism — all stable across generations.
/// </para>
/// </remarks>
[Trait("VerificationId", "server.actor-surface")]
public sealed class ActorSurfaceEndpointsTests : IAsyncLifetime
{
    WebApplication _app = null!;
    HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        // Same injection the real host performs at Program.cs:89-104 — the route is a pure read of
        // these hubs, so a test that skipped the HUD tuning could not build the DTO at all.
        //
        // The element catalog is NOT hardcoded here, and that is the correction this file needed.
        // It used to read "element-catalog.v1.json" — a literal copy of the composition root's own
        // stale revision — so every route assertion below was reasoning about a catalog the shipped
        // Server never served, and the v1/v2 split stayed invisible to all of them. It is read from
        // the same place production reads it (ServerElementCatalogFileName), which is what makes
        // the glyph assertions in this file assertions about the shipped wiring rather than about a
        // fixture. Which revision is current is pinned by
        // Server_and_Injector_hosts_read_the_same_element_catalog_revision, not by this harness.
        ActorHudTuningHub.Configure(ActorHudTuningLoader.Parse(ReadTuning("actor-hud.v7.json")));
        ActorSurfaceCatalogHub.ConfigureAll(
            AptitudeSurfaceCatalogLoader.Parse(ReadTuning("aptitude-catalog.v1.json")),
            DerivedStatSurfaceCatalogLoader.Parse(ReadTuning("derived-stat-catalog.v3.json")),
            StatusSurfaceCatalogLoader.Parse(ReadTuning("status-catalog.v1.json")),
            ResourceSurfaceCatalogLoader.Parse(ReadTuning("resource-catalog.v1.json")),
            ElementSurfaceCatalogLoader.Parse(ReadTuning(ServerElementCatalogFileName())),
            ActorSheetSurfaceCatalogLoader.Parse(ReadTuning("actor-sheet.v1.json")));

        var port = GetFreeTcpPort();
        var baseUrl = $"http://127.0.0.1:{port}";
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(baseUrl);
        _app = builder.Build();
        _app.MapActorSurface();
        await _app.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri(baseUrl) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
    }

    /// <summary>
    /// The mapping contract of the extension itself: the pattern <c>MapActorSurface</c> registers.
    /// Read from the built endpoint table, so neither a SPA fallback nor any other catch-all can
    /// satisfy it.
    /// </summary>
    /// <remarks>
    /// <b>This does not prove <c>Program.cs</c> calls it</b> — the harness above maps the extension
    /// itself, so this test passes even with the registration deleted (measured: a falsification
    /// probe that commented the <c>Program.cs</c> line out left this green). The registration wiring
    /// is a separate fact and has its own assertion,
    /// <see cref="Program_registers_the_actor_surface_route"/>.
    /// </remarks>
    [Fact]
    public void MapActorSurface_registers_the_actor_surface_pattern()
    {
        var patterns = _app.Services.GetRequiredService<EndpointDataSource>()
            .Endpoints.OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText)
            .ToList();

        Assert.Contains("/api/catalogs/actor-surface", patterns);
    }

    /// <summary>
    /// The defect itself: <c>Program.cs</c> must actually call <c>MapActorSurface()</c>. The route's
    /// DTO, its Hub, and every test in this file could all be perfect while the shipped Server never
    /// registered it — which is exactly what happened, since no host had ever mapped this path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Comments are stripped before the assertion, so a commented-out call does <b>not</b> satisfy
    /// it. That is not a hypothetical nicety: the falsification probe for this lane left exactly
    /// <c>//app.MapActorSurface();</c> in the file, and a naive substring check would have passed on
    /// the dead line.
    /// </para>
    /// <para>
    /// This is a source-text assertion on purpose and follows this project's own precedent —
    /// <c>ContentBootStartupWiringTests.The_server_boot_reads_the_latest_revision_of_the_domains_it_owns</c>
    /// reads <c>Program.cs</c> the same way, with the same recorded reason: no
    /// <c>WebApplicationFactory</c> harness exists in this test project. It proves the wiring is
    /// present in the source; it does not boot the real <c>Program</c>. The live-process proof that
    /// the wiring produces a reachable route is in
    /// <c>tasks/reports/actor-surface-route-20260926.md</c>.
    /// </para>
    /// </remarks>
    [Fact]
    public void Program_registers_the_actor_surface_route()
    {
        var program = File.ReadAllText(Path.Combine(RepoSourceRoot(), "src", "FusionRpg.Server", "Program.cs"));
        var code = StripLineComments(program);

        Assert.Contains("MapActorSurface()", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// The served path is the path the FE requests, and both live in text files this test can read:
    /// the route in <c>ActorSurfaceEndpoints.cs</c>, the request in the FE's fetch helper. A rename on
    /// either side that misses the other is the same class of silent fallback this whole file is about
    /// — a client asking for a path no host answers.
    /// </summary>
    [Fact]
    public void Served_path_matches_the_path_the_web_client_requests()
    {
        var root = RepoSourceRoot();
        var endpoint = File.ReadAllText(Path.Combine(root, "src", "FusionRpg.Server", "ActorSurfaceEndpoints.cs"));
        var client = File.ReadAllText(Path.Combine(root, "web", "fusion-rpg-web", "src", "lib", "bus", "actorSurface.ts"));

        const string path = "/api/catalogs/actor-surface";
        Assert.Contains($"\"{path}\"", StripLineComments(endpoint), StringComparison.Ordinal);
        Assert.Contains($"\"{path}\"", StripLineComments(client), StringComparison.Ordinal);
    }

    /// <summary>
    /// Line comments removed, so a commented-out call reads as absent.
    /// </summary>
    /// <remarks>
    /// Only <c>//</c> is stripped, and deliberately so. A <c>/* */</c> regex looks like the obvious
    /// completion and is actively wrong on this file: <c>Program.cs</c> holds 15 <c>/*</c> against 3
    /// <c>*/</c> because <c>/*</c> occurs inside string literals (e.g.
    /// <c>"gk-data/packs/fusion/data/seed/dungeon/layouts/*.json"</c>), so a lazy block regex pairs a string's opener with
    /// some unrelated closer and deletes a large span of real code — which is exactly what happened
    /// the first time this ran, silently un-registering the very call under test.
    /// <para>
    /// Residual gap, stated rather than hidden: a <c>MapActorSurface()</c> inside a <c>/* */</c> block
    /// comment would satisfy this. That is a far more deliberate act than commenting a line out, and
    /// closing it would mean parsing C# — not worth it for a wiring assertion. Stripping <c>//</c>
    /// cannot cause a false pass here, and its only possible false failure is a token appearing after
    /// a <c>//</c> inside a string literal, which is not the case.
    /// </para>
    /// </remarks>
    static string StripLineComments(string source) =>
        string.Join(
            "\n",
            source.Split('\n').Select(line =>
            {
                var idx = line.IndexOf("//", StringComparison.Ordinal);
                return idx < 0 ? line : line[..idx];
            }));

    /// <summary>
    /// The response is JSON, and specifically not the SPA fallback. Asserting the media type and the
    /// first non-whitespace byte is what makes this a real regression test: the missing route
    /// answered <c>200 text/html</c> on a deployed Server, which satisfies a status-code-only check.
    /// </summary>
    [Fact]
    public async Task Get_answers_json_not_the_spa_fallback()
    {
        var resp = await _http.GetAsync("/api/catalogs/actor-surface");
        var raw = await resp.Content.ReadAsStringAsync();

        resp.EnsureSuccessStatusCode();
        Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);

        // An HTML body starts with '<'; a JSON object with '{'. Cheap, and independent of the type.
        var first = raw.FirstOrDefault(c => !char.IsWhiteSpace(c));
        Assert.Equal('{', first);
        Assert.DoesNotContain("<!doctype html", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<div id=\"root\"", raw, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The route serves the Hub's DTO, with the HUD presentation the branch's HUD feature reads.
    /// The pixel fields are asserted positive because that is the loader's own bound
    /// (<c>RequiredPositiveForVersion</c>) and because it is precisely the zero that made the FE's
    /// <c>if (!texture || size &lt;= 0) return</c> skip every glyph when the fixture was served.
    /// </summary>
    [Fact]
    public async Task Serves_the_hub_dto_with_hud_presentation()
    {
        var dto = await GetDtoAsync("/api/catalogs/actor-surface");

        Assert.NotNull(dto);
        Assert.NotNull(dto.HudPresentation);
        Assert.True(dto.HudPresentation.IdentityElementPrimaryPixels > 0d);
        Assert.True(dto.HudPresentation.IdentityElementSecondaryPixels > 0d);
        Assert.True(dto.HudPresentation.IdentityElementGapPixels >= 0d);

        // Closure: the sheet catalog's `defaultOpen` must name one of the tab kinds this same
        // response serves. It is the FE's `ActorPanel` active-tab seed, so a value outside the served
        // set would open the sheet on a tab the response never described.
        Assert.Contains(dto.DefaultOpen, dto.Tabs.Select(t => t.Kind));

        // Every fan-in section is present (non-null) — a missing one is a shape defect, not a count.
        Assert.NotNull(dto.Tabs);
        Assert.NotNull(dto.Aptitudes);
        Assert.NotNull(dto.Families);
        Assert.NotNull(dto.Resources);
        Assert.NotNull(dto.Elements);
        Assert.NotNull(dto.Statuses);
        Assert.NotNull(dto.KitRoles);
    }

    /// <summary>
    /// The property-name vocabulary the FE reads, and the internal consistency of the rows it
    /// iterates. The name list is the FE's own <c>ActorSurfaceCatalog</c> type
    /// (<c>gk-web/web/fusion-rpg-web/src/lib/actorSurfaceCatalog.ts:108-119</c>) — a closed vocabulary the
    /// consumer owns, which <c>validation-ssot.md</c> permits pinning. A field added on the FE side
    /// must be served here or this fails, which is the whole bug class: the client asked for a route
    /// the server never had, and separately read a field (<c>defaultOpen</c>) the DTO dropped.
    /// </summary>
    [Fact]
    public async Task Served_shape_matches_the_fe_contract_and_is_internally_consistent()
    {
        var resp = await _http.GetAsync("/api/catalogs/actor-surface");
        using var json = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync());

        var served = json.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(
            new[]
            {
                "aptitudes", "defaultOpen", "elements", "families", "hudPresentation",
                "kitRoles", "resources", "statuses", "tabs", "versionStamp"
            },
            served);

        // Cross-artifact consistency: the stamp names every catalog the fan-in reads, so a catalog
        // added to ConfigureAll without a stamp contribution is visible here.
        var stamp = json.RootElement.GetProperty("versionStamp").GetString()!;
        foreach (var catalog in new[] { "aptitude", "derived", "status", "resource", "element", "sheet" })
            Assert.Contains($"{catalog}:", stamp, StringComparison.Ordinal);

        var dto = await GetDtoAsync("/api/catalogs/actor-surface");

        // Uniqueness of the ids the FE looks rows up by. A duplicate would make `.find()` resolve to
        // whichever row came last, silently, and no count assertion would ever notice.
        Assert.Equal(dto.Tabs.Select(t => t.Kind).Distinct(StringComparer.Ordinal).Count(), dto.Tabs.Count);
        Assert.Equal(dto.Resources.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count(), dto.Resources.Count);
        Assert.Equal(dto.Elements.Select(e => e.Id).Distinct(StringComparer.Ordinal).Count(), dto.Elements.Count);
        Assert.Equal(dto.Statuses.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count(), dto.Statuses.Count);
        Assert.Equal(dto.KitRoles.Select(k => k.RoleId).Distinct(StringComparer.Ordinal).Count(), dto.KitRoles.Count);
    }

    /// <summary>
    /// Determinism: the DTO is a pure function of the process's injected hubs, so two reads of the
    /// same process are byte-identical. This is what makes the route safe to cache later without
    /// changing its meaning, and it is a contract a reordering or a per-request random source would
    /// break.
    /// </summary>
    [Fact]
    public async Task Two_reads_are_byte_identical()
    {
        var first = await (await _http.GetAsync("/api/catalogs/actor-surface")).Content.ReadAsStringAsync();
        var second = await (await _http.GetAsync("/api/catalogs/actor-surface")).Content.ReadAsStringAsync();

        Assert.Equal(first, second);
    }

    // ---------------------------------------------------------------------------------------
    // The element glyph half. Sizing reached the client with the route; the glyph kind did not,
    // because the two hosts disagreed about which catalog revision was current and the Server's
    // choice was the older one. These three assertions each have a distinct failure to catch, and
    // none of them pins a population (docs/architecture/validation-ssot.md §1-§3): no entry count,
    // no glyph count, no glyph name, no catalog version literal.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The defect itself: two hosts, one current catalog revision.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Program.cs</c> read <c>element-catalog.v1.json</c> while <c>RpgHost.cs</c> read
    /// <c>element-catalog.v2.json</c>. v2 is a strict superset of v1 and is the only shipped
    /// revision carrying <c>hudGlyph</c>, so the split was not two contracts — it was the Server
    /// serving a catalog that predates the field, which made the whole served element section
    /// null-glyph.
    /// </para>
    /// <para>
    /// Asserted as <b>agreement</b>, not as the literal <c>"v2"</c>. That is deliberate and it is
    /// what keeps the test stable across generations: a future v3 that moves both hosts stays
    /// green, while a one-sided move — the exact shape of this bug — fails. Pinning the version
    /// string would instead be a changelog assertion that has to be edited on every publish, which
    /// is the population-pin defect wearing a different hat. The revision is still constrained:
    /// the companion assertion below requires the agreed file to actually carry glyph data, so
    /// "both hosts agree" cannot be satisfied by both agreeing on a catalog without the field.
    /// </para>
    /// <para>
    /// Source-text for the same reason as <see cref="Program_registers_the_actor_surface_route"/>:
    /// no <c>WebApplicationFactory</c> exists in this project, and a host's choice of tuning file
    /// is a boot-time fact that only the composition root can answer. <c>//</c> comments are
    /// stripped first, so a commented-out or explanatory reader does not count as the live one.
    /// </para>
    /// </remarks>
    [Fact]
    public void Server_and_Injector_hosts_read_the_same_element_catalog_revision()
    {
        var server = ServerElementCatalogFileName();
        var injector = InjectorElementCatalogFileName();

        Assert.Equal(injector, server);
    }

    /// <summary>
    /// Anti-vacuity, plus the data contract: the catalog the readers agree on must carry glyph
    /// kinds, and every row that declares one must declare it non-empty.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the first assertion exists.</b> Without it, everything else in this file is
    /// satisfiable by a catalog in which <c>hudGlyph</c> is absent from every row — which is
    /// precisely the shipped defect. "Every row that declares a glyph has a non-empty glyph" is
    /// vacuously true of a catalog where no row declares one, so it needs one companion assertion
    /// that the field is present at all.
    /// </para>
    /// <para>
    /// <b>Why it is not a population pin.</b> The bound is "at least one row declares a glyph"
    /// (>= 1), i.e. the <em>existence of the field in the artifact</em>, which is the envelope
    /// contract of validation-ssot.md §2. It is not today's count, so shipping more elements, or
    /// fewer, cannot fail it; what fails it is the field being dropped from the catalog
    /// altogether, which is a real regression. There is deliberately no upper bound and no
    /// per-glyph or per-row expectation.
    /// </para>
    /// <para>
    /// Rows that do <b>not</b> declare <c>hudGlyph</c> are asserted nothing at all. Whether a
    /// given element should carry a glyph is content, owned by the catalog's publisher — today one
    /// row (<c>omni</c>, <c>presentationOnly: true</c>) declares none — and this file must not
    /// become the place that decision is made.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_element_catalog_the_hosts_read_actually_carries_glyph_kinds()
    {
        using var catalog = JsonDocument.Parse(ReadTuning(ServerElementCatalogFileName()));

        var declared = 0;
        foreach (var entry in catalog.RootElement.GetProperty("entries").EnumerateArray())
        {
            if (!entry.TryGetProperty("hudGlyph", out var glyph)) continue;
            declared++;
            var value = glyph.GetString();
            Assert.False(
                string.IsNullOrWhiteSpace(value),
                $"element '{entry.GetProperty("id").GetString()}' declares hudGlyph but it is empty");
        }

        Assert.True(
            declared > 0,
            "the element catalog the Server reads declares no hudGlyph on any row, so no element " +
            "can resolve art again -- this is the regression, not a content change");
    }

    /// <summary>
    /// The served route carries those values, on the wire, as JSON.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The three layers this file now covers, and the failure each one catches:
    /// <see cref="Server_and_Injector_hosts_read_the_same_element_catalog_revision"/> catches a
    /// host reading the wrong revision; the assertion above catches a revision that carries no
    /// glyph data; this one catches <c>ActorSurfaceCatalogHub.BuildDto</c> dropping the field on the
    /// way out — which is a real possibility here, because the DTO is a hand-written projection and
    /// nothing else in the suite would notice a field left out of it.
    /// </para>
    /// <para>
    /// Asserted on the <b>deserialized DTO</b>, not on a status code and not on a raw substring.
    /// A bare 200 is worthless on this route: the SPA fallback answers unmapped paths with
    /// <c>200 text/html</c>, which is how the route spent its entire missing life looking green.
    /// The media type is re-asserted here so this test also cannot be satisfied by a fallback, and
    /// the values are compared against the catalog rows themselves rather than against literals —
    /// the cross-artifact join is the contract.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Served_element_rows_carry_the_catalog_glyphs()
    {
        var resp = await _http.GetAsync("/api/catalogs/actor-surface");
        var raw = await resp.Content.ReadAsStringAsync();

        resp.EnsureSuccessStatusCode();
        Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);
        Assert.Equal('{', raw.FirstOrDefault(c => !char.IsWhiteSpace(c)));

        var dto = await GetDtoAsync("/api/catalogs/actor-surface");
        Assert.NotNull(dto);
        Assert.NotNull(dto.Elements);

        // What the catalog declares, keyed by id — the join the FE performs client-side.
        var expected = new Dictionary<string, string?>(StringComparer.Ordinal);
        using (var catalog = JsonDocument.Parse(ReadTuning(ServerElementCatalogFileName())))
        {
            foreach (var entry in catalog.RootElement.GetProperty("entries").EnumerateArray())
            {
                var id = entry.GetProperty("id").GetString()!;
                expected[id] = entry.TryGetProperty("hudGlyph", out var g) ? g.GetString() : null;
            }
        }

        // Reconciliation, not a count: every row the catalog has is served, and every served row is
        // a catalog row. Either direction drifting is a projection defect.
        Assert.Equal(expected.Count, dto.Elements.Count);

        var withGlyph = 0;
        foreach (var row in dto.Elements)
        {
            Assert.True(expected.ContainsKey(row.Id), $"served element '{row.Id}' is not in the catalog");

            if (expected[row.Id] is not { } want) continue;   // declares none — assert nothing further
            withGlyph++;
            Assert.Equal(want, row.HudGlyph);
            Assert.False(string.IsNullOrWhiteSpace(row.HudGlyph));
        }

        // The anti-vacuity counterpart on the served side: a response whose every glyph is null is
        // the shape this whole file was written to prevent, and it would satisfy every join above.
        Assert.True(
            withGlyph > 0,
            "the served actor-surface catalog carries no hudGlyph on any element row; the FE's " +
            "actorHudElementArtUrl is undefined for every element");
    }

    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The element-catalog file name the Server's composition root reads, taken from
    /// <c>Program.cs</c> source.
    /// </summary>
    /// <remarks>
    /// Line comments are stripped with the same helper the wiring assertion uses, so the explanatory
    /// comment sitting above the reader does not become a candidate match. Exactly one distinct name
    /// is required: zero means the reader is gone or unparseable, and two means the file names two
    /// revisions and which one wins is no longer answerable from the source. Both are refused rather
    /// than guessed, because a guess here would silently re-point every other assertion in the file.
    /// </remarks>
    static string ServerElementCatalogFileName() =>
        SingleElementCatalogFileName("src", "FusionRpg.Server", "Program.cs");

    /// <summary>The same, for the Injector host's boot wiring in <c>RpgHost.cs</c>.</summary>
    static string InjectorElementCatalogFileName() =>
        SingleElementCatalogFileName("src", "FusionRpg.Injector", "Host", "RpgHost.cs");

    static string SingleElementCatalogFileName(params string[] relativeParts)
    {
        var path = Path.Combine(new[] { RepoSourceRoot() }.Concat(relativeParts).ToArray());
        Assert.True(File.Exists(path), "missing " + path);

        var names = Regex.Matches(StripLineComments(File.ReadAllText(path)), @"element-catalog\.v\d+\.json")
            .Select(m => m.Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            names.Length == 1,
            $"{path} must name exactly one element-catalog revision, found {names.Length} " +
            $"({(names.Length == 0 ? "none" : string.Join(", ", names))})");

        return names[0];
    }

    async Task<ActorSurfaceCatalogDto> GetDtoAsync(string path)
    {
        var resp = await _http.GetAsync(path);
        if (!resp.IsSuccessStatusCode) throw new Exception(await resp.Content.ReadAsStringAsync());
        return (await resp.Content.ReadFromJsonAsync<ActorSurfaceCatalogDto>())!;
    }

    static string ReadTuning(string fileName)
    {
        var path = Path.Combine(FindRepoRoot(), "data", "tuning", fileName);
        Assert.True(File.Exists(path), "missing " + path);
        return File.ReadAllText(path);
    }

    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }

    /// <summary>
    /// The SOURCE root, for the tests that read <c>src/</c> or <c>web/</c> sources. Deliberately not
    /// the same finder as <see cref="FindRepoRoot"/>: the build copies <c>gk-core/data/tuning</c> next to the
    /// test executable, so a tuning-file marker stops the walk in <c>bin/Debug/net8.0</c> and every
    /// <c>src/</c> path built from it throws <c>DirectoryNotFoundException</c>. The marker is the one
    /// <c>ContentBootStartupWiringTests</c> already uses, because <c>gk-fusion/src/FusionRpg.Injector</c> is a
    /// source-tree-only directory.
    /// </summary>
    static string RepoSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Injector")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("repo source root with src/FusionRpg.Injector");
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
