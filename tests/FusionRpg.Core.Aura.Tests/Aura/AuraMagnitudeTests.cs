using System.Linq;
using FusionRpg.Core.Aura;
using FusionRpg.Core.Stats.Aptitudes;
using Xunit;

namespace FusionRpg.Core.Tests.Aura;

/// <summary>aura-skill T10, Gate A: `AuraMagnitude.Compute` = `k(rung) · share^γ · P(Θ)` through the
/// SHARED `AptitudeReadFunctions.Magnitude` — never a second copy of the arithmetic.</summary>
public class AuraMagnitudeTests
{
    static AuraTuning Rung7To10() => new(new Dictionary<int, long>
    {
        [7] = 5359, [8] = 7090, [9] = 9379, [10] = 12407,
    }, MaxActiveAuras: 1);

    static AptitudeTuning LinearGammaTuning() => AptitudeTuningLoader.Parse("""
        {
          "schemaVersion": 1, "version": 1,
          "grant": { "aptitudePointsPerTheta": 3, "skillPointsPerTheta": 1 },
          "pointEconomy": { "aptitudePointsPerThetaMilliByScope": { "commander": 1, "creatureType": 4, "aspect": 4, "uniqueCreature": 6 }, "respecPrice": 10 }, "guardEconomy": { "flatCommitCost": 50, "absorbDrainSharePermille": 300, "riposteShareCapPermille": 400 }, "mitigation": { "scaleMilli": 1000, "families": ["combat.defense", "combat.dodge", "combat.parry", "combat.block", "combat.absorption", "combat.heal"] },
          "read": { "contest": { "spanPoints": 100.0, "shareExponentMilli": 1000 }, "magnitude": { "shareExponentMilli": 1000 }, "layerWeightMilliByScope": {"commander":1000,"creatureType":1000,"aspect":1000,"uniqueCreature":1000} },
          "recovery": { "scaleMilli": 374, "targetRecoveryShareMilli": 670, "families": ["resource.regen"] },
          "familyRead": { "combat.power": "magnitude" },
          "edges": [ { "channel": "combat.power.omni", "source": "Might", "kMilli": 1000 } ]
        }
        """);

    [Fact]
    public void Hand_computed_expected_value_at_a_named_rung_share_theta()
    {
        // rung=7 -> kMilli=5359; share=0.5, gamma=1.0 (linear) -> share^gamma=0.5; pTheta=1000.
        // AptitudeReadFunctions.Magnitude: k=5.359, k*0.5=2.6795, *1000=2679.5 -> rounds to 2680 (away from zero).
        var result = AuraMagnitude.Compute(rung: 7, share: 0.5, pTheta: 1000, Rung7To10(), LinearGammaTuning());
        Assert.Equal(2680, result);
    }

    [Fact]
    public void Exactly_zero_at_zero_share()
    {
        var result = AuraMagnitude.Compute(rung: 10, share: 0.0, pTheta: 999_999, Rung7To10(), LinearGammaTuning());
        Assert.Equal(0, result);
    }

    [Fact]
    public void Base_independence_the_value_never_depends_on_anything_but_its_own_two_axes()
    {
        // Not "channel-composed base-independence" (that's OverlayAdd/T1's own concern) -- this is the
        // formula's OWN purity: identical (rung, share, pTheta, tunings) always produces the identical
        // result, called from as many different "contexts" as a caller invents.
        var a = AuraMagnitude.Compute(rung: 8, share: 0.3, pTheta: 5000, Rung7To10(), LinearGammaTuning());
        var b = AuraMagnitude.Compute(rung: 8, share: 0.3, pTheta: 5000, Rung7To10(), LinearGammaTuning());
        var c = AuraMagnitude.Compute(rung: 8, share: 0.3, pTheta: 5000, Rung7To10(), LinearGammaTuning());

        Assert.Equal(a, b);
        Assert.Equal(b, c);
    }

    [Fact]
    public void Second_difference_in_share_is_zero_at_two_different_Theta_linear_gamma()
    {
        // gamma = 1.0 (linear in share) in LinearGammaTuning -> f is linear in share, so its second
        // finite difference over any equal step is (mathematically) zero. AptitudeReadFunctions.
        // Magnitude rounds its result to a long, so each of the three sample points can independently
        // round by up to 0.5 -- the discrete second difference can therefore land at -1/0/1 rather than
        // exactly 0. A tolerance of 1 is the rounding artifact of THREE independent roundings, not a
        // hidden nonlinearity: bounded (never growing with theta or the step) is what actually proves
        // linearity here.
        foreach (var theta in new long[] { 1000, 50_000 })
        {
            long f(double share) => AuraMagnitude.Compute(rung: 9, share, theta, Rung7To10(), LinearGammaTuning());
            var step = 0.2;
            var secondDiff = f(0.4 + 2 * step) - 2 * f(0.4 + step) + f(0.4);
            Assert.InRange(secondDiff, -1, 1);
        }
    }

    [Fact]
    public void Ratio_to_P_of_theta_is_constant_across_theta_for_a_fixed_rung_and_share()
    {
        // Compute(...)/P(Theta) = k*share^gamma, independent of Theta -- the Theta scaling is exactly
        // multiplicative, nothing else creeps in as Theta changes.
        var thetas = new long[] { 100, 10_000, 1_000_000 };
        var ratios = thetas.Select(theta =>
            (double)AuraMagnitude.Compute(rung: 8, share: 0.6, theta, Rung7To10(), LinearGammaTuning()) / theta).ToList();

        for (var i = 1; i < ratios.Count; i++)
            Assert.Equal(ratios[0], ratios[i], 2);
    }

    [Fact]
    public void An_undeclared_rung_below_7_is_rejected_at_use_defense_in_depth()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AuraMagnitude.Compute(rung: 3, share: 0.5, pTheta: 1000, Rung7To10(), LinearGammaTuning()));
    }

    [Fact]
    public void Rebalancing_needs_no_code_change_only_a_different_tuning_object()
    {
        // Same code path, a DIFFERENT AuraTuning (as if a balance pass edited aura.v1.json) --
        // the result changes purely from data, proving AuraMagnitude.Compute has no hardcoded k.
        // share=1.0 makes every intermediate exact (sharePowMilli=1000, rawMicro an exact multiple of
        // 1_000_000) -- no rounding ambiguity, so doubling kMilli exactly doubles the result. Any
        // fractional share risks landing on a rounding boundary where doubling the input does NOT
        // double the ROUNDED output (rounding does not commute with scaling in general) -- a real trap
        // an earlier version of this test fell into at share=0.5/0.6.
        var original = AuraMagnitude.Compute(rung: 7, share: 1.0, pTheta: 1000, Rung7To10(), LinearGammaTuning());

        var rebalanced = new AuraTuning(new Dictionary<int, long> { [7] = 10718 }, MaxActiveAuras: 1); // doubled k(7)
        var afterRebalance = AuraMagnitude.Compute(rung: 7, share: 1.0, pTheta: 1000, rebalanced, LinearGammaTuning());

        Assert.NotEqual(original, afterRebalance);
        Assert.Equal(original * 2, afterRebalance);
    }
}
