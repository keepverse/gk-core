using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Effects.Atoms.Power;
using Xunit;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// ST4.5d (manager ruling on the ST4.5a diagnosis): a triggered atom whose trigger has no
/// <c>power_trigger_frequency</c> row must be REFUSED by name, never priced at zero with a
/// <c>Priced</c> verdict. That silent zero is what the ST4.5a diagnosis found on the real corpus —
/// every <c>status.apply</c> action in the reading priced at 0 because <c>OnDamageDealt</c> had no row.
/// </summary>
public class CostFunctionTriggerFrequencyTests
{
    static readonly PowerCoefficientRow[] Coefficients =
        { new("status.apply", "", 333, 1) };

    static AtomRow StatusAtom(string trigger) => new()
    {
        AtomId = $"atom.probe.{trigger}",
        KindId = "status.apply",
        FamilyId = $"atom.probe.{trigger}",
        Tier = 1,
        Name = "probe",
        ParamsJson = """{"duration":5,"level":1,"status":"spore"}""",
        WhenJson = trigger.Length == 0 ? "{}" : $$"""{"chance":135,"trigger":"{{trigger}}"}""",
    };

    [Fact]
    public void A_trigger_with_no_frequency_row_refuses_its_atom_by_name_instead_of_pricing_it_at_zero()
    {
        var planted = new PowerTables(Coefficients, Array.Empty<TriggerFrequencyRow>());
        var atom = StatusAtom(AtomTriggers.OnDamageDealt);

        // The old behaviour, asserted so the refusal is provably replacing it rather than masking a
        // different number: conditionality is EXACTLY zero, so the atom priced at 0 under a `Priced`
        // verdict -- free, and nothing anywhere said so.
        var conditionality = CostFunction.Conditionality(
            CostFunction.Read(atom.WhenJson), CostFunction.Read(atom.ParamsJson), planted);
        Assert.Equal(0, conditionality);

        var refused = CostFunction.Price(atom, planted);

        Assert.False(refused.Ok);
        Assert.Contains(AtomTriggers.OnDamageDealt, refused.Verdict.Reason, StringComparison.Ordinal);
        Assert.Contains("no power_trigger_frequency row", refused.Verdict.Reason, StringComparison.Ordinal);

        // The power itself is unchanged (zero either way) -- this is a reporting fix, not a balance
        // one, and that is what keeps every golden where it was.
        Assert.True(refused.Power.IsZero);
    }

    [Fact]
    public void The_same_atom_prices_once_its_trigger_carries_a_row()
    {
        var withRow = new PowerTables(
            Coefficients, new[] { new TriggerFrequencyRow(AtomTriggers.OnDamageDealt, 60) });

        var priced = CostFunction.Price(StatusAtom(AtomTriggers.OnDamageDealt), withRow);

        Assert.True(priced.Ok, priced.Verdict.Reason);
        Assert.True(priced.Power.Total > 0, "a trigger with a rate must price above zero");
    }

    [Fact]
    public void A_triggerless_atom_is_untouched_by_the_refusal()
    {
        // The short-circuit `Conditionality` already documents: a permanent modifier is unconditional,
        // and a table with no frequencies at all must not refuse one.
        var planted = new PowerTables(Coefficients, Array.Empty<TriggerFrequencyRow>());

        var priced = CostFunction.Price(StatusAtom(trigger: ""), planted);

        Assert.True(priced.Ok, priced.Verdict.Reason);
        Assert.True(priced.Power.Total > 0);
    }

    [Fact]
    public void A_totaled_rate_that_is_zero_is_treated_as_no_row()
    {
        // `FrequencyOf` returns 0 for an unlisted trigger, and a row of 0 would multiply the price by
        // zero just as silently -- so the refusal is on the RATE, not on the row's existence. The seed
        // reader and the import boundary both refuse a non-positive `perMinute` for the same reason.
        var zeroRow = new PowerTables(
            Coefficients, new[] { new TriggerFrequencyRow(AtomTriggers.OnDamageDealt, 0) });

        var refused = CostFunction.Price(StatusAtom(AtomTriggers.OnDamageDealt), zeroRow);

        Assert.False(refused.Ok);
        Assert.Contains(AtomTriggers.OnDamageDealt, refused.Verdict.Reason, StringComparison.Ordinal);
    }
}
