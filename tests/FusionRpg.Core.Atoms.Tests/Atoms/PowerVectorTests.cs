using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Effects.Atoms.Power;
using Xunit;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// E9's cost function (spec-power-vector.md).
///
/// <para>The spec carried four open defects, D1–D4, as "accepted limitations". Three of them were
/// arithmetic rather than design, and this module closes them: integer <c>chance/1000</c> priced
/// every proc below 1000‰ at zero (D1), an omitted spawn <c>count</c> priced the whole spawn at zero
/// (D3), and an omitted target count did the same to every single-target atom (D4). D2 — that actor
/// power aggregates channels rather than summing per-atom prices — is implemented in
/// <see cref="ActorPowerCache"/>.</para>
/// </summary>
public class PowerVectorTests
{
    static AtomRow Atom(
        string kind, string paramsJson, string? whenJson = null, string family = "atom.sample") => new()
    {
        AtomId = AtomRow.DeriveId(family, "", 1),
        KindId = kind,
        FamilyId = family,
        Tier = 1,
        Name = "Sample",
        WhenJson = whenJson ?? "{}",
        ParamsJson = paramsJson,
    };

    static string When(string trigger, int? chance = null, int? icdMs = null)
    {
        var parts = new List<string> { $"\"trigger\":\"{trigger}\"" };
        if (chance is { } c) parts.Add($"\"chance\":{c}");
        if (icdMs is { } i) parts.Add($"\"icd_ms\":{i}");
        return "{" + string.Join(",", parts) + "}";
    }

    // ---- D1: the integer trap ---------------------------------------------------------------------

    [Fact]
    public void A_proc_below_one_thousand_permille_does_not_price_at_zero()
    {
        // The whole conditional half of the catalog. `chance/1000` in integer arithmetic is 0 for
        // every chance under 1000, so the naive formula priced every proc effect at nothing — and it
        // was written up as a limitation of the design rather than as a rounding bug.
        var atom = Atom("resource.delta", """{"channel":"hp","amount":-100}""",
            When(AtomTriggers.OnDamageDealt, chance: 50));

        var priced = CostFunction.Price(atom);

        Assert.True(priced.Ok, priced.Verdict.Reason);
        Assert.True(priced.Power.Total > 0, "a 5% proc is worth something");
    }

    [Fact]
    public void Half_the_chance_is_exactly_half_the_price()
    {
        var full = CostFunction.Price(Atom("resource.delta", """{"channel":"hp","amount":-100}""",
            When(AtomTriggers.OnDamageDealt, chance: 1000))).Power.Total;
        var half = CostFunction.Price(Atom("resource.delta", """{"channel":"hp","amount":-100}""",
            When(AtomTriggers.OnDamageDealt, chance: 500))).Power.Total;

        Assert.InRange(half, full / 2 - 1, full / 2 + 1);
    }

    // ---- conditionality ---------------------------------------------------------------------------

    [Fact]
    public void A_triggerless_atom_is_unconditional()
    {
        // Permanent modifiers are not event-driven. Without the short-circuit the 26 passive families
        // price at zero, because their trigger frequency is zero.
        var when = CostFunction.Read("{}");
        Assert.Equal(PowerMath.One, CostFunction.Conditionality(when, CostFunction.Read("{}"), PowerTables.Authored()));
    }

    [Fact]
    public void The_permanent_modifier_families_price_above_zero()
    {
        var priced = CostFunction.Price(Atom("stat.modify", """{"channel":"maxHp","op":"flat","amount":45}"""));

        Assert.True(priced.Ok, priced.Verdict.Reason);
        Assert.True(priced.Power.Survivability > 0);
    }

    [Fact]
    public void An_icd_shorter_than_the_trigger_rate_costs_nothing_and_a_longer_one_costs_a_computed_share()
    {
        // The ratio is the icdFactor, computed — not merely "lower". OnDamageDealt is 60/min, so a
        // 1000 ms cooldown permits 60/min and changes nothing; 2000 ms permits 30, i.e. half.
        var none = CostFunction.Price(Atom("status.apply", """{"status":"butter","duration":4}""",
            When(AtomTriggers.OnDamageDealt))).Power.Total;
        var short_ = CostFunction.Price(Atom("status.apply", """{"status":"butter","duration":4}""",
            When(AtomTriggers.OnDamageDealt, icdMs: 1000))).Power.Total;
        var long_ = CostFunction.Price(Atom("status.apply", """{"status":"butter","duration":4}""",
            When(AtomTriggers.OnDamageDealt, icdMs: 2000))).Power.Total;

        Assert.Equal(none, short_);
        Assert.InRange(long_, none / 2 - 1, none / 2 + 1);
    }

    [Fact]
    public void A_zero_frequency_trigger_does_not_divide_by_zero()
    {
        // The formula divides by the frequency. An unlisted trigger must read as rare, not throw.
        //
        // ST4.5d keeps that property and changes the surface: the atom is REFUSED by name (the verdict
        // that used to say `Priced` while returning zero) rather than quietly priced at nothing. Both
        // halves below still hold -- the ICD factor is neutral for a zero frequency, and nothing throws.
        var tables = new PowerTables(PowerTables.Authored().Coefficients, Array.Empty<TriggerFrequencyRow>());

        var priced = CostFunction.Price(
            Atom("status.apply", """{"status":"butter"}""", When(AtomTriggers.OnDamageDealt, icdMs: 5000)),
            tables);

        Assert.False(priced.Ok);
        Assert.Contains(AtomTriggers.OnDamageDealt, priced.Verdict.Reason, StringComparison.Ordinal);
        Assert.Equal(PowerMath.One, CostFunction.IcdFactorMilli(5000, 0));
    }

