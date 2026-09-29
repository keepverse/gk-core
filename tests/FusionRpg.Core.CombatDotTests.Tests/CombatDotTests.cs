using FusionRpg.Contracts;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Effects;
using Xunit;

namespace FusionRpg.Core.Tests;

public class CombatDotTests
{
    static FoundationHarness GrantDot(int tickBudget = 1)
    {
        var h = new FoundationHarness();
        h.SetBoard(new[]
        {
            new BoardEntitySnap { Ptr = "P1", Side = "plant", TypeId = 0, Col = 2, Row = 2 },
            new BoardEntitySnap { Ptr = "Z1", Side = "zombie", TypeId = 0, Col = 7, Row = 2 }
        });
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
                    ["tickBudget"] = tickBudget
                }
            }
        });
        return h;
    }

    static EffectEventDto Hit() => new()
    {
        Trigger = EffectTriggers.OnDamageDealt,
        ActorPtr = "P1",
        TargetPtr = "Z1",
        Side = "plant"
    };

    [Fact]
    public void Dot_five_ticks_over_five_seconds()
    {
        var h = GrantDot();
        var arm = h.OnEvent(Hit());
        Assert.DoesNotContain(arm.Actions, a => a.Action == EffectActions.ApplyResourceDelta);

        var amounts = new List<long>();
        for (var i = 0; i < 5; i++)
        {
            h.AdvanceTime(1000);
            var fa = h.Sink.Items.Where(a => a.Action == EffectActions.ApplyResourceDelta).ToList();
            Assert.Equal(i + 1, fa.Count);
            amounts.Add(Convert.ToInt64(fa[i].Params["amount"]));
            Assert.Equal("z1", fa[i].Params["targetPtr"]?.ToString(), ignoreCase: true);
        }

        Assert.All(amounts, a => Assert.Equal(-20L, a));
        Assert.NotEmpty(h.Bag.Status!.ForHost("Z1"));
    }

    [Fact]
    public void Dot_tick_budget_caps_catch_up()
    {
        var h = GrantDot(tickBudget: 1);
        h.OnEvent(Hit());
        h.AdvanceTime(5000);
        var fa = h.Sink.Items.Where(a => a.Action == EffectActions.ApplyResourceDelta).ToList();
        Assert.Single(fa);
        Assert.Equal(-20L, Convert.ToInt64(fa[0].Params["amount"]));
        Assert.NotEmpty(h.Bag.Status!.ForHost("Z1"));
    }

    [Fact]
    public void Effect_timer_maps_to_on_timer()
    {
        var ev = EffectEventAdapterCore.TryMap(
            "effect.timer",
            new Dictionary<string, object> { ["targetPtr"] = "Z1" },
            3,
            "sim-match");
        Assert.NotNull(ev);
        Assert.Equal(EffectTriggers.OnTimer, ev!.Trigger);
        Assert.Equal("Z1", ev.TargetPtr);
    }

    [Fact]
    public void Combat_dot_scenario_passes()
    {
        var path = Path.Combine(FindFixtures(), "effects", "scenarios", "combat-dot.json");
        var result = EffectScenarioRunner.RunFile(path, FindFixtures());
        Assert.True(result.Ok, result.Error);
    }

    [Fact]
    public void Status_wither_apply_scenario_passes()
    {
        var root = FindFixtures();
        var path = Path.Combine(root, "effects", "scenarios", "status-wither-apply.json");
        var result = EffectScenarioRunner.RunFile(path, root);
        Assert.True(result.Ok, result.Error);
    }

    [Fact]
    public void Status_blight_spread_scenario_passes()
    {
        var root = FindFixtures();
        var path = Path.Combine(root, "effects", "scenarios", "status-blight-spread.json");
        var result = EffectScenarioRunner.RunFile(path, root);
        Assert.True(result.Ok, result.Error);
    }

    [Fact]
    public void Area_overtime_fans_out_each_tick()
    {
        var h = new FoundationHarness();
        h.SetBoard(new[]
        {
            new BoardEntitySnap { Ptr = "P1", Side = "plant", TypeId = 0, Col = 2, Row = 2 },
            new BoardEntitySnap { Ptr = "Z1", Side = "zombie", TypeId = 0, Col = 7, Row = 2 },
            new BoardEntitySnap { Ptr = "Z2", Side = "zombie", TypeId = 0, Col = 8, Row = 2 }
        });
        h.Grant(new EffectGrantDto
        {
            GrantId = "dot-area",
            EffectId = "fx.overlay_damage",
            OwnerKey = EffectOwnerKeys.Match,
            Overlay = new Dictionary<string, object?>
            {
                ["amount"] = -20L,
                ["icd_ms"] = 0,
                ["target"] = new Dictionary<string, object?>
                {
                    ["mode"] = TargetModes.Area,
                    ["shape"] = AreaShapes.Row,
                    ["anchor"] = "EventTarget",
                    ["filters"] = new Dictionary<string, object?> { ["side"] = "zombie" }
                },
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
        h.AdvanceTime(1000);
        var fa = h.Sink.Items.Where(a => a.Action == EffectActions.ApplyResourceDelta).ToList();
        Assert.Equal(2, fa.Count);
        Assert.NotEmpty(h.Bag.Status!.ForHost("Z1"));
        Assert.NotEmpty(h.Bag.Status.ForHost("Z2"));
    }

    [Fact]
    public void Status_wither_area_scenario_passes()
    {
        var root = FindFixtures();
        var path = Path.Combine(root, "effects", "scenarios", "status-wither-area.json");
        var result = EffectScenarioRunner.RunFile(path, root);
        Assert.True(result.Ok, result.Error);
    }

    [Fact]
    public void Dot_without_target_is_skipped()
    {
        var h = GrantDot();
        var plan = h.OnEvent(new EffectEventDto
        {
            Trigger = EffectTriggers.OnDamageDealt,
            ActorPtr = "P1",
            Side = "plant"
        });
        Assert.Contains(plan.Skipped, s => s.Contains("status-no-target", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(h.Bag.Status!.AllInstances());
    }

    static string FindFixtures()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            var candidate = Path.Combine(dir, "fixtures");
            if (Directory.Exists(candidate)) return candidate;
            var up = Path.GetFullPath(Path.Combine(dir, "..", "..", "..", "..", "fixtures"));
            if (Directory.Exists(up)) return up;
            dir = Path.GetFullPath(Path.Combine(dir, ".."));
        }

        throw new DirectoryNotFoundException("fixtures");
    }
}
