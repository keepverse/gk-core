using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// ⭐ item module 15's follow-up (2026-09-05): <c>Resolver</c>'s A1 <c>Mixed</c> semantics threaded
/// into <see cref="Instantiator.DrawBudget"/>, plus the two parameters `spec-enhance-reroll.md` §2
/// asks that method for — <c>count</c> and <c>excludeGroups</c>.
///
/// <para>Two claims are under test here, and the second matters as much as the first:
/// (1) a <see cref="AffixClass.Mixed"/> affix spends one prefix roll AND one suffix roll
/// simultaneously — never doubled, never drawn twice, never picked with no paired roll to spend; and
/// (2) <b>a pool with no <c>Mixed</c> affix draws exactly what the old two-independent-draws model
/// drew</b>, proven against a verbatim transcription of that model rather than against a golden
/// recorded from the new code (which would prove nothing about the old).</para>
/// </summary>
public class InstantiatorDrawBudgetTests
{
    static readonly Dictionary<string, AtomRow> Catalog = new(StringComparer.Ordinal);
    static readonly Dictionary<string, AffixRow> Affixes = new(StringComparer.Ordinal);

    static InstantiatorDrawBudgetTests()
    {
        void Add(string family, string variant, int tier, int amount)
        {
            var id = AtomRow.DeriveId(family, variant, tier);
            Catalog[id] = new AtomRow
            {
                AtomId = id, KindId = "stat.modify", FamilyId = family, Variant = variant, Tier = tier,
                ParamsJson = $"{{\"channel\":\"atk\",\"op\":\"flat\",\"amount\":{amount}}}",
            };
        }

        Add("atom.vitality", "", 1, 45);
        Add("atom.might", "", 1, 10);
        Add("atom.thorns", "", 1, 7);   // the "triggered" half of a mixed bundle, for fixture purposes
        Add("atom.guard", "", 1, 3);
        Add("atom.spark", "", 1, 5);
        foreach (var v in new[] { "fire", "ice", "air" })
            Add("atom.ember-power", v, 1, 5);
    }

    static AtomRow? LookupAtom(string id) => Catalog.TryGetValue(id, out var a) ? a : null;
    static AffixRow? LookupAffix(string id) => Affixes.TryGetValue(id, out var a) ? a : null;

    static void Seed(params AffixRow[] affixes)
    {
        Affixes.Clear();
        foreach (var a in affixes) Affixes[a.AffixId] = a;
    }

    static AffixRow Single(string id, AffixClass cls, string atomId) =>
        new(id, cls, new[] { new AffixRefRow(1, atomId) });

    /// <summary>The mixed bundle used throughout: two concrete refs of different derived kinds, which
    /// is the ONLY way `AffixValidator` ever produces `Mixed` — so a `Mixed` affix is always multi-ref,
    /// and expanding one is not optional for these semantics to be reachable at all.</summary>
    static AffixRow Mixed(string id, params string[] atomIds) =>
        new(id, AffixClass.Mixed, atomIds.Select((a, i) => new AffixRefRow(i + 1, a)).ToArray());

    static ContainerRow Container(string id, int prefixRolls, int suffixRolls, params ContainerPoolRow[] pool) =>
        new()
        {
            ContainerId = id, Kind = ContainerKind.Item,
            PrefixRolls = prefixRolls, SuffixRolls = suffixRolls,
            Pool = pool,
        };

    static List<string> Draw(ContainerRow c, long seed) =>
        Instantiator.Draw(c, LookupAtom, LookupAffix, seed);

    // ---- A1: one roll of each budget, simultaneously -------------------------------------------------

    [Fact]
    public void A_mixed_affix_is_never_drawn_twice_in_one_draw()
    {
        // The double-draw defect the old model carried: the only pool row is Mixed-eligible for BOTH
        // passes, and with no state between them it was picked in each — four atoms, the bundle twice.
        Seed(Mixed("affix.mixed", "atom.vitality.t1", "atom.thorns.t1"));
        var c = Container("item.only-mixed", 1, 1, new ContainerPoolRow("affix.mixed", 100, "g.mixed"));

        for (long seed = 0; seed < 30; seed++)
        {
            var drawn = Draw(c, seed);
            Assert.Equal(new[] { "atom.vitality.t1", "atom.thorns.t1" }, drawn);
        }
    }