    // ---- D4: the target floor ----------------------------------------------------------------------

    [Fact]
    public void An_omitted_target_count_is_one_target_not_none()
    {
        // Zero would price every single-target atom — which is most of them — at nothing.
        Assert.Equal(PowerMath.One, CostFunction.TargetFactorMilli(CostFunction.Read("{}")));
    }

    [Fact]
    public void More_targets_are_worth_proportionally_more()
    {
        Assert.Equal(3 * PowerMath.One,
            CostFunction.TargetFactorMilli(CostFunction.Read("""{"expectedTargets":3}""")));
    }

    // ---- normalisation: the units trap ---------------------------------------------------------------

    [Fact]
    public void Ten_hit_points_and_ten_resolver_points_do_not_price_alike()
    {
        // The part that cannot be skipped. `+10 hp` is ten hit points; `+10 fire power` is ten
        // resolver points at a tenth the scale. A coefficient table without normalisation prices
        // them the same and is wrong by an order of magnitude.
        var hp = CostFunction.Price(Atom("stat.modify", """{"channel":"maxHp","op":"flat","amount":10}"""));
        var derived = CostFunction.Price(
            Atom("stat.derived", """{"channel":"combat.power.fire","op":"flat","amount":10}"""));

        Assert.True(hp.Ok && derived.Ok);
        Assert.NotEqual(hp.Power.Total, derived.Power.Total);
        Assert.True(derived.Power.Total > hp.Power.Total * 5,
            "a resolver point is worth roughly ten hit points, not the same");
    }

    // ---- purity -------------------------------------------------------------------------------------

    [Fact]
    public void The_same_atom_prices_to_the_same_vector_every_time()
    {
        // The price is stored, hashed, budgeted and compared. Two runs disagreeing in the last bit
        // would move a content hash for nothing.
        var atom = Atom("resource.delta", """{"channel":"hp","amount":-120}""",
            When(AtomTriggers.OnDamageDealt, chance: 250, icdMs: 500));

        var first = CostFunction.Price(atom).Power;
        for (var n = 0; n < 50; n++) Assert.Equal(first, CostFunction.Price(atom).Power);
    }

    [Fact]
    public void An_authored_range_is_priced_at_its_mean()
    {
        var range = CostFunction.Price(Atom("resource.delta",
            """{"channel":"hp","amount":{"min":100,"max":200,"roll":"onApply"}}""",
            When(AtomTriggers.OnDamageDealt)));
        var fixedAt150 = CostFunction.Price(Atom("resource.delta", """{"channel":"hp","amount":150}""",
            When(AtomTriggers.OnDamageDealt)));

        Assert.Equal(fixedAt150.Power, range.Power);
    }

    // ---- unpriced is not zero -------------------------------------------------------------------------

    [Fact]
    public void A_kind_with_no_coefficient_is_unpriced_rather_than_free()
    {
        // A missing coefficient silently pricing at zero is how a whole family becomes free.
        var tables = new PowerTables(Array.Empty<PowerCoefficientRow>(), PowerTables.Authored().Frequencies);

        var priced = CostFunction.Price(Atom("status.apply", """{"status":"butter"}"""), tables);

        Assert.False(priced.Ok);
        Assert.Contains("no coefficient", priced.Verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_kind_is_unpriced_rather_than_free()
    {
        var priced = CostFunction.Price(Atom("no.such.kind", "{}"));

        Assert.False(priced.Ok);
        Assert.Equal(PowerVector.Zero, priced.Power);
    }

    // ---- the vector itself ------------------------------------------------------------------------------

    [Fact]
    public void A_two_category_kind_is_not_worth_twice_a_one_category_kind_for_free()
    {
        // Counting the full price once per declared category would make a kind richer by declaring
        // itself relevant to more things.
        var split = PowerVector.FromCategory(
            PowerCategory.Offense | PowerCategory.Survivability, 100);

        Assert.Equal(100, split.Total);
        Assert.Equal(50, split.Offense);
        Assert.Equal(50, split.Survivability);
    }

    [Fact]
    public void The_vector_round_trips_through_its_json()
    {
        var v = new PowerVector(11, 22, 33, 44, 55);

        Assert.Equal(v, PowerVector.FromJson(v.ToJson()));
    }

    [Fact]
    public void Malformed_stored_power_reads_as_zero_rather_than_throwing()
    {
        Assert.Equal(PowerVector.Zero, PowerVector.FromJson("{not json"));
        Assert.Equal(PowerVector.Zero, PowerVector.FromJson(null));
    }

    [Theory]
    [InlineData(5, 2, 3)]
    [InlineData(-5, 2, -3)]
    [InlineData(4, 2, 2)]
    [InlineData(1, 3, 0)]
    public void Rounding_is_half_away_from_zero_in_both_directions(int n, int d, int expected)
    {
        // The same rule everywhere, so a price does not depend on which order two equal factors were
        // applied in. Banker's rounding would make the same catalog price differently by grouping.
        Assert.Equal(expected, PowerMath.DivRound(n, d));
    }
}
