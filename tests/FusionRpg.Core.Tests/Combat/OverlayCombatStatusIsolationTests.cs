using FusionRpg.Contracts;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Status;
using Xunit;

namespace FusionRpg.Core.Tests.Combat;

public class OverlayCombatStatusIsolationTests
{
    [Fact]
    public void Overlay_combat_does_not_create_status_instances()
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
            GrantId = "typed-only",
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

        h.OnEvent(new EffectEventDto
        {
            Trigger = EffectTriggers.OnDamageDealt,
            ActorPtr = "P1",
            TargetPtr = "Z1",
            Side = "plant",
            TypeId = 0,
            TargetTypeId = 0
        });

        Assert.Empty(h.Bag.Status!.AllInstances());
    }
}
