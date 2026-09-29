using FusionRpg.Data.Seed;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// E46 player-content-boot (spec-player-content-boot.md §5). <see cref="SeedImportRunner.RunSelfHealing"/>
/// is the whole defect this module closes: a real player install never ran <c>gk-forge/tools/AtomImporter</c>,
/// so its content tables stayed empty forever and the server booted on the shipped code fallback with
/// nothing anywhere saying so. These tests exercise the extracted routine directly — the same one both
/// the CLI and <c>FusionRpg.Server/Program.cs</c>'s startup now call — rather than standing up a full
/// ASP.NET host, since the routine itself (not the one line of DI wiring around it) is where the
/// decision logic lives.
/// </summary>
[Trait("Category", "DiskSemantics")]
public class SeedImportRunnerTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    // The self-healing importer's subject is a real seed-corpus directory on disk (the "disk is the
    // thing under test" case); only the store is in memory.
    readonly string _dir;

    public SeedImportRunnerTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _dir = Path.Combine(Path.GetTempPath(), "fusionrpg-selfheal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        _testStore.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    // ---- fixtures -----------------------------------------------------------------------------

    const string ValidAtomFile = """
        {
          "schemaVersion": 1,
          "kind": "atom",
          "entries": [
            {
              "kind": "stat.modify",
              "family": "atom.selfheal-vitality",
              "tier": 1,
              "name": "Self-Heal Vitality I",
              "params": { "channel": "maxHp", "op": "flat", "amount": 45 }
            }
          ]
        }
        """;

    /// <summary>A tree with a real, importable atoms/ folder — <c>data/&lt;root&gt;/data/seed/atoms/...</c>.</summary>
    static string MakeValidSeedTree(string underDir)
    {
        var atomsDir = Path.Combine(underDir, "data", "seed", "atoms");
        Directory.CreateDirectory(atomsDir);
        File.WriteAllText(Path.Combine(atomsDir, "vitality.json"), ValidAtomFile);
        return underDir;
    }

    // ---- test 1: a clean install imports ---------------------------------------------------------

    [Fact]
    public void A_clean_install_imports_and_the_catalog_revision_becomes_nonzero()
    {
        var searchStart = MakeValidSeedTree(_dir + "-tree1");
        try
        {
            var result = SeedImportRunner.RunSelfHealing(_store, searchStart);

            Assert.Equal(SeedImportStatus.Imported, result.Status);
            Assert.True(result.Ok);
            Assert.Equal("imported", result.ContentSource);
            Assert.NotNull(result.Outcome);
            Assert.True(_store.GetCatalogRevision() > 0);
            Assert.NotNull(_store.GetAtom("atom.selfheal-vitality.t1"));
        }
        finally
        {
            Directory.Delete(searchStart, recursive: true);
        }
    }

    // ---- test 2: import skipped (no seed tree reachable) reports the fallback -------------------

    [Fact]
    public void An_install_with_no_reachable_seed_tree_is_skipped_and_reports_the_fallback()
    {
        // Planted violation (spec §5 test 2): a search start with no gk-data/packs/fusion/data/seed anywhere above it —
        // an isolated temp directory, never the repo tree.
        var isolated = Path.Combine(Path.GetTempPath(), "fusionrpg-no-seed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(isolated);
        try
        {
            var result = SeedImportRunner.RunSelfHealing(_store, isolated);

            Assert.Equal(SeedImportStatus.SeedTreeNotFound, result.Status);
            Assert.False(result.Ok);
            Assert.Equal("codeFallback", result.ContentSource);
            Assert.NotNull(result.Detail);
            Assert.Equal(0, _store.GetCatalogRevision());

            // §3.2: absence must stop looking like success — the health surface must say so, not only
            // a log line. RecordContentBootOutcome is the one call site Program.cs makes for this.
            _store.RecordContentBootOutcome(result.ContentSource, result.Detail);
            var health = _store.ToHealth(simEnabled: false);
            Assert.Equal("codeFallback", health.ContentSource);
            Assert.NotNull(health.ContentImportError);
            Assert.Equal(0, health.CatalogRevision);
        }
        finally
        {
            Directory.Delete(isolated, recursive: true);
        }
    }

    // ---- test 3: a corrupt seed file fails loudly but never fatally -------------------------------

    [Fact]
    public void A_corrupt_seed_file_fails_visibly_and_the_store_still_boots_on_the_fallback()
    {
        var searchStart = _dir + "-tree3";
        var atomsDir = Path.Combine(searchStart, "data", "seed", "atoms");
        Directory.CreateDirectory(atomsDir);
        File.WriteAllText(Path.Combine(atomsDir, "broken.json"), "{ this is not valid json");

        try
        {
            var result = SeedImportRunner.RunSelfHealing(_store, searchStart);

            Assert.Equal(SeedImportStatus.Failed, result.Status);
            Assert.False(result.Ok);
            Assert.Equal("codeFallback", result.ContentSource);
            Assert.NotNull(result.Detail);
            Assert.Null(result.Outcome);

            // Loud, and still playable: nothing was written, and the store keeps answering — a failed
            // import must never take the caller down with it (§3.2, §4).
            Assert.Equal(0, _store.GetCatalogRevision());
            Assert.Empty(_store.ListAtoms());

            _store.RecordContentBootOutcome(result.ContentSource, result.Detail);
            Assert.Equal("codeFallback", _store.ToHealth(simEnabled: false).ContentSource);
        }
        finally
        {
            Directory.Delete(searchStart, recursive: true);
        }
    }

    // ---- test 4 + 5: second launch does not re-import; the repair happens exactly once ------------

    [Fact]
    public void A_second_launch_does_not_reimport_and_the_catalog_revision_holds()
    {
        var searchStart = MakeValidSeedTree(_dir + "-tree4");
        try
        {
            var first = SeedImportRunner.RunSelfHealing(_store, searchStart);
            Assert.Equal(SeedImportStatus.Imported, first.Status);
            var revisionAfterFirst = _store.GetCatalogRevision();
            Assert.True(revisionAfterFirst > 0);

            // Second call against the SAME store, same reachable seed tree — the revision check alone
            // must short-circuit before anything is read or written again (§4: a re-import per launch
            // would pay the read cost AND bump the revision, making every already-rolled instance
            // unbindable).
            var second = SeedImportRunner.RunSelfHealing(_store, searchStart);

            Assert.Equal(SeedImportStatus.AlreadyCurrent, second.Status);
            Assert.True(second.Ok);
            Assert.Equal("imported", second.ContentSource);
            // AlreadyCurrent never opens an import transaction at all — this is the direct proof that
            // the second call imported nothing, not just that the revision happens to match.
            Assert.Null(second.Outcome);
            Assert.Equal(revisionAfterFirst, _store.GetCatalogRevision());
        }
        finally
        {
            Directory.Delete(searchStart, recursive: true);
        }
    }

    [Fact]
    public void A_zero_revision_database_repairs_itself_exactly_once()
    {
        // Same shape as the clean-install test, phrased around "once": call the startup path twice
        // against one store starting from revision 0, and prove only the FIRST call is the one that
        // actually imports (spec §5 test 5 — the first-run repair path).
        var searchStart = MakeValidSeedTree(_dir + "-tree5");
        try
        {
            Assert.Equal(0, _store.GetCatalogRevision());

            var calls = new[]
            {
                SeedImportRunner.RunSelfHealing(_store, searchStart),
                SeedImportRunner.RunSelfHealing(_store, searchStart),
            };

            Assert.Equal(SeedImportStatus.Imported, calls[0].Status);
            Assert.Equal(SeedImportStatus.AlreadyCurrent, calls[1].Status);
            Assert.Equal(1, calls.Count(c => c.Outcome is not null));
        }
        finally
        {
            Directory.Delete(searchStart, recursive: true);
        }
    }

    // ---- CLI parity: the finer-grained members Program.cs still calls directly --------------------

    [Fact]
    public void Roots_files_and_collect_still_sweep_and_read_exactly_the_owned_folders()
    {
        var searchStart = MakeValidSeedTree(_dir + "-tree6");
        var seedRoot = Path.Combine(searchStart, "data", "seed");
        try
        {
            var roots = SeedImportRunner.Roots(seedRoot, explicitRoot: false);
            var files = SeedImportRunner.Files(roots);
            Assert.Single(files);

            var collected = SeedImportRunner.Collect(seedRoot, files);
            Assert.True(collected.IsOk, string.Join("; ", collected.Errors));
            Assert.Single(collected.Content.Atoms);
        }
        finally
        {
            Directory.Delete(searchStart, recursive: true);
        }
    }

    [Fact]
    public void The_atom_import_path_ignores_present_dungeon_seed_schemas()
    {
        var searchStart = _dir + "-tree7";
        var atomDir = Path.Combine(searchStart, "data", "seed", "atoms");
        var dungeonDir = Path.Combine(searchStart, "data", "seed", "dungeon", "domains");
        Directory.CreateDirectory(atomDir);
        Directory.CreateDirectory(dungeonDir);
        File.WriteAllText(Path.Combine(atomDir, "vitality.json"), ValidAtomFile);
        File.WriteAllText(Path.Combine(dungeonDir, "domain.json"), "{\"domainId\":\"domain.test\"}");

        try
        {
            var result = SeedImportRunner.RunSelfHealing(_store, searchStart);

            Assert.Equal(SeedImportStatus.Imported, result.Status);
            Assert.NotNull(_store.GetAtom("atom.selfheal-vitality.t1"));
            Assert.DoesNotContain("dungeon", result.Detail ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(searchStart, recursive: true);
        }
    }
}
