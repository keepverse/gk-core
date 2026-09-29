using FusionRpg.Core.Combat;
using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Combat;

/// <summary>spec-mitigation-chain.md (T5.1) — penetration/absorption inside the delta,
/// amplification/reduction after crit. First Phase 5 module: goldens are allowed to move here, but
/// AllGoldensUnchangedAtZero proves they do not, since all four families default to 0.</summary>
public class MitigationChainTests
{
    static ActorDerivedSnapshot NeutralCombat => ActorDerivedSnapshot.StubNeutral();

    static ActorDerivedSnapshot AccurateNoCritAttacker => NeutralCombat.Overlay(new[]
    {
        new KeyValuePair<string, double>(DerivedStatChannels.CombatAccuracyOmni, 500),
        new KeyValuePair<string, double>(DerivedStatChannels.CombatCritRateOmni, -500)
    });

    [Fact]
    public void AllGoldensUnchangedAtZero()
    {
        // All four new families default to 0 -> pierceFactor = ampFactor = 1.0 -> byte-identical.
        // The module's own acceptance test (also proven systemically by the full suite: every
        // pre-existing OverlayCombatCalculator test still passes unchanged).
        var calc = new OverlayCombatCalculator();
        var request = new OverlayCombatRequest
        {
            BaseOverlayDamage = 100,
            Components = new[] { new ElementPayloadComponent(ElementTypeId.Fire, 1.0) },
            Attacker = new CombatActorSnapshot(AccurateNoCritAttacker, ActorElementTypes.Neutral),
            Defender = new CombatActorSnapshot(NeutralCombat, ActorElementTypes.Create(ElementTypeId.Ice))
        };

        var (delta, breakdown) = calc.Compute(request, new SeededCombatRng(1));

        Assert.True(breakdown.Hit);
        Assert.Equal(-125, delta); // matches OverlayCombatCalculatorTests' own Fire-vs-Ice golden exactly
    }

    [Fact]
    public void PenetrationNeedsDefenseToMatter()
    {
        // spec §2.1: against a zero-defense target (StubNeutral's default), penetration changes
        // nothing -- effectiveDefense = 0 x pierceFactor = 0 regardless of pierceFactor's value.
        var calc = new OverlayCombatCalculator();
        var defender = new CombatActorSnapshot(NeutralCombat, ActorElementTypes.Create(ElementTypeId.Ice));
        var attacker = new CombatActorSnapshot(AccurateNoCritAttacker, ActorElementTypes.Neutral);
        var components = new[] { new ElementPayloadComponent(ElementTypeId.Fire, 1.0) };

        var withoutPen = new OverlayCombatRequest { BaseOverlayDamage = 100, Components = components, Attacker = attacker, Defender = defender };
        var withPen = new OverlayCombatRequest
        {
            BaseOverlayDamage = 100,
            Components = components,
            Attacker = new CombatActorSnapshot(
                AccurateNoCritAttacker.Overlay(new[] { new KeyValuePair<string, double>(DerivedStatChannels.CombatPenetrationOmni, 1000) }),
                ActorElementTypes.Neutral),
            Defender = defender
        };

        var (deltaWithout, _) = calc.Compute(withoutPen, new SeededCombatRng(1));
        var (deltaWith, _) = calc.Compute(withPen, new SeededCombatRng(1));

        Assert.Equal(deltaWithout, deltaWith);
    }

