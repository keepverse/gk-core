using System.Reflection;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Sockets;
using FusionRpg.Core.Items.Surfaces;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// strain-splice-host SSH1.1 (host-gate §1/§3) — the one matcher <see cref="CombinationEvaluator"/>
/// and <see cref="CombinationDistance"/> both now call, so the evaluator's "does it fire" and the
/// preview's "how far is it" can never disagree.
/// </summary>
public class ComboMatcherTests
{
    static InsertDef Gem(string element, int tier = 3, string family = "atom.elemental-power") =>
        new($"gem.{(element.Length == 0 ? "plain" : element)}-shard.t{tier}", family, element, tier);

    static SocketFill Fill(int index, string affinity, InsertDef insert) => new(index, affinity, insert);

    // strain-splice-host SSH1.3: Capacity defaults to `sockets` -- a fully-bored chassis, the same
    // shape every caller of this helper already meant before Capacity existed as its own axis. A
    // test that wants an unbored chassis (SocketCount < Capacity) passes both explicitly.
    static SocketHost Host(int sockets = 4, ItemRole role = ItemRole.ArmamentPrimary, bool setPiece = false) =>
        new("item.test", role, "plant", sockets, setPiece, Capacity: sockets);

    static ComboRecipe Strain(string id, int minSockets, params ComboIngredient[] ingredients) =>
        StrainFloors(id, minSockets, Floors(ingredients), ingredients);

    static ComboRecipe StrainFloors(string id, int minSockets, int[] floors, params ComboIngredient[] ingredients) =>
        new(id, ComboShape.Strain, "", 0, "", "", minSockets, BaseTier: 1, ingredients, BaseFloors: floors);

    /// <summary>SSH7.8: a test recipe with no tier axis gets the flat rung-1 floor — one t1 per
    /// required fill. The floor is the LADDER's now, so it rides the recipe's `BaseFloors` (the shape
    /// the importer fills from tuning), never a per-ingredient member.</summary>
    static int[] Floors(params ComboIngredient[] ingredients) =>
        Enumerable.Repeat(1, ingredients.Sum(i => Math.Max(1, i.Quantity))).ToArray();

    // ── Checkpoint 1: one host truth ────────────────────────────────────────────────────────────

    [Fact]
    public void Evaluator_and_preview_share_one_matcher()
    {
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var deleted in new[] { "HostMatches", "MultisetSatisfied" })
            Assert.DoesNotContain(deleted, typeof(CombinationEvaluator).GetMethods(flags).Select(m => m.Name));

        foreach (var deleted in new[] { "MultisetShortfall" })
            Assert.DoesNotContain(deleted, typeof(CombinationDistance).GetMethods(flags).Select(m => m.Name));

