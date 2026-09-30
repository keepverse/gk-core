using System.Reflection;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Sockets;
using FusionRpg.Core.Items.Surfaces;
using FusionRpg.Core.Items.Thresholds;
using FusionRpg.Core.Stats.Derived;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// `item-surfaces` (item module 20) — the deterministic half of the six player surfaces, against the
/// REAL shipped `gk-core/data/tuning/item-surfaces.v1.json`, the REAL generated 25-row resonance catalog and
/// the REAL 30-set corpus.
///
/// <para><b>What is asserted here and what deliberately is not.</b> This module's spec is largely a
/// rendering spec, and a React component's layout is not a Core fact. What IS a Core fact — and is
/// what every one of the six surfaces reads before it draws a pixel — is: how far each combination is
/// from firing, which of them the player may see, which rows a filter hides, which strategy a
/// collection uses at 10/100/1000, which state a surface is in, how a verdict is spelled, and which
/// sets a piece advances. All of that is here. The <c>.tsx</c> composition is named in the module's
/// build log as owed, with its reason.</para>
/// </summary>
public class ItemSurfaceTests
{
    static string RepoRoot() => KeepverseRoots.Core();

    static string TuningPath => Path.Combine(RepoRoot(), "data", "tuning", "item-surfaces.v1.json");

    internal static ItemSurfaceTuning Shipped() => ItemSurfaceTuning.Parse(File.ReadAllText(TuningPath));

    static SocketTuning Sockets => SocketGeometryTests.Shipped();

    static IReadOnlyList<ComboRecipe> Resonances => ResonanceGenerator.Generate(Sockets);

    static InsertDef Gem(string element, int tier = 3, string family = "atom.elemental-power") =>
        new($"gem.{(element.Length == 0 ? "plain" : element)}-shard.t{tier}", family, element, tier);

    static SocketFill Fill(int index, string affinity, InsertDef insert) => new(index, affinity, insert);

    // strain-splice-host SSH1.3: Capacity defaults to the same value as `sockets` -- a fully-bored
    // chassis, which is what every pre-existing caller of this helper already meant by "N sockets"
    // before Capacity existed as its own axis. A test that wants an UNBORED chassis (SocketCount <
    // Capacity) passes both explicitly.
    static SocketHost Host(int sockets = 4, ItemRole role = ItemRole.ArmamentPrimary, bool setPiece = false) =>
        new("item.test", role, "plant", sockets, setPiece, Capacity: sockets);

    static ArmouryEntry Row(
        string id, int rarity = 30, bool unseen = false, bool locked = false, bool assigned = false, bool stale = false) =>
        new(id, $"item.{id}", "armament-primary", "plant", rarity, "2026-09-05T00:00:00Z",
            assigned, locked, unseen, stale, RollQualityMilli: 500);

    // ── The tuning ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_shipped_tuning_parses_and_declares_an_unlock_for_every_one_of_the_six_surfaces()
    {
        var t = Shipped();

        Assert.Equal(100, t.RenderAllThrough);
        Assert.Equal(2000, t.VirtualizeThrough);
        Assert.Equal(1, t.OneAwayDistance);
        Assert.Equal(0, t.DefaultHideBelowRarityOrdinal); // "no loot filter on day one"
        Assert.Equal(6, SurfaceCatalog.All.Count);
        Assert.All(SurfaceCatalog.Ids, id => Assert.True(t.SurfaceUnlocks.ContainsKey(id), id));
    }

