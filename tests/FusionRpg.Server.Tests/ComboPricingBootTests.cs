using System.Security.Cryptography;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items.Sockets;
using Xunit;

namespace FusionRpg.Server.Tests;

/// <summary>
/// strain-splice-host SSH6.7 (`combo-budget` §5): the boot computes what it LOADED — the three tuning
/// FILENAME revisions, the structural circuit size and the digest of the combination set the store
/// accepted — and refuses by name when the published pricing was measured against something else.
///
/// <para>The corpus here is the repo's OWN shipped tree (the shape `Program.cs` runs), and the digests
/// are computed by the same Core helper the boot calls.</para>
/// </summary>
public class ComboPricingBootTests
{
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

    static string TuningDir => Path.Combine(RepoRoot(), "data", "tuning");

    /// <summary>⚠ The sockets and strain-splice readers already name their constants; the materials one
    /// moved v5 -> v6 by SSH8.5's combo-budget publish, and this file switches with it (H7: the reader
    /// constant and the publish move in the same commit).</summary>
    static ComboPricingBoot.LoadedTuningFiles ShippedFiles() =>
        new(SocketTuningFiles.Current, SocketTuningFiles.StrainSplice, SocketTuningFiles.Materials);

    /// <summary>The shipped strain-splice tuning, parsed the way the boot parses it. The boot reads the
    /// ladder's rung count from this value (<see cref="StrainSpliceTuning.TierLadder"/>), so a test that
    /// exercises "today's files, one rung" reads the same source instead of restating the number.</summary>
    static StrainSpliceTuning ShippedStrainSplice() =>
        StrainSpliceTuning.Parse(
            File.ReadAllText(Path.Combine(TuningDir, SocketTuningFiles.StrainSplice)),
            SocketTuning.Parse(File.ReadAllText(Path.Combine(TuningDir, SocketTuningFiles.Current))));

    static (IReadOnlyList<ComboRecipe> Recipes,
            IReadOnlyDictionary<string, IReadOnlyList<string>> Grants) ShippedCombinations()
    {
        var sockets = SocketTuning.Parse(File.ReadAllText(Path.Combine(TuningDir, SocketTuningFiles.Current)));
        var strainSplice = StrainSpliceTuning.Parse(
            File.ReadAllText(Path.Combine(TuningDir, SocketTuningFiles.StrainSplice)), sockets);

        var dir = Path.Combine(RepoRoot(), "data", "seed", "items", "combinations");
        var entries = new List<CombinationEntry>();
        var grants = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            var json = File.ReadAllText(file);
            entries.AddRange(CombinationCorpus.Parse(json));
            foreach (var (id, list) in CombinationCorpus.ReadGrants(json)) grants[id] = list;
        }