    [Fact]
    public void AbsorptionAnswersPenetration()
    {
        // spec §6: equal pen and absorption cancel exactly -- penDelta = 0 -> pierceFactor = 1.0,
        // identical to neither being present, even against a defender with REAL defense (so this
        // proves cancellation, not just "defense was 0 anyway" like the test above).
        var calc = new OverlayCombatCalculator();
        var defenderWithDefense = NeutralCombat.Overlay(new[] { new KeyValuePair<string, double>(DerivedStatChannels.CombatDefenseFire, 40.0) });
        var components = new[] { new ElementPayloadComponent(ElementTypeId.Fire, 1.0) };

        var neither = new OverlayCombatRequest
        {
            BaseOverlayDamage = 100,
            Components = components,
            Attacker = new CombatActorSnapshot(AccurateNoCritAttacker, ActorElementTypes.Neutral),
            Defender = new CombatActorSnapshot(defenderWithDefense, ActorElementTypes.Create(ElementTypeId.Ice))
        };
        var equalPenAndAbsorption = new OverlayCombatRequest
        {
            BaseOverlayDamage = 100,
            Components = components,
            Attacker = new CombatActorSnapshot(
                AccurateNoCritAttacker.Overlay(new[] { new KeyValuePair<string, double>(DerivedStatChannels.CombatPenetrationOmni, 500) }),
                ActorElementTypes.Neutral),
            Defender = new CombatActorSnapshot(
                defenderWithDefense.Overlay(new[] { new KeyValuePair<string, double>(DerivedStatChannels.CombatAbsorptionOmni, 500) }),
                ActorElementTypes.Create(ElementTypeId.Ice))
        };

        var (deltaNeither, _) = calc.Compute(neither, new SeededCombatRng(1));
        var (deltaEqual, _) = calc.Compute(equalPenAndAbsorption, new SeededCombatRng(1));

        Assert.Equal(deltaNeither, deltaEqual);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1000)]
    [InlineData(1_000_000)]
    [InlineData(1_000_000_000)]
    public void DefenseNeverGoesNegative(double penDelta)
    {
        // spec §2.1: unbounded penetration asymptotes to zero defense, never inverts. Bounded (0,1].
        var factor = OverlayCombatCalculator.PierceFactor(penDelta, pierceScale: 10.0);
        Assert.InRange(factor, 0.0, 1.0);
        Assert.True(factor > 0.0, "pierceFactor must never reach exactly 0 -- defense asymptotes, never inverts");
    }

    [Theory]
    [InlineData(100.0, 1.5, 30.0, 10.0)]
    [InlineData(50.0, 2.0, -20.0, 10.0)]
    [InlineData(1000.0, 1.0, 0.0, 10.0)]
    public void AmpCritOrderIrrelevant(double baseDamage, double critMult, double ampDelta, double ampScale)
    {
        // spec §2.2: both critMultiplier and ampFactor are plain multipliers on finalDamage --
        // multiplication commutes, so the order between them is arithmetically irrelevant.
        var ampFactor = OverlayCombatCalculator.AmpFactor(ampDelta, ampScale);
        var ampThenCrit = baseDamage * ampFactor * critMult;
        var critThenAmp = baseDamage * critMult * ampFactor;
        Assert.Equal(ampThenCrit, critThenAmp, 9);
    }

    [Fact]
    public void AmpIsUnclamped()
    {
        // spec §2.2 / §6: arbitrarily large amplification keeps scaling -- no ceiling (PS-8).
        var scale = 10.0;
        Assert.Equal(1.0, OverlayCombatCalculator.AmpFactor(0, scale));
        Assert.Equal(101.0, OverlayCombatCalculator.AmpFactor(1000, scale));
        Assert.Equal(100_001.0, OverlayCombatCalculator.AmpFactor(1_000_000, scale));
        Assert.True(OverlayCombatCalculator.AmpFactor(1_000_000_000, scale) > 100_000_000.0);
    }

    [Fact]
    public void AmpAppliedOnceNotPerComponent()
    {
        // spec §2.3: a 3-component payload gets ONE amp factor, computed from the weighted sum of
        // the three components' amplification deltas -- not one factor multiplied in per component
        // (which would double-count the weights, since they already sum to 1.0).
        var calc = new OverlayCombatCalculator();
        var components = new[]
        {
            new ElementPayloadComponent(ElementTypeId.Fire, 0.5),
            new ElementPayloadComponent(ElementTypeId.Ice, 0.3),
            new ElementPayloadComponent(ElementTypeId.Air, 0.2)
        };
        var request = new OverlayCombatRequest
        {
            BaseOverlayDamage = 100,
            Components = components,
            Attacker = new CombatActorSnapshot(
                AccurateNoCritAttacker.Overlay(new[]
                {
                    new KeyValuePair<string, double>(DerivedStatChannels.CombatAmplification(ElementTypeId.Fire), 100),
                    new KeyValuePair<string, double>(DerivedStatChannels.CombatAmplification(ElementTypeId.Ice), 200),
                    new KeyValuePair<string, double>(DerivedStatChannels.CombatAmplification(ElementTypeId.Air), 300)
                }),
                ActorElementTypes.Neutral),
            Defender = new CombatActorSnapshot(NeutralCombat, ActorElementTypes.Neutral)
        };

        var (delta, breakdown) = calc.Compute(request, new SeededCombatRng(1));

        var expectedAmpDelta = 0.5 * 100 + 0.3 * 200 + 0.2 * 300; // 170 -- ONE weighted factor's worth
        var expectedAmpFactor = OverlayCombatCalculator.AmpFactor(expectedAmpDelta, 10.0);

        var expectedFinal = Math.Max(0.0, breakdown.PowerAdjustedDamage);
        if (breakdown.Crit) expectedFinal *= breakdown.CritMultiplierFinal;
        expectedFinal *= expectedAmpFactor;
        var expectedSignedDelta = expectedFinal > 0 ? -(long)Math.Round(expectedFinal) : 0L;

        Assert.Equal(expectedSignedDelta, delta);
    }

    [Fact]
    public void MatchupStillAppliedOnce()
    {
        // spec §2.3 / T3's own NoReaderTouchesTheNewFamiliesYet precedent: the new families must
        // never touch the matchup matrix -- componentBonus stays computed exactly where and as often
        // as it always was, unrelated to and unchanged by T5.1's two insertions.
        var text = ReadCoreFile("Combat", "OverlayCombatCalculator.cs");
        var elementHubCalls = System.Text.RegularExpressions.Regex.Matches(text, @"_elementHub\.Resolve\w+\(").Count;
        Assert.Equal(2, elementHubCalls); // ResolvePayloadBonus (omni-fallback matchup) + ResolveComponentBonus (per component)
    }

    [Fact]
    public void LongThroughout()
    {
        // spec §7: widen before multiplying, divide by 1000 last, overflow throws. T5.1's
        // penetration/absorption/amplification/reduction math is double throughout, matching this
        // method's OWN pre-existing style -- CombatDerivedReader.cs returns double, and floating point
        // is allowed for any quantity (owner ruling 2026-09-15; audit-overflow.py's former A7 double
        // review rule was removed with the floating-point ban).
        // T5.3 (spec-evasion-chain.md) legitimately adds a NEW long boundary: ClampedContest is
        // permille `long` throughout (matching ShieldMath's own rule), so the double base/delta
        // round to long once, at the one point they cross into it -- 3 new casts (the shared base,
        // and one delta expression per parry/block branch) alongside the original signed-delta
        // conversion, 4 total. Verified empirically too: audit-overflow.py before and after T5.1+T5.3
        // reported an identical, 0-critical baseline -- no new finding either module.
        // Proven here structurally: no cast-after-multiply (the A4 violation class) anywhere in the
        // file, and every `long` appearance is one of these known, accounted-for boundaries.
        //
        // 4 → 5 when ParryNeutralShareKPm landed: the neutral removal is a FIFTH crossing into
        // ClampedContest's long domain (`effectiveBaseDamage × share/1000`). It divides by 1000
        // last and rounds once, like the four before it, and is a cast-of-a-product — never the
        // banned `(long)(a*b)` cast-AFTER-multiply, since the multiply is double-domain by
        // construction and the cast applies to the already-rounded result.
        var text = ReadCoreFile("Combat", "OverlayCombatCalculator.cs");
        Assert.DoesNotContain("(long)(", text, StringComparison.Ordinal);
        var longCasts = System.Text.RegularExpressions.Regex.Matches(text, @"\(long\)").Count;
        Assert.Equal(5, longCasts);
    }

    static string ReadCoreFile(params string[] relativeUnderCore)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(new[] { dir.FullName, "src", "FusionRpg.Core" }.Concat(relativeUnderCore).ToArray());
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("could not find " + string.Join("/", relativeUnderCore));
    }
}
