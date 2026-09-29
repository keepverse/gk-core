using FusionRpg.Contracts;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Effects;
using Xunit;

namespace FusionRpg.Core.Tests;

public class CombatFanoutTests
{
    [Fact]
    public void Area_row_enqueues_one_fa10_per_zombie()
    {
        var h = new FoundationHarness();
        h.SetBoard(new[]
        {
            new BoardEntitySnap { Ptr = "P1", Side = "plant", TypeId = 0, Col = 2, Row = 2 },
            new BoardEntitySnap { Ptr = "Z1", Side = "zombie", TypeId = 0, Col = 7, Row = 2 },
            new BoardEntitySnap { Ptr = "Z2", Side = "zombie", TypeId = 0, Col = 8, Row = 2 },
            new BoardEntitySnap { Ptr = "Z3", Side = "zombie", TypeId = 0, Col = 9, Row = 2 }
        });
        h.Grant(new EffectGrantDto
        {
            GrantId = "area-row",
            EffectId = "fx.overlay_damage",
            OwnerKey = EffectOwnerKeys.Match,
            Overlay = new Dictionary<string, object?>
            {
                ["amount"] = -50L,
                ["icd_ms"] = 0,
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

        var fa10 = plan.Actions.Where(a => a.Action == EffectActions.ApplyResourceDelta).ToList();
        Assert.Equal(3, fa10.Count);
        Assert.Equal(new[] { "z1", "z2", "z3" }, fa10.Select(a => a.Params["targetPtr"]?.ToString()).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToArray());
        Assert.All(fa10, a => Assert.Equal(-50L, Convert.ToInt64(a.Params["amount"])));
    }

    [Fact]
    public void Heal_positive_amount_is_heal_fx()
    {
        var h = new FoundationHarness();
        h.SetBoard(new[]
        {
            new BoardEntitySnap { Ptr = "Z1", Side = "zombie", TypeId = 0, Col = 7, Row = 2 }
        });
        h.Grant(new EffectGrantDto
        {
            GrantId = "heal",
            EffectId = "fx.overlay_damage",
            OwnerKey = EffectOwnerKeys.Match,
            Overlay = new Dictionary<string, object?>
            {
                ["amount"] = 30L,
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
        var fa = Assert.Single(plan.Actions, a => a.Action == EffectActions.ApplyResourceDelta);
        Assert.Equal(30L, Convert.ToInt64(fa.Params["amount"]));
        var fx = Assert.Single(h.Fx.Items);
        Assert.Equal(DamageFxTag.Heal, fx.Tag);
    }

    [Fact]
    public void Selected_empty_until_bag_ptr_set()
    {
        var h = new FoundationHarness();
        h.SetBoard(new[]
        {
            new BoardEntitySnap { Ptr = "Z1", Side = "zombie", TypeId = 0, Col = 7, Row = 2 }
        });
        h.Grant(new EffectGrantDto
        {
            GrantId = "sel",
            EffectId = "fx.overlay_damage",
            OwnerKey = EffectOwnerKeys.Match,
            Overlay = new Dictionary<string, object?>
            {
                ["amount"] = -10L,
                ["icd_ms"] = 0,
                ["target"] = new Dictionary<string, object?> { ["mode"] = TargetModes.Selected },
                ["delivery"] = new Dictionary<string, object?> { ["mode"] = DeliveryModes.Instant }
            }
        });
        var empty = h.OnEvent(new EffectEventDto
        {
            Trigger = EffectTriggers.OnDamageDealt,
            ActorPtr = "P1",
            TargetPtr = "Z2",
            Side = "plant"
        });
        Assert.DoesNotContain(empty.Actions, a => a.Action == EffectActions.ApplyResourceDelta);

        h.Bag.SelectedPtr = "Z1";
        var plan = h.OnEvent(new EffectEventDto
        {
            Trigger = EffectTriggers.OnDamageDealt,
            ActorPtr = "P1",
            TargetPtr = "Z2",
            Side = "plant"
        });
        var fa = Assert.Single(plan.Actions, a => a.Action == EffectActions.ApplyResourceDelta);
        Assert.Equal("z1", fa.Params["targetPtr"]?.ToString(), ignoreCase: true);
    }

    [Fact]
    public void Combat_area_row_scenario_passes()
    {
        var path = Path.Combine(FindFixtures(), "effects", "scenarios", "combat-area-row.json");
        var result = EffectScenarioRunner.RunFile(path, FindFixtures());
        Assert.True(result.Ok, result.Error);
    }

    [Fact]
    public void Combat_heal_and_counter_scenarios_pass()
    {
        var root = FindFixtures();
        foreach (var id in new[] { "combat-heal", "combat-counter-target", "combat-counter-actor" })
        {
            var path = Path.Combine(root, "effects", "scenarios", id + ".json");
            var result = EffectScenarioRunner.RunFile(path, root);
            Assert.True(result.Ok, id + ": " + result.Error);
        }
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
