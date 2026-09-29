using FusionRpg.Contracts;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Status;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Status;

public class StatusEffectBridgeTests
{
    static StatusRuntime Runtime() =>
        new(StatusCatalogBootstrap.CreateDefault(), (_, attackerLess) =>
            attackerLess ? ActorDerivedSnapshot.AttackerLess() : ActorDerivedSnapshot.StubNeutral());

    [Fact]
    public void OverTime_shims_to_wither()
    {
        var overlay = new Dictionary<string, object?>
        {
            ["delivery"] = new Dictionary<string, object?> { ["mode"] = DeliveryModes.OverTime }
        };
        Assert.True(StatusEffectBridge.TryResolveStatusId(overlay, out var id));
        Assert.Equal("wither", id);
    }

    [Fact]
    public void Counter_shims_to_bond()
    {
        var overlay = new Dictionary<string, object?>
        {
            ["delivery"] = new Dictionary<string, object?> { ["mode"] = DeliveryModes.Counter }
        };
        Assert.True(StatusEffectBridge.TryResolveStatusId(overlay, out var id));
        Assert.Equal("bond", id);
    }

    [Fact]
    public void Area_overtime_shims_to_wither()
    {
        var overlay = new Dictionary<string, object?>
        {
            ["target"] = new Dictionary<string, object?> { ["mode"] = TargetModes.Area },
            ["delivery"] = new Dictionary<string, object?> { ["mode"] = DeliveryModes.OverTime }
        };
        Assert.True(StatusEffectBridge.TryResolveStatusId(overlay, out var id));
        Assert.Equal("wither", id);
    }

    [Fact]
    public void BuildApplyInput_parses_spread_and_immunity_tags()
    {
        var grant = new EffectGrant
        {
            GrantId = "g1",
            EffectId = "fx.overlay_damage",
            PluginId = "test",
            OwnerKey = EffectOwnerKeys.Match,
            Overlay = new Dictionary<string, object?>()
        };
        var ev = new EffectEventDto { ActorPtr = "P1", TargetPtr = "Z1" };
        var overlay = new Dictionary<string, object?>
        {
            ["statusId"] = "blight",
            ["amount"] = -12L,
            ["periodMs"] = 1000,
            ["durationMs"] = 5000,
            ["immunityTags"] = new object[] { "poison", "dot" },
            ["spread"] = new Dictionary<string, object?>
            {
                ["chance"] = 0.25,
                ["maxHops"] = 2,
                ["icd_ms"] = 1000,
                ["statusId"] = "blight",
                ["target"] = new Dictionary<string, object?>
                {
                    ["mode"] = TargetModes.Area,
                    ["shape"] = AreaShapes.Row,
                    ["anchor"] = "EventTarget"
                }
            }
        };

        var input = StatusEffectBridge.BuildApplyInput("blight", grant, ev, overlay, "Z1");
        Assert.Equal(0.25, input.SpreadChance);
        Assert.Equal(2, input.SpreadMaxHops);
        Assert.Equal(1000, input.SpreadIcdMs);
        Assert.NotNull(input.SpreadTarget);
        Assert.Equal(TargetModes.Area, input.SpreadTarget!.Mode);
        Assert.NotNull(input.ImmunityTags);
        Assert.Equal(2, input.ImmunityTags!.Count);
    }

