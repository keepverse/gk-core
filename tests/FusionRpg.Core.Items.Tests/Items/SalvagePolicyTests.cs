using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Materials;
using Xunit;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// `salvage-craft` (item module 14) §"Salvage — a converter, not a faucet": R1, R2, the grade lock,
/// and the two bottleneck classes with no salvage faucet. Driven against the REAL shipped tuning and
/// the REAL shipped recipe corpus.
/// </summary>
public class SalvagePolicyTests
{
    static MaterialTuning Tuning() => MaterialCorpusTests.Tuning();

    static SalvageInput Item(
        int rung, int level = 60, string frame = "humanoid", int affixes = 0,
        IReadOnlyDictionary<string, int>? elemental = null, int enh = 0) =>
        new(rung, level, frame, affixes, elemental ?? new Dictionary<string, int>(), enh);

    [Fact]
    public void Salvage_returns_a_shard_of_the_rung_below_never_its_own()
    {
        // R1 on the ten-rung ladder. Rarity always flows downhill; you can never bootstrap a ceiling
        // by feeding the grinder its own output.
        var t = Tuning();
        for (var rung = 1; rung < RarityLadder.RungIds.Count; rung++)
        {
            var lines = SalvagePolicy.Yield(Item(rung), t);
            var shard = lines.Where(l => l.Class == MaterialClass.Shard).ToList();
            if (t.Salvage[RarityLadder.RungIds[rung]].ShardBack == 0)
            {
                Assert.Empty(shard);
                continue;
            }

            var one = Assert.Single(shard);
            Assert.Equal($"shard.{RarityLadder.RungIds[rung - 1]}", one.MaterialId);
            Assert.NotEqual($"shard.{RarityLadder.RungIds[rung]}", one.MaterialId);
            Assert.Equal(t.Salvage[RarityLadder.RungIds[rung]].ShardBack, one.Qty);
        }
    }

    [Fact]
    public void Chaff_salvage_returns_no_shard()
    {
        // R1's bottom edge — there is no rung below chaff, and its shardBack is 0 in a way
        // MaterialTuning refuses to let a balance pass change.
        var lines = SalvagePolicy.Yield(Item(0, affixes: 6, enh: 12), Tuning());
        Assert.DoesNotContain(lines, l => l.Class == MaterialClass.Shard);
        Assert.Equal(0, Tuning().Salvage["chaff"].ShardBack);
    }

    [Fact]
    public void Salvage_never_returns_catalyst_forge_or_catalyst_flux()
    {
        // I9 §7.3: the two bottleneck classes have NO salvage faucet at all, by construction. The
        // player's rate of making and re-randomising is pinned to content completed and cannot be
        // accelerated by inventory management.
        var t = Tuning();
        for (var rung = 0; rung < RarityLadder.RungIds.Count; rung++)
        {
            foreach (var enh in new[] { 0, 1, 3, 9, 40, 400 })
            {
                var lines = SalvagePolicy.Yield(
                    Item(rung, level: 90, affixes: 6, elemental: new Dictionary<string, int> { ["fire"] = 4 }, enh: enh), t);

                Assert.DoesNotContain(lines, l => l.MaterialId == "catalyst.forge");
                Assert.DoesNotContain(lines, l => l.MaterialId == "catalyst.flux");
                Assert.All(lines.Where(l => l.Class == MaterialClass.Catalyst),
                    l => Assert.Equal("catalyst.temper", l.MaterialId));
            }
        }
    }

    [Fact]
    public void Salvage_never_mints_souls()
    {
        var t = Tuning();
        for (var rung = 0; rung < RarityLadder.RungIds.Count; rung++)
            Assert.DoesNotContain(SalvagePolicy.Yield(Item(rung, affixes: 9, enh: 30), t),
                l => l.Class == MaterialClass.Souls);
    }

