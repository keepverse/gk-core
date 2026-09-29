using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Materials;
using FusionRpg.Core.Items.Sockets;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests.Items;

/// <summary>
/// `salvage-craft` (item module 14) at the DAL — the three tables, the one gate-serialised spend
/// transaction, replay/mismatch, and the forced mid-sequence failure. Driven with the REAL shipped
/// tuning and the REAL shipped recipe corpus.
/// </summary>
public class MaterialSpendTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    const long Player = 1;

    public MaterialSpendTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Injector"))) return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("repo root");
    }

    static MaterialTuning Tuning() => MaterialTuning.Parse(
        File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", SocketTuningFiles.Materials)));

    static MaterialRecipeCatalog Catalog() => MaterialRecipeCatalog.Load(
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), "data", "seed", "items", "recipes"), "*.json")
            .OrderBy(f => f, StringComparer.Ordinal).Select(File.ReadAllText),
        Tuning());

    /// <summary>A three-class cost in the fixed spend order, big enough that a partial spend is visible.</summary>
    static IReadOnlyList<MaterialCostLine> Cost(long souls = 40, long substrate = 4, long catalyst = 1) => new[]
    {
        MaterialCostLine.Souls(souls),
        new MaterialCostLine(MaterialClass.Substrate, "substrate.humanoid.crude", substrate),
        new MaterialCostLine(MaterialClass.Catalyst, "catalyst.forge", catalyst),
    };

    void Fund(long souls = 1000, long substrate = 100, long catalyst = 10)
    {
        _store.AwardSouls(Player, souls, "test.seed", Guid.NewGuid().ToString("N"));
        _store.GrantMaterials(Player, new[]
        {
            ("substrate.humanoid.crude", substrate),
            ("catalyst.forge", catalyst),
        });
    }

    // ---- schema + import ---------------------------------------------------------------------------

    [Fact]
    public void The_real_recipe_corpus_round_trips_through_the_store()
    {
        var catalog = Catalog();
        var imported = _store.ImportRecipeCatalog(catalog);
        Assert.Equal(catalog.Recipes.Count, imported);
        Assert.Equal(catalog.Recipes.Count, _store.CountRecipes());

        // Idempotent: importing the same catalog twice leaves the same row count, not a duplicate set.
        Assert.Equal(catalog.Recipes.Count, _store.ImportRecipeCatalog(catalog));
        Assert.Equal(catalog.Recipes.Count, _store.CountRecipes());
    }

    [Fact]
    public void Salvage_yield_is_seeded_for_all_ten_rungs_and_matches_the_tuning()
    {
        // ⭐ The sixth `rarity_budget` key, whose shape ssot-rarity.md §5 recorded as "awaiting I9"
        // until this module decided it. SetRarityBudget runs RarityBudgetKeys.Validate inside the
        // store, so a key without a decided consumer shape could not be written at all.
        var tuning = Tuning();
        _store.SeedSalvageYield(tuning.Salvage);

        foreach (var rungId in RarityLadder.RungIds)
        {
            var seeded = _store.GetRarityBudget(rungId, "salvage_yield");
            Assert.NotNull(seeded);
            Assert.Equal((int)tuning.Salvage[rungId].SubstrateBase, seeded!.Value);
        }

        Assert.True(RarityBudgetKeys.IsRegistered("salvage_yield"));

        // ⭐ reroll_cost_mult left the awaiting list 2026-09-05 (module 15), and socket_min/socket_max
        // followed the same day (module 16, `sockets` — two integers per rung, the drop grant window).
        // Every key in the closed list is now decided, so the SC7 gate is pinned against a key with no
        // consumer at all rather than against a real one: the mechanism must survive the list being
        // fully decided today, because the next key added will not be.
        foreach (var key in new[] { "socket_min", "socket_max" })
        {
            Assert.True(RarityBudgetKeys.IsRegistered(key));
            _store.SetRarityBudget("chaff", key, 0);
        }

        Assert.Throws<RarityBudgetKeyRejection>(() => _store.SetRarityBudget("chaff", "no_such_key", 1));
    }

    // ---- the spend transaction ---------------------------------------------------------------------

    [Fact]
    public void A_spend_debits_every_leg_and_writes_exactly_one_log_row()
    {
        Fund();
        var before = _store.GetSoulBalance(Player).Balance;

        var result = _store.TrySpendRecipe(Player, "recipe.001", Cost(), "corr-1");
        Assert.True(result.Ok);
        Assert.Equal("", result.Reason);

        Assert.Equal(before - 40, _store.GetSoulBalance(Player).Balance);
        Assert.Equal(96, _store.GetMaterialQty(Player, "substrate.humanoid.crude"));
        Assert.Equal(9, _store.GetMaterialQty(Player, "catalyst.forge"));
        Assert.Equal(1, _store.CountMaterialSpendLog(Player));
    }

    [Fact]
    public void A_replayed_correlation_returns_the_original_outcome_and_spends_nothing()
    {
        // Copied from RpgStore.Souls.cs's TrySpendSouls contract, not invented.
        Fund();
        var first = _store.TrySpendRecipe(Player, "recipe.001", Cost(), "corr-replay", (_, _) => "instance-42");
        Assert.True(first.Ok);
        Assert.Equal("instance-42", first.OutcomeRef);

        var balance = _store.GetSoulBalance(Player).Balance;
        var substrate = _store.GetMaterialQty(Player, "substrate.humanoid.crude");

        var replay = _store.TrySpendRecipe(Player, "recipe.001", Cost(), "corr-replay", (_, _) => "instance-99");
        Assert.True(replay.Ok);
        Assert.Equal("replay", replay.Reason);
        Assert.Equal("instance-42", replay.OutcomeRef);   // the ORIGINAL outcome, not a fresh one

        Assert.Equal(balance, _store.GetSoulBalance(Player).Balance);
        Assert.Equal(substrate, _store.GetMaterialQty(Player, "substrate.humanoid.crude"));
        Assert.Equal(1, _store.CountMaterialSpendLog(Player));
    }

    [Fact]
    public void A_reused_correlation_with_different_arguments_is_refused()
    {
        // `correlation.mismatch`, not a silent replay: a reused correlation carrying a DIFFERENT
        // request is a caller bug, and returning someone else's outcome would hide it.
        Fund();
        Assert.True(_store.TrySpendRecipe(Player, "recipe.001", Cost(), "corr-x").Ok);

        var different = _store.TrySpendRecipe(Player, "recipe.001", Cost(souls: 80), "corr-x");
        Assert.False(different.Ok);
        Assert.Equal("correlation.mismatch", different.Reason);

        var otherRecipe = _store.TrySpendRecipe(Player, "recipe.002", Cost(), "corr-x");
        Assert.False(otherRecipe.Ok);
        Assert.Equal("correlation.mismatch", otherRecipe.Reason);

        Assert.Equal(1, _store.CountMaterialSpendLog(Player));
    }

    [Fact]
    public void A_refusal_writes_nothing_so_a_retried_refusal_re_evaluates()
    {
        _store.AwardSouls(Player, 1000, "test.seed", Guid.NewGuid().ToString("N"));
        // No materials granted yet — the substrate leg cannot be paid.
        var refused = _store.TrySpendRecipe(Player, "recipe.001", Cost(), "corr-retry");
        Assert.False(refused.Ok);
        Assert.Equal("materials.insufficient", refused.Reason);
        Assert.Equal(0, _store.CountMaterialSpendLog(Player));
        Assert.Equal(1000, _store.GetSoulBalance(Player).Balance);   // the souls leg rolled back

        // Same correlation, now payable: it re-evaluates rather than replaying the refusal.
        _store.GrantMaterials(Player, new[] { ("substrate.humanoid.crude", 10L), ("catalyst.forge", 2L) });
        var second = _store.TrySpendRecipe(Player, "recipe.001", Cost(), "corr-retry");
        Assert.True(second.Ok);
        Assert.Equal("", second.Reason);
        Assert.Equal(1, _store.CountMaterialSpendLog(Player));
    }

    [Fact]
    public void A_forced_mid_sequence_failure_leaves_zero_rows_across_all_three_stores()
    {
        // The ExecuteFusion forced-failure shape: throw from step 5 (perform), which runs after every
        // leg is debited and before the log row is written. Materials, the souls ledger and the spend
        // log must all be untouched.
        Fund();
        var soulsBefore = _store.GetSoulBalance(Player).Balance;
        var substrateBefore = _store.GetMaterialQty(Player, "substrate.humanoid.crude");
        var catalystBefore = _store.GetMaterialQty(Player, "catalyst.forge");

        Assert.Throws<InvalidOperationException>(() =>
            _store.TrySpendRecipe(Player, "recipe.001", Cost(), "corr-boom",
                (_, _) => throw new InvalidOperationException("forced mid-sequence failure")));

        Assert.Equal(soulsBefore, _store.GetSoulBalance(Player).Balance);
        Assert.Equal(substrateBefore, _store.GetMaterialQty(Player, "substrate.humanoid.crude"));
        Assert.Equal(catalystBefore, _store.GetMaterialQty(Player, "catalyst.forge"));
        Assert.Equal(0, _store.CountMaterialSpendLog(Player));
    }

    [Fact]
    public void A_partial_failure_late_in_the_sequence_rolls_the_earlier_legs_back()
    {
        // The conditional decrement's own guarantee: a zero row count fails the WHOLE transaction,
        // so the souls and substrate already debited come back.
        Fund(catalyst: 0);
        var soulsBefore = _store.GetSoulBalance(Player).Balance;

        var result = _store.TrySpendRecipe(Player, "recipe.001", Cost(), "corr-partial");
        Assert.False(result.Ok);
        Assert.Equal("materials.insufficient", result.Reason);

        Assert.Equal(soulsBefore, _store.GetSoulBalance(Player).Balance);
        Assert.Equal(100, _store.GetMaterialQty(Player, "substrate.humanoid.crude"));
        Assert.Equal(0, _store.CountMaterialSpendLog(Player));
    }

    [Fact]
    public void Spend_order_is_enforced_at_the_write_boundary_not_only_in_the_resolver()
    {
        // Fixed class order is what makes two logs of one refusal byte-comparable, so a caller that
        // reorders the lines is refused rather than trusted.
        Fund();
        var outOfOrder = new[]
        {
            new MaterialCostLine(MaterialClass.Catalyst, "catalyst.forge", 1),
            MaterialCostLine.Souls(40),
        };

        var ex = Assert.Throws<ArgumentException>(() =>
            _store.TrySpendRecipe(Player, "recipe.001", outOfOrder, "corr-order"));
        Assert.Contains("fixed spend order", ex.Message);
    }

    [Fact]
    public void An_unknown_material_id_throws_at_the_write_boundary()
    {
        // RpgStore.Fusion.cs:391's guard, kept: a typo must never silently no-op into a phantom row.
        Fund();
        var bad = new[]
        {
            MaterialCostLine.Souls(10),
            new MaterialCostLine(MaterialClass.Substrate, "substrate.mineral.crude", 1),
        };

        Assert.Throws<ArgumentException>(() => _store.TrySpendRecipe(Player, "recipe.001", bad, "corr-bad"));
        Assert.Throws<ArgumentException>(() => _store.GrantMaterials(Player, new[] { ("essence.fire.pvz", 1L) }));
        Assert.Equal(0, _store.CountMaterialSpendLog(Player));
    }

    [Fact]
    public void A_real_resolved_recipe_spends_end_to_end()
    {
        // The whole loop on real content: resolve the shipped grade-2 plant forge against the real
        // reference table, fund exactly what it asks for, and spend it.
        var catalog = Catalog();
        _store.ImportRecipeCatalog(catalog);

        var recipe = catalog.Recipes.Values.First(r =>
            r.Operation == CraftOperation.Forge && r.CostLines.Any(l => l.MaterialId == "substrate.plant.sound"));
        var lines = catalog.Resolve(recipe.RecipeId, new RecipeContext(0, 1, 25, "plant", 0));

        _store.AwardSouls(Player, lines.Single(l => l.Class == MaterialClass.Souls).Qty, "test.seed",
            Guid.NewGuid().ToString("N"));
        _store.GrantMaterials(Player, lines.Where(l => l.Class != MaterialClass.Souls)
            .Select(l => (l.MaterialId, l.Qty)).ToList());

        var result = _store.TrySpendRecipe(Player, recipe.RecipeId, lines, "corr-e2e", (_, _) => "forged-instance");
        Assert.True(result.Ok, result.Reason);
        Assert.Equal("forged-instance", result.OutcomeRef);

        // Everything the recipe asked for is gone, to the unit.
        Assert.Equal(0, _store.GetSoulBalance(Player).Balance);
        foreach (var l in lines.Where(l => l.Class != MaterialClass.Souls))
            Assert.Equal(0, _store.GetMaterialQty(Player, l.MaterialId));
    }

    [Fact]
    public void The_salvage_yield_of_a_forged_base_can_be_granted_back_and_is_strictly_less()
    {
        // R2 at the DAL rather than in the abstract: forge, then salvage the output, and the
        // substrate the player holds afterwards is strictly below what they started with.
        var catalog = Catalog();
        var tuning = Tuning();
        var recipe = catalog.Recipes.Values.First(r =>
            r.Operation == CraftOperation.Forge && r.CostLines.Any(l => l.MaterialId == "substrate.plant.crude"));
        var lines = catalog.Resolve(recipe.RecipeId, new RecipeContext(0, 1, 0, "plant", 0));

        var substrateCost = lines.Single(l => l.Class == MaterialClass.Substrate);
        _store.AwardSouls(Player, 10_000, "test.seed", Guid.NewGuid().ToString("N"));
        _store.GrantMaterials(Player, new[] { (substrateCost.MaterialId, 100L), ("catalyst.forge", 10L) });

        Assert.True(_store.TrySpendRecipe(Player, recipe.RecipeId, lines, "corr-r2").Ok);
        var afterForge = _store.GetMaterialQty(Player, substrateCost.MaterialId);

        var yield = SalvagePolicy.Yield(
            new SalvageInput(0, 0, "plant", 0, new Dictionary<string, int>(), 0), tuning);
        _store.GrantMaterials(Player, yield.Select(l => (l.MaterialId, l.Qty)).ToList());

        Assert.True(_store.GetMaterialQty(Player, substrateCost.MaterialId) < 100,
            "forging and salvaging back must leave the player with strictly less substrate than they started with");
        Assert.True(_store.GetMaterialQty(Player, substrateCost.MaterialId) > afterForge);
        Assert.Equal(9, _store.GetMaterialQty(Player, "catalyst.forge"));   // and no forge catalyst comes back
    }
}

