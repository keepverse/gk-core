using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Effects.Atoms.Power;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Power;
using FusionRpg.Core.Items.Uniques;
using Xunit;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// Module 9's <c>ceilingFor</c> reader — <c>spec-rarity-bands.md:403-412</c>'s formula, asserted at
/// the consumer exactly as module 7's Testing Strategy said it would be (*"asserted at the consumer,
/// not claimed here"*).
/// </summary>
public class RarityPowerCeilingTests
{
    /// <summary>The shipped ten rungs, verbatim from `gk-data/packs/fusion/data/seed/rarity/ladder.v1.json`. Written out
    /// rather than loaded so a seed edit shows up as a failing test with a diff, not as a silently
    /// re-derived expectation.</summary>
    static IReadOnlyList<RarityRow> Ladder() => new[]
    {
        new RarityRow("chaff", 10, 0, 0, 1, 1),
        new RarityRow("sprout", 20, 0, 1, 1, 1),
        new RarityRow("grafted", 30, 0, 1, 1, 3),
        new RarityRow("cultivated", 40, 1, 1, 1, 3),
        new RarityRow("fused", 50, 1, 1, 2, 4),
        new RarityRow("chimeric", 60, 1, 2, 2, 4),
        new RarityRow("heirloom", 70, 1, 2, 3, 5),
        new RarityRow("firstseed", 80, 2, 2, 3, 5),
        new RarityRow("sunwoven", 90, 2, 2, 4, 5),
        new RarityRow("almanac", 100, 3, 2, 4, 5),
    };