    [Fact]
    public void A_mixed_affix_spends_one_roll_of_each_budget_never_doubling_either()
    {
        // One prefix roll, one suffix roll, and a pool that can satisfy either separately. Whenever
        // the mixed bundle is drawn it has spent BOTH, so no separate suffix affix may appear beside
        // it; whenever it is not, the two fillers fill the two budgets.
        Seed(
            Mixed("affix.mixed", "atom.vitality.t1", "atom.thorns.t1"),
            Single("affix.p", AffixClass.Prefix, "atom.might.t1"),
            Single("affix.s", AffixClass.Suffix, "atom.guard.t1"));
        var c = Container("item.mixed-and-fillers", 1, 1,
            new ContainerPoolRow("affix.mixed", 50, "g.mixed"),
            new ContainerPoolRow("affix.p", 50, "g.p"),
            new ContainerPoolRow("affix.s", 100, "g.s"));

        var sawMixed = false;
        var sawPlain = false;
        for (long seed = 0; seed < 60; seed++)
        {
            var drawn = Draw(c, seed);
            if (drawn.Contains("atom.vitality.t1"))
            {
                sawMixed = true;
                // Both refs together, exactly once, and the suffix budget is gone with it.
                Assert.Equal(new[] { "atom.vitality.t1", "atom.thorns.t1" }, drawn);
            }
            else
            {
                sawPlain = true;
                Assert.Equal(new[] { "atom.might.t1", "atom.guard.t1" }, drawn);
            }
        }

        Assert.True(sawMixed, "the mixed bundle was never drawn across 60 seeds — the fixture proves nothing");
        Assert.True(sawPlain, "the mixed bundle was drawn on every seed — the fixture proves nothing");
    }

    [Fact]
    public void A_mixed_affix_is_not_drawable_when_the_paired_budget_is_zero()
    {
        // Picking it would spend a suffix roll the container never authored.
        Seed(Mixed("affix.mixed", "atom.vitality.t1", "atom.thorns.t1"));
        var c = Container("item.no-suffix-budget", 1, 0, new ContainerPoolRow("affix.mixed", 100, "g.mixed"));

        for (long seed = 0; seed < 20; seed++) Assert.Empty(Draw(c, seed));
    }

    [Fact]
    public void A_second_mixed_affix_is_not_drawable_once_the_first_has_spent_the_paired_budget()
    {
        // Two prefix rolls, one suffix roll, two Mixed rows. The first Mixed pick spends the single
        // suffix roll; the second prefix roll must fall back to the plain prefix filler.
        Seed(
            Mixed("affix.mixed1", "atom.vitality.t1", "atom.thorns.t1"),
            Mixed("affix.mixed2", "atom.spark.t1", "atom.guard.t1"),
            Single("affix.p", AffixClass.Prefix, "atom.might.t1"));
        var c = Container("item.two-mixed", 2, 1,
            new ContainerPoolRow("affix.mixed1", 100, "g.m1"),
            new ContainerPoolRow("affix.mixed2", 100, "g.m2"),
            new ContainerPoolRow("affix.p", 1, "g.p"));

        for (long seed = 0; seed < 40; seed++)
        {
            var drawn = Draw(c, seed);
            var mixedBundles =
                (drawn.Contains("atom.vitality.t1") ? 1 : 0) + (drawn.Contains("atom.spark.t1") ? 1 : 0);
            Assert.True(mixedBundles <= 1,
                $"seed {seed}: {mixedBundles} mixed bundles drawn against a single suffix roll");
        }
    }

