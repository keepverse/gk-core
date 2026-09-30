using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items.Sockets;
using FusionRpg.Data;
using FusionRpg.Data.Tests;
using Xunit;

namespace FusionRpg.Server.Tests;

/// <summary>
/// Item module 3 (<c>recipe-import</c>, spec-recipe-import.md §2/§4) — the boot import against a real
/// in-memory store and the repo's OWN real <c>gk-data/packs/fusion/data/seed/items/combinations</c> tree, the shape
/// <c>Program.cs</c> actually runs. No invented corpus where a real one exists.
/// </summary>
public class CombinationImportTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public CombinationImportTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose() => _testStore.Dispose();

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Core")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("repo root");
    }

    static SocketTuning Sockets() => SocketTuning.Parse(
        File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", SocketTuningFiles.Current)));

    static StrainSpliceTuning StrainSplice(SocketTuning sockets) => StrainSpliceTuning.Parse(
        File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", SocketTuningFiles.StrainSplice)),
        sockets);

    /// <summary>The SSH4.4 container-build lookup over the REAL shipped atom catalog, and the same
    /// catalog UPSERTED into the store first — <c>UpsertContainer</c> validates each atom through the
    /// store's own <c>effect_atom</c> table, which <c>SeedImportRunner</c> populates in production
    /// before this import runs (see Program.cs's ordering note). A test that drives <c>Seed</c> directly
    /// must do the same.</summary>
    static ComboContainerBuild.ComboContainerLookups Atoms(RpgStore store)
    {
        var dir = Path.Combine(RepoRoot(), "data", "seed", "atoms");
        var files = Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => (Path: f, Json: File.ReadAllText(f)));
        var atoms = AtomSeedFile.Collect(files).Content.Atoms;
        foreach (var atom in atoms) store.UpsertAtom(atom);
        var byId = atoms.ToDictionary(a => a.AtomId, StringComparer.Ordinal);
        return new ComboContainerBuild.ComboContainerLookups(
            id => byId.TryGetValue(id, out var atom) ? atom : null);
    }

    [Fact]
    public void Boot_seeds_the_real_corpus_end_to_end()
    {
        var sockets = Sockets();
        var strainSplice = StrainSplice(sockets);
        var archetypes = CombinationBoot.ReadArchetypes(RepoRoot());
        Assert.NotEmpty(archetypes);

        var log = new List<string>();
        var result = CombinationBoot.Seed(
            _store, sockets, strainSplice, archetypes,
            Path.Combine(RepoRoot(), "data", "seed", "items", "combinations"), Atoms(_store), log.Add);

        // Every row on disk became a recipe or a named refusal, and the shipped corpus refuses none.
        Assert.True(result.Partitioned);
        Assert.Empty(result.Refusals);
        Assert.Equal(result.EntriesOnDisk, result.RecipesAccepted);
        Assert.Contains(log, line => line.Contains("combination corpus:", StringComparison.Ordinal));

        var catalog = _store.GetComboRecipes();
        var legal = StrainSpliceGrid.AllIds(archetypes);
        var authored = catalog.Where(r => ComboShapes.IsStrainOrSplice(r.Shape)).ToList();
        Assert.NotEmpty(authored);
        Assert.Contains(authored, r => r.Shape == ComboShape.Strain);
        Assert.Contains(authored, r => r.Shape == ComboShape.Splice);
        // The catalog read back is the grid's own set — a real Strain/Splice, not a stray row.
        Assert.All(authored, r => Assert.Contains(r.ComboId, legal));
        // The resonances stayed where ResonanceGenerator put them.
        Assert.Contains(catalog, r => r.Shape == ComboShape.Pure);
    }

    [Fact]
    public void A_refused_recipe_is_never_seeded()
    {
        var sockets = Sockets();
        var strainSplice = StrainSplice(sockets);
        var archetypes = CombinationBoot.ReadArchetypes(RepoRoot());

        // The `host-cannot-hold` row names `jewel-major` deliberately: its ceiling is 2 in every
        // revision the ceiling table has ever shipped, so the refusal stays the reason asserted here.
        // Pinned to `head-guard` instead (v1's 3), the row was silently ACCEPTED once v2 lifted the
        // helm to 4 and this test failed on a count — the stale-pin class SSH5.1 closed elsewhere.
        var dir = Path.Combine(Path.GetTempPath(), "combo-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "fixture.json"),
                """
                {"kind":"combination","entries":[
                  {"id":"combo.strain-might-offense","shape":"strain","aptitudes":["Might"],
                   "archetype":"offense","hostRole":"armament-primary","minSockets":4,
                   "ingredients":[{"family":"atom.might","minTier":1,"quantity":4}],"grants":["atom.might"],"grantedTier":1},
                  {"id":"combo.strain-nonesuch-offense","shape":"strain","aptitudes":["Might"],
                   "archetype":"offense","hostRole":"armament-primary","minSockets":4,
                   "ingredients":[{"family":"atom.might","minTier":1,"quantity":4}],"grants":["atom.might"],"grantedTier":1},
                  {"id":"combo.splice-might-agility","shape":"splice","aptitudes":["Might","Agility"],
                   "hostRole":"core-guard","minSockets":4,
                   "ingredients":[{"family":"atom.might","minTier":1,"quantity":3}],"grants":["atom.might"],"grantedTier":1},
                  {"id":"combo.strain-might-balance","shape":"strain","aptitudes":["Might"],
                   "archetype":"balance","hostRole":"jewel-major","minSockets":4,
                   "ingredients":[{"family":"atom.might","minTier":1,"quantity":4}],"grants":["atom.might"],"grantedTier":1}
                ]}
                """);

            var result = CombinationBoot.Seed(_store, sockets, strainSplice, archetypes, dir, Atoms(_store), _ => { });

            Assert.Equal(4, result.EntriesOnDisk);
            Assert.Equal(1, result.RecipesAccepted);
            Assert.Equal(3, result.Refusals.Count);
            Assert.Contains(result.Refusals, d => d.Contains("not-on-the-grid", StringComparison.Ordinal));
            Assert.Contains(result.Refusals, d => d.Contains("ingredient-count", StringComparison.Ordinal));
            Assert.Contains(result.Refusals, d => d.Contains("host-cannot-hold", StringComparison.Ordinal));

            var catalog = _store.GetComboRecipes();
            Assert.Contains(catalog, r => r.ComboId == "combo.strain-might-offense");
            Assert.DoesNotContain(catalog, r => r.ComboId == "combo.strain-nonesuch-offense");
            Assert.DoesNotContain(catalog, r => r.ComboId == "combo.splice-might-agility");
            Assert.DoesNotContain(catalog, r => r.ComboId == "combo.strain-might-balance");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A_recipe_whose_container_cannot_build_is_neither_seeded_nor_previewed()
    {
        // SSH4.4 (spec-combo-bind §1, "one acceptance set"): the grid accepts this recipe, but its
        // grant family has no atom, so the container cannot build — seeding it would make a word that
        // visibly fires and binds nothing. The accepted set is grid-valid ∩ buildable, so the recipe
        // is refused BY NAME and left out.
        var sockets = Sockets();
        var strainSplice = StrainSplice(sockets);
        var archetypes = CombinationBoot.ReadArchetypes(RepoRoot());

        var dir = Path.Combine(Path.GetTempPath(), "combo-container-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "fixture.json"),
                """
                {"kind":"combination","entries":[
                  {"id":"combo.strain-might-offense","shape":"strain","aptitudes":["Might"],
                   "archetype":"offense","hostRole":"armament-primary","minSockets":4,
                   "ingredients":[{"family":"atom.might","minTier":1,"quantity":4}],
                   "grants":["atom.unsuch"]}
                ]}
                """);

            var result = CombinationBoot.Seed(
                _store, sockets, strainSplice, archetypes, dir, Atoms(_store), _ => { });

            Assert.Equal(1, result.EntriesOnDisk);
            Assert.Equal(0, result.RecipesAccepted);
            var refusal = Assert.Single(result.Refusals);
            Assert.Contains("atom.unsuch", refusal, StringComparison.Ordinal);
            Assert.Contains("not in the real generated atom catalog", refusal, StringComparison.Ordinal);

            // Neither seeded nor previewable: no recipe row, and no container row either.
            Assert.DoesNotContain(_store.GetComboRecipes(), r => r.ComboId == "combo.strain-might-offense");
            Assert.Null(_store.GetContainer("combo.strain-might-offense-t" +
                strainSplice.GrantedTier(ComboShape.Strain, 1, sockets, allAttuned: false)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void The_item_card_and_the_endpoint_read_the_same_catalog()
    {
        // §4: after this module every consumer reads the same GetComboRecipes() result. Asserted as a
        // source contract, because "one read" is a wiring fact two endpoints cannot demonstrate by
        // themselves: the endpoint and the card both name that one method, and no file other than the
        // boot import constructs a recipe list.
        var root = RepoRoot();
        Assert.Contains("store.GetComboRecipes()", File.ReadAllText(
            Path.Combine(root, "src", "FusionRpg.Server", "ItemSurfaceEndpoints.cs")));
        Assert.Contains("GetComboRecipes()", File.ReadAllText(
            Path.Combine(root, "src", "FusionRpg.Data", "Sqlite", "RpgStore.ItemCard.cs")));

        var constructionSites = new List<string>();
        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            if (text.Contains("ResonanceGenerator.Generate(", StringComparison.Ordinal) ||
                text.Contains("CombinationCorpus.ToRecipes(", StringComparison.Ordinal))
                constructionSites.Add(Path.GetFileName(file));
        }

        Assert.Equal(new[] { "CombinationBoot.cs" },
            constructionSites.OrderBy(f => f, StringComparer.Ordinal));
    }
}
