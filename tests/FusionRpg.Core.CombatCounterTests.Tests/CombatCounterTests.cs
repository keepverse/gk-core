using FusionRpg.Contracts;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Effects;
using Xunit;

namespace FusionRpg.Core.Tests;

public class CombatCounterTests
{
    static FoundationHarness GrantCounter(string scope)
    {
        var h = new FoundationHarness();
        h.SetBoard(new[]
        {
            new BoardEntitySnap { Ptr = "Z1", Side = "zombie", TypeId = 0, Col = 7, Row = 2 },
            new BoardEntitySnap { Ptr = "Z2", Side = "zombie", TypeId = 0, Col = 8, Row = 2 }
        });
        h.Grant(new EffectGrantDto
        {
            GrantId = "streak",
            EffectId = "fx.overlay_damage",
            OwnerKey = EffectOwnerKeys.Match,
            Overlay = new Dictionary<string, object?>
            {
                ["icd_ms"] = 0,
                ["delivery"] = new Dictionary<string, object?>
                {
                    ["mode"] = DeliveryModes.Counter,
                    ["everyHits"] = 5,
                    ["resetOnBurst"] = true,
                    ["counterScope"] = scope
                },
                ["burst"] = new Dictionary<string, object?>
                {
                    ["amount"] = -500L,
                    ["target"] = new Dictionary<string, object?> { ["mode"] = TargetModes.EventTarget },
                    ["delivery"] = new Dictionary<string, object?> { ["mode"] = DeliveryModes.Instant }
                }
            }
        });
        return h;
    }

    static EffectEventDto Hit(string target, string actor = "P1") => new()
    {
        Trigger = EffectTriggers.OnDamageDealt,
        ActorPtr = actor,
        TargetPtr = target,
        Side = "plant"
    };

    [Fact]
    public void Counter_target_bursts_on_fifth_hit_same_zombie()
    {
        var h = GrantCounter(CounterScopes.Target);
        for (var i = 0; i < 4; i++)
            Assert.DoesNotContain(h.OnEvent(Hit("Z1")).Actions, a => a.Action == EffectActions.ApplyResourceDelta);

        var plan = h.OnEvent(Hit("Z1"));
        var fa = Assert.Single(plan.Actions.Where(a => a.Action == EffectActions.ApplyResourceDelta));
        Assert.Equal(-500L, Convert.ToInt64(fa.Params["amount"]));
        Assert.Equal("z1", fa.Params["targetPtr"]?.ToString(), ignoreCase: true);
        Assert.NotEmpty(h.Bag.Status!.ForHost("Z1"));
        Assert.Contains(h.Bag.Status.ForHost("Z1"), i =>
            string.Equals(i.StatusId, "bond", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(h.Bag.Status.CounterSnapshot(), kv => kv.Value >= 5);
    }

    [Fact]
    public void Counter_actor_counts_across_targets()
    {
        var h = GrantCounter(CounterScopes.Actor);
        h.OnEvent(Hit("Z1"));
        h.OnEvent(Hit("Z2"));
        h.OnEvent(Hit("Z1"));
        h.OnEvent(Hit("Z2"));
        var plan = h.OnEvent(Hit("Z2"));
        var fa = Assert.Single(plan.Actions.Where(a => a.Action == EffectActions.ApplyResourceDelta));
        Assert.Equal(-500L, Convert.ToInt64(fa.Params["amount"]));
        Assert.Equal("z2", fa.Params["targetPtr"]?.ToString(), ignoreCase: true);
    }

    [Fact]
    public void Proc_depth_limit_skips_burst()
    {
        var h = GrantCounter(CounterScopes.Target);
        var policy = CombatPolicy.FromDefault();
        policy.ProcDepthLimit = 1;
        h.Bag.CombatPolicy = policy;
        for (var i = 0; i < 5; i++)
            h.OnEvent(Hit("Z1"));
        Assert.Contains(h.Bag.LastSkipped, s => s.Contains("proc-depth", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Counter_default_icd_blocks_burst_in_same_window()
    {
        var h = new FoundationHarness();
        h.SetBoard(new[]
        {
            new BoardEntitySnap { Ptr = "Z1", Side = "zombie", TypeId = 0, Col = 7, Row = 2 }
        });
        h.Grant(new EffectGrantDto
        {
            GrantId = "streak",
            EffectId = "fx.overlay_damage",
            OwnerKey = EffectOwnerKeys.Match,
            Overlay = new Dictionary<string, object?>
            {
                ["delivery"] = new Dictionary<string, object?>
                {
                    ["mode"] = DeliveryModes.Counter,
                    ["everyHits"] = 5,
                    ["resetOnBurst"] = true,
                    ["counterScope"] = CounterScopes.Target
                },
                ["burst"] = new Dictionary<string, object?>
                {
                    ["amount"] = -500L,
                    ["target"] = new Dictionary<string, object?> { ["mode"] = TargetModes.EventTarget },
                    ["delivery"] = new Dictionary<string, object?> { ["mode"] = DeliveryModes.Instant }
                }
            }
        });
        for (var i = 0; i < 5; i++)
            h.OnEvent(Hit("Z1"));
        Assert.DoesNotContain(h.Sink.Items, a => a.Action == EffectActions.ApplyResourceDelta);
    }
}
