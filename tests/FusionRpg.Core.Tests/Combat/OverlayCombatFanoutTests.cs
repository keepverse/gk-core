using FusionRpg.Contracts;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Status;
using Xunit;

namespace FusionRpg.Core.Tests.Combat;

public class OverlayCombatFanoutTests
{
    static FoundationHarness AreaRowHarness(int combatSeed)
    {
        var h = new FoundationHarness().WithOverlayCombatMath(combatSeed);
        h.SetBoard(new[]
        {
            new BoardEntitySnap { Ptr = "P1", Side = "plant", TypeId = 0, Col = 2, Row = 2 },
            new BoardEntitySnap { Ptr = "Z1", Side = "zombie", TypeId = 0, Col = 7, Row = 2 },
            new BoardEntitySnap { Ptr = "Z2", Side = "zombie", TypeId = 0, Col = 8, Row = 2 },
            new BoardEntitySnap { Ptr = "Z3", Side = "zombie", TypeId = 0, Col = 9, Row = 2 }
        });
        foreach (var ptr in new[] { "Z1", "Z2", "Z3" })
            h.PinElementTypes(ptr, ActorElementTypes.Create(ElementTypeId.Ice));
        h.PinDerived("P1", ActorDerivedSnapshot.StubNeutral().Overlay(new[]
        {
            new KeyValuePair<string, double>(DerivedStatChannels.CombatAccuracyOmni, 500),
            new KeyValuePair<string, double>(DerivedStatChannels.CombatCritRateOmni, -500)
        }));
        return h;
    }

    [Fact]
    public void Area_row_runs_overlay_math_per_target()
    {
        var h = AreaRowHarness(combatSeed: 1);
        h.Grant(new EffectGrantDto
        {
            GrantId = "area-typed",
            EffectId = "fx.overlay_damage",
            OwnerKey = EffectOwnerKeys.Match,
            Overlay = new Dictionary<string, object?>
            {
                ["amount"] = -50L,
                ["icd_ms"] = 0,
                ["elementPayload"] = new object[]
                {
                    new Dictionary<string, object?> { ["element"] = "fire", ["weight"] = 1.0 }
                },
                ["target"] = new Dictionary<string, object?>
                {
                    ["mode"] = TargetModes.Area,
                    ["shape"] = AreaShapes.Row,
                    ["anchor"] = "EventTarget",
                    ["filters"] = new Dictionary<string, object?> { ["side"] = "zombie" },
                    ["maxTargets"] = 8
                },
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

        Assert.Equal(3, h.CombatBreakdowns.Count);
        Assert.All(h.CombatBreakdowns, b => Assert.Equal(12.5, b.MatchupBonus, 3));
        var fa10 = plan.Actions.Where(a => a.Action == EffectActions.ApplyResourceDelta).ToList();
        Assert.Equal(3, fa10.Count);
        Assert.All(fa10, a => Assert.NotEqual(-50L, Convert.ToInt64(a.Params["amount"])));
    }

    [Fact]
    public void Area_row_uses_independent_hit_rolls_per_target()
    {
        var h = AreaRowHarness(combatSeed: 42);
        h.PinDerived("P1", ActorDerivedSnapshot.StubNeutral());
        h.Grant(new EffectGrantDto
        {
            GrantId = "area-mixed-hits",
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
                ["target"] = new Dictionary<string, object?>
                {
                    ["mode"] = TargetModes.Area,
                    ["shape"] = AreaShapes.Row,
                    ["anchor"] = "EventTarget",
                    ["filters"] = new Dictionary<string, object?> { ["side"] = "zombie" },
                    ["maxTargets"] = 8
                },
                ["delivery"] = new Dictionary<string, object?> { ["mode"] = DeliveryModes.Instant }
            }
        });

        h.OnEvent(new EffectEventDto
        {
            Trigger = EffectTriggers.OnDamageDealt,
            ActorPtr = "P1",
            TargetPtr = "Z1",
            Side = "plant"
        });

        Assert.Equal(3, h.CombatBreakdowns.Count);
        var hits = h.CombatBreakdowns.Select(b => b.Hit).ToList();
        Assert.Contains(true, hits);
        Assert.Contains(false, hits);
    }
}