    [Fact]
    public void A_mixed_affix_is_never_drawn_by_the_suffix_pass_once_a_plain_prefix_spent_the_prefix_budget()
    {
        // The other direction of "one of each, simultaneously": a `Mixed` bundle costs a prefix roll
        // too, so it can only ever be picked by the pass that still HAS one. Here the prefix budget is
        // one and the pool offers a plain prefix, so a `Mixed` pick in the suffix pass would push the
        // item to two prefix-eligible affixes against `PrefixRolls = 1` — the exact post-op invariant
        // `RerollPolicy.ValidatePostOp` refuses.
        Seed(
            Single("affix.p", AffixClass.Prefix, "atom.might.t1"),
            Mixed("affix.mixed", "atom.vitality.t1", "atom.thorns.t1"));
        var c = Container("item.prefix-spent", 1, 1,
            new ContainerPoolRow("affix.p", 100, "g.p"),
            new ContainerPoolRow("affix.mixed", 100, "g.mixed"));

        for (long seed = 0; seed < 60; seed++)
        {
            var drawn = Draw(c, seed);
            var tookPlainPrefix = drawn.Contains("atom.might.t1");
            var tookMixed = drawn.Contains("atom.vitality.t1");
            Assert.False(tookPlainPrefix && tookMixed,
                $"seed {seed}: a plain prefix AND a mixed bundle — two prefix-eligible affixes against PrefixRolls=1");
        }
    }

    // ---- the regression claim: byte-identical wherever no Mixed affix exists -------------------------

    [Fact]
    public void Every_mixed_free_pool_draws_exactly_what_the_two_independent_draws_model_drew()
    {
        // ⭐ The safety proof for threading A1 into a path every module instantiates through. The
        // oracle below is the pre-2026-09-05 implementation transcribed verbatim; the new code must
        // agree with it on every seed for every pool that has no `Mixed` row.
        Seed(
            Single("affix.p1", AffixClass.Prefix, "atom.vitality.t1"),
            Single("affix.p2", AffixClass.Prefix, "atom.might.t1"),
            Single("affix.p3", AffixClass.Prefix, "atom.spark.t1"),
            Single("affix.s1", AffixClass.Suffix, "atom.thorns.t1"),
            Single("affix.s2", AffixClass.Suffix, "atom.guard.t1"));

        var pool = new[]
        {
            new ContainerPoolRow("affix.p1", 70, "g.p1"),
            new ContainerPoolRow("affix.p2", 20, "g.p2"),
            new ContainerPoolRow("affix.p3", 3, "g.p3"),
            new ContainerPoolRow("affix.s1", 45, "g.s1"),
            new ContainerPoolRow("affix.s2", 55, "g.s2"),
            new ContainerPoolRow("affix.p1", 0, "g.zero"), // weight 0: offered, never drawn
        };

        var shapes = new List<ContainerRow>();
        for (var prefix = 0; prefix <= 3; prefix++)
            for (var suffix = 0; suffix <= 3; suffix++)
                shapes.Add(Container($"item.shape.{prefix}.{suffix}", prefix, suffix, pool));

        foreach (var shape in shapes)
            for (long seed = 0; seed < 25; seed++)
                Assert.Equal(
                    LegacyDraw(shape, LookupAtom, LookupAffix, seed),
                    Instantiator.Draw(shape, LookupAtom, LookupAffix, seed));
    }

    /// <summary>The pre-2026-09-05 <c>Instantiator.Draw</c>, transcribed verbatim: two budget draws
    /// with no state carried between them. Kept as the byte-compatibility oracle above — a golden
    /// recorded from the new code could not tell a preserved sequence from a shifted one.</summary>
    static List<string> LegacyDraw(
        ContainerRow container, Func<string, AtomRow?> lookupAtom, Func<string, AffixRow?> lookupAffix,
        long rollSeed)
    {
        var picked = new List<string>();
        if (container.Pool.Count == 0) return picked;

        if (container.PrefixRolls > 0)
            LegacyBudget(container, lookupAtom, lookupAffix, rollSeed, "prefix", container.PrefixRolls,
                a => a.Class is AffixClass.Prefix or AffixClass.Mixed, picked);
        if (container.SuffixRolls > 0)
            LegacyBudget(container, lookupAtom, lookupAffix, rollSeed, "suffix", container.SuffixRolls,
                a => a.Class is AffixClass.Suffix or AffixClass.Mixed, picked);

        return picked;
    }

