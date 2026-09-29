using System.Linq;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Combat;

/// <summary>
/// `elemental-resolver` (solid-remediation D14, T2.2) — the three elemental rules, asserted as rules.
///
/// <para>⚠️ Every test here pins a <b>property</b>, not a number: that two equal totals resolve
/// identically however they were composed, that omni is never multiplied, that the matrix is
/// intransitive, and that a probability stays in [0,1]. None of them pins a curve value, because the
/// scales and the steepness are tunables — `gk-core/data/tuning/stats.v1.json` owns them, and a balance pass
/// republishing that file must not turn this file red.</para>
/// </summary>
[Trait("VerificationId", "core.elemental-resolver")]
public class ElementalResolverTests
{
    const double Scale = 100.0;

    /// <summary>
    /// <b>Rule 1, stated as the property that makes omni anti-one-trick.</b> An actor whose capability
    /// is all omni and an actor whose capability is all element must resolve identically against the
    /// same defender when their totals match. That is only true while the composition is a SUM — the
    /// moment omni multiplies element, an all-omni actor and an all-element actor with equal totals
    /// stop agreeing, and the specialist wins for free.
    /// </summary>
    [Theory]
    [InlineData(300.0, 0.0)]
    [InlineData(0.0, 300.0)]
    [InlineData(150.0, 150.0)]
    [InlineData(1.0, 299.0)]
    public void Equal_totals_resolve_identically_however_they_are_composed(double omni, double element)
    {
        var reference = ElementalResolver.Contest(ElementalResolver.Total(300.0, 0.0), 100.0, Scale);
        var actual = ElementalResolver.Contest(ElementalResolver.Total(omni, element), 100.0, Scale);

        Assert.Equal(reference, actual);
    }

    /// <summary>
    /// <b>Rule 1's anti-snowball half, stated as an inequality rather than a value.</b> Doubling omni
    /// must add its own increase and nothing more — it must never scale what the element term already
    /// contributes. Multiplication would make the gain depend on the element term; addition cannot.
    /// </summary>
    [Fact]
    public void Doubling_omni_never_multiplies_the_element_term()
    {
        const double omni = 120.0;

        foreach (var element in new[] { 0.0, 50.0, 400.0, 5000.0 })
        {
            var gain = ElementalResolver.Total(omni * 2, element) - ElementalResolver.Total(omni, element);

            // The increase is the omni increase, whatever the element term happens to be. A
            // multiplicative rule would make this grow with `element`.
            Assert.Equal(omni, gain);
        }
    }

    /// <summary>
    /// <b>Rule 2's bound is mathematical.</b> Extreme inputs in either direction stay inside [0,1] with
    /// no clamp anywhere — the sigmoid does it by being a sigmoid. Asserted so a later cap sweep, which
    /// this repo runs because it bans hard progression ceilings, reads the intent rather than guessing.
    /// </summary>
    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(1e9, 0.0)]
    [InlineData(0.0, 1e9)]
    [InlineData(-1e9, 1e9)]
    [InlineData(double.MaxValue / 4, 0.0)]
    public void A_contest_probability_stays_inside_zero_and_one(double attacker, double defender)
    {
        var p = ElementalResolver.Contest(attacker, defender, Scale);

        Assert.InRange(p, 0.0, 1.0);
        Assert.False(double.IsNaN(p), "a contest produced NaN");
    }

    /// <summary>
    /// A contest is <b>difference-based</b>, so shifting both sides by the same amount changes nothing.
    /// That is what makes it read Θ (the linear ladder) rather than P(Θ): two actors who both gained
    /// the same absolute capability are in the same contest they were before.
    /// </summary>
    [Fact]
    public void A_contest_reads_the_difference_so_a_shared_shift_changes_nothing()
    {
        var baseline = ElementalResolver.Contest(240.0, 180.0, Scale);

        foreach (var shift in new[] { 1.0, 75.0, 10_000.0 })
            Assert.Equal(baseline, ElementalResolver.Contest(240.0 + shift, 180.0 + shift, Scale));
    }

    /// <summary>
    /// The linear-from-zero shape must give <b>zero at parity</b>. This is the whole reason it is not a
    /// sigmoid: a sigmoid is 0.5 at delta 0, which would hand an actor with no reflect stat at all a
    /// coin-flip reflect — "a new default nobody chose", and a NoGoldensMoveAtZero violation.
    /// </summary>
    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(500.0, 500.0)]
    [InlineData(0.0, 900.0)]
    public void An_unstatted_actor_gets_nothing_from_the_linear_shape(double rate, double resist)
    {
        Assert.Equal(0.0, ElementalResolver.RateFromZero(rate, resist, 1000.0));
    }

    [Theory]
    [InlineData(1e12, 0.0)]
    [InlineData(1000.0, 0.0)]
    public void The_linear_shape_is_also_bounded(double rate, double resist)
    {
        Assert.InRange(ElementalResolver.RateFromZero(rate, resist, 1000.0), 0.0, 1.0);
    }

    /// <summary>
    /// <b>Rule 3 — intransitivity, the property that stops a single-element build dominating.</b>
    ///
    /// <para>Asserted as "no element is strong against every other", which is what intransitivity means
    /// for a player. Not asserted: which element beats which, or how many elements there are. The ring
    /// is roster-generated and a seventh element is rows plus regeneration, so both of those are
    /// readings — the property has to survive them.</para>
    /// </summary>
    [Fact]
    public void No_element_is_strong_against_every_other()
    {
        var elements = Enum.GetValues<ElementTypeId>();

        foreach (var attacker in elements)
        {
            var others = elements.Where(d => !d.Equals(attacker)).ToArray();
            if (others.Length == 0) continue;

            var dominatesAll = others.All(d =>
                ElementalResolver.Relation(attacker, d) == ElementMatchupRelation.Strong);

            Assert.False(dominatesAll, $"{attacker} is strong against every other element — the matrix is transitive");
        }
    }

    [Fact]
    public void Every_element_is_neutral_against_itself_rather_than_falling_through()
    {
        foreach (var element in Enum.GetValues<ElementTypeId>())
            Assert.Equal(ElementMatchupRelation.Same, ElementalResolver.Relation(element, element));
    }

    /// <summary>
    /// The resolver delegates rather than re-deriving: its contest is the shipped
    /// <see cref="CombatProbability.Sigmoid"/>, reached through the one declaration in
    /// `ResistanceEvaluator`. Asserted rather than trusted, because a resolver that quietly grew its own
    /// curve is precisely the second-owner defect this module exists to prevent — and the assertion
    /// compares against the shipped function, so it cannot drift when tuning republishes.
    /// </summary>
    [Theory]
    [InlineData(240.0, 180.0)]
    [InlineData(0.0, 0.0)]
    [InlineData(-90.0, 410.0)]
    public void The_contest_is_the_shipped_sigmoid_and_not_a_second_curve(double attacker, double defender)
    {
        Assert.Equal(
            CombatProbability.Sigmoid(attacker - defender, Scale),
            ElementalResolver.Contest(attacker, defender, Scale));
    }
}
