using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items.Materials;
using FusionRpg.Core.Items.Sockets;
using Xunit;

namespace FusionRpg.Core.Tests.Balance;

/// <summary>
/// strain-splice-host SSH6.8 (`combo-budget` §1, acceptance: "the BalanceGuard test holds on the
/// shipped tuning and corpus"): <b>no shipped combination is a cheaper route to power than the rarity
/// route.</b>
///
/// <para>This is the SSH6.8 CONTRACT guard, and the one test the CI BalanceGuard step runs
/// (<c>--filter "Category=BalanceGuard"</c>). It reads the REAL shipped tree — the sockets revision
/// <see cref="SocketTuningFiles.Current"/> names, the newest materials/strain-splice revisions, the
/// recipe catalog, the combination corpus, and the atom + rarity seeds through
/// <see cref="AtomSeedFile.Collect"/> (the importer's own reader) — and asserts the INEQUALITY for
/// every cell, never a pinned price, power or ratio. A balance pass may move any coefficient freely;
/// only breaking the contract (a word buying power more cheaply than a rung of base) fails this.</para>
///
/// <para>The measurement itself is <see cref="ComboPricing"/>, the ONE implementation. This test does
/// not re-derive a price — it drives the shipped computation over the shipped content, which is why it
/// can guard the bound without becoming a second pricer.</para>
/// </summary>
[Trait("Category", "BalanceGuard")]
public class ComboPricingBoundGuardTests
{
    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Injector")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("repo root not found above " + AppContext.BaseDirectory);
    }

    static string TuningDir => Path.Combine(RepoRoot(), "data", "tuning");
    static string SeedDir => Path.Combine(RepoRoot(), "data", "seed");

    static IEnumerable<(string Path, string Json)> SeedJson(string directory) =>
        Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories)
                .OrderBy(f => f, StringComparer.Ordinal)
                .Select(f => (Path: f, Json: File.ReadAllText(f)))
            : Enumerable.Empty<(string, string)>();

    /// <summary>The highest <c>{domain}.v{n}.json</c> by FILENAME revision — what the boot's own
    /// `LatestRevision` reads, never a file's internal `version` field.</summary>
    static string Latest(string domain) =>
        Directory.GetFiles(TuningDir, $"{domain}.v*.json")
            .OrderByDescending(path => SocketTuningFiles.RevisionOf(Path.GetFileName(path)))
            .First();

    [Fact]
    public void No_shipped_combination_is_a_cheaper_route_to_power_than_the_rarity_route()
    {
        var sockets = SocketTuning.Parse(File.ReadAllText(Path.Combine(TuningDir, SocketTuningFiles.Current)));
        var strainSplice = StrainSpliceTuning.Parse(
            File.ReadAllText(Path.Combine(TuningDir, SocketTuningFiles.StrainSplice)), sockets);
        var materialsTuning = MaterialTuning.Parse(File.ReadAllText(Latest("materials")));
        var materials = MaterialRecipeCatalog.Load(
            SeedJson(Path.Combine(SeedDir, "items", "recipes")).Select(f => f.Json), materialsTuning);

        // Atoms and the rarity ladder through the importer's own reader, exactly as the report does.
        var collected = AtomSeedFile.Collect(
            SeedJson(Path.Combine(SeedDir, "atoms")).Concat(SeedJson(Path.Combine(SeedDir, "rarity"))));
        Assert.True(collected.IsOk,
            "the atom/rarity seed tree did not collect cleanly:\n"
            + string.Join("\n", collected.Errors.Take(10).Select(e => e.ToString())));
        var atoms = collected.Content.Atoms.ToDictionary(a => a.AtomId, StringComparer.Ordinal);
        var lookups = new ComboContainerBuild.ComboContainerLookups(
            id => atoms.TryGetValue(id, out var atom) ? atom : null);

        // The combination corpus (recipes + the grants its containers are minted from).
        var entries = new List<CombinationEntry>();
        var grants = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (_, json) in SeedJson(Path.Combine(SeedDir, "items", "combinations")))
        {
            entries.AddRange(CombinationCorpus.Parse(json));
            foreach (var (id, list) in CombinationCorpus.ReadGrants(json)) grants[id] = list;
        }
        var (recipes, _) = CombinationCorpus.ToRecipes(entries, strainSplice);
        Assert.NotEmpty(recipes);

        var maxRatio = sockets.ComboPricingMaxRatioToRarityRouteMilli ?? 1000;
        var inputs = new ComboPricingInputs(sockets, collected.Content.Rarities, materials, lookups, maxRatio);
        var steps = ComboPricing.Steps(inputs.RarityLadder, materials);
        var reference = ComboPricing.ChosenStep(steps);

        var failing = new List<string>();
        var measured = 0;
        foreach (var recipe in recipes)
        {
            var comboGrants = grants.TryGetValue(recipe.ComboId, out var list)
                ? list
                : Array.Empty<string>();
            var tiers = recipe.BaseFloors?.ToList() ?? new List<int>();
            Assert.True(tiers.Count == sockets.StrainSpliceIngredientCount,
                $"'{recipe.ComboId}' carries {tiers.Count} rung-1 floor(s); a recipe is " +
                $"{sockets.StrainSpliceIngredientCount} inserts wide");

            foreach (var attuned in new[] { false, true })
            {
                var request = new ComboPricingRequest(
                    ComboId: recipe.ComboId,
                    Grants: comboGrants,
                    Tier: attuned ? recipe.BaseTier + sockets.AttunedTierBonus : recipe.BaseTier,
                    IngredientTiers: tiers,
                    HostRole: string.IsNullOrEmpty(recipe.HostRole) ? null : recipe.HostRole,
                    Attuned: attuned);
                var cell = ComboPricing.MeasureCell(request, inputs, reference);
                measured++;
                if (!cell.Passes)
                    failing.Add(
                        $"{cell.ComboId} t{cell.Tier}{(cell.Attuned ? "+attuned" : "")}: " +
                        $"power {cell.Power} / floor {cell.PriceFloorSouls} souls = {cell.RatioMilli} " +
                        $"> reference {cell.ReferenceMilli} (per 1000 souls)");
            }
        }

        Assert.True(measured > 0, "the shipped corpus priced no cells");
        Assert.True(failing.Count == 0,
            $"{failing.Count} shipped combination cell(s) undercut the rarity route — a word buys power " +
            $"more cheaply than a rung of base, which is the contract this pricing exists to hold:\n"
            + string.Join("\n", failing.Take(20)));
    }
}