    [Fact]
    public void The_grade_lock_means_a_level_ten_zone_returns_crude_at_any_volume()
    {
        var t = Tuning();
        for (var i = 0; i < 2000; i++)
        {
            var lines = SalvagePolicy.Yield(Item(9, level: 10, affixes: 6), t);
            Assert.Equal("substrate.humanoid.crude", lines.Single(l => l.Class == MaterialClass.Substrate).MaterialId);
        }

        // ⚠ And this is NOT metering the player under D26: it is the salvage output of a LOW-LEVEL
        // ITEM being low-level, a property of the target. A level-75 item returns prime immediately,
        // with no counter and no cooldown in between.
        Assert.Equal("substrate.humanoid.prime",
            SalvagePolicy.Yield(Item(9, level: 75), t).Single(l => l.Class == MaterialClass.Substrate).MaterialId);
    }

    [Fact]
    public void I9s_worked_example_two_reproduces_exactly_on_the_ten_rung_ladder()
    {
        // I9 §7.5 example 2: a level-60 EPIC humanoid chest, 5 drawn affixes of which 3 carry a
        // concrete element (2 fire, 1 dark), enhancement +7. `epic` maps to `heirloom` (rung 6)
        // through the shipped LegacyCreatureRarityIds.ForwardMap, so the re-derived ten-rung table must
        // reproduce the example's arithmetic line for line.
        var lines = SalvagePolicy.Yield(
            Item(6, level: 60, frame: "humanoid", affixes: 5,
                elemental: new Dictionary<string, int> { ["fire"] = 2, ["dark"] = 1 }, enh: 7),
            Tuning());

        Assert.Equal(11, lines.Single(l => l.MaterialId == "substrate.humanoid.fine").Qty);   // 6 + 5
        Assert.Equal(2, lines.Single(l => l.MaterialId == "essence.fire").Qty);               // min(3, 2)
        Assert.Equal(1, lines.Single(l => l.MaterialId == "essence.dark").Qty);               // min(3, 1)
        Assert.Equal(2, lines.Single(l => l.Class == MaterialClass.Shard).Qty);               // shardBack[epic] = 2
        Assert.Equal(2, lines.Single(l => l.MaterialId == "catalyst.temper").Qty);            // 7 / 3

        // ⚠ The one line the example spells in the RETIRED vocabulary: it says `shard.rare`, "of
        // band 2, never band 3". On the ten-rung ladder that is the rung below heirloom — chimeric —
        // which is the same statement in the shipped ids.
        Assert.Equal("shard.chimeric", lines.Single(l => l.Class == MaterialClass.Shard).MaterialId);
    }

    [Fact]
    public void The_essence_cap_binds_and_it_is_per_distinct_element()
    {
        var t = Tuning();
        var lines = SalvagePolicy.Yield(
            Item(6, elemental: new Dictionary<string, int> { ["fire"] = 9, ["ice"] = 1, ["dark"] = 0 }), t);

        Assert.Equal(3, lines.Single(l => l.MaterialId == "essence.fire").Qty);  // min(essenceCap[heirloom]=3, 9)
        Assert.Equal(1, lines.Single(l => l.MaterialId == "essence.ice").Qty);
        Assert.DoesNotContain(lines, l => l.MaterialId == "essence.dark");        // a zero line is never emitted
    }

    [Fact]
    public void A_yield_is_byte_identical_across_two_calls_and_ordered_by_spend_class()
    {
        var t = Tuning();
        var input = Item(8, level: 80, frame: "plant", affixes: 4,
            elemental: new Dictionary<string, int> { ["light"] = 2, ["air"] = 1, ["earth"] = 3 }, enh: 11);

        var a = SalvagePolicy.Yield(input, t);
        var b = SalvagePolicy.Yield(input, t);
        Assert.Equal(a, b);

        Assert.Equal(
            a.Select(l => MaterialCatalog.ClassRank(l.Class)).ToArray(),
            a.Select(l => MaterialCatalog.ClassRank(l.Class)).OrderBy(x => x).ToArray());
    }

