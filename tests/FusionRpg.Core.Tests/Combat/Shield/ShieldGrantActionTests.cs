using FusionRpg.Contracts;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Combat.Shield;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Combat.Shield;

/// <summary>GrantShield effect action — spec §2.6 grant path (T10).</summary>
public class ShieldGrantActionTests
{
    static FoundationHarness Harness()
    {
        var h = new FoundationHarness().WithShieldGate();
        h.SetBoard(new[]
        {
            new BoardEntitySnap { Ptr = "P1", Side = "plant", TypeId = 0, Col = 2, Row = 2 },
            new BoardEntitySnap { Ptr = "P2", Side = "plant", TypeId = 0, Col = 3, Row = 2 },
            new BoardEntitySnap { Ptr = "Z1", Side = "zombie", TypeId = 0, Col = 7, Row = 2 }
        });
        return h;
    }

    static EffectEventDto Hit(string actor = "P1", string target = "Z1") => new()
    {
        Trigger = EffectTriggers.OnDamageDealt,
        ActorPtr = actor,
        TargetPtr = target,
        Side = "plant",
        TypeId = 0,
        TargetTypeId = 0
    };

    static EffectGrantDto ShieldGrantDto(Dictionary<string, object?> overlay, string grantId = "sg1") => new()
    {
        GrantId = grantId,
        EffectId = "fx.shield_grant",
        OwnerKey = EffectOwnerKeys.Match,
        Overlay = overlay
    };

    [Fact]
    public void Grant_action_shields_the_event_target()
    {
        var h = Harness();
        h.Grant(ShieldGrantDto(new Dictionary<string, object?>
        {
            ["amount"] = 120L,
            ["icd_ms"] = 0,
            ["target"] = new Dictionary<string, object?> { ["mode"] = TargetModes.EventTarget }
        }));
        h.OnEvent(Hit());

        var shield = Assert.Single(h.ShieldRuntime!.GetShields(EffectOwnerKeys.Entity("z1")));
        Assert.Equal(120, shield.Hp);
        Assert.Equal(ShieldPolicy.PrioritySkill, shield.Priority);
        Assert.True(shield.RefillOnMerge);   // skill default
        Assert.Equal("sg1:none", shield.ShieldId);
    }

    [Fact]
    public void Grant_action_self_shield_via_actor_mode()
    {
        var h = Harness();
        h.Grant(ShieldGrantDto(new Dictionary<string, object?>
        {
            ["amount"] = 80L,
            ["element"] = "fire",
            ["icd_ms"] = 0,
            ["target"] = new Dictionary<string, object?> { ["mode"] = TargetModes.Actor }
        }));
        h.OnEvent(Hit());

        var shield = Assert.Single(h.ShieldRuntime!.GetShields(EffectOwnerKeys.Entity("p1")));
        Assert.Equal(ElementTypeId.Fire, shield.Element);
        Assert.Equal("sg1:fire", shield.ShieldId);
    }

    [Fact]
    public void Unknown_element_rejects_and_reports()
    {
        var h = Harness();
        h.Grant(ShieldGrantDto(new Dictionary<string, object?>
        {
            ["amount"] = 80L,
            ["element"] = "water",
            ["icd_ms"] = 0,
            ["target"] = new Dictionary<string, object?> { ["mode"] = TargetModes.EventTarget }
        }));
        h.OnEvent(Hit());
        Assert.False(h.ShieldRuntime!.HasAnyInstances());
    }

    [Fact]
    public void Aura_source_class_defaults_priority_and_no_refill()
    {
        var h = Harness();
        h.Grant(ShieldGrantDto(new Dictionary<string, object?>
        {
            ["amount"] = 60L,
            ["sourceClass"] = "aura",
            ["icd_ms"] = 0,
            ["target"] = new Dictionary<string, object?> { ["mode"] = TargetModes.EventTarget }
        }));
        h.OnEvent(Hit());

        var shield = Assert.Single(h.ShieldRuntime!.GetShields(EffectOwnerKeys.Entity("z1")));
        Assert.Equal(ShieldPolicy.PriorityAura, shield.Priority);
        Assert.False(shield.RefillOnMerge);
    }

    [Fact]
    public void Granted_shield_absorbs_subsequent_damage_end_to_end()
    {
        var h = Harness();
        // Slice: shield grant fires on the hit, then a second grant deals overlay damage.
        h.Grant(ShieldGrantDto(new Dictionary<string, object?>
        {
            ["amount"] = 40L,
            ["icd_ms"] = 0,
            ["target"] = new Dictionary<string, object?> { ["mode"] = TargetModes.EventTarget }
        }));
        h.OnEvent(Hit());
        // Withdrawing the granting effect does NOT revoke the shield — it lives until
        // broken or expired. Also keeps the grant from re-firing on the damage event.
        h.Withdraw("sg1");
        Assert.True(h.ShieldRuntime!.HasAnyInstances());

        h.Grant(new EffectGrantDto
        {
            GrantId = "dmg",
            EffectId = "fx.overlay_damage",
            OwnerKey = EffectOwnerKeys.Match,
            Overlay = new Dictionary<string, object?>
            {
                ["amount"] = -100L,
                ["icd_ms"] = 0,
                ["target"] = new Dictionary<string, object?> { ["mode"] = TargetModes.EventTarget },
                ["delivery"] = new Dictionary<string, object?> { ["mode"] = DeliveryModes.Instant }
            }
        });
        var plan = h.OnEvent(Hit());

        // d = 100, shield 40 → spent 40, remainder round(100×60/100) = 60.
        var fa = Assert.Single(plan.Actions.Where(a => a.Action == EffectActions.ApplyResourceDelta));
        Assert.Equal(-60L, Convert.ToInt64(fa.Params["amount"]));
        Assert.False(h.ShieldRuntime!.HasAnyInstances());
    }

    [Fact]
    public void Regrant_same_grant_id_merges_idempotently()
    {
        var h = Harness();
        h.Grant(ShieldGrantDto(new Dictionary<string, object?>
        {
            ["amount"] = 100L,
            ["icd_ms"] = 0,
            ["target"] = new Dictionary<string, object?> { ["mode"] = TargetModes.EventTarget }
        }));
        h.OnEvent(Hit());
        h.OnEvent(Hit());   // second fire, same grantId → merge, still one instance

        Assert.Single(h.ShieldRuntime!.GetShields(EffectOwnerKeys.Entity("z1")));
    }
}