    [Fact]
    public void A_tuning_that_forgets_a_surfaces_unlock_is_refused_at_load_not_at_render()
    {
        // GG-44 mechanically: a surface with no declared unlock renders as present-but-dead, which is
        // the state the rule exists to forbid. Refusing at parse time is what makes it unreachable.
        var broken = File.ReadAllText(TuningPath).Replace("\"compendium\": \"first-socketed-item\"", "\"unused\": \"x\"");
        var ex = Assert.Throws<ItemSurfaceTuningRejection>(() => ItemSurfaceTuning.Parse(broken));
        Assert.Contains("compendium", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_virtualize_threshold_below_the_render_all_threshold_is_refused()
    {
        var broken = File.ReadAllText(TuningPath).Replace("\"virtualizeThrough\": 2000", "\"virtualizeThrough\": 10");
        var ex = Assert.Throws<ItemSurfaceTuningRejection>(() => ItemSurfaceTuning.Parse(broken));
        Assert.Contains("virtualizeThrough", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_one_away_distance_of_zero_is_refused_because_it_would_name_the_active_set()
    {
        var broken = File.ReadAllText(TuningPath).Replace("\"oneAwayDistance\": 1", "\"oneAwayDistance\": 0");
        Assert.Throws<ItemSurfaceTuningRejection>(() => ItemSurfaceTuning.Parse(broken));
    }

    // ── GG-50: 10 / 100 / 1000 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void The_armoury_declares_its_strategy_at_10_100_and_1000()
    {
        var t = Shipped();
        Assert.Equal(RenderStrategy.RenderAll, CollectionStrategy.For(10, t));
        Assert.Equal(RenderStrategy.RenderAll, CollectionStrategy.For(100, t));
        Assert.Equal(RenderStrategy.Virtualize, CollectionStrategy.For(1_000, t));
        Assert.Equal(RenderStrategy.Virtualize, CollectionStrategy.For(2_000, t));
        Assert.Equal(RenderStrategy.SearchFirst, CollectionStrategy.For(2_001, t));
    }

    [Fact]
    public void No_render_band_refuses_a_row_which_is_what_keeps_it_a_layout_call_and_not_a_bag_cap()
    {
        // §2.5 forbids a bag capacity outright. Every band is defined at every magnitude, including
        // far past RpgStore.InventoryCeiling — which is module 2's own structural abuse guard and a
        // different thing entirely.
        var t = Shipped();
        Assert.Equal(RenderStrategy.RenderAll, CollectionStrategy.For(0, t));
        Assert.Equal(RenderStrategy.SearchFirst, CollectionStrategy.For(1_000_000, t));
        Assert.Throws<ArgumentOutOfRangeException>(() => CollectionStrategy.For(-1, t));
    }

    // ── GG-17 / GG-44: four designed states on every surface ───────────────────────────────────

    [Fact]
    public void Every_one_of_the_six_surfaces_has_loading_empty_error_and_locked()
    {
        var t = Shipped();
        var all = new HashSet<string>(t.SurfaceUnlocks.Values, StringComparer.Ordinal);
        var none = new HashSet<string>(StringComparer.Ordinal);

        foreach (var surface in SurfaceCatalog.All)
        {
            Assert.Equal(SurfaceState.Locked, SurfaceCatalog.Resolve(surface, t, none, false, false, 5).State);
            Assert.Equal(SurfaceState.Loading, SurfaceCatalog.Resolve(surface, t, all, true, false, 5).State);
            Assert.Equal(SurfaceState.Error, SurfaceCatalog.Resolve(surface, t, all, false, true, 5).State);
            Assert.Equal(SurfaceState.Empty, SurfaceCatalog.Resolve(surface, t, all, false, false, 0).State);
            Assert.Equal(SurfaceState.Ready, SurfaceCatalog.Resolve(surface, t, all, false, false, 5).State);
        }
    }

    [Fact]
    public void A_locked_surface_says_what_unlocks_it_and_never_spins()
    {
        var t = Shipped();
        var none = new HashSet<string>(StringComparer.Ordinal);

        foreach (var surface in SurfaceCatalog.All)
        {
            // Locked beats loading: the player cannot make the spinner finish.
            var status = SurfaceCatalog.Resolve(surface, t, none, loading: true, errored: true, rowCount: 0);
            Assert.Equal(SurfaceState.Locked, status.State);
            Assert.False(string.IsNullOrWhiteSpace(status.UnlockKey));
        }
    }

    [Fact]
    public void An_error_never_renders_as_empty_because_they_are_different_sentences()
    {
        var t = Shipped();
        var all = new HashSet<string>(t.SurfaceUnlocks.Values, StringComparer.Ordinal);
        var status = SurfaceCatalog.Resolve(ItemSurface.Armoury, t, all, loading: false, errored: true, rowCount: 0);
        Assert.Equal(SurfaceState.Error, status.State);
    }

    // ── The near-miss evaluator ────────────────────────────────────────────────────────────────

    [Fact]
    public void Near_miss_uses_the_same_evaluator_as_the_active_set_called_exactly_once()
    {
        var fill = new[] { Fill(0, "", Gem("fire")), Fill(1, "", Gem("fire")), Fill(2, "", Gem("ice")) };
        var rows = CombinationDistance.Evaluate(Host(), fill, Resonances, Sockets, Shipped(), out var diag);

        var direct = CombinationEvaluator.Evaluate(Host(), fill, Resonances, Sockets)
            .Select(r => r.ComboId).OrderBy(s => s, StringComparer.Ordinal).ToArray();
        var viaDistance = rows.Where(r => r.State == CombinationDisplayState.Active)
            .Select(r => r.ComboId).OrderBy(s => s, StringComparer.Ordinal).ToArray();

        Assert.Equal(direct, viaDistance);
        Assert.Equal(1, diag.ActiveSetEvaluations);
    }

    [Fact]
    public void A_recipe_one_insert_away_is_decided_without_enumerating_a_single_arrangement()
    {
        // The tractability claim, asserted by instrumenting the pass rather than by reading the code.
        // A naive "which arrangement satisfies this" would be 4! = 24 per candidate recipe.
        var strain = new ComboRecipe(
            "combo.strain-might-offense", ComboShape.Strain, "", 0, "", "", 4, 2,
            new[] { new ComboIngredient("atom.elemental-power", 3), new ComboIngredient("atom.vitality", 1) },
            BaseFloors: new[] { 1, 1, 1, 1 });
        var catalog = Resonances.Concat(new[] { strain }).ToList();

        var fill = new[] { Fill(0, "", Gem("fire")), Fill(1, "", Gem("ice")), Fill(2, "", Gem("earth")) };
        var rows = CombinationDistance.Evaluate(Host(), fill, catalog, Sockets, Shipped(), out var diag);

        var row = Assert.Single(rows, r => r.ComboId == "combo.strain-might-offense");
        Assert.Equal(CombinationDisplayState.OneAway, row.State);
        Assert.Equal(1, row.Distance);
        var missing = Assert.Single(row.Missing);
        Assert.Equal("atom.vitality", missing.FamilyId);
        Assert.Equal(1, missing.Quantity);

        Assert.Equal(0, diag.PermutationsEnumerated);
        Assert.Equal(catalog.Count, diag.RecipesExamined);
        Assert.True(diag.MultisetComparisons <= catalog.Count,
            $"{diag.MultisetComparisons} multiset comparisons over {catalog.Count} recipes — at most one per recipe");
    }

    [Fact]
    public void D41_made_recipes_unordered_so_every_arrangement_of_one_fill_reports_the_same_distance()
    {
        // ⛔ spec-item-surfaces.md's INSERT/SWAP split (with its n − cycles swap leg) predates D41 by
        // one day. D41's own consequence table names this module: "distance counts missing kinds,
        // never positions". Module 16 shipped it that way — ComboIngredient carries no position field.
        // So there is no swap state, and this is the assertion that keeps it that way.
        Assert.DoesNotContain("Swap", Enum.GetNames(typeof(CombinationDisplayState)));
        Assert.DoesNotContain(
            typeof(MissingIngredient).GetProperties().Select(p => p.Name),
            n => n.Contains("Position", StringComparison.OrdinalIgnoreCase)
                 || n.Contains("Ordinal", StringComparison.OrdinalIgnoreCase));

        var strain = new ComboRecipe(
            "combo.strain-x", ComboShape.Strain, "", 0, "", "", 4, 1,
            new[] { new ComboIngredient("atom.elemental-power", 2), new ComboIngredient("atom.vitality", 1) },
            BaseFloors: new[] { 1, 1, 1 });
        var catalog = new[] { strain };

        var inserts = new[]
        {
            Gem("fire"), Gem("ice"), Gem("", family: "atom.vitality"), Gem("earth"),
        };

        string? reference = null;
        foreach (var order in Permutations(inserts))
        {
            var fill = order.Select((ins, i) => Fill(i, "", ins)).ToArray();
            var rows = CombinationDistance.Evaluate(Host(), fill, catalog, Sockets, Shipped(), out _);
            var signature = $"{rows[0].State}:{rows[0].Distance}";
            reference ??= signature;
            Assert.Equal(reference, signature);
        }

        Assert.Equal($"{CombinationDisplayState.Active}:0", reference);
    }

    static IEnumerable<T[]> Permutations<T>(T[] source)
    {
        if (source.Length <= 1) { yield return source; yield break; }
        for (var i = 0; i < source.Length; i++)
        {
            var rest = source.Where((_, j) => j != i).ToArray();
            foreach (var tail in Permutations(rest))
                yield return new[] { source[i] }.Concat(tail).ToArray();
        }
    }

    [Fact]
    public void A_two_socket_item_never_shows_a_four_insert_recipe_as_one_away()
    {
        // G3 §4.3's ∞ rule. An unreachable combination is `undiscovered`, never `one-away` — that is
        // what stops a two-socket item promising a four-insert resonance.
        var fill = new[] { Fill(0, "", Gem("fire")), Fill(1, "", Gem("fire")) };
        var rows = CombinationDistance.Evaluate(Host(2), fill, Resonances, Sockets, Shipped(), out _);

        var pure4 = Assert.Single(rows, r => r.ComboId == "combo.pure-fire-4");
        Assert.Equal(CombinationDisplayState.Undiscovered, pure4.State);
        Assert.Null(pure4.Distance);

        var pure2 = Assert.Single(rows, r => r.ComboId == "combo.pure-fire-2");
        Assert.Equal(CombinationDisplayState.Active, pure2.State);

        Assert.DoesNotContain(rows, r => r.State == CombinationDisplayState.OneAway && r.Distance is null);
    }

    [Fact]
    public void A_set_piece_is_never_one_away_from_a_strain_or_a_splice()
    {
        // D21's exclusivity, at the bench. It is not "one insert away" from one; it will never have
        // one, and saying "one away" would be the tooltip lie in its purest form.
        var strain = new ComboRecipe(
            "combo.strain-z", ComboShape.Strain, "", 0, "", "", 2, 1,
            new[] { new ComboIngredient("atom.elemental-power", 2) }, BaseFloors: new[] { 1, 1 });
        var fill = new[] { Fill(0, "", Gem("fire")) };

        var onPlain = CombinationDistance.Evaluate(Host(4), fill, new[] { strain }, Sockets, Shipped(), out _);
        Assert.Equal(CombinationDisplayState.OneAway, onPlain[0].State);

        var onSetPiece = CombinationDistance.Evaluate(
            Host(4, setPiece: true), fill, new[] { strain }, Sockets, Shipped(), out _);
        Assert.Equal(CombinationDisplayState.Undiscovered, onSetPiece[0].State);
        Assert.Null(onSetPiece[0].Distance);
    }

    [Fact]
    public void A_matched_affinity_changes_a_strains_result_not_its_distance()
    {
        // D22 was reverted: matching affinity grants an ENHANCED TIER, reusing the +1 pattern. On a
        // Strain that is exactly true — the granted tier moves and the ingredient requirement does not.
        var strain = new ComboRecipe(
            "combo.strain-affinity", ComboShape.Strain, "", 0, "", "", 4, 2,
            new[] { new ComboIngredient("atom.elemental-power", 2) }, BaseFloors: new[] { 1, 1 });

        var attuned = new[] { Fill(0, "fire", Gem("fire")), Fill(1, "fire", Gem("fire")) };
        var loose = new[] { Fill(0, "", Gem("fire")), Fill(1, "", Gem("fire")) };

        var withAffinity = CombinationDistance.Evaluate(Host(), attuned, new[] { strain }, Sockets, Shipped(), out _)[0];
        var without = CombinationDistance.Evaluate(Host(), loose, new[] { strain }, Sockets, Shipped(), out _)[0];

        Assert.Equal(withAffinity.State, without.State);
        Assert.Equal(withAffinity.Distance, without.Distance);
        Assert.True(withAffinity.GrantedTier > without.GrantedTier, "affinity must change the RESULT");
    }

    [Fact]
    public void Pure_distance_follows_the_shipped_evaluator_including_attunements_effective_count()
    {
        // ⚠ The one place spec-item-surfaces.md's "affinity never changes a distance" is false against
        // shipped code, and the same-evaluator rule decides it: the shipped Pure arm adds
        // attunedEffectiveCountBonus to the CONTRIBUTOR COUNT, which is the very thing the threshold
        // compares against. A distance that ignored it would say "one more" about a resonance that is
        // already firing — the exact "the tooltip said one more and it did not fire" failure, inverted.
        var pure3 = Resonances.Single(r => r.ComboId == "combo.pure-fire-3");

        var twoAttuned = new[] { Fill(0, "fire", Gem("fire")), Fill(1, "fire", Gem("fire")) };
        var twoLoose = new[] { Fill(0, "", Gem("fire")), Fill(1, "", Gem("fire")) };

        var attunedRow = CombinationDistance.Evaluate(Host(), twoAttuned, new[] { pure3 }, Sockets, Shipped(), out _)[0];
        var looseRow = CombinationDistance.Evaluate(Host(), twoLoose, new[] { pure3 }, Sockets, Shipped(), out _)[0];

        Assert.Equal(CombinationDisplayState.Active, attunedRow.State);  // 2 + 1 attunement == 3
        Assert.Equal(CombinationDisplayState.OneAway, looseRow.State);
        Assert.Equal(1, looseRow.Distance);

        // And the preview never disagrees with the evaluator, which is the property that matters.
        Assert.Single(CombinationEvaluator.Evaluate(Host(), twoAttuned, new[] { pure3 }, Sockets));
        Assert.Empty(CombinationEvaluator.Evaluate(Host(), twoLoose, new[] { pure3 }, Sockets));
    }

    [Fact]
    public void Every_row_of_the_real_generated_catalog_gets_exactly_one_of_the_four_states()
    {
        var fill = new[] { Fill(0, "", Gem("fire")), Fill(1, "", Gem("ice")) };
        var rows = CombinationDistance.Evaluate(Host(2), fill, Resonances, Sockets, Shipped(), out var diag);

        // The counts are fully implied by the element-wise equality below and the self-reference to
        // rows.Count -- never pinned as literals (population-pin SE3.3, 2026-09-19).
        Assert.Equal(rows.Count, diag.RecipesExamined);
        Assert.Equal(Resonances.Select(r => r.ComboId), rows.Select(r => r.ComboId));
        Assert.All(rows, r => Assert.True(Enum.IsDefined(typeof(CombinationDisplayState), r.State)));

        // Ring fire→ice fires on this fill; four-socket shapes are out of reach on a two-socket item.
        Assert.Equal(CombinationDisplayState.Active, rows.Single(r => r.ComboId == "combo.ring-fire-ice").State);
        Assert.Equal(CombinationDisplayState.Undiscovered, rows.Single(r => r.ComboId == "combo.diversity-4").State);
    }

    // ── The compendium ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_recipe_is_revealed_only_after_every_ingredient_has_been_held()
    {
        var strain = new ComboRecipe(
            "combo.strain-reveal", ComboShape.Strain, "", 0, "", "", 4, 1,
            new[] { new ComboIngredient("atom.elemental-power", 1), new ComboIngredient("atom.vitality", 1) },
            BaseFloors: new[] { 1, 1 });

        Assert.False(CompendiumReveal.IsRevealed(strain, HeldLedger.Empty, Sockets));
        Assert.False(CompendiumReveal.IsRevealed(strain, HeldLedger.From(new[] { Gem("fire") }), Sockets));
        Assert.True(CompendiumReveal.IsRevealed(
            strain, HeldLedger.From(new[] { Gem("fire"), Gem("", family: "atom.vitality") }), Sockets));

        // A generated resonance names no families, so its reveal reads off its shape instead.
        var pure3 = Resonances.Single(r => r.ComboId == "combo.pure-fire-3");
        Assert.False(CompendiumReveal.IsRevealed(pure3, HeldLedger.From(new[] { Gem("ice") }), Sockets));
        Assert.True(CompendiumReveal.IsRevealed(pure3, HeldLedger.From(new[] { Gem("fire") }), Sockets));
    }

    [Fact]
    public void The_full_catalog_is_never_rendered_at_once_and_an_unheld_recipe_is_absent()
    {
        var fill = new[] { Fill(0, "", Gem("fire")), Fill(1, "", Gem("fire")) };
        var rows = CombinationDistance.Evaluate(Host(4), fill, Resonances, Sockets, Shipped(), out _);

        var blind = CompendiumReveal.Render(rows, Resonances, HeldLedger.Empty, Sockets, Shipped());
        Assert.All(blind, r => Assert.Equal(CombinationDisplayState.Active, r.State));
        Assert.True(blind.Count < Resonances.Count);

        var seenFire = CompendiumReveal.Render(
            rows, Resonances, HeldLedger.From(new[] { Gem("fire") }), Sockets, Shipped());
        Assert.Contains(seenFire, r => r.ComboId == "combo.pure-fire-3" && r.State == CombinationDisplayState.OneAway);
        Assert.DoesNotContain(seenFire, r => r.ComboId.Contains("ice", StringComparison.Ordinal));
        Assert.DoesNotContain(seenFire, r => r.State == CombinationDisplayState.Undiscovered);

        // active → one-away → known-inactive, and the order is stable.
        var states = seenFire.Select(r => (int)r.State).ToList();
        Assert.Equal(states.OrderBy(s => s), states);
    }

    [Fact]
    public void The_known_inactive_tail_is_capped_but_active_and_one_away_rows_never_are()
    {
        var everything = HeldLedger.From(ElementRoster.Concrete.Select(e => Gem(e.ToString().ToLowerInvariant())));
        var fill = new[] { Fill(0, "", Gem("fire")) };
        var rows = CombinationDistance.Evaluate(Host(4), fill, Resonances, Sockets, Shipped(), out _);

        var tuning = Shipped();
        var full = CompendiumReveal.Render(rows, Resonances, everything, Sockets, tuning);

        var capped = ItemSurfaceTuning.Parse(
            File.ReadAllText(TuningPath).Replace("\"knownInactiveRowCap\": 40", "\"knownInactiveRowCap\": 0"));
        var trimmed = CompendiumReveal.Render(rows, Resonances, everything, Sockets, capped);

        Assert.Empty(trimmed.Where(r => r.State == CombinationDisplayState.KnownInactive));
        Assert.Equal(
            full.Count(r => r.State != CombinationDisplayState.KnownInactive),
            trimmed.Count);
        Assert.NotEmpty(trimmed);
    }

    // ── The loot filter ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_loot_filter_hides_rows_and_reaches_nothing_that_could_change_a_drop()
    {
        // ⛔ D26: the 40/day line is an interface requirement, not a cap. Asserted from two directions —
        // behaviour, and the source itself naming no generation type.
        var rows = new[] { Row("a", rarity: 10), Row("b", rarity: 50), Row("c", rarity: 30) };
        var hidden = LootFilterView.Apply(rows, new LootFilterRule(HideBelowRarityOrdinal: 30)).ToList();

        Assert.Equal(new[] { "b", "c" }, hidden.Select(r => r.InstanceId));
        Assert.Equal(3, rows.Length); // the source list is untouched

        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "FusionRpg.Core", "Items", "Surfaces", "LootFilterRule.cs"));
        var code = string.Join('\n', source.Split('\n').Where(l => !l.TrimStart().StartsWith("///", StringComparison.Ordinal)
                                                                   && !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        foreach (var forbidden in new[] { "LootPipeline", "DropTable", "LootPity", "DropEnvelope", "RpgStore" })
            Assert.DoesNotContain(forbidden, code, StringComparison.Ordinal);
    }

    [Fact]
    public void A_locked_row_is_never_hidden_and_the_caller_sort_order_survives()
    {
        var rows = new[] { Row("a", rarity: 10, locked: true), Row("b", rarity: 50), Row("c", rarity: 10) };
        var visible = LootFilterView.Apply(rows, new LootFilterRule(HideBelowRarityOrdinal: 40)).ToList();
        Assert.Equal(new[] { "a", "b" }, visible.Select(r => r.InstanceId));
    }

    [Fact]
    public void The_default_filter_hides_nothing_because_there_is_no_loot_filter_on_day_one()
    {
        var rows = new[] { Row("a", rarity: 10), Row("b", rarity: 100) };
        Assert.Equal(2, LootFilterView.Apply(rows, LootFilterRule.Default(Shipped())).Count());
    }

    [Fact]
    public void The_inbox_count_falls_to_zero_when_the_unseen_are_reviewed()
    {
        var t = Shipped();
        var unread = new[] { Row("a", unseen: true), Row("b", unseen: true), Row("c") };
        Assert.Equal(2, LootFilterView.Inbox(unread, t).Unseen);

        var reviewed = unread.Select(r => r with { Unseen = false }).ToList();
        Assert.Equal(0, LootFilterView.Inbox(reviewed, t).Unseen);

        // Counted over the WHOLE armoury, never over the filtered view — an inbox you can empty by
        // hiding it is not an inbox.
        var filtered = LootFilterView.Apply(unread, new LootFilterRule(HideBelowRarityOrdinal: 999)).ToList();
        Assert.Empty(filtered);
        Assert.Equal(2, LootFilterView.Inbox(unread, t).Unseen);
    }

    [Fact]
    public void Review_pressure_is_a_warning_and_never_a_refusal()
    {
        var t = Shipped();
        var flood = Enumerable.Range(0, t.ReviewPressurePerContentEvent + 1)
            .Select(i => Row($"i{i}", unseen: true)).ToList();

        var inbox = LootFilterView.Inbox(flood, t);
        Assert.True(inbox.OverReviewPressure);
        Assert.Equal(flood.Count, inbox.Total);
        // Every row is still there — the flag changes what is drawn, never what the player holds.
        Assert.Equal(flood.Count, LootFilterView.Apply(flood, LootFilterRule.Default(t)).Count());
    }

    // ── Comparison ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Dominance_is_a_word_and_a_shape_never_colour_alone()
    {
        var badges = Enum.GetValues(typeof(DominanceVerdict)).Cast<DominanceVerdict>()
            .Select(DominancePresentation.Badge).ToList();

        Assert.Equal(4, badges.Count);
        Assert.All(badges, b => Assert.False(string.IsNullOrWhiteSpace(b.LabelKey)));
        Assert.All(badges, b => Assert.False(string.IsNullOrWhiteSpace(b.Shape)));
        Assert.Equal(4, badges.Select(b => b.Shape).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(4, badges.Select(b => b.LabelKey).Distinct(StringComparer.Ordinal).Count());

        // The redundancy rule, mechanically: the badge carries no colour channel at all, so a renderer
        // cannot fall back to hue by reading one off it.
        Assert.DoesNotContain(
            typeof(VerdictBadge).GetProperties().Select(p => p.Name),
            n => n.Contains("Colour", StringComparison.OrdinalIgnoreCase)
                 || n.Contains("Color", StringComparison.OrdinalIgnoreCase)
                 || n.Contains("Hex", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_sidegrade_renders_the_trade_and_an_incomparable_renders_the_reason()
    {
        var deltas = new[]
        {
            new ChannelDelta("maxHp", UnitClass.GameUnits, 100, 80, -20),
            new ChannelDelta("atk", UnitClass.GameUnits, 10, 18, 8),
        };

        var trade = DominancePresentation.Trade(deltas);
        Assert.Equal(new[] { "atk" }, trade.YouGain.Select(d => d.Channel));
        Assert.Equal(new[] { "maxHp" }, trade.YouGiveUp.Select(d => d.Channel));

        Assert.False(string.IsNullOrWhiteSpace(DominancePresentation.IncomparableReasonKey));
        Assert.Equal("◇", DominancePresentation.Badge(DominanceVerdict.Incomparable).Shape);
    }

    [Fact]
    public void Comparison_never_mixes_two_unit_classes_in_one_column()
    {
        // SC4 as a layout invariant, over a generated channel-pair matrix drawn from the real registry
        // rather than from a hand-picked pair.
        var registry = DerivedStatRegistry.CreateDefault();
        var channels = new[] { "maxHp", "atk", "attackInterval", "zombieSpeed" }
            .Concat(registry.AllRegistered.OrderBy(d => d.ChannelId, StringComparer.Ordinal).Take(12).Select(d => d.ChannelId))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var deltas = channels
            .Select((c, i) => new ChannelDelta(c, ChannelUnitsOf(c, registry), 0, i, i))
            .ToList();
        var groups = DominancePresentation.GroupByUnitClass(deltas, registry);

        Assert.Equal(deltas.Count, groups.Sum(g => g.Deltas.Count));
        foreach (var group in groups)
        {
            var units = group.Deltas.Select(d => ChannelUnitsOf(d.Channel, registry)).Distinct().ToList();
            Assert.Single(units);
            Assert.Equal(group.Unit, units[0]);
        }
        Assert.Equal(groups.Count, groups.Select(g => g.Unit).Distinct().Count());
    }

    static UnitClass? ChannelUnitsOf(string channel, DerivedStatRegistry registry) =>
        FusionRpg.Core.Items.Display.ChannelUnits.For(channel, registry);

    [Fact]
    public void The_no_single_score_footnote_is_persistent_and_there_is_no_api_to_dismiss_it()
    {
        Assert.False(string.IsNullOrWhiteSpace(DominancePresentation.NoSingleScoreFootnoteKey));

        var members = typeof(DominancePresentation).Assembly.GetTypes()
            .Where(t => t.Namespace == "FusionRpg.Core.Items.Surfaces")
            .SelectMany(t => t.GetMembers(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Select(m => m.Name)
            .ToList();

        Assert.DoesNotContain(members, n => n.Contains("Dismiss", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(members, n => n.Contains("HideFootnote", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Comparison_is_the_default_presentation_when_a_candidate_is_selected()
    {
        Assert.True(DominancePresentation.ComparisonIsDefault(candidateSelected: true, roleHasIncumbent: true));
        Assert.False(DominancePresentation.ComparisonIsDefault(candidateSelected: true, roleHasIncumbent: false));
        Assert.False(DominancePresentation.ComparisonIsDefault(candidateSelected: false, roleHasIncumbent: true));
    }

    // ── The multi-set disclosure module 12 filed here ──────────────────────────────────────────

    [Fact]
    public void One_shipped_piece_advances_three_sets_and_the_disclosure_names_all_three()
    {
        // CONTRACT, not a count: the set corpus is generator-authored and grows every generation, so
        // the shared-member total (28 → 524 and climbing) is stale by construction. What §disclosure
        // actually asserts is that a member legal in MORE THAN ONE set is disclosed against every set
        // it advances — never a merged or dropped count. So the test finds a real multi-set member of
        // whatever size the corpus holds and proves the disclosure names all of them, agreed with the
        // per-set evaluator.
        var sets = ThresholdGrantCorpusTests.Sets();
        var shared = SetDisclosure.SharedMembers(sets);

        Assert.NotEmpty(shared);
        Assert.All(shared, kv => Assert.True(kv.Value.Count >= 2,
            $"{kv.Key.ContainerId} is in `shared` but advances fewer than two sets"));

        var multi = shared.First(kv => kv.Value.Count >= 2);
        var disclosure = Assert.Single(SetDisclosure.ForWearer(
            new[] { new EquippedPiece(multi.Key.Role, multi.Key.ContainerId) }, sets));

        Assert.Equal(multi.Value.Count, disclosure.AdvancesSetIds.Count);
        Assert.Empty(disclosure.RedundantSetIds);
        Assert.Equal(multi.Value.OrderBy(s => s, StringComparer.Ordinal), disclosure.AdvancesSetIds);

        // And it agrees with module 12's own per-set view — one counter each, never one merged count.
        var progress = SetEvaluator.Progress(new[] { new EquippedPiece(multi.Key.Role, multi.Key.ContainerId) }, sets);
        Assert.Equal(
            disclosure.AdvancesSetIds.OrderBy(s => s, StringComparer.Ordinal),
            progress.Select(p => p.SetId).OrderBy(s => s, StringComparer.Ordinal));
    }

    [Fact]
    public void A_second_piece_in_a_claimed_set_role_is_disclosed_as_redundant_never_refused()
    {
        var set = new SetDef("set.two", "Two", new[]
        {
            new SetMemberDef("item.a", ItemRole.JewelMinorA, ItemFrame.Humanoid),
            new SetMemberDef("item.b", ItemRole.JewelMinorA, ItemFrame.Humanoid),
        }, Array.Empty<SetTierDef>());

        var worn = new[]
        {
            new EquippedPiece(ItemRole.JewelMinorA, "item.a"),
            new EquippedPiece(ItemRole.JewelMinorA, "item.b"),
        };

        var disclosure = SetDisclosure.ForWearer(worn, new[] { set });
        Assert.Equal(new[] { "set.two" }, disclosure[0].AdvancesSetIds);
        Assert.Empty(disclosure[0].RedundantSetIds);
        Assert.Empty(disclosure[1].AdvancesSetIds);
        Assert.Equal(new[] { "set.two" }, disclosure[1].RedundantSetIds);

        // Counting is per ROLE, so the set still reads 1 — and equipping the second stays legal.
        var progress = Assert.Single(SetEvaluator.Progress(worn, new[] { set }));
        Assert.Equal(1, progress.Count);
    }
}
