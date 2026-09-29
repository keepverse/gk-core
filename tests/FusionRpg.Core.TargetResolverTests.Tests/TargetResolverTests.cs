using System.Text.Json;
using FusionRpg.Contracts;
using FusionRpg.Core.Combat;
using Xunit;

namespace FusionRpg.Core.Tests;

public class TargetResolverTests
{
    static BoardSnapshot LoadRow2()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "combat", "boards", "row2-zombies.json");
        var json = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json);
        var list = new List<BoardEntitySnap>();
        foreach (var el in doc.RootElement.GetProperty("entities").EnumerateArray())
        {
            list.Add(new BoardEntitySnap
            {
                Ptr = el.GetProperty("ptr").GetString() ?? "",
                Side = el.GetProperty("side").GetString() ?? "",
                TypeId = el.GetProperty("typeId").GetInt32(),
                Col = el.GetProperty("col").GetInt32(),
                Row = el.GetProperty("row").GetInt32(),
                MindControlled = el.TryGetProperty("mindControlled", out var mc) && mc.GetBoolean()
            });
        }

        return new BoardSnapshot(list);
    }

    static EffectEventDto HitZ1() => new()
    {
        Trigger = EffectTriggers.OnDamageDealt,
        ActorPtr = "P1",
        TargetPtr = "Z1",
        Side = "plant"
    };

    [Fact]
    public void EventTarget_returns_event_ptr()
    {
        var got = TargetResolver.Resolve(new TargetSpec { Mode = TargetModes.EventTarget }, LoadRow2(), HitZ1());
        Assert.Equal(new[] { "Z1" }, got);
    }

    [Fact]
    public void Actor_returns_actor_ptr()
    {
        var got = TargetResolver.Resolve(new TargetSpec { Mode = TargetModes.Actor }, LoadRow2(), HitZ1());
        Assert.Equal(new[] { "P1" }, got);
    }

    [Fact]
    public void Selected_returns_empty_in_core()
    {
        var got = TargetResolver.Resolve(new TargetSpec { Mode = TargetModes.Selected }, LoadRow2(), HitZ1());
        Assert.Empty(got);
    }

    [Fact]
    public void Single_uses_explicit_ptr()
    {
        var got = TargetResolver.Resolve(new TargetSpec { Mode = TargetModes.Single, Ptr = "Z4" }, LoadRow2(), HitZ1());
        Assert.Equal(new[] { "Z4" }, got);
    }

    [Fact]
    public void Area_row_hits_zombies_in_lane_with_filters()
    {
        var spec = new TargetSpec
        {
            Mode = TargetModes.Area,
            Shape = AreaShapes.Row,
            Anchor = "EventTarget",
            Filters = new Dictionary<string, object?>
            {
                ["side"] = "zombie",
                ["excludeMindControlled"] = true
            },
            MaxTargets = 8
        };
        var got = TargetResolver.Resolve(spec, LoadRow2(), HitZ1());
        Assert.Equal(new[] { "Z1", "Z2", "Z3" }, got);
    }

    [Fact]
    public void Area_column_uses_anchor_col()
    {
        var spec = new TargetSpec
        {
            Mode = TargetModes.Area,
            Shape = AreaShapes.Column,
            Anchor = new Dictionary<string, object?> { ["col"] = 7, ["row"] = 2 },
            Filters = new Dictionary<string, object?> { ["side"] = "zombie" }
        };
        var got = TargetResolver.Resolve(spec, LoadRow2(), HitZ1());
        Assert.Equal(new[] { "Z1", "Z4" }, got);
    }

    [Fact]
    public void Area_square_default_size_from_policy()
    {
        var policy = CombatPolicy.FromDefault();
        policy.AreaDefaultSquareSize = 3;
        var spec = new TargetSpec
        {
            Mode = TargetModes.Area,
            Shape = AreaShapes.Square,
            Anchor = new Dictionary<string, object?> { ["col"] = 7, ["row"] = 2 },
            Filters = new Dictionary<string, object?> { ["side"] = "zombie" }
        };
        var got = TargetResolver.Resolve(spec, LoadRow2(), HitZ1(), policy);
        Assert.Contains("Z1", got);
        Assert.Contains("Z2", got);
        Assert.Contains("Z4", got);
        Assert.DoesNotContain("Z3", got);
    }

    [Fact]
    public void All_caps_maxTargets_stable_ptr_order()
    {
        var spec = new TargetSpec
        {
            Mode = TargetModes.All,
            Filters = new Dictionary<string, object?> { ["side"] = "zombie" },
            MaxTargets = 2
        };
        var got = TargetResolver.Resolve(spec, LoadRow2(), HitZ1());
        Assert.Equal(new[] { "Z1", "Z2" }, got);
    }

    [Fact]
    public void Random_is_deterministic_with_seed()
    {
        var spec = new TargetSpec
        {
            Mode = TargetModes.Random,
            Count = 2,
            Filters = new Dictionary<string, object?>
            {
                ["side"] = "zombie",
                ["excludeMindControlled"] = true
            }
        };
        var a = TargetResolver.Resolve(spec, LoadRow2(), HitZ1(), rng: new SeededCombatRng(42));
        var b = TargetResolver.Resolve(spec, LoadRow2(), HitZ1(), rng: new SeededCombatRng(42));
        Assert.Equal(a, b);
        Assert.Equal(2, a.Count);
    }

    [Fact]
    public void TypeIdIn_filters_pool()
    {
        var spec = new TargetSpec
        {
            Mode = TargetModes.All,
            Filters = new Dictionary<string, object?>
            {
                ["side"] = "zombie",
                ["typeIdIn"] = new object[] { 1 }
            }
        };
        var got = TargetResolver.Resolve(spec, LoadRow2(), HitZ1());
        Assert.Equal(new[] { "Z3" }, got);
    }

    [Fact]
    public void Packet_builder_defaults_event_target_instant()
    {
        var overlay = new Dictionary<string, object?> { ["amount"] = -100L };
        var p = DamagePacketBuilder.FromOverlay(overlay, HitZ1(), grantId: "g1");
        Assert.Equal(TargetModes.EventTarget, p.Target.Mode);
        Assert.Equal(DeliveryModes.Instant, p.Delivery.Mode);
        Assert.Equal(-100, p.SignedAmount);
        Assert.Equal("g1", p.SourceGrantId);
    }

    [Fact]
    public void Packet_builder_parses_area_and_counter_burst()
    {
        var overlay = new Dictionary<string, object?>
        {
            ["amount"] = -50L,
            ["target"] = new Dictionary<string, object?>
            {
                ["mode"] = TargetModes.Area,
                ["shape"] = AreaShapes.Row,
                ["filters"] = new Dictionary<string, object?> { ["side"] = "zombie" }
            },
            ["delivery"] = new Dictionary<string, object?>
            {
                ["mode"] = DeliveryModes.Counter,
                ["everyHits"] = 5,
                ["counterScope"] = CounterScopes.Target
            },
            ["burst"] = new Dictionary<string, object?>
            {
                ["amount"] = -500L,
                ["target"] = new Dictionary<string, object?> { ["mode"] = TargetModes.EventTarget },
                ["delivery"] = new Dictionary<string, object?> { ["mode"] = DeliveryModes.Instant }
            }
        };
        var p = DamagePacketBuilder.FromOverlay(overlay, HitZ1(), grantId: "c1");
        Assert.Equal(TargetModes.Area, p.Target.Mode);
        Assert.Equal(DeliveryModes.Counter, p.Delivery.Mode);
        Assert.NotNull(p.Burst);
        Assert.Equal(-500, p.Burst!.SignedAmount);
        Assert.Equal(1, p.Burst.ChainDepth);
    }

    [Fact]
    public void Multi_uses_count_then_caps_maxTargets()
    {
        var board = TenZombies();
        var multi = TargetResolver.Resolve(
            new TargetSpec
            {
                Mode = TargetModes.Multi,
                Count = 3,
                MaxTargets = 8,
                Filters = new Dictionary<string, object?> { ["side"] = "zombie" }
            },
            board,
            HitZ1());
        Assert.Equal(3, multi.Count);

        var all = TargetResolver.Resolve(
            new TargetSpec
            {
                Mode = TargetModes.All,
                MaxTargets = 8,
                Filters = new Dictionary<string, object?> { ["side"] = "zombie" }
            },
            board,
            HitZ1());
        Assert.Equal(8, all.Count);
    }

    [Fact]
    public void Area_omitted_shape_is_empty()
    {
        var spec = new TargetSpec
        {
            Mode = TargetModes.Area,
            Anchor = new Dictionary<string, object?> { ["col"] = 7, ["row"] = 2 },
            Filters = new Dictionary<string, object?> { ["side"] = "zombie" }
        };
        Assert.Empty(TargetResolver.Resolve(spec, LoadRow2(), HitZ1()));
    }

    [Fact]
    public void Area_anchor_missing_keys_is_empty()
    {
        var spec = new TargetSpec
        {
            Mode = TargetModes.Area,
            Shape = AreaShapes.Row,
            Anchor = new Dictionary<string, object?> { ["col"] = 7 },
            Filters = new Dictionary<string, object?> { ["side"] = "zombie" }
        };
        Assert.Empty(TargetResolver.Resolve(spec, LoadRow2(), HitZ1()));
    }

    [Fact]
    public void Area_rectangle_corner_vs_center()
    {
        var specCorner = new TargetSpec
        {
            Mode = TargetModes.Area,
            Shape = AreaShapes.Rectangle,
            Width = 2,
            Height = 1,
            AnchorOrigin = AnchorOrigins.Corner,
            Anchor = new Dictionary<string, object?> { ["col"] = 7, ["row"] = 2 },
            Filters = new Dictionary<string, object?> { ["side"] = "zombie" }
        };
        var corner = TargetResolver.Resolve(specCorner, LoadRow2(), HitZ1());
        Assert.Equal(new[] { "Z1", "Z2" }, corner);

        var specCenter = new TargetSpec
        {
            Mode = TargetModes.Area,
            Shape = AreaShapes.Rectangle,
            Width = 3,
            Height = 1,
            AnchorOrigin = AnchorOrigins.Center,
            Anchor = new Dictionary<string, object?> { ["col"] = 7, ["row"] = 2 },
            Filters = new Dictionary<string, object?> { ["side"] = "zombie" }
        };
        var center = TargetResolver.Resolve(specCenter, LoadRow2(), HitZ1());
        Assert.Contains("Z1", center);
        Assert.DoesNotContain("Z3", center);
    }

    [Fact]
    public void Default_exclude_mind_controlled_on_zombie_side()
    {
        var spec = new TargetSpec
        {
            Mode = TargetModes.Area,
            Shape = AreaShapes.Row,
            Anchor = "EventTarget",
            Filters = new Dictionary<string, object?> { ["side"] = "zombie" }
        };
        var got = TargetResolver.Resolve(spec, LoadRow2(), HitZ1());
        Assert.Equal(new[] { "Z1", "Z2", "Z3" }, got);
        Assert.DoesNotContain("Z5", got);
    }

    [Fact]
    public void FindPtr_normalizes_0x_and_entity_prefix()
    {
        var snap = LoadRow2();
        Assert.NotNull(snap.FindPtr("0xZ1"));
        Assert.NotNull(snap.FindPtr("entity:Z1"));
        Assert.Equal("Z1", snap.FindPtr("z1")!.Ptr);
    }

    static BoardSnapshot TenZombies()
    {
        var list = new List<BoardEntitySnap>
        {
            new() { Ptr = "P1", Side = "plant", TypeId = 0, Col = 2, Row = 2 }
        };
        for (var i = 0; i < 10; i++)
            list.Add(new BoardEntitySnap
            {
                Ptr = "Z" + i,
                Side = "zombie",
                TypeId = 0,
                Col = i,
                Row = 2
            });
        return new BoardSnapshot(list);
    }
}
