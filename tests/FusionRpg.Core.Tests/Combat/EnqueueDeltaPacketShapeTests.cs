using FusionRpg.Contracts;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Combat;

public class EnqueueDeltaPacketShapeTests
{
    [Fact]
    public void Flat_targetPtr_shape_parses_single_target_and_payload()
    {
        var overlay = new Dictionary<string, object?>
        {
            ["amount"] = -100L,
            ["targetPtr"] = "Z1",
            ["elementPayload"] = new object[]
            {
                new Dictionary<string, object?> { ["element"] = "fire", ["weight"] = 1.0 }
            }
        };
        var packet = DamagePacketBuilder.FromOverlay(overlay);
        Assert.Equal(TargetModes.Single, packet.Target.Mode);
        Assert.Equal("Z1", packet.Target.Ptr);
        Assert.NotNull(packet.ElementPayload);
        Assert.Single(packet.ElementPayload!);
    }

    [Fact]
    public void Flat_shape_applies_overlay_math_via_dispatch()
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

        var overlay = new Dictionary<string, object?>
        {
            ["amount"] = -100L,
            ["targetPtr"] = "Z1",
            ["elementPayload"] = new object[]
            {
                new Dictionary<string, object?> { ["element"] = "fire", ["weight"] = 1.0 }
            }
        };
        var packet = DamagePacketBuilder.FromOverlay(
            overlay,
            new EffectEventDto { ActorPtr = "P1", TargetPtr = "Z1" },
            pluginId: "debug");
        var resolved = CombatDamageDispatcher.DispatchInstant(
            packet,
            h.BoardSnapshot,
            new EffectEventDto { ActorPtr = "P1", TargetPtr = "Z1" },
            h.Funnel,
            h.Bag.CombatPolicy,
            h.Bag.CombatRng,
            h.Bag.CombatMath);

        Assert.Equal(1, resolved);
        Assert.Single(h.CombatBreakdowns);
        Assert.Equal(25, h.CombatBreakdowns[0].MatchupBonus, 3);
        Assert.Equal(-125, h.CombatBreakdowns[0].FinalSignedDelta);
    }
}
