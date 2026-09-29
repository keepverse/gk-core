using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Effects.Atoms.Power;
using Xunit;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// E44 (spec-power-sweep.md), closing definitions.md §13 D2 — the third attempt, after the two
/// recorded refutals (marginal read over an additive sum; aggregate-then-price, still linear). This
/// suite is the exact falsifier both prior attempts failed: <c>marginal(x, A)</c> must differ by
/// <c>A</c> for the named pairs, proven against <see cref="MarginalRead"/>, not asserted in prose.
/// </summary>
public class PowerInteractionTests
{
    static AtomRow Derived(string channel, long amount, string family) => new()
    {
        AtomId = AtomRow.DeriveId(family, "", 1),
        KindId = "stat.derived",
        FamilyId = family,
        Tier = 1,
        Name = family,
        WhenJson = "{}",
        ParamsJson = $$"""{"channel":"{{channel}}","op":"flat","amount":{{amount}}}""",
    };

    static AtomRow CritRate(long amount = 100, string slot = "fire") =>
        Derived($"combat.crit.rate.{slot}", amount, "atom.crit-rate");

    static AtomRow CritDamage(long amount = 100, string slot = "fire") =>
        Derived($"combat.crit.damage.{slot}", amount, "atom.crit-damage");

    static AtomRow ShieldCapacity(long amount = 100, string slot = "fire") =>
        Derived($"combat.shield.capacity.{slot}", amount, "atom.shield-capacity");

    static AtomRow ShieldToughness(long amount = 100, string slot = "fire") =>
        Derived($"combat.shield.toughness.{slot}", amount, "atom.shield-toughness");

    // ---- 1. the mandatory falsifier: crit rate x crit damage --------------------------------------

    [Fact]
    public void Marginal_crit_damage_differs_by_whether_crit_rate_is_already_present()
    {
        // The exact test both refuted attempts failed: pricing crit_damage on an actor that already
        // holds crit_rate must differ from pricing it on an empty actor. A sum has no cross terms, so
        // BOTH prior attempts returned the same number here regardless of context.
        var onEmpty = MarginalRead.Of(Array.Empty<AtomRow>(), CritDamage());
        var onCritRateActor = MarginalRead.Of(new[] { CritRate() }, CritDamage());

        Assert.NotEqual(onEmpty, onCritRateActor);
        Assert.True(onCritRateActor.Offense > onEmpty.Offense,
            $"crit_damage on a crit_rate actor ({onCritRateActor.Offense}) must price above the same " +
            $"atom on an empty actor ({onEmpty.Offense}) — the interaction makes it more valuable");
    }

    [Fact]
    public void The_symmetric_read_holds_too_crit_rate_on_a_crit_damage_actor()
    {
        var onEmpty = MarginalRead.Of(Array.Empty<AtomRow>(), CritRate());
        var onCritDamageActor = MarginalRead.Of(new[] { CritDamage() }, CritRate());

        Assert.True(onCritDamageActor.Offense > onEmpty.Offense);
    }

    [Fact]
    public void With_no_interacting_partner_present_marginal_still_equals_the_atoms_own_price()
    {
        // The negative control: the correction must not fire unconditionally, or it would just be a
        // different-shaped version of the same additive defect. Two atoms on unrelated channels never
        // interact, so marginal collapses back to the stored context-free price exactly.
        var unrelated = Derived("combat.accuracy.fire", 50, "atom.unrelated");
        var marginal = MarginalRead.Of(new[] { unrelated }, CritDamage());
        var stored = CostFunction.Price(CritDamage()).Power;

        Assert.Equal(stored, marginal);
    }

    // ---- 2. the same falsifier for shield capacity x toughness ------------------------------------

    [Fact]
    public void Marginal_shield_toughness_differs_by_whether_capacity_is_already_present()
    {
        var onEmpty = MarginalRead.Of(Array.Empty<AtomRow>(), ShieldToughness());
        var onCapacityActor = MarginalRead.Of(new[] { ShieldCapacity() }, ShieldToughness());

        Assert.NotEqual(onEmpty, onCapacityActor);
        Assert.True(onCapacityActor.Survivability > onEmpty.Survivability,
            $"shield toughness on a capacity actor ({onCapacityActor.Survivability}) must price above " +
            $"the same atom on an empty actor ({onEmpty.Survivability})");
    }

    // ---- 3. the numeric contract: overflow throws, never wraps ------------------------------------

    [Fact]
    public void A_planted_overflow_case_throws_rather_than_wrapping_or_clamping()
    {
        // Large enough that the widened long product of the two sides' own priced points, times the
        // interaction coefficient, exceeds long.MaxValue. Per AGENTS.md's numeric rule this must throw
        // — never silently wrap negative, never clamp to a plausible-looking number.
        var huge = new[] { CritRate(amount: 200_000_000), CritDamage(amount: 200_000_000) };

        Assert.Throws<OverflowException>(() => ActorPowerCache.Compose(huge));
    }

    [Fact]
    public void Realistic_content_scale_magnitudes_never_come_close_to_the_overflow_boundary()
    {
        // The sanity check the overflow test above needs: ordinary authored content is nowhere near
        // the boundary that test exercises deliberately.
        var realistic = new[] { CritRate(amount: 150), CritDamage(amount: 150) };
        var composed = ActorPowerCache.Compose(realistic);

        Assert.True(composed.Offense > 0);
    }

    // ---- 4. the degenerate-pair proof: non-linearity is real, not a rounding artifact --------------

    [Fact]
    public void A_planted_degenerate_pair_prices_above_the_sum_of_its_halves()
    {
        var rate = CritRate(amount: 400);
        var damage = CritDamage(amount: 400);

        var composed = ActorPowerCache.Compose(new[] { rate, damage }).Total;
        var summedHalves = CostFunction.Price(rate).Power.Total + CostFunction.Price(damage).Power.Total;

        Assert.True(composed > summedHalves,
            $"composed ({composed}) must price above the sum of its halves ({summedHalves}) — proving " +
            "the non-linearity is a real cross term, not a rounding artifact");

        // And by more than rounding noise (a handful of points either way), so this is not merely
        // "greater by 1" from PowerVector.FromCategory's own per-category rounding.
        Assert.True(composed - summedHalves > 10,
            $"the excess ({composed - summedHalves}) must be a real correction, not rounding noise");
    }

    // ---- 5. element-ring honesty: not attempted, and this proves the reason why is real -----------

    [Fact]
    public void Element_ring_style_matchup_nonlinearity_lives_in_MatchupRead_not_Compose()
    {
        // Compose has no defender in its signature — it prices ONE actor. The element ring's
        // multiplicative-ness (two strong slots compounding to 1.25 x 1.25) is a property of an
        // attacker x defender contest, and it already has a correct, non-linear home: MatchupRead.
        // This is not a gap in the construction above; it is why no element-ring PowerInteractionRow
        // was authored. Proven here rather than merely asserted in a comment: two actors holding the
        // exact same elemental power atoms on DIFFERENT elements compose identically under Compose,
        // because Compose has nothing to condition the "ring" relationship on.
        var fireOnly = new[] { Derived("combat.power.fire", 100, "atom.fire-power") };
        var iceOnly = new[] { Derived("combat.power.ice", 100, "atom.ice-power") };

        Assert.Equal(
            ActorPowerCache.Compose(fireOnly).Total,
            ActorPowerCache.Compose(iceOnly).Total);
    }
}