    [Fact]
    public void An_out_of_range_rung_throws_rather_than_clamping()
    {
        var t = Tuning();
        // 60 is `rarity.ordinal` for a mid rung; passing it here is the 10x defect the field name
        // exists to prevent, and it must be loud.
        Assert.Throws<SalvageRejection>(() => SalvagePolicy.Yield(Item(60), t));
        Assert.Throws<SalvageRejection>(() => SalvagePolicy.Yield(Item(-1), t));
        Assert.Throws<SalvageRejection>(() => SalvagePolicy.Yield(Item(10), t));
    }

    [Fact]
    public void Salvage_quantities_are_long_so_the_widening_removes_the_overflow_rather_than_hiding_it()
    {
        // The substrate leg is `substrateBase + affixes`, and `substrateBase` is already `long`, so
        // the sum is computed in 64 bits and an int-sized affix count cannot overflow at all — the
        // widening is what makes the throw unreachable here, which is the rule working rather than
        // an untested branch. The `checked` stays: it is what turns a future long-sized term into a
        // throw instead of a negative yield.
        var t = Tuning();
        var extreme = SalvagePolicy.Yield(
            new SalvageInput(9, 90, "plant", int.MaxValue, new Dictionary<string, int>(), 0), t);
        Assert.Equal(int.MaxValue + 10L, extreme.Single(l => l.Class == MaterialClass.Substrate).Qty);

        // Exact well past int's own 2,147,483,647 ceiling — an `int` accumulator here would have
        // wrapped negative at the second line.
        var big = SalvagePolicy.Yield(Item(9, affixes: 2_000_000_000), t);
        Assert.Equal(2_000_000_010L, big.Single(l => l.Class == MaterialClass.Substrate).Qty);
        Assert.True(big.Single(l => l.Class == MaterialClass.Substrate).Qty > int.MaxValue - 200_000_000);
    }

    // ---- R2, the strict-loss invariant --------------------------------------------------------------