    static void LegacyBudget(
        ContainerRow container, Func<string, AtomRow?> lookupAtom, Func<string, AffixRow?> lookupAffix,
        long rollSeed, string budgetName, int rolls, Func<AffixRow, bool> eligible, List<string> picked)
    {
        var rng = new AtomRandom(unchecked((ulong)rollSeed),
            AtomStreams.Pool + "." + budgetName + "." + container.ContainerId);

        string GroupOf(ContainerPoolRow row, AffixRow affix) =>
            !string.IsNullOrWhiteSpace(row.Group)
                ? row.Group!
                : lookupAtom(affix.Refs[0].AtomId!)!.FamilyId + "|" + lookupAtom(affix.Refs[0].AtomId!)!.Variant;

        var remaining = container.Pool
            .Where(p => p.Weight > 0 && eligible(lookupAffix(p.AffixId)!))
            .Select(p => (Row: p, Group: GroupOf(p, lookupAffix(p.AffixId)!)))
            .ToList();

        for (var roll = 0; roll < rolls && remaining.Count > 0; roll++)
        {
            var total = remaining.Sum(c => c.Row.Weight);
            var target = rng.NextInclusive(1, total);

            var running = 0;
            var chosen = remaining[^1];
            foreach (var candidate in remaining)
            {
                running += candidate.Row.Weight;
                if (running < target) continue;
                chosen = candidate;
                break;
            }

            var affix = lookupAffix(chosen.Row.AffixId)!;
            picked.Add(affix.Refs[0].AtomId!); // the old expansion: single concrete ref only
            remaining.RemoveAll(c => string.Equals(c.Group, chosen.Group, StringComparison.Ordinal));
        }
    }

    // ---- spec-enhance-reroll.md §2's two parameters ---------------------------------------------------

    [Fact]
    public void DrawBudget_spends_only_the_count_it_is_given()
    {
        Seed(
            Single("affix.p1", AffixClass.Prefix, "atom.vitality.t1"),
            Single("affix.p2", AffixClass.Prefix, "atom.might.t1"),
            Single("affix.p3", AffixClass.Prefix, "atom.spark.t1"));
        var c = Container("item.partial", 3, 0,
            new ContainerPoolRow("affix.p1", 100, "g.p1"),
            new ContainerPoolRow("affix.p2", 100, "g.p2"),
            new ContainerPoolRow("affix.p3", 100, "g.p3"));

        Assert.Empty(Instantiator.DrawBudget(c, LookupAtom, LookupAffix, 1, AffixClass.Prefix, 0).AtomIds);
        Assert.Single(Instantiator.DrawBudget(c, LookupAtom, LookupAffix, 1, AffixClass.Prefix, 1).AtomIds);
        Assert.Equal(2, Instantiator.DrawBudget(c, LookupAtom, LookupAffix, 1, AffixClass.Prefix, 2).AtomIds.Count);
    }

    [Fact]
    public void DrawBudget_never_draws_into_an_excluded_group()
    {
        // The one behavioural change §2 needs from the instantiator: a partial redraw seeds the
        // exclusion set with the groups of that budget's RETAINED affixes, so one-per-group survives.
        Seed(
            Single("affix.p1", AffixClass.Prefix, "atom.vitality.t1"),
            Single("affix.p2", AffixClass.Prefix, "atom.might.t1"),
            Single("affix.p3", AffixClass.Prefix, "atom.spark.t1"));
        var c = Container("item.excluded", 3, 0,
            new ContainerPoolRow("affix.p1", 100, "g.p1"),
            new ContainerPoolRow("affix.p2", 100, "g.p2"),
            new ContainerPoolRow("affix.p3", 100, "g.p3"));
        var retained = new HashSet<string>(new[] { "g.p1", "g.p2" }, StringComparer.Ordinal);

        for (long seed = 0; seed < 30; seed++)
        {
            var drawn = Instantiator.DrawBudget(c, LookupAtom, LookupAffix, seed, AffixClass.Prefix, 1, retained);
            Assert.Equal(new[] { "atom.spark.t1" }, drawn.AtomIds);
        }

        // Every group excluded: nothing left to draw, and the pass ends without a roll rather than
        // throwing or repeating a retained group.
        var all = new HashSet<string>(new[] { "g.p1", "g.p2", "g.p3" }, StringComparer.Ordinal);
        Assert.Empty(Instantiator.DrawBudget(c, LookupAtom, LookupAffix, 1, AffixClass.Prefix, 3, all).AtomIds);
    }

