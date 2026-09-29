using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Battle;

/// <summary>spec-skill-modifiers.md — ten channels (already registered, T2), plus the two mechanisms
/// their spec requires: effectiveness applied pre-mitigation, and a cooldown-reduction formula with a
/// structural one-tick floor.</summary>
public class SkillModifiersTests
{
    static OverlayCombatRequest BaseRequest(double effectiveness = 1.0) => new()
    {
        BaseOverlayDamage = 100,
        Components = new[] { new ElementPayloadComponent(ElementTypeId.Fire, 1.0) },
        Attacker = new CombatActorSnapshot(
            ActorDerivedSnapshot.FromValues(new[]
            {
                new KeyValuePair<string, double>(DerivedStatChannels.CombatPowerFire, 20)
            }),
            ActorElementTypes.Neutral),
        Defender = new CombatActorSnapshot(
            ActorDerivedSnapshot.FromValues(new[]
            {
                new KeyValuePair<string, double>(DerivedStatChannels.CombatDefenseFire, 10)
            }),
            ActorElementTypes.Neutral),
        ForceHit = true,
        ForceCrit = false,
        EffectivenessMultiplier = effectiveness
    };

    [Fact]
    public void EffectivenessIsPreMitigation()
    {
        // Raising effectiveness and raising defense by the SAME ratio should not cancel if
        // effectiveness only scaled the flat base and defense only scaled the delta -- the proof that
        // effectiveness lands where combat-damage-ssot.md §6.7 says (inside baseDamage, before the
        // weightedDelta defense already answers), not as an independent post-mitigation multiplier.
        var calculator = new OverlayCombatCalculator();
        var rng = new FixedCombatRng(0.0);

        // Asserted as an INVARIANT — the mitigated fraction (damage / pre-mitigation offense) is flat
        // in effectiveness — rather than as the old literal `baseline - 100 == doubled`.
        //
        // That literal was only true while mitigation SUBTRACTED. It quietly encoded one DefenseShape
        // into a test whose subject is skill.effectiveness, so adopting divisive mitigation would have
        // demanded a re-bless of a contract that had not actually changed. The invariant below is the
        // thing §2 really promises: effectiveness scales the pre-mitigation term, and defense answers
        // it — proportionally under divisive, absolutely under subtractive, but never bypassed.
        //
        // It is also the regression guard for a real defect. `K` must read LADDER quantities only
        // (authored base + power). An earlier draft used `offense`, which also carries effectiveness —
        // that let effectiveness scale the numerator AND shrink the divisor's bite, making a
        // Feeder-class modifier superlinear. Measured when it did: 1000x effectiveness against a
        // 5000x defense wall leaked 826 damage; reading ladder scale only, it leaks 1.
        double MitigatedFraction(double effectiveness)
        {
            var (delta, _) = calculator.Compute(BaseRequest(effectiveness), rng);
            var offense = 100.0 * effectiveness + 20.0; // base x effectiveness + power(20)
            return -delta / offense;
        }

        var atTen = MitigatedFraction(10.0);
        Assert.Equal(atTen, MitigatedFraction(100.0), 3);
        Assert.Equal(atTen, MitigatedFraction(1000.0), 3);
    }

    [Fact]
    public void EffectivenessCannotBypassDefense()
    {
        // The executable form of §2's contract: effectiveness scales the ATTACKER's base, but an
        // overwhelming DEFENSE still floors total damage at 0 -- effectiveness never creates damage
        // out of a defense wall the way a post-mitigation multiplier would (multiplying zero stays
        // zero either way, but this proves the pre-mitigation delta is what actually floors, not a
        // multiplier papering over a negative).
        var calculator = new OverlayCombatCalculator();
        var rng = new FixedCombatRng(0.0);

        var request = new OverlayCombatRequest
        {
            BaseOverlayDamage = 10,
            EffectivenessMultiplier = 1000.0, // absurdly high -- still must not bypass defense
            Components = new[] { new ElementPayloadComponent(ElementTypeId.Fire, 1.0) },
            Attacker = new CombatActorSnapshot(
                ActorDerivedSnapshot.FromValues(new[]
                {
                    new KeyValuePair<string, double>(DerivedStatChannels.CombatPowerFire, 0)
                }),
                ActorElementTypes.Neutral),
            Defender = new CombatActorSnapshot(
                ActorDerivedSnapshot.FromValues(new[]
                {
                    // Defense large enough to exceed even the 1000x-scaled base (10 * 1000 = 10,000).
                    new KeyValuePair<string, double>(DerivedStatChannels.CombatDefenseFire, 50_000)
                }),
                ActorElementTypes.Neutral),
            ForceHit = true,
            ForceCrit = false
        };

        var (signedDelta, breakdown) = calculator.Compute(request, rng);

        // Overwhelming defense reduces a 1000x-scaled hit to essentially nothing — but NOT to
        // exactly nothing, and that distinction is the point of DefenseShape.Divisive.
        //
        // This assertion was `Assert.Equal(0, signedDelta)` while mitigation subtracted. Reaching
        // exactly zero IS total immunity, and it is the same defect this session removed from
        // ampFactor: a defender past a threshold takes literally nothing from any attacker at any
        // power. Divisive mitigation approaches zero asymptotically instead, so defense can be
        // arbitrarily strong and still never make an actor invulnerable. Keeping the old assertion
        // would have preserved immunity in `combat.defense` while banning it in `combat.reduction` —
        // the same rule enforced in one place and not the other.
        //
        // 10 base x 1000 effectiveness = 10,000 offense against 50,000 defense leaks 1.
        Assert.True(signedDelta < 0, "defense must never zero a hit outright — that is immunity");
        Assert.True(-signedDelta <= 10, $"defense must still absorb ~all of it, leaked {-signedDelta}");
        Assert.Equal(signedDelta, breakdown.FinalSignedDelta);
    }