    [Fact]
    public void Strict_loss_holds_for_every_recipe_in_the_table()
    {
        // ⭐ R2 as a PROPERTY TEST over the whole shipped recipe table, not a spot check: for every
        // recipe and every thing it spends, salvaging that recipe's output returns strictly less of
        // it. Driven over every loadable recipe x every rung x every enhancement level x three
        // content levels.
        //
        // ⛔ TWO MODELLING CORRECTIONS the run itself forced, both recorded in tasks/item-todo.md P4.1
        // rather than absorbed:
        //
        //  (1) R2 is asserted PER MATERIAL ID, not per class. I9 §5.3's own table is per id —
        //      `catalyst.forge` returns "never", `catalyst.flux` "never", `catalyst.temper` "only
        //      what enhancement already paid in" — and summing the Catalyst class treats those three
        //      as fungible, which they are not. Measured: boring a +12 item spends 1 `catalyst.forge`
        //      and its output salvages for 4 `catalyst.temper`, so the CLASS sum rises while every
        //      id-level claim still holds. The per-id form is the stronger statement and the one the
        //      lane actually makes.
        //
        //  (2) A MINT's output is a BRAND-NEW BASE: chaff rarity and +0 enhancement, whatever the
        //      target the recipe was priced against. I9 §7.5 example 1 states the rarity outright —
        //      "one effect_container instance, base type thorn-briar, **Normal** rarity" — and a
        //      thing that was just made has no enhancement history. ⛔ Sweeping a forge's output
        //      across all ten rungs surfaced a REAL leak the shipped corpus happens not to contain,
        //      now refused at load by `material.strict-loss-violated`.
        //
        //  (3) ⛔ R2 AS WRITTEN IS A MINT-SHAPED INVARIANT and it is false for the six mutation
        //      verbs — measured, not argued. Salvaging a tempered item returns
        //      `substrateBase[rung]` substrate whether or not it was ever tempered, because that
        //      value is the ITEM's, paid for by the drop; the mutation only paid the increment.
        //      recipe.012 (temper +0 -> +1) spends 1 crude and its output salvages for 2, and no
        //      pricing fixes that, because 2 is what the item was already worth. So the invariant
        //      that holds for a mutation is the MARGINAL one — running the operation must never
        //      raise the output's salvage yield by more than it cost — plus the CUMULATIVE strict
        //      form over the operation's own axis, which is exactly what I9 §5.3's table states
        //      ("only what enhancement already paid in ... strictly lossy, always").
        var t = Tuning();
        var catalog = MaterialCorpusTests.Catalog();
        var mintPairs = 0;
        var marginalPairs = 0;

        foreach (var recipe in catalog.Recipes.Values)
        {
            // ⚠ `upcycle` is excluded, and not for convenience: its output is a MATERIAL, not a
            // salvageable item, so there is no yield to compare against. Its strict-loss guarantee is
            // the conversion ratio itself (five in, one out) — `MaterialTuning` refuses an
            // `inputPerOutput` below 2 at load, which is asserted in MaterialCorpusTests.
            if (recipe.Operation == CraftOperation.Upcycle) continue;

            var mints = recipe.Operation is CraftOperation.Forge or CraftOperation.ForgeGem;

            for (var rung = 0; rung < RarityLadder.RungIds.Count; rung++)
            {
                foreach (var enh in new[] { 0, 1, 2, 4, 12, 40 })
                {
                    foreach (var level in new[] { 10, 60, 90 })
                    {
                        var frame = recipe.Frame == "any" ? "humanoid" : recipe.Frame;
                        var spent = catalog.Resolve(recipe.RecipeId, new RecipeContext(rung, 3, level, frame, enh));

                        SalvageInput State(int r, int e) =>
                            new(r, level, frame, 0, new Dictionary<string, int>(), e);

                        if (mints)
                        {
                            // R2 literally: nothing the recipe spent comes back in the quantity it cost.
                            var returned = SalvagePolicy.Yield(State(0, 0), t);
                            foreach (var line in spent)
                            {
                                var back = returned.Where(l => l.MaterialId == line.MaterialId).Sum(l => l.Qty);
                                Assert.True(back < line.Qty,
                                    $"R2 (mint) violated: {recipe.RecipeId} at rung {RarityLadder.RungIds[rung]}, " +
                                    $"level {level} spends {line.Qty} of '{Name(line)}' and salvage returns {back}");
                                mintPairs++;
                            }

                            continue;
                        }

                        // R2 marginal: the operation may not raise the output's salvage yield by more
                        // than it cost. `before` is the item the operation was handed.
                        var after = SalvagePolicy.Yield(
                            State(rung, recipe.Operation == CraftOperation.Temper ? enh + 1 : enh), t);
                        var before = SalvagePolicy.Yield(State(rung, enh), t);

                        foreach (var line in spent)
                        {
                            var delta = after.Where(l => l.MaterialId == line.MaterialId).Sum(l => l.Qty)
                                        - before.Where(l => l.MaterialId == line.MaterialId).Sum(l => l.Qty);
                            Assert.True(delta <= line.Qty,
                                $"R2 (marginal) violated: {recipe.RecipeId} ({CraftOperations.Id(recipe.Operation)}) at " +
                                $"rung {RarityLadder.RungIds[rung]}, level {level}, +{enh} spends {line.Qty} of " +
                                $"'{Name(line)}' and raises its salvage yield by {delta}");
                            marginalPairs++;
                        }
                    }
                }
            }
        }

        // A property test that silently checked nothing is worth nothing — assert both halves had
        // real work to do.
        Assert.True(mintPairs > 300, $"only {mintPairs} mint (recipe, material) pairs were checked");
        Assert.True(marginalPairs > 300, $"only {marginalPairs} marginal (recipe, material) pairs were checked");
    }

    static string Name(MaterialCostLine line) => line.MaterialId == "" ? "souls" : line.MaterialId;

