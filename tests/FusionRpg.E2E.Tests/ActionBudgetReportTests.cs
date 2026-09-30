using System.Linq;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FusionRpg.Core.Actions.Corpus;
using FusionRpg.Core.Actions.Rungs;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Data;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.E2E.Tests;

/// <summary>
/// ST4.3 (`spec-budget-calibration-report.md` contract 4, spec test 7): the read-only RPG Server Debug
/// endpoint returns the calibration over the REAL imported catalog and performs no write.
///
/// <para><b>Why this lives in E2E and not `Server.Tests`.</b> The acceptance names the endpoint going
/// through the real bootstrap, and `Server.Tests` has no host-boot project reference at all — T63 hit
/// the same wall for its unlock-tuning proof and recorded the same deviation. The real bootstrap is the
/// thing under test, so the test sits where it is reachable.</para>
///
/// <para><b>What makes it a real reading.</b> The host's boot import
/// (`Program.cs` → `ActionCorpusImporter.Import`, the same call this test makes when the boot import
/// left nothing) is the real import path; the endpoint then prices those rows through ST4.1's one
/// helper. Nothing here fabricates an action, and nothing pins a percentile or a scalar.</para>
/// </summary>
[Collection("e2e")]
public class ActionBudgetReportTests : IAsyncLifetime
{
    readonly RpgApiFactory _factory;
    readonly HttpClient _http;

    public ActionBudgetReportTests(RpgApiFactory factory)
    {
        _factory = factory;
        _http = factory.CreateClient();
    }

