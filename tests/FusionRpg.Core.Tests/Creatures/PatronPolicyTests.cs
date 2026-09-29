using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Patron;
using FusionRpg.Core.Power;
using Xunit;

namespace FusionRpg.Core.Tests.Creatures;

/// <summary>PT1: patron aura magnitudes + the patron-modified kill-earn shape
/// (spec-patron-creature.md, owner locks; tuning ask-first).</summary>
public class PatronPolicyTests
{
    [Theory]
    [InlineData(CreatureRarity.Chaff, 20)]
    [InlineData(CreatureRarity.Cultivated, 30)]
    [InlineData(CreatureRarity.Heirloom, 45)]
    [InlineData(CreatureRarity.Sunwoven, 60)]
    public void Rarity_bases_are_locked(CreatureRarity rarity, int baseMilli) =>
        Assert.Equal(baseMilli, PatronPolicy.RarityBaseMilli(rarity));

    // aura-skill T22 (owner sign-off 2026-08-30): AuraMilli = flatPart (the ORIGINAL, still-clamped
    // formula, unchanged) + pThetaTermMilli (NEW). At Θ=0, P(Θ)=C (the ladder's own floor, NOT zero
    // — docs/architecture/numeric-types.md's own table), so even Θ=0 contributes a nonzero P(Θ) term; there is no Θ value that
    // makes the new term vanish, so these tests assert the COMBINED value explicitly rather than
    // pretending the old flat-only numbers still hold on their own.
    static long PThetaTermAt(int pTheta, PowerTuning tuning) =>
        FusionRpg.Core.Creatures.Patron.PatronPolicy.AuraMilli(CreatureRarity.Chaff, 0, 0, pTheta, tuning)
        - FusionRpg.Core.Creatures.Patron.PatronPolicy.RarityBaseMilli(CreatureRarity.Chaff); // isolates the term: flatPart(Common,0,0) = RarityBaseMilli(Common) exactly

    [Fact]
    public void Aura_formula_flat_part_plus_pTheta_term()
    {
        var tuning = TuningAt(400);
        var term0 = PThetaTermAt(pTheta: 0, tuning);
        Assert.True(term0 > 0, "P(0) = C, never zero -- the new term must contribute even at Θ=0");

        Assert.Equal(20 + term0, PatronPolicy.AuraMilli(CreatureRarity.Chaff, star: 0, level: 0, pTheta: 0, tuning));
        Assert.Equal(75 + term0, PatronPolicy.AuraMilli(CreatureRarity.Heirloom, star: 2, level: 10, pTheta: 0, tuning)); // 45+20+10 flat
        // The OLD flat part is still clamped at 150 regardless of star/level -- the P(Θ) term is the
        // ONLY thing that keeps growing past it.
        Assert.Equal(150 + term0, PatronPolicy.AuraMilli(CreatureRarity.Sunwoven, star: 5, level: 90, pTheta: 0, tuning));
        Assert.Equal(PatronPolicy.AuraClampMilli + term0, PatronPolicy.AuraMilli(CreatureRarity.Sunwoven, 5, 999, pTheta: 0, tuning));
    }

    [Fact]
    public void The_pTheta_term_grows_with_Theta_this_is_the_whole_point_of_T22()
    {
        var tuning = TuningAt(400);
        var low = PThetaTermAt(pTheta: 0, tuning);
        var mid = PThetaTermAt(pTheta: 20, tuning);   // the shipped pin
        var high = PThetaTermAt(pTheta: 1000, tuning);

        Assert.True(mid > low, $"pTheta term must grow with Θ: Θ=0 -> {low}, Θ=20 -> {mid}");
        Assert.True(high > mid, $"pTheta term must keep growing past the pin: Θ=20 -> {mid}, Θ=1000 -> {high}");

        // The flat part clamps at 150 forever; the pTheta term must eventually exceed that ceiling --
        // proving patron genuinely stays relevant instead of being permanently capped.
        Assert.True(high > PatronPolicy.AuraClampMilli,
            $"at Θ=1000 the pTheta term ({high}) must exceed the old flat ceiling ({PatronPolicy.AuraClampMilli})");
    }