        var (recipes, _) = CombinationCorpus.ToRecipes(entries, strainSplice);
        return (recipes.Where(r => ComboShapes.IsStrainOrSplice(r.Shape)).ToList(), grants);
    }

    [Fact]
    public void Today_s_files_boot_with_the_published_provenance()
    {
        var (recipes, grants) = ShippedCombinations();
        var loaded = ComboPricingBoot.LoadedRevisions(ShippedFiles(), recipes, grants);

        // Filename revisions, not the files' internal `version` fields. These are READINGS of the files
        // on disk (SSH6.8 published sockets v3), so they are asserted as the loaded/current agreement,
        // never pinned to a literal the next publish would stale.
        Assert.Equal(SocketTuningFiles.RevisionOf(SocketTuningFiles.Current), loaded.SocketsVersion);
        Assert.Equal(SocketTuningFiles.RevisionOf(SocketTuningFiles.StrainSplice), loaded.StrainSpliceVersion);
        Assert.Equal(SocketTuningFiles.RevisionOf(SocketTuningFiles.Materials), loaded.MaterialsVersion);
        Assert.Equal(SocketLimits.SocketCircuitSize, loaded.CircuitSize);
        // SHA-256 renders as 32 bytes of lowercase hex, so the digest's WIDTH is a property of the algorithm:
        // this is the contract, not a corpus count. spec-population-pin: an unmarked pin IS a population pin,
        // and the fix is to rewrite it as the contract — never to add a marker to it.
        Assert.Equal(SHA256.HashSizeInBytes * 2, loaded.CombinationCorpusDigest.Length);
        Assert.True(loaded.CombinationCorpusDigest.All(Uri.IsHexDigit));

        // The published `comboPricing` names exactly what the boot loaded — a one-rung ladder binds
        // nothing today, but the provenance is present and matching, so a corpus or revision drift after
        // the publish is refused by name (the next test proves each field).
        var shippedPricing = SocketTuning.Parse(
            File.ReadAllText(Path.Combine(TuningDir, SocketTuningFiles.Current))).ComboPricingMeasuredAgainst;
        ComboPricingBoot.RequireVerified(shippedPricing, loaded, ShippedStrainSplice());
    }

    [Fact]
    public void Boot_refuses_a_revision_or_corpus_the_pricing_was_not_measured_against()
    {
        var (recipes, grants) = ShippedCombinations();
        var loaded = ComboPricingBoot.LoadedRevisions(ShippedFiles(), recipes, grants);
        var matching = new ComboPricingMeasuredAgainst(
            loaded.SocketsVersion, loaded.StrainSpliceVersion, loaded.MaterialsVersion,
            loaded.CircuitSize, loaded.CombinationCorpusDigest);

        // An exact match binds.
        ComboPricingBoot.RequireVerified(matching, loaded, ShippedStrainSplice());

        // One field at a time, refused by NAME.
        foreach (var (field, changed, expected) in new (string, ComboPricingMeasuredAgainst, string)[]
                 {
                     ("socketsVersion", matching with { SocketsVersion = loaded.SocketsVersion + 1 },
                      "socketsVersion measured"),
                     ("strainSpliceVersion",
                      matching with { StrainSpliceVersion = loaded.StrainSpliceVersion + 1 },
                      "strainSpliceVersion measured"),
                     ("materialsVersion",
                      matching with { MaterialsVersion = loaded.MaterialsVersion + 1 },
                      "materialsVersion measured"),
                     ("circuitSize", matching with { CircuitSize = loaded.CircuitSize + 4 },
                      "circuitSize measured"),
                     ("combinationCorpusDigest", matching with { CombinationCorpusDigest = new string('b', 64) },
                      "combinationCorpusDigest measured"),
                 })
        {
            var refusal = Assert.Throws<ComboPricingProvenanceException>(
                () => ComboPricingBoot.RequireVerified(changed, loaded, ShippedStrainSplice()));
            Assert.Equal(ComboPricingProvenanceRefusal.StaleRule, refusal.Refusal.Rule);
            Assert.Contains(expected, refusal.Message);
            Assert.Contains("re-run the report", refusal.Message);
            Assert.Contains(field, refusal.Refusal.Detail);
        }
    }

    [Fact]
    public void The_digest_covers_ids_families_grants_and_host_pins_and_is_order_independent()
    {
        var (recipes, grants) = ShippedCombinations();
        var digest = CombinationCorpus.Digest(recipes, grants);

        // Order-independent: the reading is a set, and the boot may read the files in any order.
        var shuffled = recipes.OrderByDescending(r => r.ComboId, StringComparer.Ordinal).ToList();
        var shuffledGrants = grants.Reverse().ToDictionary(kv => kv.Key, kv => kv.Value);
        Assert.Equal(digest, CombinationCorpus.Digest(shuffled, shuffledGrants));

        // Every one of the four fields moves it: the id, an ingredient family, a grant, a host pin.
        var first = recipes[0];
        Assert.NotEqual(digest, CombinationCorpus.Digest(
            recipes.Select(r => r == first ? first with { ComboId = first.ComboId + "-x" } : r).ToList(),
            grants));

        var ingredient = first.Ingredients[0];
        var changedIngredients = first with
        {
            Ingredients = first.Ingredients
                .Select((i, at) => at == 0 ? new ComboIngredient("atom.not-the-same", i.Quantity) : i)
                .ToList(),
        };
        Assert.NotEqual(digest, CombinationCorpus.Digest(
            recipes.Select(r => r == first ? changedIngredients : r).ToList(), grants));

        var changedGrant = grants.ToDictionary(
            kv => kv.Key,
            kv => kv.Key == first.ComboId && kv.Value.Count > 0
                ? kv.Value.Select((g, at) => at == 0 ? "atom.not-the-same" : g).ToList()
                : kv.Value);
        Assert.NotEqual(digest, CombinationCorpus.Digest(recipes, changedGrant));

        var changedHost = first with { HostRole = first.HostRole == "core-guard" ? "head-guard" : "core-guard" };
        Assert.NotEqual(digest, CombinationCorpus.Digest(
            recipes.Select(r => r == first ? changedHost : r).ToList(), grants));

        _ = ingredient;
    }
}
