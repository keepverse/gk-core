using FusionRpg.Contracts;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Combat.Shield;

/// <summary>
/// Dispatcher-gate integration — shield-system-spec.md §2.2: instant damage and status DoT
/// pulses drain shields and the Funnel receives exactly the remainder; heals bypass; the
/// no-shield path stays byte-identical.
/// </summary>
public class ShieldGateIntegrationTests
{
    static FoundationHarness TypedHitHarness()
    {
        // Mirrors OverlayCombatMathIntegrationTests.Harness_applies_typed_overlay_damage_via_funnel:
        // fire 1.0 vs ice defender, forced hit / no crit → final delta −125.
        var h = new FoundationHarness().WithOverlayCombatMath(combatSeed: 1).WithShieldGate();
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
        return h;
    }

    static EffectEventDto Hit() => new()
    {
        Trigger = EffectTriggers.OnDamageDealt,
        ActorPtr = "P1",
        TargetPtr = "Z1",
        Side = "plant",
        TypeId = 0,
        TargetTypeId = 0
    };

    [Fact]
    public void Gate_present_but_no_shield_is_byte_identical()
    {
        var h = TypedHitHarness();
        var plan = h.OnEvent(Hit());
        var fa = Assert.Single(plan.Actions.Where(a => a.Action == EffectActions.ApplyResourceDelta));
        Assert.Equal(-125L, Convert.ToInt64(fa.Params["amount"]));
    }

    [Fact]
    public void Partial_absorb_funnel_receives_remainder()
    {
        var h = TypedHitHarness();
        h.GrantShield("Z1", baseHp: 60);   // untyped: d = 125, spent 60, remainder 65
        var plan = h.OnEvent(Hit());
        var fa = Assert.Single(plan.Actions.Where(a => a.Action == EffectActions.ApplyResourceDelta));
        Assert.Equal(-65L, Convert.ToInt64(fa.Params["amount"]));
        Assert.False(h.ShieldRuntime!.HasAnyInstances());   // 60-HP shield broke
    }

    [Fact]
    public void Full_absorb_enqueues_nothing()
    {
        var h = TypedHitHarness();
        h.GrantShield("Z1", baseHp: 500);
        var plan = h.OnEvent(Hit());
        Assert.DoesNotContain(plan.Actions, a => a.Action == EffectActions.ApplyResourceDelta);
        var shield = Assert.Single(h.ShieldRuntime!.GetShields(EffectOwnerKeys.Entity("z1")));
        Assert.Equal(375, shield.Hp);   // 500 − 125
    }

    [Fact]
    public void Typed_shield_matchup_scales_shield_burn()
    {
        var h = TypedHitHarness();
        // Ice shield vs fire hit: STR → d = 125 + 0.25×125 = 156 (round), breaks 60 HP faster;
        // remainder = round(125 × (156−60)/156) = 77.
        h.GrantShield("Z1", baseHp: 60, element: ElementTypeId.Ice);
        var plan = h.OnEvent(Hit());
        var fa = Assert.Single(plan.Actions.Where(a => a.Action == EffectActions.ApplyResourceDelta));
        Assert.Equal(-77L, Convert.ToInt64(fa.Params["amount"]));
    }

    [Fact]
    public void Heal_bypasses_shields()
    {
        var h = new FoundationHarness().WithShieldGate();
        h.SetBoard(new[]
        {
            new BoardEntitySnap { Ptr = "P1", Side = "plant", TypeId = 0, Col = 2, Row = 2 },
            new BoardEntitySnap { Ptr = "Z1", Side = "zombie", TypeId = 0, Col = 7, Row = 2 }
        });
        h.GrantShield("Z1", baseHp: 100);
        h.Grant(new EffectGrantDto
        {
            GrantId = "heal",
            EffectId = "fx.overlay_damage",
            OwnerKey = EffectOwnerKeys.Match,
            Overlay = new Dictionary<string, object?>
            {
                ["amount"] = 50L,
                ["icd_ms"] = 0,
                ["target"] = new Dictionary<string, object?> { ["mode"] = TargetModes.EventTarget },
                ["delivery"] = new Dictionary<string, object?> { ["mode"] = DeliveryModes.Instant }
            }
        });
        var plan = h.OnEvent(Hit());
        var fa = Assert.Single(plan.Actions.Where(a => a.Action == EffectActions.ApplyResourceDelta));
        Assert.Equal(50L, Convert.ToInt64(fa.Params["amount"]));
        Assert.Equal((100, 100), h.ShieldRuntime!.Totals(EffectOwnerKeys.Entity("z1")));
    }

    [Fact]
    public void Status_dot_pulses_drain_shield_and_leak_remainder_after_break()
    {
        var h = new FoundationHarness().WithShieldGate();
        h.SetBoard(new[]
        {
            new BoardEntitySnap { Ptr = "P1", Side = "plant", TypeId = 0, Col = 2, Row = 2 },
            new BoardEntitySnap { Ptr = "Z1", Side = "zombie", TypeId = 0, Col = 7, Row = 2 }
        });
        h.GrantShield("Z1", baseHp: 50);
        h.Grant(new EffectGrantDto
        {
            GrantId = "dot",
            EffectId = "fx.overlay_damage",
            OwnerKey = EffectOwnerKeys.Match,
            Overlay = new Dictionary<string, object?>
            {
                ["amount"] = -20L,
                ["icd_ms"] = 0,
                ["delivery"] = new Dictionary<string, object?>
                {
                    ["mode"] = DeliveryModes.OverTime,
                    ["periodMs"] = 1000,
                    ["durationMs"] = 5000,
                    ["tickBudget"] = 1
                }
            }
        });
        h.OnEvent(Hit());

        // Tick 1: −20 fully absorbed (shield 50 → 30), no FA10.
        h.AdvanceTime(1000);
        Assert.DoesNotContain(h.Sink.Items, a => a.Action == EffectActions.ApplyResourceDelta);
        Assert.Equal(30, h.ShieldRuntime!.Totals(EffectOwnerKeys.Entity("z1")).Hp);

        // Tick 2: −20 absorbed (30 → 10), still nothing.
        h.AdvanceTime(1000);
        Assert.DoesNotContain(h.Sink.Items, a => a.Action == EffectActions.ApplyResourceDelta);

        // Tick 3: shield 10 < 20 → breaks; remainder round(20 × 10/20) = 10 reaches the Funnel.
        h.AdvanceTime(1000);
        var fa = Assert.Single(h.Sink.Items.Where(a => a.Action == EffectActions.ApplyResourceDelta));
        Assert.Equal(-10L, Convert.ToInt64(fa.Params["amount"]));
        Assert.False(h.ShieldRuntime.HasAnyInstances());
    }
}
