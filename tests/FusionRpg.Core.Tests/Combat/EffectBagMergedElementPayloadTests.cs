using FusionRpg.Contracts;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Combat;

public class EffectBagMergedElementPayloadTests
{
    [Fact]
    public void Action_param_elementPayload_reaches_overlay_math()
    {
        var typedDef = new EffectDef
        {
            EffectId = "test.typed_action_payload",
            EffectType = EffectTypes.Triggered,
            Name = "typed action payload",
            Enabled = true,
            SourceTag = "test",
            Triggers = new List<string> { EffectTriggers.OnDamageDealt },
            Actions = new List<EffectActionRow>
            {
                new()
                {
                    Seq = 1,
                    Action = EffectActions.ApplyResourceDelta,
                    Params = new Dictionary<string, object?>
                    {
                        ["channel"] = "hp",
                        ["elementPayload"] = new object[]
                        {
                            new Dictionary<string, object?> { ["element"] = "fire", ["weight"] = 1.0 }
                        }
                    }
                }
            }
        };
        var catalog = EffectSeedCatalog.CreateAll().Append(typedDef).ToList();
        var h = new FoundationHarness().WithOverlayCombatMath(combatSeed: 1).WithCatalog(catalog);
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
            GrantId = "merged-payload",
            EffectId = "test.typed_action_payload",
            OwnerKey = EffectOwnerKeys.Match,
            Overlay = new Dictionary<string, object?>
            {
                ["amount"] = -100L,
                ["icd_ms"] = 0,
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
        Assert.Equal(-125, h.CombatBreakdowns[0].FinalSignedDelta);
        var fa10 = Assert.Single(plan.Actions, a => a.Action == EffectActions.ApplyResourceDelta);
        Assert.Equal(-125L, Convert.ToInt64(fa10.Params["amount"]));
    }
}
