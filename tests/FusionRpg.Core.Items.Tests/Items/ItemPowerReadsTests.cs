using System.Reflection;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Effects.Atoms.Power;
using FusionRpg.Core.Items.Power;
using FusionRpg.Core.Stats.Aptitudes;
using Xunit;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// `item-power-reads` (item module 9) — **D13 is VOID**: this module calls E9/E10, it builds no
/// vector, coefficient or cost function. Every test either exercises a real shipped price or asserts
/// the honesty rules (unpriced-never-zero, coefficient sensitivity stated per read).
/// </summary>
public class ItemPowerReadsTests
{
    static AtomRow Vitality(int tier = 3) => new()
    {
        AtomId = AtomRow.DeriveId("atom.vitality", "", tier), KindId = "stat.modify", FamilyId = "atom.vitality",
        Variant = "", Tier = tier, Name = "Vitality",
        ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":45}",
    };

    static ItemPowerTuning Tuning(int capMilli = 150) =>
        new(capMilli, GrantedActionShareCapMilli: null, ShowPowerOnCard: true, PowerDisplaySigFigs: 2, PowerDisplayBandPercent: 25);

    // ---- D13-VOID boundary --------------------------------------------------------------------

    [Fact]
    public void E9_is_consumed_not_reimplemented()
    {
        var powerNamespace = typeof(ItemPowerReads).Namespace!;
        var types = typeof(ItemPowerReads).Assembly.GetTypes().Where(t => t.Namespace == powerNamespace);
        string[] forbidden = { "PowerVector", "CostFunction", "CoefficientTable", "PowerTables" };
        foreach (var t in types)
            Assert.DoesNotContain(t.Name, forbidden);
    }

    // ---- R1 -------------------------------------------------------------------------------------

    [Fact]
    public void Implicit_share_is_a_ratio_of_two_prices_from_one_function()
    {
        // R1's claim: the ‰ SHARE is coefficient-insensitive because BOTH the atom's price and the
        // rarity ceiling are prices from the same function (module 7's power_ceiling is itself "the
        // price of one reference slate through the SAME cost function the consumers use"). Pricing a
        // reference atom under two coefficient tables and feeding each price in as that table's own
        // ceiling proves the ratio survives a uniform rescale, even though the absolute price does not.
        var atom = Vitality();
        var reference = Vitality(tier: 5); // stands in for the seeded reference slate
        var authored = PowerTables.Authored();
        var rescaled = new PowerTables(
            authored.Coefficients.Select(c => c with { CoeffMilli = c.CoeffMilli * 2 }).ToList(),
            authored.Frequencies, authored.PredicateFrequencies);

        var ceilingAuthored = CostFunction.Price(reference, authored).Power.Total;
        var ceilingRescaled = CostFunction.Price(reference, rescaled).Power.Total;
        Assert.True(ceilingRescaled > ceilingAuthored); // sanity: the rescale really did move the price

        var shareAuthored = ItemPowerReads.ImplicitShare(atom, ceilingAuthored, Tuning(), authored);
        var shareRescaled = ItemPowerReads.ImplicitShare(atom, ceilingRescaled, Tuning(), rescaled);

        Assert.False(shareAuthored.Unpriced);
        Assert.False(shareRescaled.Unpriced);
        Assert.Equal(shareAuthored.ShareMilli, shareRescaled.ShareMilli);
    }

    [Fact]
    public void Implicit_over_budget_is_a_finding_not_a_generation_input()
    {
        var atom = Vitality(tier: 5);
        var read = ItemPowerReads.ImplicitShare(atom, rarityCeiling: 1, Tuning(capMilli: 1), PowerTables.Authored());
        Assert.True(read.Over);
        // The read is a value, not an action -- nothing here mutates a container, a drop table or the
        // atom itself. There is no method on this type that takes a container and writes to it.
        Assert.DoesNotContain(typeof(ItemPowerReads).GetMethods(), m => m.GetParameters().Any(p => p.ParameterType == typeof(ContainerRow)));
    }

    [Fact]
    public void Unpriced_never_reads_as_zero_for_implicit_share()
    {
        var unknownKindAtom = Vitality() with { KindId = "not-a-real-kind" };
        var read = ItemPowerReads.ImplicitShare(unknownKindAtom, rarityCeiling: 1000, Tuning());
        Assert.True(read.Unpriced);
        Assert.Null(read.ShareMilli);
        Assert.False(string.IsNullOrEmpty(read.UnpricedReason));
    }

