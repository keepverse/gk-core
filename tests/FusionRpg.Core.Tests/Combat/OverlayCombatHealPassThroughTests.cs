using FusionRpg.Contracts;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Combat;

public class OverlayCombatHealTests
{
    [Fact]
    public void Heal_bypasses_overlay_math()
    {
        var math = OverlayCombatMath.Create(
            (_, _) => new CombatActorSnapshot(ActorDerivedSnapshot.StubNeutral(), ActorElementTypes.Neutral));
        var packet = new DamagePacket
        {
            SignedAmount = 50,
            ElementPayload = new List<ElementPayloadComponentDto>
            {
                new() { Element = "fire", Weight = 1.0 }
            }
        };
        Assert.Equal(50, math.Finalize(50, "Z1", packet, null));
    }

    [Fact]
    public void Harness_heal_with_payload_emits_no_breakdown()
    {
        var h = new FoundationHarness().WithOverlayCombatMath(combatSeed: 1);
        h.SetBoard(new[]
        {
            new BoardEntitySnap { Ptr = "Z1", Side = "zombie", TypeId = 0, Col = 7, Row = 2 }
        });
        h.Grant(new EffectGrantDto
        {
            GrantId = "typed-heal",
            EffectId = "fx.overlay_damage",
            OwnerKey = EffectOwnerKeys.Match,
            Overlay = new Dictionary<string, object?>
            {
                ["amount"] = 50L,
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

        Assert.Empty(h.CombatBreakdowns);
        var fa10 = Assert.Single(plan.Actions, a => a.Action == EffectActions.ApplyResourceDelta);
        Assert.Equal(50L, Convert.ToInt64(fa10.Params["amount"]));
    }
}

public class OverlayCombatPassThroughTests
{
    [Fact]
    public void No_payload_passes_through()
    {
        var math = OverlayCombatMath.Create(
            (_, _) => new CombatActorSnapshot(ActorDerivedSnapshot.StubNeutral(), ActorElementTypes.Neutral));
        var packet = new DamagePacket { SignedAmount = -100 };
        Assert.Equal(-100, math.Finalize(-100, "Z1", packet, null));
    }

    [Fact]
    public void Empty_payload_passes_through()
    {
        var math = OverlayCombatMath.Create(
            (_, _) => new CombatActorSnapshot(ActorDerivedSnapshot.StubNeutral(), ActorElementTypes.Neutral));
        var packet = new DamagePacket { SignedAmount = -100, ElementPayload = new List<ElementPayloadComponentDto>() };
        Assert.Equal(-100, math.Finalize(-100, "Z1", packet, null));
    }
}
