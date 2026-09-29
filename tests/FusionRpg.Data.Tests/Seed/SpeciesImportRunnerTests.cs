using FusionRpg.Data.Seed;
using FusionRpg.TestSupport;
using Xunit;

namespace FusionRpg.Data.Tests.Seed;

/// <summary>
/// E46 player-content-boot, the species half (CS-F3; owner ruling 2026-09-23 in
/// <c>tasks/content-stack-todo.md</c>). <see cref="SpeciesImportRunner.RunSelfHealing"/> is what makes a
/// fresh player install boot at all: since the <c>catalog-runtime</c> flip the roster is store-backed and
/// <c>CreatureSpeciesCatalog.Configure</c> throws on an empty one, while the only writer of those tables
/// was the developer CLI <c>gk-forge/tools/CreatureSpeciesImport</c>.
///
/// <para>The subject is a real species tree on disk (the "disk is the thing under test" case — one test
/// reads the committed tree, one builds a throwaway one); only the store is in memory.</para>
/// </summary>
[Trait("Category", "DiskSemantics")]
public class SpeciesImportRunnerTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    readonly string _dir;

    public SpeciesImportRunnerTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _dir = Path.Combine(Path.GetTempPath(), "fusionrpg-species-heal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        _testStore.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    static string CommittedTree => Path.Combine(ContentRoot.Path, "data", "generated", "creatures");

    static string FirstCommittedSpeciesFile() =>
        Directory.EnumerateFiles(CommittedTree, "*.json")
            .Where(p => !Path.GetFileName(p).StartsWith('_'))
            .OrderBy(p => p, StringComparer.Ordinal)
            .First();

    [Fact]
    public void A_fresh_store_self_heals_the_roster_from_the_committed_tree()
    {
        Assert.Empty(_store.ListSpeciesIds()); // the precondition the whole row is about

        var boot = SpeciesImportRunner.RunSelfHealing(_store, CommittedTree);

        Assert.Equal(SpeciesImportStatus.Imported, boot.Status);
        Assert.True(boot.Ok);
        Assert.NotNull(boot.Outcome);

        // The CONTRACT, not a population: the ids the store reports and the snapshot Program.cs hands to
        // `CreatureSpeciesCatalog.Configure` are the same roster, and that roster is not empty. The size is
        // a reading and is never pinned (validation-ssot.md).
        var ids = _store.ListSpeciesIds();
        Assert.NotEmpty(ids);
        Assert.Equal(ids.Count, _store.BuildCreatureSpeciesSnapshot().Count);
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ids, id => Assert.False(string.IsNullOrWhiteSpace(id)));
    }

    [Fact]
    public void A_second_call_is_a_no_op_and_does_not_re_write_the_roster()
    {
        Assert.Equal(SpeciesImportStatus.Imported, SpeciesImportRunner.RunSelfHealing(_store, CommittedTree).Status);
        var afterFirst = _store.ListSpeciesIds().Count;

        var second = SpeciesImportRunner.RunSelfHealing(_store, CommittedTree);

        Assert.Equal(SpeciesImportStatus.AlreadyCurrent, second.Status);
        Assert.True(second.Ok);
        Assert.Null(second.Outcome); // nothing was read or written
        Assert.Equal(afterFirst, _store.ListSpeciesIds().Count);
    }

    [Fact]
    public void A_missing_tree_is_reported_and_never_throws()
    {
        // No gk-data/packs/fusion/data/generated/creatures anywhere above a fresh temp dir. The runner must answer, not throw:
        // the caller decides, and the server's decision is to log it and let Configure refuse loudly.
        var boot = SpeciesImportRunner.RunSelfHealing(_store, _dir);

        Assert.Equal(SpeciesImportStatus.SpeciesTreeNotFound, boot.Status);
        Assert.False(boot.Ok);
        Assert.NotNull(boot.Detail);
        Assert.Contains("data/generated/creatures", boot.Detail, StringComparison.Ordinal);
        Assert.Empty(_store.ListSpeciesIds());
    }

    [Fact]
    public void An_underscore_sibling_is_not_a_species_row_and_one_real_row_still_imports()
    {
        // `gk-data/packs/fusion/data/generated/creatures` holds `_species-build-plan.json` and `_fusion-recipes.json` beside the
        // species rows. All three readers (this runner, `RpgHost`, `gk-forge/tools/CreatureSpeciesImport`) skip
        // `_`-prefixed entries; this proves the skip by making the plan file unparseable as a species.
        var tree = Path.Combine(_dir, "data", "generated", "creatures");
        Directory.CreateDirectory(tree);
        File.WriteAllText(Path.Combine(tree, "_species-build-plan.json"), "{ not a species at all }");
        File.Copy(FirstCommittedSpeciesFile(), Path.Combine(tree, "OneRealSpecies.json"));

        var boot = SpeciesImportRunner.RunSelfHealing(_store, _dir);

        Assert.Equal(SpeciesImportStatus.Imported, boot.Status);
        Assert.Single(_store.ListSpeciesIds());
    }

    [Fact]
    public void One_unreadable_species_file_writes_nothing_rather_than_a_partial_roster()
    {
        var tree = Path.Combine(_dir, "data", "generated", "creatures");
        Directory.CreateDirectory(tree);
        File.Copy(FirstCommittedSpeciesFile(), Path.Combine(tree, "OneRealSpecies.json"));
        File.WriteAllText(Path.Combine(tree, "Broken.json"), "{ this is not a ConcreteSpecies }");

        var boot = SpeciesImportRunner.RunSelfHealing(_store, _dir);

        Assert.Equal(SpeciesImportStatus.Failed, boot.Status);
        Assert.False(boot.Ok);
        Assert.Contains("Broken.json", boot.Detail!, StringComparison.Ordinal);
        Assert.Empty(_store.ListSpeciesIds()); // all-or-nothing, so the caller's loud path still fires
    }
}
