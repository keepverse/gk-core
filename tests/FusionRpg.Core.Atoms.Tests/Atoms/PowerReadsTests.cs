using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Effects.Atoms.Power;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// One price, three reads (spec-power-reads.md, E10).
///
/// <para>The scalar sorts; the matchup read answers "against whom"; the marginal read is the one
/// that is actually right about multiplicative pairs. Each has a consumer, and the contract's teeth
/// are in what each read is <b>not</b> allowed to be used for.</para>
/// </summary>
public class PowerReadsTests
{
    // ---- the display scalar -----------------------------------------------------------------------

    [Fact]
    public void Every_category_zero_is_exactly_zero()
    {
        Assert.Equal(0, PowerScalar.Of(PowerVector.Zero));
    }

    [Fact]
    public void A_balanced_vector_beats_a_glass_cannon_of_the_same_total()
    {
        // The whole reason the scalar is a geometric mean rather than a sum.
        var balanced = new PowerVector(20, 20, 20, 20, 20);
        var cannon = new PowerVector(100, 0, 0, 0, 0);

        Assert.Equal(balanced.Total, cannon.Total);
        Assert.True(PowerScalar.Of(balanced) > PowerScalar.Of(cannon));
    }

    [Fact]
    public void An_untouched_category_is_a_factor_of_one_not_an_annihilator()
    {
        // The plain geometric mean makes any zero factor zero the whole product, and most atoms touch
        // one or two of five — so nearly every atom would score 0 and nothing would compare.
        var twoOfFive = new PowerVector(50, 30, 0, 0, 0);

        Assert.True(PowerScalar.Of(twoOfFive) > 0);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(99)]
    public void Domination_holds_over_random_vectors(int seed)
    {
        // If v ≥ w componentwise then scalar(v) ≥ scalar(w). This is the rule the previous formula
        // broke, so it is a property test rather than three examples.
        var rng = new Random(seed);
        for (var n = 0; n < 500; n++)
        {
            var w = new PowerVector(
                rng.Next(0, 200), rng.Next(0, 200), rng.Next(0, 200), rng.Next(0, 200), rng.Next(0, 200));
            var v = new PowerVector(
                w.Offense + rng.Next(0, 50), w.Survivability + rng.Next(0, 50),
                w.Control + rng.Next(0, 50), w.Utility + rng.Next(0, 50), w.Economy + rng.Next(0, 50));

            Assert.True(PowerScalar.Of(v) >= PowerScalar.Of(w),
                $"domination broken: {v} scored below {w}");
        }
    }

    [Fact]
    public void The_scalar_is_computed_on_integers_so_two_runs_cannot_disagree()
    {
        // `Math.Pow` may differ in the last bit between runtimes, and this number is stamped into
        // hashed reports. The root is an integer binary search instead.
        var v = new PowerVector(37, 11, 5, 2, 91);
        var first = PowerScalar.Of(v);

        for (var n = 0; n < 100; n++) Assert.Equal(first, PowerScalar.Of(v));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(31, 1)]
    [InlineData(32, 2)]
    [InlineData(242, 2)]
    [InlineData(243, 3)]
    [InlineData(100000, 10)]
    public void The_integer_fifth_root_is_the_largest_r_with_r5_at_most_the_value(long value, long expected)
    {
        Assert.Equal(expected, PowerScalar.IntegerFifthRoot(value));
    }

    [Fact]
    public void A_very_large_vector_does_not_overflow_the_root_search()
    {
        // The search squares its candidate twice; an unsaturated Pow5 wraps and the search walks off.
        var huge = new PowerVector(1_000_000, 1_000_000, 1_000_000, 1_000_000, 1_000_000);

        Assert.InRange(PowerScalar.Of(huge), 999_000, 1_001_000);
    }

    // ---- the matchup read --------------------------------------------------------------------------

    [Fact]
    public void Fire_against_ice_reads_higher_than_the_neutral_read()
    {
        var v = new PowerVector(100, 0, 0, 0, 0);

        var neutral = MatchupRead.AgainstCombat(v,
            new[] { ElementTypeId.Fire }, new[] { ElementTypeId.Earth });
        var strong = MatchupRead.AgainstCombat(v,
            new[] { ElementTypeId.Fire }, new[] { ElementTypeId.Ice });

        Assert.Equal(v.Offense, neutral.Offense);
        Assert.True(strong.Offense > neutral.Offense);
    }

