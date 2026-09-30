using System.Text.Json;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Sockets;
using FusionRpg.Core.Items.Uniques;
using Xunit;
using Xunit.Abstractions;

namespace FusionRpg.Server.Tests;

/// <summary>
/// species-gear-chain T8 (spec-gem-tier.md Testing strategy): the tier is DERIVED from the authored
/// powerBand through the one shared mirror — asserted as the relationship recomputed per entry,
/// never as a table of expected values. No population count is asserted anywhere: gem,
/// combination, recipe and ingredient totals are readings that move when content ships
/// (validation-ssot.md §1); what is pinned is joins, the closed band vocabulary, and the
/// mirror/registry reconciliation.
/// </summary>
public class GemTierTests
{
    readonly ITestOutputHelper _out;

    public GemTierTests(ITestOutputHelper output) => _out = output;

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Core"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("repo root");
    }

    static string Seed(params string[] parts) =>
        Path.Combine(new[] { RepoRoot(), "data", "seed" }.Concat(parts).ToArray());

    static SocketTuning ShippedSocketTuning() => SocketTuning.Parse(
        File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", SocketTuningFiles.Current)));

    static IReadOnlyList<(string Id, string Family, string Band)> GemEntries()
    {
        var entries = new List<(string, string, string)>();
        foreach (var file in Directory.EnumerateFiles(Seed("items", "gems"), "*.json", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ThenBy(f => f))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (!doc.RootElement.TryGetProperty("entries", out var arr) || arr.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var e in arr.EnumerateArray())
                entries.Add((
                    e.GetProperty("id").GetString()!,
                    e.GetProperty("family").GetString()!,
                    e.GetProperty("powerBand").GetString()!));
        }
        return entries;
    }

    // ── the derivation ───────────────────────────────────────────────────────────────

    [Fact]
    public void Every_gem_entry_reports_TierOfPowerBand_of_its_own_powerBand()
    {
        var lookup = GemInsertCorpus.Load(Seed("items", "gems"));
        var count = 0;
        foreach (var (id, _, band) in GemEntries())
        {
            var found = lookup(id);
            Assert.NotNull(found);
            Assert.Equal(UniqueBudget.TierOfPowerBand(band), found!.Value.Def.Tier);
            count++;
        }
        _out.WriteLine($"gem entries carrying a derived tier: {count}");
        Assert.True(count > 0);
    }

    [Fact]
    public void An_unknown_powerBand_is_a_rejection_naming_the_gem_never_a_silent_tier_1()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gem-tier-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "bad.json"),
                """{"entries": [{"id": "gem.bad-band", "family": "atom.might", "element": "fire", "powerBand": "mythic", "nameKey": "x", "name": "Bad Band"}]}""");
            var ex = Assert.Throws<GemCorpusRejection>(() => GemInsertCorpus.Load(dir));
            Assert.Contains("gem.bad-band", ex.Message);
            Assert.Contains("mythic", ex.Message);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void UnauthoredInsertTier_is_reached_only_for_a_container_the_corpus_does_not_carry()
    {
        Assert.Equal(1, GemInsertCorpus.UnauthoredInsertTier);
        var lookup = GemInsertCorpus.Load(Seed("items", "gems"));
        Assert.Null(lookup("gem.not-a-gem"));
    }

    // ── the mirror and its source ────────────────────────────────────────────────────

    [Fact]
    public void TierOfPowerBand_agrees_with_bands_v1_tierMap_key_for_key()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(RepoRoot(), "data", "seed", "items", "_registry", "bands.v1.json")));
        var tierMap = doc.RootElement.GetProperty("powerBand").GetProperty("tierMap");
        var names = new List<string>();
        foreach (var band in tierMap.EnumerateObject())
        {
            Assert.Equal(band.Value.GetInt32(), UniqueBudget.TierOfPowerBand(band.Name));
            names.Add(band.Name);
        }
        // The closed five — pinned with its reason (bands.v1.json:46: one band per atom tier).
        Assert.Equal(5, names.Count);
        Assert.Equal(5, doc.RootElement.GetProperty("powerBand").GetProperty("bandCount").GetInt32());
    }

    // ── the soft axis stays soft ─────────────────────────────────────────────────────

    [Fact]
    public void A_tier_above_insertTiers_count_is_accepted_by_the_socket_layer()
    {
        // insertTiers.count bounds RECIPE authoring; it must never clamp what an insert may become.
        // An insert above the ladder's rung-1 floor satisfies it like any other sufficient tier
        // (SSH7.8: the floor is the recipe's `BaseFloors`, no longer a per-ingredient member).
        var tuning = ShippedSocketTuning();
        var recipe = new ComboRecipe("combo.test-high", ComboShape.Strain, "", 0,
            "", "", MinSockets: 1, BaseTier: 1,
            new[] { new ComboIngredient("atom.might", Quantity: 1) }, BaseFloors: new[] { 2 });
        var host = new SocketHost("item.test", ItemRole.ArmamentPrimary, "humanoid", SocketCount: 1);
        var fill = new[] { new SocketFill(0, "", new InsertDef("gem.high", "atom.might", "fire", Tier: 6)) };
        var results = CombinationEvaluator.Evaluate(host, fill, new[] { recipe }, tuning);
        Assert.Contains(results, r => r.ComboId == "combo.test-high");
    }

    [Fact]
    public void Every_ingredient_minTier_falls_inside_the_recipe_authoring_range()
    {
        // SSH7.6: the corpus no longer carries a `minTier` at all — the floor is the LADDER's, read
        // from tuning — so this asserts the new SHAPE and the range guarantee at its new home.
        using var socketDoc = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(RepoRoot(), "data", "tuning", SocketTuningFiles.Current)));
        var count = socketDoc.RootElement.GetProperty("insertTiers").GetProperty("count").GetInt32();

        var offenders = new List<string>();
        foreach (var file in new[] { "strains.json", "splices.json" })
        {
            using var combos = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(RepoRoot(), "data", "seed", "items", "combinations", file)));
            foreach (var entry in combos.RootElement.GetProperty("entries").EnumerateArray())
            {
                var id = entry.GetProperty("id").GetString();
                if (entry.TryGetProperty("grantedTier", out _)) offenders.Add($"{id}: carries grantedTier");
                foreach (var ing in entry.GetProperty("ingredients").EnumerateArray())
                    if (ing.TryGetProperty("minTier", out _)) offenders.Add($"{id}: ingredient carries minTier");
            }
        }
        Assert.True(offenders.Count == 0,
            "a combination row still carries a tier number:\n" + string.Join("\n", offenders));

        Assert.All(ShippedStrainSplice().TierLadder[0].Floors, floor => Assert.InRange(floor, 1, count));
    }

    // ── the acceptance test ──────────────────────────────────────────────────────────

    [Fact]
    public void Every_combination_whose_families_are_all_in_the_corpus_is_satisfiable()
    {
        var lookup = GemInsertCorpus.Load(Seed("items", "gems"));
        var byFamily = new Dictionary<string, List<InsertDef>>(StringComparer.Ordinal);
        foreach (var (id, family, _) in GemEntries())
        {
            var found = lookup(id);
            Assert.NotNull(found);
            if (!byFamily.TryGetValue(family, out var list))
                byFamily[family] = list = new List<InsertDef>();
            list.Add(found!.Value.Def);
        }

        var tuning = ShippedSocketTuning();
        var unfireable = new List<string>();
        var uncoveredFamilies = new HashSet<string>(StringComparer.Ordinal);
        var fired = 0;
        foreach (var file in new[] { "strains.json", "splices.json" })
        {
            using var combos = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(RepoRoot(), "data", "seed", "items", "combinations", file)));
            foreach (var entry in combos.RootElement.GetProperty("entries").EnumerateArray())
            {
                var recipe = ToRecipe(entry);
                var fill = new List<SocketFill>();
                var index = 0;
                var covered = true;
                foreach (var ing in recipe.Ingredients)
                {
                    if (!byFamily.TryGetValue(ing.FamilyId, out var gems))
                    {
                        covered = false;
                        uncoveredFamilies.Add(ing.FamilyId);
                        break;
                    }
                    var tiered = gems.OrderByDescending(g => g.Tier).ToList();
                    // SSH7.8: no per-ingredient floor exists — the rung floors ride the recipe's
                    // `BaseFloors`, so a family with ANY gem is covered; the highest tiers are used
                    // so the recipe's own rung 1 is met.
                    for (var q = 0; q < ing.Quantity; q++)
                        fill.Add(new SocketFill(index++, "", tiered[q % tiered.Count]));
                }
                if (!covered) continue;

                var host = new SocketHost("item.test",
                    ItemRoles.TryParse(recipe.HostRole, out var role) ? role : ItemRole.ArmamentPrimary,
                    string.IsNullOrEmpty(recipe.HostFrame) ? "humanoid" : recipe.HostFrame,
                    SocketCount: fill.Count);
                var results = CombinationEvaluator.Evaluate(host, fill, new[] { recipe }, tuning);
                if (results.Any(r => r.ComboId == recipe.ComboId))
                    fired++;
                else
                    unfireable.Add(recipe.ComboId);
            }
        }

        _out.WriteLine($"combinations fired: {fired}; ingredient families with no gem: {uncoveredFamilies.Count}");
        Assert.True(uncoveredFamilies.Count == 0,
            "ingredient families with no shipped gem:\n" + string.Join("\n", uncoveredFamilies));
        Assert.True(unfireable.Count == 0,
            "covered combinations that did not fire:\n" + string.Join("\n", unfireable));
    }

    [Fact]
    public void An_item_with_no_sockets_and_an_item_with_empty_sockets_resolve_like_today()
    {
        var tuning = ShippedSocketTuning();
        var host = new SocketHost("item.test", ItemRole.ArmamentPrimary, "humanoid", SocketCount: 0);
        var recipe = new ComboRecipe("combo.test-empty", ComboShape.Strain, "", 0,
            "", "", MinSockets: 1, BaseTier: 1,
            new[] { new ComboIngredient("atom.might", Quantity: 1) }, BaseFloors: new[] { 1 });
        Assert.Empty(CombinationEvaluator.Evaluate(host, Array.Empty<SocketFill>(), new[] { recipe }, tuning));
    }

    static FusionRpg.Core.Items.Sockets.StrainSpliceTuning ShippedStrainSplice() =>
        FusionRpg.Core.Items.Sockets.StrainSpliceTuning.Parse(
            File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning",
                FusionRpg.Core.Items.Sockets.SocketTuningFiles.StrainSplice)),
            ShippedSocketTuning());

    static ComboRecipe ToRecipe(JsonElement entry) => new(
        entry.GetProperty("id").GetString()!,
        Enum.Parse<ComboShape>(entry.GetProperty("shape").GetString()!, ignoreCase: true),
        "",
        0,
        // Two strains omit host constraints — "" is the recipe's own "any" (ComboRecipe docs).
        entry.TryGetProperty("hostRole", out var role) ? role.GetString()! : "",
        entry.TryGetProperty("hostFrame", out var frame) ? frame.GetString()! : "",
        entry.GetProperty("minSockets").GetInt32(),
        // SSH7.5/7.6: the tier and rung-1 floors are TUNING's — the corpus carries neither, so this
        // mirrors `CombinationCorpus.ToRecipes` rather than reading fields that no longer exist.
        ShippedStrainSplice().BaseTierFor(
            Enum.Parse<ComboShape>(entry.GetProperty("shape").GetString()!, ignoreCase: true)),
        entry.GetProperty("ingredients").EnumerateArray()
            .Select(i => new ComboIngredient(
                i.GetProperty("family").GetString()!,
                i.TryGetProperty("quantity", out var q) ? q.GetInt32() : 1))
            .ToList(),
        ShippedStrainSplice().TierLadder[0].Floors);
}
