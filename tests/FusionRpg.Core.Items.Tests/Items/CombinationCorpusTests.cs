using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items.Sockets;
using Xunit;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// Item module 3 (<c>recipe-import</c>, spec-recipe-import.md §1): the pure mapper from the real
/// <c>gk-data/packs/fusion/data/seed/items/combinations/*.json</c> corpus to <see cref="ComboRecipe"/>. Core reads no
/// file — the tests do the reading, exactly as the host (SSH3.3) will.
///
/// ⛔ No test asserts how many combinations load (<c>recipes + refusals == entries on disk</c> is
/// the contract; the population is content and grows).
/// </summary>
public class CombinationCorpusTests
{
    static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CONTRIBUTING.md")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repo root not found");
    }

    static SocketTuning Sockets() => SocketTuning.Parse(
        File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", SocketTuningFiles.Current)));

    static StrainSpliceTuning Shipped() => StrainSpliceTuning.Parse(
        File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", SocketTuningFiles.StrainSplice)),
        Sockets());

    static string CombinationsDir() =>
        Path.Combine(RepoRoot(), "data", "seed", "items", "combinations");

    static IReadOnlyList<CombinationEntry> ShippedEntries()
    {
        var entries = new List<CombinationEntry>();
        foreach (var file in Directory.EnumerateFiles(CombinationsDir(), "*.json")
                     .OrderBy(f => f, StringComparer.Ordinal))
            entries.AddRange(CombinationCorpus.Parse(File.ReadAllText(file)));
        return entries;
    }

    static ComboIngredient Ingredient(string family, int quantity = 1) =>
        new(family, quantity);

    [Fact]
    public void Every_shipped_combination_entry_maps_or_is_refused_by_name()
    {
        var entries = ShippedEntries();
        Assert.NotEmpty(entries);   // the corpus exists; its size is a reading, never pinned

        var (recipes, refusals) = CombinationCorpus.ToRecipes(entries, Shipped());

        // The contract: every row on disk lands in exactly one list. A silent drop is the defect.
        Assert.Equal(entries.Count, recipes.Count + refusals.Count);
        // A shipped row comes from the same grid the mapper reads, so the shipped corpus maps
        // cleanly today and the skip path is reachable only by an un-validated local tree.
        Assert.Empty(refusals);
        Assert.Equal(entries.Count, recipes.Count);
    }

    [Fact]
    public void A_refusal_names_the_entry_that_caused_it()
    {
        var malformed = new List<CombinationEntry>
        {
            // no id
            new(null, "strain", null, null, null, null, 4, new[] { Ingredient("atom.might") }),
            // unknown shape
            new("combo.strain-might-offense", "puzzle", null, null, null, null, 4,
                new[] { Ingredient("atom.might") }),
            // no ingredients array
            new("combo.splice-might-agility", "splice", null, null, null, null, 4, null),
            // duplicate id
            new("combo.strain-might-offense", "strain", null, null, null, null, 4,
                new[] { Ingredient("atom.might") }),
        };

        var (recipes, refusals) = CombinationCorpus.ToRecipes(malformed, Shipped());

        Assert.Empty(recipes);
        Assert.Equal(malformed.Count, refusals.Count);
        Assert.All(refusals, r => Assert.Equal(AtomRejectionReason.ContentRuleViolated, r.Reason));
        Assert.All(refusals, r => Assert.Contains(StrainSpliceRules.MalformedEntry, r.Detail));
        // "by name": the detail carries the id (or the row's position when it has no id).
        Assert.Contains("entry #1", refusals[0].Detail);
        Assert.Contains("'combo.strain-might-offense'", refusals[1].Detail);
        Assert.Contains("'combo.splice-might-agility'", refusals[2].Detail);
        Assert.Contains("repeats an id", refusals[3].Detail);
    }

    [Fact]
    public void A_local_row_can_never_throw_the_mapper_down()
    {
        // A null row and a non-object JSON row are the two shapes no `CombinationEntry`-typed check
        // can see, and both must still be refusals, never an exception.
        var entries = new CombinationEntry[] { null!, new(null, null, null, null, null, null, 0, null) };
        var (recipes, refusals) = CombinationCorpus.ToRecipes(entries, Shipped());
        Assert.Empty(recipes);
        Assert.Equal(2, refusals.Count);
        Assert.Contains("entry #1 is null", refusals[0].Detail);
    }

    [Fact]
    public void A_shipped_recipe_carries_the_mapped_fields()
    {
        var entries = ShippedEntries();
        var (recipes, refusals) = CombinationCorpus.ToRecipes(entries, Shipped());
        Assert.Empty(refusals);

        var byId = entries.ToDictionary(e => e.Id!, e => e, StringComparer.Ordinal);
        Assert.NotEmpty(recipes);
        foreach (var recipe in recipes)
        {
            var entry = byId[recipe.ComboId];
            Assert.Equal(entry.Shape, ComboShapes.Id(recipe.Shape));
            Assert.Equal(entry.MinSockets, recipe.MinSockets);
            // SSH7.5/7.6: the tier is TUNING's, never the row's — the re-emitted corpus carries no
            // `grantedTier` at all, and the loader would ignore one if it did. SSH7.8 dropped the
            // per-ingredient floor member outright, so the recipe carries only the LADDER's floors.
            Assert.Equal(Shipped().BaseTierFor(recipe.Shape), recipe.BaseTier);
            Assert.Equal(entry.HostRole ?? "", recipe.HostRole);
            Assert.Equal(entry.HostFrame ?? "", recipe.HostFrame);
            Assert.Equal(entry.Ingredients!.Count, recipe.Ingredients.Count);
            // The element/threshold axes belong to the resonances, never to an authored word.
            Assert.Equal("", recipe.Element);
            Assert.Equal(0, recipe.Threshold);
        }
    }

    [Fact]
    public void Parse_reads_a_combination_seed_document_and_skips_a_non_seed_document()
    {
        var strains = CombinationCorpus.Parse(
            File.ReadAllText(Path.Combine(CombinationsDir(), "strains.json")));
        Assert.NotEmpty(strains);
        Assert.All(strains, e => Assert.NotNull(e.Id));

        // A ledger / still-blocked artefact shares the directory and is not corpus content: a
        // document that does not DECLARE the kind reads as zero entries, never as a hard failure.
        Assert.Empty(CombinationCorpus.Parse("{\"schemaVersion\":1,\"rows\":[]}"));

        // A document that DECLARES the kind but has no rows is unreadable, not empty.
        Assert.Throws<InvalidOperationException>(
            () => CombinationCorpus.Parse("{\"kind\":\"combination\"}"));
    }

    [Fact]
    public void The_shipped_corpus_has_no_refusal()
    {
        // The CI contract spec-recipe-import §2 buys with the boot's "skip by name" path: boot may
        // skip a bad row in an un-validated local tree, but a COMMITTED corpus is never allowed to
        // carry one — so the skip path stays unreachable from a commit. Not a population count:
        // zero refusals is a contract on the shipped files.
        var entries = ShippedEntries();
        var sockets = Sockets();
        var strainSplice = Shipped();
        var (recipes, mappingRefusals) = CombinationCorpus.ToRecipes(entries, strainSplice);
        Assert.Empty(mappingRefusals);

        var archetypes = StrainSpliceGridTests.Archetypes();
        var validationRefusals = recipes
            .SelectMany(r => StrainSpliceGrid.ValidateRecipe(r, sockets, archetypes, strainSplice)
                .Select(p => $"{r.ComboId}: {p.Detail}"))
            .ToList();

        // SSH4.4 (spec-combo-bind §1, "one acceptance set"): "no refusal" now covers the CONTAINER
        // build too. A recipe the grid accepts but whose container cannot build (a grant family with no
        // atom) would seed, evaluate as firing, preview as firing — and bind nothing. The accepted set
        // is grid-valid ∩ buildable at every tier the ladder can grant.
        var atomsDir = Path.Combine(RepoRoot(), "data", "seed", "atoms");
        var atomFiles = Directory.EnumerateFiles(atomsDir, "*.json", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => (Path: f, Json: File.ReadAllText(f)));
        var atoms = AtomSeedFile.Collect(atomFiles).Content.Atoms
            .ToDictionary(a => a.AtomId, StringComparer.Ordinal);
        var lookups = new ComboContainerBuild.ComboContainerLookups(
            id => atoms.TryGetValue(id, out var atom) ? atom : null);

        var grantsById = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(CombinationsDir(), "*.json")
                     .OrderBy(f => f, StringComparer.Ordinal))
            foreach (var (id, granted) in CombinationCorpus.ReadGrants(File.ReadAllText(file)))
                grantsById[id] = granted;

        var containerRefusals = new List<string>();
        foreach (var recipe in recipes)
        {
            var grants = grantsById.TryGetValue(recipe.ComboId, out var granted)
                ? granted
                : Array.Empty<string>();
            var tiers = new SortedSet<int>();
            foreach (var rung in strainSplice.TierLadder)
                foreach (var attuned in new[] { false, true })
                    tiers.Add(strainSplice.GrantedTier(recipe.Shape, rung.Rung, sockets, attuned));
            foreach (var tier in tiers)
                if (ComboContainerBuild.TryBuild(recipe.ComboId, grants, tier, lookups, out var refusal) is null)
                    containerRefusals.Add($"{recipe.ComboId} (t{tier}): {refusal}");
        }

        Assert.Empty(containerRefusals);
        Assert.Empty(validationRefusals);
    }

    [Fact]
    public void The_imported_recipe_takes_its_tier_and_floors_from_tuning_never_from_the_row()
    {
        // SSH7.5 (spec-tier-ladder §3): a row that declares a nonsense tier and a real per-ingredient
        // minTier still imports with the TUNING's base tier and the ladder's rung-1 floors carried on
        // the RECIPE — SSH7.8 removed the per-ingredient member, so a row's `minTier` cannot be read
        // even if an older document still carries one, and ITEM-grantedtier-1 removed the entry's own
        // `grantedTier` member for the same reason: the parser no longer models a tier at all, so a
        // legacy document's 9 is not merely ignored — there is nothing to read it into.
        var tuning = Shipped();
        var entries = CombinationCorpus.Parse("""
            {"kind":"combination","entries":[
              {"id":"combo.strain-tier-source","shape":"strain","aptitudes":["Might"],
               "archetype":"offense","hostRole":"core-guard","minSockets":4,
               "ingredients":[{"family":"atom.might","minTier":5,"quantity":4}],
               "grants":["atom.might"],"grantedTier":9}
            ]}
            """);

        var (recipes, refusals) = CombinationCorpus.ToRecipes(entries, tuning);
        var recipe = Assert.Single(recipes);
        Assert.Empty(refusals);

        Assert.Equal(tuning.BaseTierFor(ComboShape.Strain), recipe.BaseTier);
        Assert.NotEqual(9, recipe.BaseTier);                       // the row's own tier is UNREAD
        // The row's own `minTier` is UNREAD too: the ingredient is (family, quantity).
        var ingredient = Assert.Single(recipe.Ingredients);
        Assert.Equal("atom.might", ingredient.FamilyId);
        Assert.Equal(4, ingredient.Quantity);
        Assert.Equal(tuning.TierLadder[0].Floors, recipe.BaseFloors);
    }
}