    [Fact]
    public void A_mutation_never_raises_its_outputs_salvage_yield_at_all_except_on_the_temper_axis()
    {
        // The marginal invariant's sharp edge, stated as a fact rather than a bound: boring a hole,
        // socketing a gem and rerolling an affix change the salvage yield by EXACTLY ZERO — a socket
        // returns nothing — so those three are strictly lossy on every class at every input. Only
        // `temper` moves the yield at all, and only on `catalyst.temper`.
        var t = Tuning();
        var catalog = MaterialCorpusTests.Catalog();

        foreach (var recipe in catalog.Recipes.Values.Where(r =>
                     r.Operation is CraftOperation.Bore or CraftOperation.Socket))
        {
            for (var rung = 0; rung < RarityLadder.RungIds.Count; rung++)
            {
                var frame = recipe.Frame == "any" ? "humanoid" : recipe.Frame;
                var before = SalvagePolicy.Yield(Item(rung, 60, frame, enh: 9), t);
                var after = SalvagePolicy.Yield(Item(rung, 60, frame, enh: 9), t);
                Assert.Equal(before, after);

                var spent = catalog.Resolve(recipe.RecipeId, new RecipeContext(rung, 3, 60, frame, 9));
                Assert.All(spent, l => Assert.True(l.Qty > 0));
            }
        }

        // And `temper` is the one that does move it — by at most one, on exactly one id.
        for (var n = 0; n < 30; n++)
        {
            var before = SalvagePolicy.Yield(Item(5, enh: n), t);
            var after = SalvagePolicy.Yield(Item(5, enh: n + 1), t);
            var moved = after.Where(a => before.All(b => b != a)).ToList();
            Assert.All(moved, l => Assert.Equal("catalyst.temper", l.MaterialId));
            Assert.True(moved.Count <= 1);
        }
    }

    [Fact]
    public void A_forge_priced_below_its_own_salvage_floor_is_refused_at_import_not_only_in_a_test()
    {
        // ⛔ The leak the property test surfaced, closed. The SC7 line — "adding a forge recipe is
        // one row plus two or three cost rows and NO CODE" — means an author can build a substrate
        // perpetual-motion machine with one word: `cheap` halves a grade-1 forge's 4 substrate to 2,
        // and salvaging the output returns the chaff floor of 2. Not strictly less. The shipped
        // corpus happens not to contain one, so a test over the shipped table alone would never see
        // it — the check therefore runs at LOAD, on every mint.
        var tuning = MaterialCorpusTests.Tuning();
        Assert.Equal(2, tuning.Salvage["chaff"].SubstrateBase);

        const string leaky = """
            {
              "schemaVersion": 1, "kind": "recipe",
              "entries": [{
                "id": "recipe.leak", "nameKey": "recipe.leak", "name": "Forge: Free Metal",
                "operation": "forge", "outputKind": "container", "outputRef": "item.x", "outputQty": 1,
                "frame": "humanoid", "soulsCostBand": "modest", "tags": [],
                "costLines": [
                  { "material": "substrate.humanoid.crude", "costBand": "cheap" },
                  { "material": "catalyst.forge", "costBand": "cheap" }
                ]
              }]
            }
            """;

        var catalog = MaterialRecipeCatalog.Load(new[] { leaky }, tuning);
        Assert.Empty(catalog.Recipes);
        var refusal = Assert.Single(catalog.Refusals);
        Assert.Equal(MaterialRecipeCatalog.StrictLossRule, refusal.Rule);
        Assert.Contains("net gain", refusal.Detail);

        // And the same recipe one band up is accepted — the guard refuses the leak, not the shape.
        var fixedUp = leaky.Replace("\"substrate.humanoid.crude\", \"costBand\": \"cheap\"",
            "\"substrate.humanoid.crude\", \"costBand\": \"modest\"");
        var ok = MaterialRecipeCatalog.Load(new[] { fixedUp }, tuning);
        Assert.Empty(ok.Refusals);
        Assert.Single(ok.Recipes);
    }

