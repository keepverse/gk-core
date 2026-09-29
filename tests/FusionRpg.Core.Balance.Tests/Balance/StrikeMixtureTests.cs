using FusionRpg.Core.Balance.Analytic;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Balance;

/// <summary>class-system-todo.md P4.1 — StrikeMixture's five atoms cross-checked against the real
/// <see cref="OverlayCombatCalculator"/> it is required to call into rather than re-implement
/// (spec-deterministic-core.md §2, §7 "Never: re-implement a combat formula"). Same
/// <c>CountingRng</c>/snapshot-construction idiom as <c>EvasionChainTests.cs</c>.</summary>
public class StrikeMixtureTests
{
    static ActorDerivedSnapshot NeutralCombat => ActorDerivedSnapshot.StubNeutral();

    sealed class CountingRng : ICombatRng
    {
        public int Draws;
        readonly int _value;
        public CountingRng(int value) => _value = value;
        public int Next(int exclusiveMax) { Draws++; return _value; }
    }

    static CombatActorSnapshot Snap(ActorDerivedSnapshot s) => new(s, ActorElementTypes.Neutral);

    [Fact]
    public void NullArguments_reject()
    {
        var s = Snap(NeutralCombat);
        Assert.Throws<ArgumentNullException>(() => StrikeMixture.Compute(100, null!, s));
        Assert.Throws<ArgumentNullException>(() => StrikeMixture.Compute(100, s, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => StrikeMixture.Compute(-1, s, s));
    }

    [Fact]
    public void AtomProbabilities_sumToOne()
    {
        var attacker = NeutralCombat.Overlay(new[]
        {
            new KeyValuePair<string, double>(DerivedStatChannels.CombatCritRateOmni, 200)
        });
        var defender = NeutralCombat.Overlay(new[]
        {
            new KeyValuePair<string, double>(DerivedStatChannels.CombatParryRateOmni, 250),
            new KeyValuePair<string, double>(DerivedStatChannels.CombatBlockRateOmni, 150)
        });
        var r = StrikeMixture.Compute(100, Snap(attacker), Snap(defender));
        var total = r.Miss.Probability + r.Parried.Probability + r.Blocked.Probability + r.Clean.Probability + r.CleanCrit.Probability;
        Assert.Equal(1.0, total, 12);
    }

    [Fact]
    public void Miss_probabilityIsOneMinusPHit_neutralIsFiftyFifty()
    {
        // sigmoid(0, anything) = 0.5 -- CombatProbability.Sigmoid's own identity point.
        var r = StrikeMixture.Compute(100, Snap(NeutralCombat), Snap(NeutralCombat));
        Assert.Equal(0.5, r.Miss.Probability, 9);
        Assert.Equal(0.0, r.Miss.Damage);
    }

    [Fact]
    public void CleanHit_damageMatchesOverlayCombatCalculator_forcedHitNoCrit()
    {
        var attacker = NeutralCombat.Overlay(new[]
        {
            new KeyValuePair<string, double>(DerivedStatChannels.CombatPowerOmni, 300),
            new KeyValuePair<string, double>(DerivedStatChannels.CombatCritRateOmni, 800)
        });
        var defender = NeutralCombat.Overlay(new[]
        {
            new KeyValuePair<string, double>(DerivedStatChannels.CombatDefenseOmni, 120)
        });

        var mixture = StrikeMixture.Compute(100, Snap(attacker), Snap(defender));

        var calc = new OverlayCombatCalculator();
        var request = new OverlayCombatRequest
        {
            BaseOverlayDamage = 100,
            Attacker = Snap(attacker),
            Defender = Snap(defender),
            ForceHit = true,
            ForceCrit = false
        };
        var (delta, breakdown) = calc.Compute(request, new CountingRng(0));

        // OverlayCombatCalculator rounds every individual hit to a whole HP delta
        // (signedDelta = -(long)Math.Round(finalDamage)); the closed form must NOT round per-swing --
        // rounding every term before averaging would bias the mean across many swings. Compare rounded.
        Assert.False(breakdown.Crit);
        Assert.Equal(-delta, (long)Math.Round(mixture.Clean.Damage, MidpointRounding.AwayFromZero));
    }

    [Fact]
    public void CleanCrit_damageMatchesOverlayCombatCalculator_forcedHitForcedCrit()
    {
        var attacker = NeutralCombat.Overlay(new[]
        {
            new KeyValuePair<string, double>(DerivedStatChannels.CombatPowerOmni, 300),
            new KeyValuePair<string, double>(DerivedStatChannels.CombatCritDamageOmni, 400)
        });
        var defender = NeutralCombat.Overlay(new[]
        {
            new KeyValuePair<string, double>(DerivedStatChannels.CombatDefenseOmni, 120)
        });

        var mixture = StrikeMixture.Compute(100, Snap(attacker), Snap(defender));

        var calc = new OverlayCombatCalculator();
        var request = new OverlayCombatRequest
        {
            BaseOverlayDamage = 100,
            Attacker = Snap(attacker),
            Defender = Snap(defender),
            ForceHit = true,
            ForceCrit = true
        };
        var (delta, breakdown) = calc.Compute(request, new CountingRng(0));

        Assert.True(breakdown.Crit);
        Assert.Equal(-delta, (long)Math.Round(mixture.CleanCrit.Damage, MidpointRounding.AwayFromZero));
        // Crit strictly amplifies over the non-crit clean hit for any positive crit multiplier.
        Assert.True(mixture.CleanCrit.Damage > mixture.Clean.Damage);
    }

    [Fact]
    public void Parried_damageMatchesOverlayCombatCalculator()
    {
        var attacker = NeutralCombat.Overlay(new[]
        {
            new KeyValuePair<string, double>(DerivedStatChannels.CombatPowerOmni, 300)
        });
        var defender = NeutralCombat.Overlay(new[]
        {
            new KeyValuePair<string, double>(DerivedStatChannels.CombatParryRateOmni, 500),
            new KeyValuePair<string, double>(DerivedStatChannels.CombatParryStrengthOmni, 200)
        });
        // pHit = 0.5, pParryRaw = 0.5, pBlockRaw = 0 -- miss[.5,1) parried[0,.5). r=0.1 lands parried.
        var mixture = StrikeMixture.Compute(100, Snap(attacker), Snap(defender));
        Assert.True(mixture.Parried.Probability > 0, "expected a nonzero parry band for this setup");

        var calc = new OverlayCombatCalculator();
        var request = new OverlayCombatRequest
        {
            BaseOverlayDamage = 100,
            Attacker = Snap(attacker),
            Defender = Snap(defender)
        };
        var (delta, breakdown) = calc.Compute(request, new CountingRng(100_000)); // r = 0.1

        Assert.True(breakdown.Parried);
        Assert.Equal(-delta, (long)Math.Round(mixture.Parried.Damage, MidpointRounding.AwayFromZero));
    }

    [Fact]
    public void Blocked_damageMatchesOverlayCombatCalculator()
    {
        var attacker = NeutralCombat.Overlay(new[]
        {
            new KeyValuePair<string, double>(DerivedStatChannels.CombatPowerOmni, 300)
        });
        var defender = NeutralCombat.Overlay(new[]
        {
            new KeyValuePair<string, double>(DerivedStatChannels.CombatBlockRateOmni, 500),
            new KeyValuePair<string, double>(DerivedStatChannels.CombatBlockStrengthOmni, 150)
        });
        // pHit = 0.5, pBlockRaw = 0.5 -- miss[.5,1) blocked[0,.5). r=0.1 lands blocked (no parry band at all).
        var mixture = StrikeMixture.Compute(100, Snap(attacker), Snap(defender));
        Assert.True(mixture.Blocked.Probability > 0, "expected a nonzero block band for this setup");

        var calc = new OverlayCombatCalculator();
        var request = new OverlayCombatRequest
        {
            BaseOverlayDamage = 100,
            Attacker = Snap(attacker),
            Defender = Snap(defender)
        };
        var (delta, breakdown) = calc.Compute(request, new CountingRng(100_000)); // r = 0.1

        Assert.True(breakdown.Blocked);
        Assert.Equal(-delta, (long)Math.Round(mixture.Blocked.Damage, MidpointRounding.AwayFromZero));
    }

    // ── Mean/Variance formula, in isolation from combat wiring ─────────────────────────────────────

    [Fact]
    public void Mean_isTheProbabilityWeightedSum_handComputedCoinFlip()
    {
        var result = new StrikeMixture.Result(
            Miss: new StrikeAtom(0.5, 0.0),
            Parried: new StrikeAtom(0.0, 0.0),
            Blocked: new StrikeAtom(0.0, 0.0),
            Clean: new StrikeAtom(0.5, 100.0),
            CleanCrit: new StrikeAtom(0.0, 0.0));

        Assert.Equal(50.0, result.Mean, 9);
    }

    [Fact]
    public void Variance_matchesHandComputedCoinFlip()
    {
        // Two-point distribution {0 w.p. .5, 100 w.p. .5}: E[D]=50, E[D^2]=5000, Var=5000-2500=2500.
        var result = new StrikeMixture.Result(
            Miss: new StrikeAtom(0.5, 0.0),
            Parried: new StrikeAtom(0.0, 0.0),
            Blocked: new StrikeAtom(0.0, 0.0),
            Clean: new StrikeAtom(0.5, 100.0),
            CleanCrit: new StrikeAtom(0.0, 0.0));

        Assert.Equal(2500.0, result.Variance, 6);
    }

    [Fact]
    public void Variance_isZero_whenAllProbabilityMassIsOnOneAtom()
    {
        var result = new StrikeMixture.Result(
            Miss: new StrikeAtom(0.0, 0.0),
            Parried: new StrikeAtom(0.0, 0.0),
            Blocked: new StrikeAtom(0.0, 0.0),
            Clean: new StrikeAtom(1.0, 42.0),
            CleanCrit: new StrikeAtom(0.0, 0.0));

        Assert.Equal(42.0, result.Mean, 9);
        Assert.Equal(0.0, result.Variance, 9);
    }

    [Fact]
    public void Variance_isNeverNegative_acrossARangeOfRealCombatSetups()
    {
        // Structural invariant: Var = E[D^2] - E[D]^2 can go slightly negative only through floating
        // rounding on a near-degenerate distribution -- assert the formula stays sane across several
        // real, varied setups rather than trusting the algebra alone.
        var setups = new (double atkPower, double defDefense, double critRate, double parryRate)[]
        {
            (0, 0, 0, 0), (500, 0, 0, 0), (0, 500, 0, 0), (300, 100, 400, 0), (300, 100, 0, 600), (1000, 1000, 900, 900)
        };
        foreach (var (power, defense, crit, parry) in setups)
        {
            var attacker = NeutralCombat.Overlay(new[]
            {
                new KeyValuePair<string, double>(DerivedStatChannels.CombatPowerOmni, power),
                new KeyValuePair<string, double>(DerivedStatChannels.CombatCritRateOmni, crit)
            });
            var defender = NeutralCombat.Overlay(new[]
            {
                new KeyValuePair<string, double>(DerivedStatChannels.CombatDefenseOmni, defense),
                new KeyValuePair<string, double>(DerivedStatChannels.CombatParryRateOmni, parry)
            });
            var r = StrikeMixture.Compute(1000, Snap(attacker), Snap(defender));
            Assert.True(r.Variance >= -1e-6, $"negative variance for power={power} defense={defense} crit={crit} parry={parry}: {r.Variance}");
        }
    }
}
