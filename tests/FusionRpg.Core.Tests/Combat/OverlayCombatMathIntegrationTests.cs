using FusionRpg.Contracts;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Status;
using Xunit;

namespace FusionRpg.Core.Tests.Combat;

public class OverlayCombatMathIntegrationTests
{
    [Fact]
    public void Harness_applies_typed_overlay_damage_via_funnel()
    {
        var h = new FoundationHarness().WithOverlayCombatMath(combatSeed: 1);
        h.SetBoard(new[]
        {
            new BoardEntitySnap { Ptr = "P1", Side = "plant", TypeId = 0, Col = 2, Row = 2 },
            new BoardEntitySnap { Ptr = "Z1", Side = "zombie", TypeId = 0, Col = 7, Row = 2 }
        });
        h.PinDerived("P1", ActorDerivedSnapshot.StubNeutral().Overlay(new[]
        {
            new KeyValuePair<string, double>(DerivedStatChannels.CombatAccuracyOmni, 500),
            new KeyValuePair<string, double>(DerivedStatChannels.CombatCritRateOmni, -500)
        }));
        h.PinElementTypes("Z1", ActorElementTypes.Create(ElementTypeId.Ice));

        h.Grant(new EffectGrantDto
        {
            GrantId = "typed-hit",
            EffectId = "fx.overlay_damage",
            OwnerKey = EffectOwnerKeys.Match,
            Overlay = new Dictionary<string, object?>
            {
                ["amount"] = -100L,
                ["icd_ms"] = 0,
                ["elementPayload"] = new object[]
                {
                    new Dictionary<string, object?> { ["element"] = "fire", ["weight"] = 1.0 }
                },
                ["target"] = new Dictionary<string, object?> { ["mode"] = TargetModes.EventTarget },
                ["delivery"] = new Dictionary<string, object?> { ["mode"] = DeliveryModes.Instant }
            }
        });

        var plan = h.OnEvent(new EffectEventDto
        {
            Trigger = EffectTriggers.OnDamageDealt,
            ActorPtr = "P1",
            TargetPtr = "Z1",
            Side = "plant",
            TypeId = 0,
            TargetTypeId = 0
        });

        Assert.Single(h.CombatBreakdowns);
        Assert.Equal(25, h.CombatBreakdowns[0].MatchupBonus, 3);
        Assert.Equal(-125, h.CombatBreakdowns[0].FinalSignedDelta);
        var fa10 = plan.Actions.Where(a => a.Action == EffectActions.ApplyResourceDelta).ToList();
        Assert.Single(fa10);
        Assert.Equal(-125L, Convert.ToInt64(fa10[0].Params["amount"]));
    }

    [Fact]
    public void Miss_does_not_enqueue_damage()
    {
        var h = new FoundationHarness().WithOverlayCombatMath(combatSeed: 1);
        h.SetBoard(new[]
        {
            new BoardEntitySnap { Ptr = "P1", Side = "plant", TypeId = 0, Col = 2, Row = 2 },
            new BoardEntitySnap { Ptr = "Z1", Side = "zombie", TypeId = 0, Col = 7, Row = 2 }
        });
        h.PinDerived("P1", ActorDerivedSnapshot.StubNeutral().Overlay(new[]
        {
            new KeyValuePair<string, double>(DerivedStatChannels.CombatAccuracyOmni, -500)
        }));
        h.PinElementTypes("Z1", ActorElementTypes.Create(ElementTypeId.Ice));

        h.Grant(new EffectGrantDto
        {
            GrantId = "typed-miss",
            EffectId = "fx.overlay_damage",
            OwnerKey = EffectOwnerKeys.Match,
            Overlay = new Dictionary<string, object?>
            {
                ["amount"] = -100L,
                ["icd_ms"] = 0,
                ["elementPayload"] = new object[]
                {
                    new Dictionary<string, object?> { ["element"] = "fire", ["weight"] = 1.0 }
                },
                ["target"] = new Dictionary<string, object?> { ["mode"] = TargetModes.EventTarget },
                ["delivery"] = new Dictionary<string, object?> { ["mode"] = DeliveryModes.Instant }
            }
        });

        var plan = h.OnEvent(new EffectEventDto
        {
            Trigger = EffectTriggers.OnDamageDealt,
            ActorPtr = "P1",
            TargetPtr = "Z1",
            Side = "plant",
            TypeId = 0,
            TargetTypeId = 0
        });

        Assert.Single(h.CombatBreakdowns);
        Assert.False(h.CombatBreakdowns[0].Hit);
        Assert.Empty(plan.Actions.Where(a => a.Action == EffectActions.ApplyResourceDelta));
    }

    [Fact]
    public void Combat_fire_caster_profile_boosts_typed_damage()
    {
        var h = new FoundationHarness().WithOverlayCombatMath(combatSeed: 1);
        h.SetBoard(new[]
        {
            new BoardEntitySnap { Ptr = "P1", Side = "plant", TypeId = 0, Col = 2, Row = 2 },
            new BoardEntitySnap { Ptr = "Z1", Side = "zombie", TypeId = 0, Col = 7, Row = 2 }
        });
        h.PinDerived("P1", ActorDerivedProfiles.Get(ActorDerivedProfiles.CombatFireCaster));
        h.PinElementTypes("Z1", ActorElementTypes.Create(ElementTypeId.Ice));

        h.Grant(new EffectGrantDto
        {
            GrantId = "fire-caster-hit",
            EffectId = "fx.overlay_damage",
            OwnerKey = EffectOwnerKeys.Match,
            Overlay = new Dictionary<string, object?>
            {
                ["amount"] = -100L,
                ["icd_ms"] = 0,
                ["elementPayload"] = new object[]
                {
                    new Dictionary<string, object?> { ["element"] = "fire", ["weight"] = 1.0 }
                },
                ["target"] = new Dictionary<string, object?> { ["mode"] = TargetModes.EventTarget },
                ["delivery"] = new Dictionary<string, object?> { ["mode"] = DeliveryModes.Instant }
            }
        });

        var plan = h.OnEvent(new EffectEventDto
        {
            Trigger = EffectTriggers.OnDamageDealt,
            ActorPtr = "P1",
            TargetPtr = "Z1",
            Side = "plant"
        });

        Assert.Single(h.CombatBreakdowns);
        Assert.Equal(-175, h.CombatBreakdowns[0].FinalSignedDelta);
        var fa10 = Assert.Single(plan.Actions, a => a.Action == EffectActions.ApplyResourceDelta);
        Assert.Equal(-175L, Convert.ToInt64(fa10.Params["amount"]));
    }
}