    /// <summary>The shipped ‰ column, verbatim from `gk-core/data/tuning/item-rarity.v1.json` and identical to
    /// `spec-rarity-bands.md:415-424`'s published table.</summary>
    static readonly IReadOnlyDictionary<string, int> Shares = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["chaff"] = 0,
        ["sprout"] = 22,
        ["grafted"] = 51,
        ["cultivated"] = 84,
        ["fused"] = 173,
        ["chimeric"] = 243,
        ["heirloom"] = 492,
        ["firstseed"] = 632,
        ["sunwoven"] = 818,
        ["almanac"] = 1000,
    };

    static int? ShareOf(string rarityId) => Shares.TryGetValue(rarityId, out var v) ? v : null;

    static RarityPowerCeilings Table(PowerTables? tables = null) =>
        RarityPowerCeilings.Build(Ladder(), ShareOf, tables ?? PowerTables.Authored());

    /// <summary>The authored table with every coefficient multiplied by <paramref name="factor"/> —
    /// the "uniform coefficient rescale" X6 is, and the only thing the share invariance is claimed
    /// against.</summary>
    static PowerTables Rescaled(int factor)
    {
        var authored = PowerTables.Authored();
        var scaled = authored.Coefficients
            .Select(c => new PowerCoefficientRow(c.KindId, c.Channel, c.CoeffMilli * factor, c.ReferenceScale))
            .ToList();
        return new PowerTables(scaled, authored.Frequencies, authored.PredicateFrequencies, authored.Interactions);
    }

    // ---- pinAE ---------------------------------------------------------------------------------

    [Fact]
    public void PinAe_is_one_reference_almanac_slate_priced_by_the_consumers_own_cost_function()
    {
        // The slate: almanac's own count-band floor (3 prefix + 2 suffix = 5 affixes), each one AE --
        // the midpoint magnitude of the middle tier of its authored t4-t5 window. Every term is read
        // from the seeded ladder and the shipped AE reckoner; nothing here is a new number.
        var almanac = RarityPowerCeilings.WindowOf(Ladder().Single(r => r.RarityId == "almanac"));
        Assert.Equal(5, almanac.AffixCount);
        Assert.Equal(4, UniqueBudget.ReferenceTier(almanac));
        Assert.Equal(92L, UniqueBudget.ReferenceMagnitude(almanac));

        // 5 x 92 hp = 460 on one channel; maxHp's reference scale is 10 and its coefficient 1000‰,
        // so 46,000 points. This is the exact arithmetic ActorPowerCache.Compose runs on a real
        // container, which is the entire safety argument for the column.
        Assert.Equal(46_000L, RarityPowerCeilings.PriceReferenceSlate(almanac, PowerTables.Authored()));
        Assert.Equal(46_000L, Table().PinAe);
        Assert.Equal("almanac", Table().TopRungId);
    }

    [Fact]
    public void The_reference_slate_prices_through_ActorPowerCache_not_a_second_cost_function()
    {
        // Compose aggregates same-channel atoms and prices the total ONCE (its own doc: "adding +10
        // atk twice is one +20 atk actor"). A hand-built container of the same five affixes must
        // therefore price identically -- if it did not, the ratio the ceiling is used in would be a
        // ratio of two different functions.
        var almanac = RarityPowerCeilings.WindowOf(Ladder().Single(r => r.RarityId == "almanac"));
        var byHand = Enumerable.Range(0, almanac.AffixCount)
            .Select(i => MaxHpAtom($"atom.by-hand.{i}", UniqueBudget.ReferenceMagnitude(almanac)))
            .ToList();

        Assert.Equal(
            ActorPowerCache.Compose(byHand, PowerTables.Authored()).Total,
            RarityPowerCeilings.PriceReferenceSlate(almanac, PowerTables.Authored()));
    }

    // ---- the formula ---------------------------------------------------------------------------

    [Theory]
    [InlineData("chaff", 0, 0L)]
    [InlineData("sprout", 22, 1_012L)]
    [InlineData("grafted", 51, 2_346L)]
    [InlineData("cultivated", 84, 3_864L)]
    [InlineData("fused", 173, 7_958L)]
    [InlineData("chimeric", 243, 11_178L)]
    [InlineData("heirloom", 492, 22_632L)]
    [InlineData("firstseed", 632, 29_072L)]
    [InlineData("sunwoven", 818, 37_628L)]
    [InlineData("almanac", 1000, 46_000L)]
    public void The_ceiling_is_pinAe_times_the_seeded_share_divided_by_1000(
        string rarityId, int expectedShare, long expectedCeiling)
    {
        var read = Table().Of(rarityId);

        Assert.Equal(expectedShare, read.LadderShareMilli!.Value);
        Assert.Equal(expectedCeiling, read.CeilingPoints!.Value);
        Assert.Equal(expectedCeiling, 46_000L * expectedShare / 1000L); // the spec's own arithmetic
    }

    [Fact]
    public void The_top_rungs_ceiling_is_pinAe_itself()
    {
        // 1000‰ of top is the top. Stated as a test because it is the one row where an off-by-a-scale
        // error would still look plausible.
        var table = Table();
        Assert.Equal(table.PinAe, table.Of("almanac").CeilingPoints!.Value);
    }

    [Fact]
    public void Every_seeded_rung_resolves_to_a_ceiling()
    {
        var table = Table();
        Assert.Equal(RarityLadder.RungIds.Count, table.PricedRungs);
        foreach (var rungId in RarityLadder.RungIds)
            Assert.False(table.Of(rungId).Unpriced, $"'{rungId}' is unpriced");
    }

    // ---- the safety argument -------------------------------------------------------------------

    [Fact]
    public void The_share_is_invariant_under_a_uniform_coefficient_rescale()
    {
        // spec-rarity-bands.md's whole reason for shipping the column provisionally: pinAE and an
        // atom's price move by the SAME factor, so every share read off the column is exact today and
        // stays exact when X6 lands. Only the absolute threshold moves.
        var flat = Table(PowerTables.Authored());
        var doubled = Table(Rescaled(2));

        Assert.Equal(flat.PinAe * 2, doubled.PinAe);
        foreach (var rungId in RarityLadder.RungIds)
        {
            var a = flat.Of(rungId);
            var b = doubled.Of(rungId);
            Assert.Equal(a.LadderShareMilli, b.LadderShareMilli);
            Assert.Equal(a.CeilingPoints!.Value * 2, b.CeilingPoints!.Value);
        }
    }

    [Fact]
    public void The_provisional_flag_is_measured_off_the_live_table_not_asserted()
    {
        // Flat-1000 everywhere means X6 has not landed. A fitted (non-flat) table clears the flag with
        // nobody having to remember to -- spec-rarity-bands.md's "X6 landing: re-price pinAE and clear
        // the flag", made automatic.
        Assert.True(Table(PowerTables.Authored()).Provisional);
        Assert.True(Table(PowerTables.Authored()).Of("heirloom").Provisional);

        var fitted = Table(Rescaled(2));
        Assert.False(fitted.Provisional);
        Assert.False(fitted.Of("heirloom").Provisional);
    }

    [Fact]
    public void The_absolute_figure_cannot_be_rendered_without_its_provisional_flag()
    {
        // The illegal use spec-rarity-bands.md names is "quoting the absolute AE figure without the
        // provisional flag". Render is the only printer, and it cannot.
        var flat = Table(PowerTables.Authored());
        Assert.Contains("provisional", flat.RenderPinAe(), StringComparison.Ordinal);
        Assert.Contains("provisional", flat.Of("almanac").Render(), StringComparison.Ordinal);

        var fitted = Table(Rescaled(2));
        Assert.DoesNotContain("provisional", fitted.RenderPinAe(), StringComparison.Ordinal);
    }

    // ---- unpriced is never zero ------------------------------------------------------------------

    [Fact]
    public void A_rung_with_no_seeded_share_is_unpriced_never_zero()
    {
        var table = RarityPowerCeilings.Build(
            Ladder(), id => id == "heirloom" ? null : ShareOf(id), PowerTables.Authored());

        var read = table.Of("heirloom");
        Assert.True(read.Unpriced);
        Assert.Null(table.CeilingFor("heirloom"));
        Assert.Contains("power_ceiling", read.UnpricedReason!, StringComparison.Ordinal);

        // ...and the ladder around it still prices, so one missing row is one skipped rung rather
        // than a silent whole-table green.
        Assert.Equal(RarityLadder.RungIds.Count - 1, table.PricedRungs);
    }

    [Fact]
    public void A_zero_share_is_a_real_ceiling_of_zero_not_a_missing_one()
    {
        // chaff rolls no affixes (prefix 0 + suffix 0), so "may spend 0 rolled power" is the ladder's
        // own statement about it. Returning null here would put chaff straight back into the silent
        // skip this reader exists to end.
        var read = Table().Of("chaff");
        Assert.False(read.Unpriced);
        Assert.Equal(0L, read.CeilingPoints!.Value);
        Assert.Equal(0, Table().CeilingFor("chaff")!.Value);
    }

    [Fact]
    public void An_unknown_rarity_is_unpriced_and_says_so()
    {
        var read = Table().Of("mythic");
        Assert.True(read.Unpriced);
        Assert.Null(Table().CeilingFor("mythic"));
        Assert.Contains("mythic", read.UnpricedReason!, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unseeded_ladder_names_its_own_absence_rather_than_pricing_nothing_quietly()
    {
        var table = RarityPowerCeilings.Build(Array.Empty<RarityRow>(), _ => 1000, PowerTables.Authored());

        Assert.Equal(0, table.PricedRungs);
        Assert.NotNull(table.UnavailableReason);
        Assert.Null(table.CeilingFor("almanac"));
        Assert.Contains("no seeded rows", table.UnavailableReason!, StringComparison.Ordinal);
    }

    // ---- the consumer ----------------------------------------------------------------------------

    [Fact]
    public void ContentValidation_Budget_evaluates_a_rarity_bearing_container_once_the_reader_is_supplied()
    {
        // ⭐ The red-first pair, in Core. The FIRST call is what every caller of this overload did
        // before this reader existed -- ContentValidation.cs:73's `is not { } ceiling` skips, the
        // report is green, and it evaluated NOTHING. The second is the same content with a real
        // ceilingFor.
        var atoms = new[] { MaxHpAtom("atom.slate.t1", 40) };
        var container = new ContainerRow
        {
            ContainerId = "item.first-clear-almanac-seed",
            Kind = ContainerKind.Item,
            Rarity = "almanac",
            Atoms = new[] { new ContainerAtomRow(0, atoms[0].AtomId) },
        };

        var withoutReader = ContentValidation.Budget(new[] { container }, _ => atoms, _ => null);
        Assert.True(withoutReader.Ok);
        Assert.Equal(0, withoutReader.Evaluated);   // green over zero containers -- the silent degrade

        var withReader = ContentValidation.Budget(new[] { container }, _ => atoms, Table().CeilingFor);
        Assert.True(withReader.Ok);
        Assert.Equal(1, withReader.Evaluated);      // the same content, actually checked
    }

    [Fact]
    public void An_over_budget_container_is_a_finding_that_names_it_rather_than_a_clamp()
    {
        // sprout's ceiling is 1012 points. One 400 hp affix prices at 40,000 -- far over -- and the
        // check must NAME it, never shrink it (ContentValidation's own "never a generation input").
        var atoms = new[] { MaxHpAtom("atom.overspent.t1", 400) };
        var container = new ContainerRow
        {
            ContainerId = "item.overspent",
            Kind = ContainerKind.Item,
            Rarity = "sprout",
            Atoms = new[] { new ContainerAtomRow(0, atoms[0].AtomId) },
        };

        var report = ContentValidation.Budget(new[] { container }, _ => atoms, Table().CeilingFor);

        Assert.False(report.Ok);
        Assert.Equal(1, report.Evaluated);
        var failure = Assert.Single(report.Failures);
        Assert.Equal("item.overspent", failure.Subject);
        Assert.Contains("1012", failure.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void The_reader_declares_the_budget_key_it_consumes_and_that_key_is_registered()
    {
        // SC7: a rarity_budget key is registered with a named consumer, and this reader IS module 9,
        // the consumer RarityBudgetKeys records for it.
        Assert.True(RarityBudgetKeys.IsRegistered(RarityPowerCeilings.BudgetKey));
        Assert.Equal("item-power-reads (9)",
            RarityBudgetKeys.All.Single(k => k.Key == RarityPowerCeilings.BudgetKey).ConsumerModule);
    }

    [Fact]
    public void The_narrowing_to_the_overloads_int_delegate_throws_it_does_not_clamp()
    {
        // ContentValidation.Budget's rarity-keyed delegate is Func<string,int?> while its rung-keyed
        // sibling's is Func<int,long?>. A silent (int) cast on a magnitude is a cap wearing a cast's
        // clothes -- so a ceiling past int.MaxValue must fail loudly rather than wrap negative and
        // fail every container in the game.
        var huge = RarityPowerCeilings.Build(Ladder(), _ => int.MaxValue, PowerTables.Authored());
        Assert.True(huge.Of("almanac").CeilingPoints > int.MaxValue);
        Assert.Throws<OverflowException>(() => huge.CeilingFor("almanac"));
    }

    static AtomRow MaxHpAtom(string atomId, long amount) => new()
    {
        AtomId = atomId,
        KindId = "stat.modify",
        FamilyId = "atom.test",
        Tier = 1,
        Name = atomId,
        ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":" + amount + "}",
    };
}