        var matcherFlags = BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var matcherMethods = typeof(ComboMatcher).GetMethods(matcherFlags).Select(m => m.Name).ToHashSet();
        Assert.Contains("HostAdmits", matcherMethods);
        Assert.Contains("Match", matcherMethods);
        Assert.Contains("Fits", matcherMethods);
    }

    [Fact]
    public void No_combination_contract_carries_a_base_or_slot_key()
    {
        // strain-splice-host SSH1.4, host-gate ruling 2 (§4 row 2): a combination pins ROLE/FRAME/
        // SIZE (HostRole/HostFrame/MinSockets) and never a specific base type or a slot -- the C#
        // side of the same claim `CombinationKindTests.No_combination_contract_carries_a_base_or_slot_key`
        // (FusionRpg.ItemSeedValidator.Tests) proves for the generator's own KindCatalog entry, and
        // `test_the_schema_offers_no_base_type_or_slot_pin` (gk-forge/tools/seedsmith) proves for the model's
        // own output schema.
        var fields = typeof(ComboRecipe).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name.ToLowerInvariant())
            .ToList();

        Assert.DoesNotContain(fields, f => f.Contains("basetype"));
        Assert.DoesNotContain(fields, f => f.Contains("slot"));
    }

    // ── ComboMatcher.HostAdmits ─────────────────────────────────────────────────────────────────

    [Fact]
    public void HostAdmits_refuses_too_few_sockets_a_wrong_role_or_a_wrong_frame_and_admits_otherwise()
    {
        var recipe = new ComboRecipe(
            "combo.strain-role", ComboShape.Strain, "", 0, "armament-primary", "plant", 4, 1,
            new[] { new ComboIngredient("atom.elemental-power", 1) });

        Assert.True(ComboMatcher.HostAdmits(Host(4, ItemRole.ArmamentPrimary), recipe));
        Assert.False(ComboMatcher.HostAdmits(Host(3, ItemRole.ArmamentPrimary), recipe));
        Assert.False(ComboMatcher.HostAdmits(new SocketHost("item.test", ItemRole.ArmamentSecondary, "plant", 4), recipe));
        Assert.False(ComboMatcher.HostAdmits(new SocketHost("item.test", ItemRole.ArmamentPrimary, "insect", 4), recipe));
    }

    // ── ComboMatcher.Match / Fits ───────────────────────────────────────────────────────────────

    [Fact]
    public void A_fully_satisfied_recipe_claims_exactly_its_needed_fills_and_reports_no_shortfall()
    {
        var recipe = Strain("combo.strain-full", 4, new ComboIngredient("atom.elemental-power", 4));
        var fill = Enumerable.Range(0, 4).Select(i => Fill(i, "", Gem("fire"))).ToArray();

        var match = ComboMatcher.Match(recipe, fill);

        Assert.True(match.Satisfied);
        Assert.True(ComboMatcher.Fits(recipe, fill));
        Assert.Equal(4, match.Used.Count);
        Assert.Empty(match.Missing);
    }

    [Fact]
    public void A_short_fill_is_unsatisfied_and_names_exactly_what_is_missing()
    {
        var recipe = Strain("combo.strain-short", 4, new ComboIngredient("atom.elemental-power", 4));
        var fill = Enumerable.Range(0, 2).Select(i => Fill(i, "", Gem("fire"))).ToArray();

        var match = ComboMatcher.Match(recipe, fill);

        Assert.False(match.Satisfied);
        Assert.False(ComboMatcher.Fits(recipe, fill));
        var missing = Assert.Single(match.Missing);
        Assert.Equal("atom.elemental-power", missing.FamilyId);
        Assert.Equal(2, missing.Quantity);
    }

    [Fact]
    public void A_higher_tier_requirement_is_matched_before_a_lower_one_so_it_is_never_starved()
    {
        // One t5 insert and one t1 insert; two ingredients, one needing t5 and one needing t1 of the
        // same family. Matched most-specific-first, the t5 requirement claims the t5 insert and the
        // t1 requirement claims the t1 one -- both satisfied. Matched the other order, the t1
        // requirement (which accepts t5 too) could starve the t5 requirement of its only candidate.
        var recipe = Strain(
            "combo.strain-specificity", 2,
            new ComboIngredient("atom.elemental-power", 1),
            new ComboIngredient("atom.elemental-power", 1));
        var fill = new[] { Fill(0, "", Gem("fire", tier: 1)), Fill(1, "", Gem("fire", tier: 5)) };

        var match = ComboMatcher.Match(recipe, fill);

        Assert.True(match.Satisfied);
        Assert.Equal(2, match.Used.Count);
    }

    [Fact]
    public void A_zero_quantity_ingredient_names_nothing_to_satisfy_and_refuses()
    {
        var recipe = Strain("combo.strain-zero", 1, new ComboIngredient("atom.elemental-power", 0));
        var fill = new[] { Fill(0, "", Gem("fire", tier: 1)) };

        var match = ComboMatcher.Match(recipe, fill);

        Assert.False(match.Satisfied);
        Assert.NotEmpty(match.Missing);
    }

    [Fact]
    public void A_recipe_with_no_ingredients_at_all_never_satisfies()
    {
        // The same "names nothing, satisfies nothing" rule MultisetSatisfied always held -- an empty
        // ingredient list is not vacuously true.
        var recipe = Strain("combo.strain-empty", 0);
        var match = ComboMatcher.Match(recipe, Array.Empty<SocketFill>());

        Assert.False(match.Satisfied);
        Assert.False(ComboMatcher.Fits(recipe, Array.Empty<SocketFill>()));
    }

    // ── SocketHost.Capacity (SSH1.3) ────────────────────────────────────────────────────────────

    [Fact]
    public void Socket_host_refuses_opened_above_capacity()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SocketHost("item.test", ItemRole.ArmamentPrimary, "plant", SocketCount: 5, Capacity: 4));
        Assert.Contains("SocketCount", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_socket_count_within_capacity_constructs_cleanly()
    {
        var host = new SocketHost("item.test", ItemRole.ArmamentPrimary, "plant", SocketCount: 2, Capacity: 4);
        Assert.Equal(2, host.SocketCount);
        Assert.Equal(4, host.Capacity);
    }

    [Fact]
    public void CanEverHold_reads_capacity_not_the_opened_count()
    {
        var recipe = Strain("combo.capacity-role", 4, new ComboIngredient("atom.elemental-power", 4));

        // Zero sockets opened, but the chassis could hold four -- still a real goal.
        Assert.True(ComboMatcher.CanEverHold(
            new SocketHost("item.test", ItemRole.ArmamentPrimary, "plant", SocketCount: 0, Capacity: 4), recipe));

        // Four opened, but the chassis could never grow past two -- never a goal.
        Assert.False(ComboMatcher.CanEverHold(
            new SocketHost("item.test", ItemRole.ArmamentPrimary, "plant", SocketCount: 2, Capacity: 2), recipe));
    }

    [Fact]
    public void An_unbored_chassis_with_capacity_reads_reachable_not_undiscovered()
    {
        var tuning = SocketGeometryTests.Shipped();
        var surfaceTuning = ItemSurfaceTests.Shipped();
        var host = new SocketHost("item.test", ItemRole.ArmamentPrimary, "plant", SocketCount: 0, Capacity: 4);
        var recipe = Strain("combo.capacity-unbored", 4, new ComboIngredient("atom.elemental-power", 4));

        var row = Assert.Single(
            CombinationDistance.Evaluate(host, Array.Empty<SocketFill>(), new[] { recipe }, tuning, surfaceTuning, out _));

        Assert.NotEqual(CombinationDisplayState.Undiscovered, row.State);
        Assert.Equal(4, row.Distance);
    }

    [Fact]
    public void A_host_whose_capacity_is_below_the_recipe_is_undiscovered()
    {
        var tuning = SocketGeometryTests.Shipped();
        var surfaceTuning = ItemSurfaceTests.Shipped();
        var host = new SocketHost("item.test", ItemRole.ArmamentPrimary, "plant", SocketCount: 0, Capacity: 2);
        var recipe = Strain("combo.capacity-too-small", 4, new ComboIngredient("atom.elemental-power", 4));

        var row = Assert.Single(
            CombinationDistance.Evaluate(host, Array.Empty<SocketFill>(), new[] { recipe }, tuning, surfaceTuning, out _));

        Assert.Equal(CombinationDisplayState.Undiscovered, row.State);
        Assert.Null(row.Distance);
    }

    // ── Property: distance zero <=> the evaluator fires ────────────────────────────────────────

    [Fact]
    public void A_fill_the_preview_reports_at_distance_zero_is_a_fill_the_evaluator_fires()
    {
        // One recipe per catalog, deliberately: with two Strain/Splice recipes BOTH satisfied by the
        // same fill, the evaluator's own "at most one identity, lowest ComboId wins" rule (D21/§8)
        // makes the LOSING recipe distance-zero without ever being active -- a real, pre-existing
        // evaluator rule, not a ComboMatcher defect, and not what this property is about. Isolating
        // one recipe per run is how the property is checked without tripping over that rule.
        var tuning = SocketGeometryTests.Shipped();
        var surfaceTuning = ItemSurfaceTests.Shipped();
        var host = Host(4);

        var recipes = new[]
        {
            Strain("combo.strain-p1", 4, new ComboIngredient("atom.elemental-power", 2)),
            Strain("combo.strain-p2", 4, new ComboIngredient("atom.elemental-power", 3)),
            new ComboRecipe("combo.splice-p3", ComboShape.Splice, "", 0, "", "", 4, 1,
                new[] { new ComboIngredient("atom.elemental-power", 2), new ComboIngredient("atom.elemental-power", 1) },
                BaseFloors: new[] { 1, 1, 1 }),
        };

        var elements = ElementRoster.Concrete.Select(e => e.ToString().ToLowerInvariant()).ToArray();
        var rng = new Random(20260919);
        var affinities = new[] { "", "fire", "ice", "earth" };

        foreach (var recipe in recipes)
        {
            var catalog = new[] { recipe };
            for (var trial = 0; trial < 300; trial++)
            {
                var fillCount = rng.Next(0, 5);
                var fill = new List<SocketFill>();
                for (var i = 0; i < fillCount; i++)
                {
                    var element = elements[rng.Next(elements.Length)];
                    var tier = rng.Next(1, 6);
                    var affinity = affinities[rng.Next(affinities.Length)];
                    fill.Add(Fill(i, affinity, Gem(element, tier)));
                }

                var fires = CombinationEvaluator.Evaluate(host, fill, catalog, tuning)
                    .Any(r => r.ComboId == recipe.ComboId);
                var row = CombinationDistance.Evaluate(host, fill, catalog, tuning, surfaceTuning, out _)
                    .Single(r => r.ComboId == recipe.ComboId);

                Assert.True((row.Distance == 0) == fires,
                    $"recipe '{recipe.ComboId}' trial {trial}: distance-zero={row.Distance == 0} but evaluator-fires={fires}");
            }
        }
    }

    // ── strain-splice-host SSH7.2: the matcher reads the LADDER ──────────────────────────────────

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Core")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("repo root not found");
    }

    static SocketTuning Sockets() => SocketTuning.Parse(File.ReadAllText(
        Path.Combine(RepoRoot(), "data", "tuning", SocketTuningFiles.Current)));

    /// <summary>The shipped strain-splice tuning with a <c>tierLadder</c> injected — the ladder is
    /// tuning's, so a test that wants rungs publishes them rather than inventing a second parser.</summary>
    static StrainSpliceTuning WithLadder(SocketTuning sockets, string ladderJson)
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning",
            SocketTuningFiles.StrainSplice));
        var root = System.Text.Json.Nodes.JsonNode.Parse(text)!.AsObject();
        root["recipe"]!["tierLadder"] = System.Text.Json.Nodes.JsonNode.Parse(ladderJson);
        return StrainSpliceTuning.Parse(root.ToJsonString(), sockets);
    }

    static IReadOnlyList<TierLadderRung> Ladder(params (int Rung, int[] Floors, int Delta)[] rungs) =>
        rungs.Select(r => new TierLadderRung(r.Rung, r.Floors, r.Delta)).ToList();

    static readonly IReadOnlyList<TierLadderRung> TwoRungs = Ladder(
        (1, new[] { 1, 1, 2, 2 }, 0), (2, new[] { 2, 2, 3, 3 }, 1));

    static ComboRecipe FourFamilies() => Strain("combo.strain-ladder", 4,
        new("a", 1), new("b", 1), new("c", 1), new("d", 1));

    static SocketFill[] Filled(params int[] tiers) =>
        new[] { "a", "b", "c", "d" }
            .Select((family, at) => Fill(at, "fire", Gem("fire", tiers[at], family)))
            .ToArray();

    [Fact]
    public void Rung_one_reproduces_the_shipped_flat_floor()
    {
        // No ladder passed is NOT a second rule: it is rung 1 with the floors the recipe carries in
        // `BaseFloors` (the importer reads them from tuning) — the flat floor the shipped corpus
        // describes today.
        var recipe = StrainFloors("combo.strain-flat", 4, new[] { 1, 1, 2, 2 },
            new("a", 1), new("b", 1), new("c", 1), new("d", 1));

        var met = ComboMatcher.Match(recipe, Filled(1, 1, 2, 2));
        Assert.True(met.Satisfied);
        Assert.Equal(1, met.Rung);
        Assert.Empty(met.ShortOfNext);

        // A fill whose tiers miss that floor does NOT fire on a family-quantity claim alone.
        var belowFloor = ComboMatcher.Match(recipe, Filled(1, 1, 1, 1));
        Assert.False(belowFloor.Satisfied);
        Assert.Equal(0, belowFloor.Rung);
    }

    [Fact]
    public void Higher_tier_gems_reach_a_higher_rung()
    {
        var recipe = FourFamilies();

        Assert.Equal(1, ComboMatcher.Match(recipe, Filled(1, 1, 2, 2), TwoRungs).Rung);
        Assert.Equal(2, ComboMatcher.Match(recipe, Filled(2, 2, 3, 3), TwoRungs).Rung);

        // The same families at the SAME tiers answer the same way whichever order the fill lists
        // them in — the claim is a multiset, so order may never matter.
        Assert.Equal(2, ComboMatcher.Match(recipe, Filled(3, 3, 2, 2), TwoRungs).Rung);
        Assert.Equal(2, ComboMatcher.Match(recipe, Filled(2, 3, 2, 3), TwoRungs).Rung);
    }

    [Fact]
    public void Every_permutation_of_a_fill_reaches_the_same_rung()
    {
        var recipe = FourFamilies();
        var tiers = new[] { 1, 2, 3, 3 };
        var reference = ComboMatcher.Match(recipe, Filled(tiers), TwoRungs).Rung;

        foreach (var order in new[] { new[] { 0, 1, 2, 3 }, new[] { 3, 2, 1, 0 },
                                      new[] { 2, 0, 3, 1 }, new[] { 1, 3, 0, 2 } })
        {
            var fill = new[] { "a", "b", "c", "d" }
                .Select((family, at) => Fill(at, "fire", Gem("fire", tiers[order[at]], family)))
                .ToArray();
            Assert.Equal(reference, ComboMatcher.Match(recipe, fill, TwoRungs).Rung);
        }
    }

    [Fact]
    public void Attunement_adds_its_bonus_on_top_of_the_rung_and_never_gates()
    {
        var sockets = Sockets();
        var tuning = WithLadder(sockets,
            """[{"rung":1,"floors":[1,1,2,2],"grantDelta":0},{"rung":2,"floors":[2,2,3,3],"grantDelta":1}]""");
        var baseTier = tuning.BaseTierFor(ComboShape.Strain);

        Assert.Equal(1, tuning.RungGrantDelta(2));
        Assert.Equal(baseTier + 1, tuning.GrantedTier(ComboShape.Strain, rung: 2, sockets, allAttuned: false));
        Assert.Equal(baseTier + 1 + sockets.AttunedTierBonus,
            tuning.GrantedTier(ComboShape.Strain, rung: 2, sockets, allAttuned: true));
        // Rung 1 carries no delta, which is why every pre-ladder caller is unchanged.
        Assert.Equal(baseTier, tuning.GrantedTier(ComboShape.Strain, rung: 1, sockets, allAttuned: false));

        // Never a gate: the rung is a property of the TIERS, so a fill whose affinities match nothing
        // reaches exactly the same rung as one where every insert is attuned.
        var recipe = FourFamilies();
        var attuned = ComboMatcher.Match(recipe, Filled(2, 2, 3, 3), tuning.TierLadder);
        var loose = ComboMatcher.Match(recipe,
            new[] { "a", "b", "c", "d" }
                .Select((family, at) => Fill(at, "", Gem("fire", new[] { 2, 2, 3, 3 }[at], family))).ToArray(),
            tuning.TierLadder);
        Assert.Equal(2, attuned.Rung);
        Assert.Equal(attuned.Rung, loose.Rung);
    }

    [Fact]
    public void A_fill_below_rung_one_reports_tier_shortfalls_not_missing_families()
    {
        var recipe = FourFamilies();
        var match = ComboMatcher.Match(recipe, Filled(1, 1, 1, 1), TwoRungs);

        Assert.False(match.Satisfied);
        Assert.Equal(0, match.Rung);
        // Every FAMILY was claimed — the report names positions and tiers, never a missing family.
        Assert.Empty(match.Missing);
        Assert.Equal(4, match.Used.Count);
        Assert.NotEmpty(match.ShortOfNext);
        Assert.Contains(match.ShortOfNext, s => s.Position == 2 && s.Have == 1 && s.Need == 2);
        Assert.Contains(match.ShortOfNext, s => s.Position == 3 && s.Have == 1 && s.Need == 2);
    }

    [Fact]
    public void Rebalancing_the_ladder_needs_no_regeneration()
    {
        // A tuning change to the FLOORS moves the rung with the corpus and the fill untouched: the
        // ladder is read at match time, never baked into a row.
        var recipe = FourFamilies();
        var fill = Filled(2, 2, 2, 2);

        Assert.Equal(1, ComboMatcher.Match(recipe, fill, Ladder((1, new[] { 2, 2, 2, 2 }, 0))).Rung);
        // The same fill under a ladder whose rung 1 asks for t3 does not fire at all...
        var stricter = ComboMatcher.Match(recipe, fill, Ladder((1, new[] { 3, 3, 3, 3 }, 0)));
        Assert.False(stricter.Satisfied);
        Assert.NotEmpty(stricter.ShortOfNext);
        // ...and a SECOND rung an increment away reaches it with no content change.
        Assert.Equal(2, ComboMatcher.Match(recipe, fill,
            Ladder((1, new[] { 2, 2, 2, 2 }, 0), (2, new[] { 2, 2, 2, 2 }, 1))).Rung);
    }
}