    [Fact]
    public void Aura_shape_primary_full_secondary_half_defense_half()
    {
        var tuning = TuningAt(400);
        var flatPlusTerm = PatronPolicy.AuraMilli(CreatureRarity.Heirloom, star: 2, level: 10, pTheta: 0, tuning);
        var aura = PatronPolicy.Aura(CreatureRarity.Heirloom, star: 2, level: 10, pTheta: 0, tuning, "fire", "ice");
        Assert.Equal("fire", aura.ElementPrimary);
        Assert.Equal(flatPlusTerm, aura.PowerMilli);
        Assert.Equal(flatPlusTerm / 2, aura.DefenseMilli); // half, truncating
        Assert.Equal("ice", aura.ElementSecondary);
        Assert.Equal(flatPlusTerm / 2, aura.SecondaryPowerMilli);
        Assert.Equal(flatPlusTerm / 4, aura.SecondaryDefenseMilli);

        var mono = PatronPolicy.Aura(CreatureRarity.Chaff, 0, 0, pTheta: 0, tuning, "dark", null);
        Assert.Null(mono.ElementSecondary);
        Assert.Equal(0, mono.SecondaryPowerMilli);
    }

    [Fact]
    public void Switch_cost_is_locked() => Assert.Equal(100, PatronPolicy.SwitchCostSouls);

    static PowerTuning TuningAt(long bMilli) => PowerTuning.Build(
        1, 1, PowerTuning.FixedCMilli, bMilli, PowerTuning.FixedPinIndex, PowerTuning.FixedPinValue,
        1000, 25000, 250, 1000, 5000, 5000, 25000);
    const int Pin = 20;

    [Fact]
    public void Patron_kill_earn_pays_a_bonus_every_tenth_earning_kill_at_the_pin()
    {
        // At the pin (contentScale=1.000) this is byte-identical to the pre-T3.6 shape: kill 10 pays
        // 2 (base + bonus); the rest pay 1.
        var tuning = TuningAt(400);
        Assert.Equal(1, PatronPolicy.KillEarnWithPatron(0, Pin, tuning));
        Assert.Equal(1, PatronPolicy.KillEarnWithPatron(8, Pin, tuning));
        Assert.Equal(2, PatronPolicy.KillEarnWithPatron(9, Pin, tuning));   // the 10th earning kill
        Assert.Equal(1, PatronPolicy.KillEarnWithPatron(10, Pin, tuning));
        Assert.Equal(2, PatronPolicy.KillEarnWithPatron(19, Pin, tuning)); // the 20th
    }

    [Fact]
    public void Patron_kill_earn_is_uncapped_past_the_old_fifty_soul_boundary()
    {
        // T3.6: KillSoulCap is deleted. Walking well past the old 50-soul boundary (the 46th earning
        // kill used to be exactly where the cap engaged) must keep paying the same +1/+1-per-10th
        // shape, never dropping to zero the way the pre-T3.6 Math.Min did.
        var tuning = TuningAt(400);
        Assert.Equal(1, PatronPolicy.KillEarnWithPatron(45, Pin, tuning)); // used to be the 46th->cap kill
        Assert.Equal(1, PatronPolicy.KillEarnWithPatron(46, Pin, tuning)); // used to be capped at 0 -- not anymore
        Assert.Equal(2, PatronPolicy.KillEarnWithPatron(99, Pin, tuning)); // the 100th earning kill, still paying the bonus

        long souls = 0;
        var counted = 0;
        for (var kill = 0; kill < 200; kill++)
        {
            var delta = PatronPolicy.KillEarnWithPatron(counted, Pin, tuning);
            if (delta > 0) counted++;
            souls += delta;
        }
        Assert.True(souls > 50, $"200 kills must earn more than the old 50-soul cap, got {souls}");
    }

    [Fact]
    public void Patron_kill_earn_scales_with_content_depth_same_as_the_unpatroned_path()
    {
        // Deliberate extension beyond SSOT §11.7a's own named formula list (power-todo.md T3.6):
        // the patron bonus scales too, so owning a patron never becomes a net PENALTY at depth.
        var tuning = TuningAt(400);
        var atPin = PatronPolicy.KillEarnWithPatron(9, Pin, tuning);   // the 10th earning kill, +2 at the pin
        var atDepth = PatronPolicy.KillEarnWithPatron(9, 100, tuning); // same kill, much deeper content
        Assert.True(atDepth > atPin, $"patron bonus must scale with depth: pin={atPin}, depth={atDepth}");
    }
}