    [Fact]
    public void Unknown_statusId_adds_skipped_tag()
    {
        var rt = Runtime();
        var skipped = new List<string>();
        var grant = new EffectGrant
        {
            GrantId = "bad",
            EffectId = "fx.overlay_damage",
            OwnerKey = EffectOwnerKeys.Match,
            Overlay = new Dictionary<string, object?> { ["statusId"] = "not_a_status" }
        };
        var ev = new EffectEventDto { TargetPtr = "Z1" };
        Assert.True(StatusEffectBridge.TryApplyFromGrant(
            rt, grant, ev, grant.Overlay, BoardSnapshot.Empty, new FixedStatusRng(0), DateTimeOffset.UtcNow, skipped));
        Assert.Contains(skipped, s => s.Contains("unknown-statusId", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Status_icd_adds_icd_skipped_tag_not_resisted()
    {
        var rt = Runtime();
        var skipped = new List<string>();
        var grant = new EffectGrant
        {
            GrantId = "g1",
            EffectId = "fx.overlay_damage",
            OwnerKey = EffectOwnerKeys.Match,
            Overlay = new Dictionary<string, object?>
            {
                ["statusId"] = "wither",
                ["amount"] = -20L,
                ["statusIcdMs"] = 5000,
                ["periodMs"] = 1000,
                ["durationMs"] = 5000
            }
        };
        var ev = new EffectEventDto { ActorPtr = "P1", TargetPtr = "Z1" };
        var now = DateTimeOffset.UtcNow;
        StatusEffectBridge.TryApplyFromGrant(rt, grant, ev, grant.Overlay, BoardSnapshot.Empty, new FixedStatusRng(0), now, skipped);
        var grant2 = new EffectGrant
        {
            GrantId = "g2",
            EffectId = grant.EffectId,
            OwnerKey = grant.OwnerKey,
            Overlay = grant.Overlay
        };
        StatusEffectBridge.TryApplyFromGrant(rt, grant2, ev, grant.Overlay, BoardSnapshot.Empty, new FixedStatusRng(0), now.AddMilliseconds(100), skipped);
        Assert.Contains(skipped, s => s.EndsWith(":status-icd", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(rt.ResistedEvents);
    }

    // ---- C2 (completeness-audit.md): a stat overlay on a status that never declared ModifyStat -------

    [Fact]
    public void A_stat_overlay_on_a_status_without_ModifyStat_is_refused_not_silently_dropped()
    {
        // blight is a real, registered status — Contagion/PulseHp/Spread, never ModifyStat. This is
        // the exact shape the shipped blight-row.overlay.json carried before C2: an overlay that
        // parsed and validated at the allowlist stage and then did nothing at apply time.
        var rt = Runtime();
        var skipped = new List<string>();
        var grant = new EffectGrant
        {
            GrantId = "g1",
            EffectId = "fx.overlay_damage",
            OwnerKey = EffectOwnerKeys.Match,
            Overlay = new Dictionary<string, object?>
            {
                ["statusId"] = "blight",
                ["amount"] = -12L,
                ["stat"] = System.Text.Json.JsonDocument.Parse("""{"atk":{"more":-0.1}}""").RootElement,
            },
        };
        var ev = new EffectEventDto { TargetPtr = "Z1" };

        var handled = StatusEffectBridge.TryApplyFromGrant(
            rt, grant, ev, grant.Overlay, BoardSnapshot.Empty, new FixedStatusRng(0), DateTimeOffset.UtcNow, skipped);

        Assert.True(handled);
        Assert.Contains(skipped, s => s.EndsWith(":status-stat-overlay-without-ModifyStat", StringComparison.Ordinal));
    }

    [Fact]
    public void A_stat_overlay_on_a_status_that_declares_ModifyStat_applies_normally()
    {
        // expose declares ModifyStat (E17) — the same block on the right status must not be refused.
        var rt = Runtime();
        var skipped = new List<string>();
        var grant = new EffectGrant
        {
            GrantId = "g1",
            EffectId = "fx.overlay_damage",
            OwnerKey = EffectOwnerKeys.Match,
            Overlay = new Dictionary<string, object?>
            {
                ["statusId"] = "expose",
                ["amount"] = 0L,
                ["stat"] = System.Text.Json.JsonDocument.Parse("""{"defense":{"more":-0.15}}""").RootElement,
            },
        };
        var ev = new EffectEventDto { TargetPtr = "Z1" };

        StatusEffectBridge.TryApplyFromGrant(
            rt, grant, ev, grant.Overlay, BoardSnapshot.Empty, new FixedStatusRng(0), DateTimeOffset.UtcNow, skipped);

        Assert.DoesNotContain(skipped, s => s.Contains("status-stat-overlay-without-ModifyStat", StringComparison.Ordinal));
        var instance = Assert.Single(rt.ForHost("Z1"));
        Assert.Equal("expose", instance.StatusId);
        Assert.NotEmpty(instance.StatMods);
    }

    [Fact]
    public void A_status_without_a_stat_overlay_at_all_is_unaffected_by_the_C2_check()
    {
        // The check must gate on the OVERLAY carrying `stat`, not on what the status merely could
        // carry — blight applies fine as long as nothing asks it to modify a stat.
        var rt = Runtime();
        var skipped = new List<string>();
        var grant = new EffectGrant
        {
            GrantId = "g1",
            EffectId = "fx.overlay_damage",
            OwnerKey = EffectOwnerKeys.Match,
            Overlay = new Dictionary<string, object?> { ["statusId"] = "blight", ["amount"] = -12L },
        };
        var ev = new EffectEventDto { TargetPtr = "Z1" };

        StatusEffectBridge.TryApplyFromGrant(
            rt, grant, ev, grant.Overlay, BoardSnapshot.Empty, new FixedStatusRng(0), DateTimeOffset.UtcNow, skipped);

        Assert.DoesNotContain(skipped, s => s.Contains("status-stat-overlay-without-ModifyStat", StringComparison.Ordinal));
        Assert.NotEmpty(rt.ForHost("Z1"));
    }
}