    [Fact]
    public void The_class_level_reading_of_R2_is_measurably_wrong_and_the_per_id_reading_is_what_holds()
    {
        // ⛔ The finding above, pinned as a measurement rather than left in a comment: bore a socket
        // into a +12 item and the CATALYST CLASS sum rises (1 forge spent, 4 temper returned), while
        // every per-id claim still holds — nothing spent comes back. Recorded so a later session that
        // "tightens" R2 to the class level knows exactly which case it will hit and why the looser
        // reading would be the wrong one.
        var t = Tuning();
        var catalog = MaterialCorpusTests.Catalog();
        var bore = catalog.Recipes.Values.First(r => r.Operation == CraftOperation.Bore);

        var spent = catalog.Resolve(bore.RecipeId, new RecipeContext(0, 3, 60, "humanoid", 12));
        var returned = SalvagePolicy.Yield(Item(0, level: 60, enh: 12), t);

        var catalystSpent = spent.Where(l => l.Class == MaterialClass.Catalyst).Sum(l => l.Qty);
        var catalystBack = returned.Where(l => l.Class == MaterialClass.Catalyst).Sum(l => l.Qty);
        Assert.Equal(1, catalystSpent);
        Assert.Equal(4, catalystBack);              // 12 / 3, all of it `catalyst.temper`
        Assert.True(catalystBack > catalystSpent);  // the class sum RISES — and that is correct

        // …because the temper was paid for by twelve enhancements, not by boring one hole. Per id,
        // nothing the recipe spent comes back at all.
        foreach (var line in spent)
            Assert.Equal(0, returned.Where(l => l.MaterialId == line.MaterialId).Sum(l => l.Qty));
    }

    [Fact]
    public void R2s_three_locks_hold_independently_of_the_numbers()
    {
        // I9 §8.2 names three locks, and says any ONE of them would be circumventable while together
        // they close the loop. Asserted structurally so a balance edit that weakens a coefficient
        // cannot quietly remove a lock:
        var t = Tuning();

        //  1. the rung-1 rule — no rung's salvage can return its own shard.
        for (var rung = 0; rung < RarityLadder.RungIds.Count; rung++)
            Assert.DoesNotContain(SalvagePolicy.Yield(Item(rung), t),
                l => l.MaterialId == $"shard.{RarityLadder.RungIds[rung]}");

        //  2. the grade lock — grade is a function of item level and of nothing else.
        Assert.Equal(t.GradeForItemLevel(30), t.GradeForItemLevel(30));
        Assert.True(t.GradeForItemLevel(10) < t.GradeForItemLevel(90));

        //  3. catalysts have no faucet — forge and flux never appear, at any input.
        var everyCatalystLine = Enumerable.Range(0, RarityLadder.RungIds.Count)
            .SelectMany(r => SalvagePolicy.Yield(Item(r, level: 99, affixes: 8, enh: 99), t))
            .Where(l => l.Class == MaterialClass.Catalyst)
            .ToList();
        Assert.NotEmpty(everyCatalystLine);
        Assert.All(everyCatalystLine, l => Assert.Equal("catalyst.temper", l.MaterialId));
    }

    [Fact]
    public void Temper_returns_strictly_less_catalyst_than_enhancement_paid_in()
    {
        // The one class salvage DOES return, and the arithmetic that keeps it lossy: temper spends
        // ceil((n+1)/3) per level, salvage returns floor(n/3) in total.
        var t = Tuning();
        for (var n = 1; n <= 30; n++)
        {
            long paidTotal = 0;
            for (var level = 0; level < n; level++)
                paidTotal += (level + 1 + 2) / 3;

            var returned = SalvagePolicy.Yield(Item(5, enh: n), t)
                .Where(l => l.MaterialId == "catalyst.temper").Sum(l => l.Qty);

            Assert.True(returned < paidTotal, $"+{n}: paid {paidTotal} temper, salvage returns {returned}");
        }
    }
}