    [Fact]
    public void A_missing_rarity_ceiling_is_unpriced_not_a_zero_share()
    {
        var read = ItemPowerReads.ImplicitShare(Vitality(), rarityCeiling: null, Tuning());
        Assert.True(read.Unpriced);
    }

    // ---- R2 -------------------------------------------------------------------------------------

    [Fact]
    public void Granted_action_price_uses_the_rung_path()
    {
        var reference = PowerVector.FromCategory(PowerCategory.Offense, 1000);
        var expected = reference.ScaleMilli(2000).Total;

        var read = ItemPowerReads.GrantedActionPrice(qPowerMilli: 2000, rarityCeiling: 1000);
        Assert.False(read.Unpriced);
        Assert.Equal(checked((long)expected * 1000L) / 1000, read.ShareMilli);
    }

    [Fact]
    public void An_action_with_no_rung_is_unpriced_never_free()
    {
        var read = ItemPowerReads.GrantedActionPrice(qPowerMilli: null, rarityCeiling: 1000);
        Assert.True(read.Unpriced);
        Assert.Null(read.ShareMilli);
    }

    [Fact]
    public void Granted_action_price_is_flagged_coefficient_sensitive()
    {
        var read = ItemPowerReads.GrantedActionPrice(qPowerMilli: 1000, rarityCeiling: 1000);
        Assert.True(read.CoefficientSensitive);
    }

    [Fact]
    public void Implicit_share_is_flagged_coefficient_insensitive()
    {
        var read = ItemPowerReads.ImplicitShare(Vitality(), rarityCeiling: 1000, Tuning());
        Assert.False(read.CoefficientSensitive);
    }

    // ---- R3 -------------------------------------------------------------------------------------

    [Fact]
    public void Card_power_renders_two_sig_figs_with_its_band()
    {
        var v = new PowerVector(1284, 1284, 1284, 1284, 1284);
        var display = ItemPowerReads.CardPower(v, Tuning());

        Assert.True(display.Shown);
        Assert.Equal(25, display.BandPercent);
        var rendered = display.Render();
        Assert.Contains("(±25%)", rendered);
        Assert.DoesNotContain(",284", rendered); // never four sig figs of confidence
    }

    [Fact]
    public void Display_band_equals_content_validation_drift_tolerance()
    {
        Assert.Equal(ContentValidation.DriftTolerancePercent, Tuning().PowerDisplayBandPercent);
    }

    [Fact]
    public void Show_power_on_card_false_suppresses_the_row_and_nothing_else()
    {
        var v = new PowerVector(100, 0, 0, 0, 0);
        var shown = ItemPowerReads.CardPower(v, Tuning());
        var suppressed = ItemPowerReads.CardPower(v, Tuning() with { ShowPowerOnCard = false });

        Assert.True(shown.Shown);
        Assert.False(suppressed.Shown);
        Assert.Equal("", suppressed.Render());
    }

    [Fact]
    public void Power_scalar_is_deterministic_across_repeated_reads()
    {
        var v = new PowerVector(1234, 987, 555, 12, 3000);
        var first = ItemPowerReads.CardPower(v, Tuning());
        var second = ItemPowerReads.CardPower(v, Tuning());
        Assert.Equal(first.RoundedValue, second.RoundedValue);
    }

    // ---- R4 -------------------------------------------------------------------------------------

    [Fact]
    public void Aptitude_read_is_inert_and_says_why_without_its_vocabulary()
    {
        var result = AptitudeAffixPrice.Read(Array.Empty<AtomRow>(), Vitality(), shareDeltaMilli: 100);
        Assert.False(result.Available);
        Assert.Contains("AllocationScope", result.Reason);
        Assert.Null(result.Marginal);
    }

    [Fact]
    public void The_vocabulary_gate_also_checks_AllocationScopes_own_member_count()
    {
        // A fifth AllocationScope value landing without the module also being updated is caught here,
        // not silently believed.
        Assert.Equal(4, Enum.GetValues<AllocationScope>().Length);
        Assert.False(AptitudeAffixPrice.VocabularyReady);
    }
}