    public async Task InitializeAsync()
    {
        var r = await _http.PostAsJsonAsync("/api/test/reset", new { });
        r.EnsureSuccessStatusCode();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task The_report_prices_the_real_imported_catalog_and_writes_nothing()
    {
        using var store = _factory.OpenStore(); // the same memory databases the host opened
        store.Init();
        SeedThroughTheRealImportPath(store);

        var hashBefore = store.ComputeContentHash().ToCompact();
        var pricedBefore = store.ListActionPricing().Count;

        var doc = await _http.GetFromJsonAsync<JsonElement>("/api/debug/action-budget-report");

        Assert.Equal("action-budget-report", doc.GetProperty("kind").GetString());
        Assert.True(doc.GetProperty("cap").GetInt32() > 0, "the loaded rung table must have a cap");

        var priced = doc.GetProperty("pricedActionCount").GetInt32();
        Assert.Equal(pricedBefore, priced);
        Assert.True(priced > 0, "the real import path must leave priced actions for the report to read");

        // Reconciliation, not a count: the per-rung readings account for every priced action.
        var rungs = doc.GetProperty("rungs").EnumerateArray().ToList();
        Assert.NotEmpty(rungs);
        Assert.Equal(priced, rungs.Sum(r => r.GetProperty("n").GetInt32()));

        foreach (var rung in rungs.Where(r => r.GetProperty("n").GetInt32() > 0))
        {
            var min = rung.GetProperty("min").GetInt64();
            var p50 = rung.GetProperty("p50").GetInt64();
            var p90 = rung.GetProperty("p90").GetInt64();
            var max = rung.GetProperty("max").GetInt64();
            Assert.True(min <= p50 && p50 <= p90 && p90 <= max,
                        $"rung {rung.GetProperty("rung").GetInt32()} is not ordered: {min}/{p50}/{p90}/{max}");
        }

        foreach (var rung in rungs.Where(r => r.GetProperty("n").GetInt32() == 0))
            Assert.Equal(JsonValueKind.Null, rung.GetProperty("max").ValueKind);

        // ST4.5b (contract 6): every rung carries the outlier list, and every id it names is a priced
        // action that really sits on that rung -- checked against the store rather than a count.
        var rungOf = store.ListActionPricing()
            .ToDictionary(p => p.ActionId, p => p.AuthoredRung, StringComparer.Ordinal);
        foreach (var rung in rungs)
        {
            var rungNumber = rung.GetProperty("rung").GetInt32();
            var aboveP90 = rung.GetProperty("aboveP90").EnumerateArray().Select(e => e.GetString()!).ToList();

            if (rung.GetProperty("n").GetInt32() == 0) Assert.Empty(aboveP90);
            foreach (var id in aboveP90) Assert.Equal(rungNumber, rungOf[id]);
        }

        Assert.Equal(JsonValueKind.Number, doc.GetProperty("recommendedReferencePower").ValueKind);

        // ST4.5f: the argmax, always named when there is a scalar to name -- and every id in it is a
        // priced action, checked against the store rather than a count. `aboveP90` cannot carry this on
        // a rung with fewer than ten actions, which is what the field exists for.
        var recommendedBy = doc.GetProperty("recommendedBy").EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.NotEmpty(recommendedBy);
        foreach (var id in recommendedBy) Assert.Contains(id, rungOf.Keys);

        // ST4.5e: the per-action unpriced findings reconcile with the store's own, so a 0 in a rung's
        // distribution can be told apart from an action whose atoms could not be priced.
        var unpricedInStore = store.ListActionPricing()
            .Where(p => p.UnpricedAtomIds is { Count: > 0 })
            .Select(p => p.ActionId)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
        var unpricedOnWire = doc.GetProperty("unpricedActions").EnumerateArray()
            .Select(e => e.GetProperty("actionId").GetString()!)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(unpricedInStore, unpricedOnWire);
        foreach (var entry in doc.GetProperty("unpricedActions").EnumerateArray())
            Assert.Contains(entry.GetProperty("actionId").GetString()!, rungOf.Keys);

        // No write: the content hash and the priced set are identical after the read.
        Assert.Equal(hashBefore, store.ComputeContentHash().ToCompact());
        Assert.Equal(pricedBefore, store.ListActionPricing().Count);
    }

    /// <summary>The real import path, the same three committed brief files `Program.cs` boots with.
    ///
    /// <para>The atom seed comes first and is the reason the corpus is small here: exactly two of the
    /// families the real briefs name exist under `gk-data/packs/fusion/data/seed/atoms/` (a documented content-authoring gap
    /// in the sibling atom pipeline, measured in `ActionCorpusRealContentQualityTests`), so a real
    /// import composes three actions rather than twenty-four. Three priced actions is all this test
    /// needs, and taking the gap into account is the difference between a real reading and a fixture.
    /// The boot import is idempotent, so both halves are safe on a host that already imported.</para>
    /// </summary>
    static void SeedThroughTheRealImportPath(RpgStore store)
    {
        var root = RepoRoot();

        var atomsPath = Path.Combine(root, "data", "seed", "atoms", "generated", "family-expand.g-life.json");
        var collected = AtomSeedFile.Collect(new[] { (atomsPath, File.ReadAllText(atomsPath)) });
        Assert.True(collected.IsOk, string.Join("; ", collected.Errors));
        store.UpsertAtoms(collected.Content.Atoms);

        if (store.ListActionIds().Count > 0) return;

        var template = ActionCorpusCostTemplateLoader.Parse(File.ReadAllText(
            Path.Combine(root, "data", "tuning", "action-corpus-cost-templates.v2.json")));

        var briefs = new List<ActionCorpusBrief>();
        foreach (var file in new[] { "committed-round-1.json", "committed-round-2.json", "authored-basics.json" })
        {
            var path = Path.Combine(root, "data", "seed", "actions", file);
            if (File.Exists(path)) briefs.AddRange(ActionCorpusBriefJson.Parse(File.ReadAllText(path)));
        }

        ActionCorpusImporter.Import(store, briefs, template, RungPolicy.Table);
    }

    static string RepoRoot([CallerFilePath] string here = "")
    {
        return KeepverseRoots.Core();
    }
}
