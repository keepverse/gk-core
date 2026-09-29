using FusionRpg.Contracts;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Combat.Shield;
using FusionRpg.Core.Effects;
using Xunit;

namespace FusionRpg.Core.Tests.Combat.Shield;

/// <summary>
/// Aura path (T11, owner decision 3): OnTimer cadence → Area multi-resolve → one apply per
/// resolved ally, refillOnMerge=false so re-assertion is genuinely idempotent (spec §2.6).
/// </summary>
public class ShieldAuraTests
{
    static FoundationHarness Harness()
    {
        var h = new FoundationHarness().WithShieldGate();
        h.SetBoard(new[]
        {
            new BoardEntitySnap { Ptr = "P1", Side = "plant", TypeId = 0, Col = 2, Row = 2 },
            new BoardEntitySnap { Ptr = "P2", Side = "plant", TypeId = 0, Col = 4, Row = 2 },
            new BoardEntitySnap { Ptr = "P3", Side = "plant", TypeId = 0, Col = 6, Row = 2 },
            new BoardEntitySnap { Ptr = "P4", Side = "plant", TypeId = 0, Col = 2, Row = 4 },   // other lane
            new BoardEntitySnap { Ptr = "Z1", Side = "zombie", TypeId = 0, Col = 7, Row = 2 }
        });
        return h;
    }

    static EffectGrantDto AuraGrant(long amount = 50) => new()
    {
        GrantId = "guardian-aura",
        EffectId = "fx.shield_grant",
        OwnerKey = EffectOwnerKeys.Match,
        Overlay = new Dictionary<string, object?>
        {
            ["amount"] = amount,
            ["sourceClass"] = "aura",
            ["icd_ms"] = 0,
            ["target"] = new Dictionary<string, object?>
            {
                ["mode"] = TargetModes.Area,
                ["shape"] = AreaShapes.Row,
                ["anchor"] = "EventTarget",
                ["filters"] = new Dictionary<string, object?> { ["side"] = "plant" },
                ["maxTargets"] = 8
            }
        }
    };

    static EffectEventDto TimerPulse() => new()
    {
        Trigger = EffectTriggers.OnTimer,
        ActorPtr = "P1",
        TargetPtr = "P1",   // aura anchors on the guardian's own cell
        Side = "plant",
        TypeId = 0
    };

    [Fact]
    public void Aura_pulse_shields_every_ally_in_the_lane()
    {
        var h = Harness();
        h.Grant(AuraGrant());
        h.OnEvent(TimerPulse());

        // Lane row 2 plants: P1, P2, P3 — P4 (row 4) and Z1 (zombie) excluded.
        Assert.Equal(50, h.ShieldRuntime!.Totals(EffectOwnerKeys.Entity("p1")).Hp);
        Assert.Equal(50, h.ShieldRuntime.Totals(EffectOwnerKeys.Entity("p2")).Hp);
        Assert.Equal(50, h.ShieldRuntime.Totals(EffectOwnerKeys.Entity("p3")).Hp);
        Assert.Equal(0, h.ShieldRuntime.Totals(EffectOwnerKeys.Entity("p4")).Hp);
        Assert.Equal(0, h.ShieldRuntime.Totals(EffectOwnerKeys.Entity("z1")).Hp);
        Assert.All(h.ShieldRuntime.GetShields(EffectOwnerKeys.Entity("p2")),
            s => Assert.Equal(ShieldPolicy.PriorityAura, s.Priority));
    }

    [Fact]
    public void Reassertion_is_idempotent_no_free_heal()
    {
        var h = Harness();
        h.Grant(AuraGrant());
        h.OnEvent(TimerPulse());

        // Damage P2's aura shield, then pulse again — hp must NOT refill.
        var shield = Assert.Single(h.ShieldRuntime!.GetShields(EffectOwnerKeys.Entity("p2")));
        shield.Hp = 15;
        h.OnEvent(TimerPulse());

        var after = Assert.Single(h.ShieldRuntime.GetShields(EffectOwnerKeys.Entity("p2")));
        Assert.Same(shield, after);
        Assert.Equal(15, after.Hp);
    }

    [Fact]
    public void Reassertion_restores_a_broken_aura_shield_as_new_instance()
    {
        var h = Harness();
        h.Grant(AuraGrant());
        h.OnEvent(TimerPulse());

        // Break P2's shield entirely via absorb, then pulse again → fresh instance.
        h.ShieldRuntime!.Absorb(EffectOwnerKeys.Entity("p2"), 500, 1,
            Array.Empty<FusionRpg.Core.Combat.Element.ElementPayloadComponent>(),
            null, FusionRpg.Core.Stats.Derived.ActorDerivedSnapshot.StubNeutral());
        Assert.Empty(h.ShieldRuntime.GetShields(EffectOwnerKeys.Entity("p2")));

        h.OnEvent(TimerPulse());
        var restored = Assert.Single(h.ShieldRuntime.GetShields(EffectOwnerKeys.Entity("p2")));
        Assert.Equal(50, restored.Hp);
    }
}