    [Fact]
    public void DrawBudget_reports_the_paired_budget_it_spent_and_the_affixes_it_drew()
    {
        Seed(Mixed("affix.mixed", "atom.vitality.t1", "atom.thorns.t1"));
        var c = Container("item.report", 1, 1, new ContainerPoolRow("affix.mixed", 100, "g.mixed"));

        var prefixPass = Instantiator.DrawBudget(
            c, LookupAtom, LookupAffix, 3, AffixClass.Prefix, 1, crossBudget: 1);

        Assert.Equal(new[] { "affix.mixed" }, prefixPass.AffixIds);
        Assert.Equal(1, prefixPass.CrossBudgetSpent);

        // And with that spent, the suffix pass has nothing left to roll for.
        var suffixPass = Instantiator.DrawBudget(
            c, LookupAtom, LookupAffix, 3, AffixClass.Suffix, 1 - prefixPass.CrossBudgetSpent,
            excludeAffixIds: new HashSet<string>(prefixPass.AffixIds, StringComparer.Ordinal));
        Assert.Empty(suffixPass.AtomIds);
    }

    [Fact]
    public void DrawBudget_refuses_a_mixed_budget_and_negative_counts_rather_than_guessing()
    {
        Seed(Single("affix.p1", AffixClass.Prefix, "atom.vitality.t1"));
        var c = Container("item.guard", 1, 0, new ContainerPoolRow("affix.p1", 100, "g.p1"));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Instantiator.DrawBudget(c, LookupAtom, LookupAffix, 1, AffixClass.Mixed, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Instantiator.DrawBudget(c, LookupAtom, LookupAffix, 1, AffixClass.Prefix, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Instantiator.DrawBudget(c, LookupAtom, LookupAffix, 1, AffixClass.Prefix, 1, crossBudget: -1));
    }

    // ---- expansion ------------------------------------------------------------------------------------

    [Fact]
    public void A_multi_concrete_ref_bundle_expands_to_every_ref_in_seq_order()
    {
        // Not a widening for its own sake: `AffixValidator` derives `Mixed` only when a bundle spans
        // two ref kinds, so a Mixed affix is multi-ref by construction — without this expansion the
        // Mixed semantics above would be unreachable through Draw().
        Seed(new AffixRow("affix.bundle", AffixClass.Prefix, new[]
        {
            new AffixRefRow(2, "atom.might.t1"),
            new AffixRefRow(1, "atom.vitality.t1"),
        }));
        var c = Container("item.bundle", 1, 0, new ContainerPoolRow("affix.bundle", 100, "g.bundle"));

        Assert.Equal(new[] { "atom.vitality.t1", "atom.might.t1" }, Draw(c, 1));
    }

    [Fact]
    public void A_slot_bearing_affix_still_throws_and_names_the_resolver_that_can_expand_it()
    {
        // The residual, stated exactly: this entry point returns bare atom ids and rolls no domain
        // member, tier or value. That is not an unbuilt module — `resolution-order` landed
        // 2026-09-02 — it is Draw()'s own shape.
        Seed(new AffixRow("affix.elemental", AffixClass.Prefix, new[]
        {
            new AffixRefRow(1, null, "E1", "element", 1, "atom.ember-power.$E1"),
        }));
        var c = Container("item.slot", 1, 0, new ContainerPoolRow("affix.elemental", 100, "g.elemental"));

        var ex = Assert.Throws<NotSupportedException>(() => Draw(c, 1));
        Assert.Contains("Resolver.Resolve", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_pool_draws_nothing()
    {
        Seed();
        Assert.Empty(Draw(Container("item.empty", 3, 3), 1));
    }
}