    [Fact]
    public void Two_strong_slots_multiply_rather_than_add()
    {
        // 1.25 × 1.25 = 1.5625, +562.5‰ — not the +500‰ that adding gives. The gap grows with every
        // slot, and it is why a matchup read cannot be retrofitted onto a stored scalar.
        var factor = MatchupRead.CombatFactorMilli(
            new[] { ElementTypeId.Fire, ElementTypeId.Ice },
            new[] { ElementTypeId.Ice, ElementTypeId.Earth });

        Assert.Equal(1563, factor); // 1.5625 rounded once, half away from zero
        Assert.NotEqual(1500, factor);
    }

    [Fact]
    public void A_weak_matchup_reads_lower()
    {
        var v = new PowerVector(100, 0, 0, 0, 0);

        var weak = MatchupRead.AgainstCombat(v,
            new[] { ElementTypeId.Fire }, new[] { ElementTypeId.Air });

        Assert.True(weak.Offense < v.Offense);
    }

    [Fact]
    public void The_shield_read_uses_the_shield_table_and_the_combat_read_uses_the_ring()
    {
        // The two are seeded identical today, so this cannot be proven by comparing outputs on the
        // shipped roster — it is proven by diverging one table and watching only one read move.
        var shipped = FusionRpg.Core.Combat.Element.ElementTable.Shipped();
        var divergedShield = shipped.ShieldRows
            .Select(r => r.Attacker == "fire" && r.Defender == "ice" ? r with { Unit = -1 } : r)
            .ToList();

        using var _ = FusionRpg.Core.Combat.Element.ElementTable.UseScoped(
            new FusionRpg.Core.Combat.Element.ElementTable(
                shipped.Elements, shipped.CombatRows, divergedShield));

        var attackers = new[] { ElementTypeId.Fire };
        var defenders = new[] { ElementTypeId.Ice };

        Assert.True(MatchupRead.CombatFactorMilli(attackers, defenders) > PowerMath.One);
        Assert.True(MatchupRead.ShieldFactorMilli(attackers, defenders) < PowerMath.One);
    }

    [Fact]
    public void A_neutral_matchup_changes_nothing()
    {
        // Without this the factor could be scaling everything and the tests above would still pass.
        Assert.Equal(PowerMath.One, MatchupRead.CombatFactorMilli(
            new[] { ElementTypeId.Fire }, new[] { ElementTypeId.Earth }));
    }

    // ---- the marginal read --------------------------------------------------------------------------

    static AtomRow Atk(int amount, string family) => new()
    {
        AtomId = AtomRow.DeriveId(family, "", 1),
        KindId = "stat.modify",
        FamilyId = family,
        Tier = 1,
        Name = family,
        ParamsJson = $$"""{"channel":"atk","op":"flat","amount":{{amount}}}""",
    };

    [Fact]
    public void Marginal_on_an_empty_actor_is_the_stored_price()
    {
        // Which is what "base stats contribute nothing" buys: the two reads agree where there is no
        // context for them to disagree about.
        var atom = Atk(10, "atom.might");

        var marginal = MarginalRead.Of(Array.Empty<AtomRow>(), atom);

        Assert.Equal(CostFunction.Price(atom).Power, marginal);
    }

    [Fact]
    public void Marginal_measures_the_difference_the_atom_actually_makes()
    {
        var actor = new[] { Atk(50, "atom.base") };
        var atom = Atk(10, "atom.might");

        var marginal = MarginalRead.Of(actor, atom);

        Assert.Equal(
            ActorPowerCache.Compose(actor.Append(atom).ToList()) - ActorPowerCache.Compose(actor),
            marginal);
    }

    [Fact]
    public void The_gap_against_stored_power_is_reported_rather_than_hidden()
    {
        // The gap is the deliverable: it is the running list of shapes the cost function misprices.
        var actor = new[] { Atk(50, "atom.base") };
        var atom = Atk(10, "atom.might");

        var gap = MarginalRead.GapAgainstStored(actor, atom);

        Assert.Equal(MarginalRead.Of(actor, atom) - CostFunction.Price(atom).Power, gap);
    }

    [Fact]
    public void Marginal_is_computed_and_never_read_off_a_scalar()
    {
        // A scalar cannot answer this: two different vectors can share a scalar, and the difference
        // between their scalars is not the scalar of their difference.
        var a = new PowerVector(100, 0, 0, 0, 0);
        var b = new PowerVector(0, 100, 0, 0, 0);

        Assert.Equal(PowerScalar.Of(a), PowerScalar.Of(b));
        Assert.NotEqual(a, b);
    }
}
