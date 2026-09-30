using FusionRpg.Data;
using FusionRpg.Data.Seed;
using FusionRpg.Data.Tests;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Server.Tests;

/// <summary>
/// E46 player-content-boot — the exact sequence <c>FusionRpg.Server/Program.cs</c> now runs between
/// <c>store.Init()</c> and <c>store.LoadContentIntoRuntime()</c>: check <c>catalog_revision</c>, run
/// <see cref="SeedImportRunner.RunSelfHealing"/> if it is zero, record the outcome, then load runtime
/// tables. <see cref="SeedImportRunnerTests"/> in <c>FusionRpg.Data.Tests</c> already covers the
/// routine's decision logic in isolation with synthetic fixtures; this file proves the SAME wiring
/// against the repo's OWN real <c>gk-data/packs/fusion/data/seed</c> tree — the shape a player's actual first launch hits —
/// rather than inventing another synthetic one, following this project's own precedent
/// (<c>WalkingSkeletonTests</c>'s "never invented from nothing where a real one exists").
///
/// <para>No <c>WebApplicationFactory</c> harness exists in this test project today (checked — every
/// other file here either hits a real endpoint over a live host started elsewhere, or drives
/// <c>RpgStore</c>/service classes directly, the same way <c>WalkingSkeletonTests</c> does), so this
/// follows that existing pattern rather than inventing a full ASP.NET boot for one module.</para>
/// </summary>
public class ContentBootStartupWiringTests : IDisposable
{
    readonly DataTestStore _testStore;

