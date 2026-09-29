using FusionRpg.Core.Combat;
using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Combat;

/// <summary>
/// U3 — omni fallback (spec-combat-resolver-core): an EMPTY component list resolves over the
/// omni halves only, replacing today's hard throw. Malformed (non-empty) payloads still throw,
/// and the overlay dispatcher path never reaches this branch (payload-null pass-through).
/// </summary>
public class OmniFallbackTests
{
    static OverlayCombatRequest Request(
        ActorDerivedSnapshot? attacker = null, ActorDerivedSnapshot? defender = null,
        long baseDamage = 100, bool? forceHit = null, bool? forceCrit = null) =>
        new()
        {
            BaseOverlayDamage = baseDamage,
            Components = Array.Empty<ElementPayloadComponent>(),
            Attacker = new CombatActorSnapshot(attacker ?? ActorDerivedSnapshot.StubNeutral(), ActorElementTypes.Neutral),
            Defender = new CombatActorSnapshot(defender ?? ActorDerivedSnapshot.StubNeutral(), ActorElementTypes.Neutral),
            ForceHit = forceHit,
            ForceCrit = forceCrit
        };

    [Fact]
    public void Neutral_omni_fallback_locks_the_half_half_one_point_five_table()
    {
        var calc = new OverlayCombatCalculator();
        var (_, breakdown) = calc.Compute(Request(forceHit: true, forceCrit: false), new SeededCombatRng(1));
        Assert.Equal(0.5, breakdown.PHitFinal, 9);
        Assert.Equal(0.5, breakdown.PCritFinal, 9);
        Assert.Equal(1.5, breakdown.CritMultiplierFinal, 9);
        Assert.Equal(0.0, breakdown.MatchupBonus, 9);
        Assert.Equal(-100, breakdown.FinalSignedDelta);
    }

    [Fact]
    public void Omni_channels_feed_the_fallback()
    {
        var composer = new DerivedComposer();
        var attacker = composer.Compose(new[]
        {
            new DerivedModifier(DerivedStatChannels.CombatPowerOmni, DerivedModifierOp.Flat, 30.0)
        });
        var defender = composer.Compose(new[]
        {
            new DerivedModifier(DerivedStatChannels.CombatDefenseOmni, DerivedModifierOp.Flat, 10.0)
        });
        var calc = new OverlayCombatCalculator();
        var (delta, breakdown) = calc.Compute(Request(attacker, defender, forceHit: true, forceCrit: false),
            new SeededCombatRng(1));
        Assert.Equal(20.0, breakdown.WeightedDelta, 9);   // omni power − omni defense, unchanged
        // DefenseShape.Divisive (2026-08-25): offense 130 (base 100 + omni power 30),
        // ladderScale 130, K = 0.45 × 130 = 58.5, defense 10
        //   → 130 × 58.5/(58.5+10) = 111.02 → 111.
        // Was -120 under the subtractive shape (100 + 30 − 10). WeightedDelta above is deliberately
        // still the subtractive difference: it is a reported breakdown field, not the formula.
        Assert.Equal(-111, delta);
    }

    [Fact]
    public void Typed_channels_are_ignored_by_the_fallback()
    {
        var composer = new DerivedComposer();
        var attacker = composer.Compose(new[]
        {
            new DerivedModifier(DerivedStatChannels.CombatPowerFire, DerivedModifierOp.Flat, 500.0),
            new DerivedModifier(DerivedStatChannels.CombatAccuracyFire, DerivedModifierOp.Flat, 500.0)
        });
        var calc = new OverlayCombatCalculator();
        var (_, breakdown) = calc.Compute(Request(attacker, forceHit: true, forceCrit: false), new SeededCombatRng(1));
        Assert.Equal(0.0, breakdown.WeightedDelta, 9);    // fire halves invisible to omni fallback
        Assert.Equal(0.5, breakdown.PHitFinal, 9);
    }

    [Fact]
    public void Malformed_nonempty_payloads_still_throw()
    {
        var calc = new OverlayCombatCalculator();
        var bad = new OverlayCombatRequest
        {
            BaseOverlayDamage = 100,
            Components = new[] { new ElementPayloadComponent(ElementTypeId.Fire, 0.4) }   // sum ≠ 1.0
        };
        Assert.Throws<ArgumentException>(() => calc.Compute(bad, new SeededCombatRng(1)));
    }

    [Fact]
    public void ElementHub_payload_bonus_is_zero_for_empty()
    {
        Assert.Equal(0.0, ElementHub.Default.ResolvePayloadBonus(
            Array.Empty<ElementPayloadComponent>(), ActorElementTypes.Neutral, 100));
    }

    [Fact]
    public void Content_boundary_stays_strict()
    {
        // ElementPayload.Validate keeps rejecting empty — the fallback lives in the resolver
        // entry points only; content/DTO validation is unchanged.
        Assert.Throws<ArgumentException>(() =>
            ElementPayload.Validate(Array.Empty<ElementPayloadComponent>()));
    }
}
