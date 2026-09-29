using FusionRpg.Contracts;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Effects;
using Xunit;

namespace FusionRpg.Core.Tests;

public class OverlayProcTests
{
    [Fact]
    public void Overlay_damage_fires_other_grant_not_self()
    {
        var h = new FoundationHarness();
        h.SetBoard(new[]
        {
            new BoardEntitySnap { Ptr = "Z1", Side = "zombie", TypeId = 0, Col = 7, Row = 2 }
        });
        h.Grant(new EffectGrantDto
        {
            GrantId = "nuke",
            EffectId = "fx.overlay_damage",
            OwnerKey = EffectOwnerKeys.Match,
            Overlay = new Dictionary<string, object?>
            {
                ["amount"] = -50L,
                ["icd_ms"] = 0,
                ["delivery"] = new Dictionary<string, object?> { ["mode"] = DeliveryModes.Instant }
            }
        });
        h.Grant(new EffectGrantDto
        {
            GrantId = "sun",
            EffectId = "fx.economy_sun",
            OwnerKey = EffectOwnerKeys.Match,
            Overlay = new Dictionary<string, object?> { ["icd_ms"] = 0 }
        });

        var plan = h.OnEvent(new EffectEventDto
        {
            Trigger = EffectTriggers.OnDamageDealt,
            ActorPtr = "P1",
            TargetPtr = "Z1",
            Side = "plant"
        });

        Assert.Single(plan.Actions, a => a.Action == EffectActions.ApplyResourceDelta);
        Assert.Equal(2, plan.Actions.Count(a => a.Action == EffectActions.Economy));
        Assert.DoesNotContain("TakeDamage", string.Join(",", plan.Actions.Select(a => a.Action)));
    }

    [Fact]
    public void Overlay_proc_respects_proc_depth_on_second_grant()
    {
        var h = new FoundationHarness();
        h.SetBoard(new[]
        {
            new BoardEntitySnap { Ptr = "Z1", Side = "zombie", TypeId = 0, Col = 7, Row = 2 }
        });
        h.Grant(new EffectGrantDto
        {
            GrantId = "a",
            EffectId = "fx.overlay_damage",
            OwnerKey = EffectOwnerKeys.Match,
            Overlay = new Dictionary<string, object?>
            {
                ["amount"] = -50L,
                ["icd_ms"] = 0,
                ["procDepthLimit"] = 1,
                ["delivery"] = new Dictionary<string, object?> { ["mode"] = DeliveryModes.Instant }
            }
        });
        h.Grant(new EffectGrantDto
        {
            GrantId = "b",
            EffectId = "fx.overlay_damage",
            OwnerKey = EffectOwnerKeys.Match,
            Overlay = new Dictionary<string, object?>
            {
                ["amount"] = -10L,
                ["icd_ms"] = 0,
                ["procDepthLimit"] = 1,
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

        var fa = Assert.Single(plan.Actions, a => a.Action == EffectActions.ApplyResourceDelta);
        Assert.Equal(-60L, Convert.ToInt64(fa.Params["amount"]));
        Assert.Contains(h.Bag.LastSkipped, s => s.Contains("proc-depth", StringComparison.OrdinalIgnoreCase));
    }
}