    public ContentBootStartupWiringTests()
    {
        _testStore = DataTestStore.Create();
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    static string RepoRoot()
    {
        return KeepverseRoots.Core();
    }

    /// <summary>The exact block Program.cs runs, reproduced here so a change to either one only needs
    /// to keep matching the other's shape, never a divergent parallel implementation.</summary>
    static (SeedImportRunResult Boot, RpgStore Store) RunStartupSequence(RpgStore store, string searchStartDir)
    {
        var contentBoot = SeedImportRunner.RunSelfHealing(store, searchStartDir);
        store.RecordContentBootOutcome(contentBoot.ContentSource, contentBoot.Detail);

        store.LoadContentIntoRuntime();
        return (contentBoot, store);
    }

    [Fact]
    public void A_fresh_scratch_install_imports_the_repos_real_seed_tree_on_first_boot()
    {
        var (boot, store) = RunStartupSequence(_testStore.Store, RepoRoot());

        Assert.Equal(SeedImportStatus.Imported, boot.Status);
        Assert.Equal("imported", boot.ContentSource);
        Assert.True(store.GetCatalogRevision() > 0);
        Assert.NotEmpty(store.ListAtoms());

        var health = store.ToHealth(simEnabled: false);
        Assert.Equal("imported", health.ContentSource);
        Assert.Null(health.ContentImportError);
        Assert.True(health.CatalogRevision > 0);
    }

    [Fact]
    public void A_second_boot_against_the_same_scratch_db_does_not_reimport()
    {
        var repoRoot = RepoRoot();
        var (firstBoot, _) = RunStartupSequence(_testStore.Store, repoRoot);
        Assert.Equal(SeedImportStatus.Imported, firstBoot.Status);

        // A new RpgStore instance over the SAME storage — the shape a real server restart is:
        // a fresh process, the same databases.
        var (secondBoot, secondStore) = RunStartupSequence(_testStore.Reopen(), repoRoot);

        Assert.Equal(SeedImportStatus.AlreadyCurrent, secondBoot.Status);
        Assert.Null(secondBoot.Outcome);
        Assert.Equal("imported", secondStore.ToHealth(simEnabled: false).ContentSource);
    }

    [Fact]
    public void A_boot_with_no_reachable_seed_tree_still_boots_and_reports_the_fallback()
    {
        // No gk-data/packs/fusion/data/seed anywhere above an isolated temp directory — the shape a distributed player zip
        // is in today, since publish_player.py does not bundle gk-data/packs/fusion/data/seed (see this module's own
        // report: a real, separate packaging gap this test documents but does not fix).
        var isolatedSearchStart = Path.Combine(Path.GetTempPath(), "fusionrpg-no-seed-wiring-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(isolatedSearchStart);
        try
        {
            var (boot, store) = RunStartupSequence(_testStore.Store, isolatedSearchStart);

            Assert.Equal(SeedImportStatus.SeedTreeNotFound, boot.Status);
            Assert.Equal(0, store.GetCatalogRevision());

            // The whole point of E46 (§3.2): the server must still boot and answer, on the fallback,
            // with the fallback VISIBLE rather than indistinguishable from a real import.
            var health = store.ToHealth(simEnabled: false);
            Assert.Equal("codeFallback", health.ContentSource);
            Assert.NotNull(health.ContentImportError);
        }
        finally
        {
            Directory.Delete(isolatedSearchStart, recursive: true);
        }
    }

    /// <summary>
    /// H7 regression tripwire (2026-09-22, lane sgc-4). Two `Program.cs` reader lines were silently reverted
    /// by one cross-lane merge resolution (`8b81e395d`, "Merge branch 'features/mega-merge' into cmdc/cai4"),
    /// with these consequences:
    /// <list type="bullet">
    /// <item>`items` — the `ItemsTuningHub` is read by the live `GET /api/items/{instanceId}/card` path
    /// (`RpgStore.ItemCard.cs:329` -> `ItemNameComposer.cs:32`), which THROWS when unconfigured, and the hub
    /// had no production reader at all before species-gear-chain T59 wired it.</item>
    /// <item>`ai` — `WorldAiTuningLoader.Parse` requires the newest revision's `buildScorer` block
    /// (`WorldAiTuning.cs:81`), so reverting the reader to `ai.v2.json` stopped the server BOOTING: the E2E
    /// harness threw `WorldAiTuningRejection: ai tuning: missing or non-object 'buildScorer'`.</item>
    /// </list>
    /// <para>
    /// The assertions are deliberately **revision-agnostic**: they read the latest revision on disk and
    /// require the boot to name it, which is H7's actual contract (a reader switch lands with its publish).
    /// A future publish therefore keeps this green, while any resolution that pins an older revision fails
    /// here instead of in play.
    /// </para>
    /// </summary>
    [Fact]
    public void The_server_boot_reads_the_latest_revision_of_the_domains_it_owns()
    {
        var program = File.ReadAllText(Path.Combine(RepoRoot(), "src", "FusionRpg.Server", "Program.cs"));
        var tuningDir = Path.Combine(RepoRoot(), "data", "tuning");

        foreach (var domain in new[] { "items", "ai", "build-preset" })
        {
            var latest = Directory.EnumerateFiles(tuningDir, $"{domain}.v*.json")
                .Select(f => int.Parse(Path.GetFileName(f)[(domain.Length + 2)..^".json".Length]))
                .Max();
            Assert.Contains($"{domain}.v{latest}.json", program);
        }

        // And the file the `items` boot line reads must be one the loader actually accepts (no built-in
        // default to fall back to), so the wiring cannot be "present" while pointing at a rejected file.
        var text = File.ReadAllText(Path.Combine(tuningDir, "items.v1.json"));
        var tuning = FusionRpg.Core.Items.ItemsTuningLoader.Parse(text);
        using var doc = System.Text.Json.JsonDocument.Parse(text);
        Assert.Equal(doc.RootElement.GetProperty("rareNameThreshold").GetInt32(), tuning.RareNameThreshold);
        Assert.Equal(doc.RootElement.GetProperty("defaultMaxTier").GetInt32(), tuning.DefaultMaxTier);

        // Same shape for build-preset BP1.10: the first version of a NEW domain, so the boot line and
        // the file land together and the file the boot names is one its loader accepts.
        var buildPresetText = File.ReadAllText(Path.Combine(tuningDir, "build-preset.v1.json"));
        var buildPreset = FusionRpg.Core.BuildPresets.BuildPresetTuningLoader.Parse(buildPresetText);
        using var buildPresetDoc = System.Text.Json.JsonDocument.Parse(buildPresetText);
        Assert.Equal(buildPresetDoc.RootElement.GetProperty("softMaxBuildPresets").GetInt64(),
            buildPreset.SoftMaxBuildPresets);
    }
}