    [Fact]
    public void EffectivenessDefaultsToOneAndMovesNoGolden()
    {
        // NoGoldensMoveAtDefaults: EffectivenessMultiplier's default (1.0) must be a true no-op.
        var calculator = new OverlayCombatCalculator();
        var withDefault = new OverlayCombatRequest
        {
            BaseOverlayDamage = 100,
            Components = new[] { new ElementPayloadComponent(ElementTypeId.Fire, 1.0) },
            ForceHit = true,
            ForceCrit = false
        };
        var withExplicitOne = new OverlayCombatRequest
        {
            BaseOverlayDamage = 100,
            Components = new[] { new ElementPayloadComponent(ElementTypeId.Fire, 1.0) },
            ForceHit = true,
            ForceCrit = false,
            EffectivenessMultiplier = 1.0
        };

        var (a, _) = calculator.Compute(withDefault, new FixedCombatRng(0.0));
        var (b, _) = calculator.Compute(withExplicitOne, new FixedCombatRng(0.0));
        Assert.Equal(a, b);
    }

    [Fact]
    public void CooldownFloorsAtOneTick()
    {
        Assert.Equal(1, CooldownMath.ApplyReduction(100, 990));   // 99%
        Assert.Equal(1, CooldownMath.ApplyReduction(100, 1000));  // 100%
        Assert.Equal(1, CooldownMath.ApplyReduction(1, 0));
    }

    [Fact]
    public void CooldownReductionUncapped()
    {
        // Arbitrarily large reduction never yields 0 or negative, and never throws -- only the
        // duration is floored, the reduction ratio itself has no ceiling (§3).
        Assert.Equal(1, CooldownMath.ApplyReduction(1000, 50_000));        // 5,000%
        Assert.Equal(1, CooldownMath.ApplyReduction(1000, 1_000_000_000)); // 100,000,000%
    }

    [Fact]
    public void CooldownReductionScalesNormallyBelowTheFloor()
    {
        Assert.Equal(50, CooldownMath.ApplyReduction(100, 500));  // 50%
        Assert.Equal(75, CooldownMath.ApplyReduction(100, 250));  // 25%
        Assert.Equal(100, CooldownMath.ApplyReduction(100, 0));
    }

    [Fact]
    public void NoCooldownCounterpartRegistered()
    {
        // skill.cooldown.* is Race -- guard-stat-pairs.ps1 enforces this at the catalog level; this is
        // the same claim proven directly against the live registry.
        var registry = DerivedStatRegistry.CreateDefault();
        foreach (var category in DerivedStatChannels.ActionCategories)
        {
            var channel = DerivedStatChannels.SkillCooldown(category);
            Assert.True(registry.TryGet(channel, out var def), $"missing {channel}");
            Assert.Equal(StatClass.Race, def.Class);
            Assert.Null(def.CounterpartOf);
        }
    }

    [Fact]
    public void SkillEffectivenessIsFeederWithNoCounterpart()
    {
        var registry = DerivedStatRegistry.CreateDefault();
        foreach (var category in DerivedStatChannels.ActionCategories)
        {
            var channel = DerivedStatChannels.SkillEffectiveness(category);
            Assert.True(registry.TryGet(channel, out var def), $"missing {channel}");
            Assert.Equal(StatClass.Feeder, def.Class);
            Assert.Null(def.CounterpartOf);
        }
    }

    [Fact]
    public void EnvelopeReferencesCatalog()
    {
        var envelope = ActionEnvelope.NoOp with { CooldownChannel = DerivedStatChannels.SkillCooldown("attack") };
        var registry = DerivedStatRegistry.CreateDefault();

        Assert.NotNull(envelope.CooldownChannel);
        Assert.True(registry.IsKnown(envelope.CooldownChannel!));
    }

    [Fact]
    public void EnvelopeDefaultsToNoCooldownChannel()
    {
        // No universal default the way SpeedChannel has one -- which category applies is the action's
        // own choice, so declaring none must not silently pick one.
        Assert.Null(ActionEnvelope.NoOp.CooldownChannel);
    }

    sealed class FixedCombatRng : ICombatRng
    {
        readonly double _value;
        public FixedCombatRng(double value) => _value = value;
        public int Next(int maxExclusive) => (int)(_value * maxExclusive);
    }
}
