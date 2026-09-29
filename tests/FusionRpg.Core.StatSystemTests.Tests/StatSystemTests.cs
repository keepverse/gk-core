using FusionRpg.Contracts;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Plugins;
using Xunit;

namespace FusionRpg.Core.Tests;

public class StatSystemTests
{
    [Fact]
    public void Phased_more_doubles_hp_like_legacy_percent()
    {
        var sys = StatSystemBootstrap.CreateDefault();
        var y0 = new EntityBaseline { Hp = 300, MaxHp = 300, Atk = 20 };
        var cfg = new StatsConfig { ApplyStats = true, Plants = { HpPercent = 2f } };
        var ctx = sys.Contexts.ForPlant("P1", y0, cheatScale: cfg);
        var y = sys.Resolve(ctx);
        Assert.Equal(600, y.Hp);
        Assert.Equal(600, y.MaxHp);
    }

    [Fact]
    public void Two_plugins_stack_increased_then_more()
    {
        var sys = new StatSystem();
        sys.Plugins.Register(new ClassStatPlugin());
        sys.Plugins.Register(new TestIncreasedPlugin());
        sys.Plugins.Register(new CheatScaleStatPlugin());

        var y0 = new EntityBaseline { Hp = 100, MaxHp = 100, Atk = 10 };
        var cfg = new StatsConfig { ApplyStats = true, Plants = { HpPercent = 2f } };
        var ctx = sys.Contexts.ForPlant("P1", y0, cheatScale: cfg);
        var y = sys.Resolve(ctx);
        Assert.Equal(300, y.Hp);
    }

    [Fact]
    public void Session_upsert_withdraw_changes_y_without_touching_y0()
    {
        var sys = new StatSystem();
        var y0 = new EntityBaseline { Hp = 100, MaxHp = 100, Atk = 10 };
        sys.CaptureOrGet("P1", () => y0);
        var ctx = sys.Contexts.ForPlant("P1", y0);

        sys.Upsert(sys.Modifiers.Flat("session", "rpg.item", "sword", StatChannels.Hp, 50));
        sys.Upsert(sys.Modifiers.Flat("session", "rpg.item", "sword", StatChannels.MaxHp, 50));
        Assert.Equal(150, sys.Resolve(ctx).Hp);

        sys.WithdrawSource("rpg.item", "sword");
        Assert.Equal(100, sys.Resolve(ctx).Hp);
        Assert.True(sys.TryGetBaseline("P1", out var still));
        Assert.Equal(100, still.Hp);
    }

    [Fact]
    public void Invalidate_sets_dirty_and_consume_clears()
    {
        var sys = new StatSystem();
        Assert.False(sys.IsDirty);
        var fired = 0;
        sys.Invalidated += _ => fired++;
        sys.Invalidate(new StatScope { EntityKey = "P1" });
        Assert.True(sys.IsDirty);
        Assert.Equal(1, fired);
        Assert.True(sys.ConsumeDirty(out var scope));
        Assert.Equal("P1", scope?.EntityKey);
        Assert.False(sys.IsDirty);
        Assert.False(sys.ConsumeDirty(out _));
    }

    [Fact]
    public void Absolute_override_wins()
    {
        var sys = StatSystemBootstrap.CreateDefault();
        var y0 = new EntityBaseline { Hp = 300, MaxHp = 300, Atk = 20 };
        var cfg = new StatsConfig { ApplyStats = true, Plants = { HpPercent = 2f } };
        var abs = new Dictionary<string, int> { [StatChannels.Hp] = 999 };
        var ctx = sys.Contexts.ForPlant("P1", y0, cheatScale: cfg, cheatAbsolute: abs);
        var y = sys.Resolve(ctx);
        Assert.Equal(999, y.Hp);
        Assert.Equal(600, y.MaxHp);
    }

    /// <summary>
    /// Mirrors EntityApply shouldWrite when A-APPLY is off but Tab B absolute is set:
    /// cheat.scale skips, cheat.absolute Override still resolves.
    /// </summary>
    [Fact]
    public void Absolute_override_when_applyStats_false()
    {
        var sys = StatSystemBootstrap.CreateDefault();
        var y0 = new EntityBaseline { Hp = 300, MaxHp = 300, Atk = 20 };
        var cfg = new StatsConfig { ApplyStats = false, Plants = { HpPercent = 2f, AttackPercent = 2f } };
        var abs = new Dictionary<string, int>
        {
            [StatChannels.Hp] = 9999,
            [StatChannels.MaxHp] = 9999,
            [StatChannels.Atk] = 50
        };
        var ctx = sys.Contexts.ForPlant("P1", y0, cheatScale: cfg, cheatAbsolute: abs, applyStats: false);
        var y = sys.Resolve(ctx);
        Assert.Equal(9999, y.Hp);
        Assert.Equal(9999, y.MaxHp);
        Assert.Equal(50, y.Atk);
    }

    [Fact]
    public void ApplyStats_false_without_absolute_keeps_baseline()
    {
        var sys = StatSystemBootstrap.CreateDefault();
        var y0 = new EntityBaseline { Hp = 300, MaxHp = 300, Atk = 20 };
        var cfg = new StatsConfig { ApplyStats = false, Plants = { HpPercent = 2f } };
        var ctx = sys.Contexts.ForPlant("P1", y0, cheatScale: cfg, applyStats: false);
        var y = sys.Resolve(ctx);
        Assert.Equal(300, y.Hp);
        Assert.Equal(300, y.MaxHp);
        Assert.Equal(20, y.Atk);
    }

    [Fact]
    public void Identity_scale_percent_one_leaves_baseline_zero_clamps_to_one()
    {
        var sys = StatSystemBootstrap.CreateDefault();
        var y0 = new EntityBaseline { Hp = 300, MaxHp = 300, Atk = 20 };
        var idCfg = new StatsConfig { ApplyStats = true, Plants = { HpPercent = 1f } };
        var idY = sys.Resolve(sys.Contexts.ForPlant("P1", y0, cheatScale: idCfg, applyStats: true));
        Assert.Equal(300, idY.Hp);

        var zeroCfg = new StatsConfig { ApplyStats = true, Plants = { HpPercent = 0f } };
        var zeroY = sys.Resolve(sys.Contexts.ForPlant("P2", y0, cheatScale: zeroCfg, applyStats: true));
        Assert.Equal(1, zeroY.Hp); // compose clamp — why FE must not send unset as 0
    }

    [Fact]
    public void Empty_absolute_bag_with_identity_scales_keeps_baseline()
    {
        var sys = StatSystemBootstrap.CreateDefault();
        var y0 = new EntityBaseline { Hp = 500, MaxHp = 500, Atk = 10 };
        var cfg = new StatsConfig { ApplyStats = true }; // all percents default 1
        var ctx = sys.Contexts.ForPlant("P1", y0, cheatScale: cfg, cheatAbsolute: new Dictionary<string, int>(), applyStats: true);
        var y = sys.Resolve(ctx);
        Assert.Equal(500, y.Hp);
        Assert.Equal(10, y.Atk);
    }

    [Fact]
    public void Defense_more_and_flat_compose()
    {
        var sys = StatSystemBootstrap.CreateDefault();
        var y0 = new EntityBaseline { Hp = 100, MaxHp = 100, Atk = 10 };
        var cfg = new StatsConfig
        {
            ApplyStats = true,
            Plants = { DefensePercent = 2f, DefenseFlat = 10 }
        };
        var y = sys.Resolve(sys.Contexts.ForPlant("P1", y0, cheatScale: cfg));
        Assert.Equal(2f, y.DefensePercent);
        Assert.Equal(10, y.DefenseFlat);
    }

    [Fact]
    public void Defense_override_clears_flat()
    {
        var sys = new StatSystem();
        var y0 = new EntityBaseline { Hp = 100, MaxHp = 100, Atk = 10 };
        sys.Upsert(sys.Modifiers.Flat("s", "t", "a", StatChannels.Defense, 10));
        sys.Upsert(sys.Modifiers.More("s", "t", "a", StatChannels.Defense, 1));
        sys.Upsert(sys.Modifiers.Override("s", "t", "a", StatChannels.Defense, 3, priority: 100));
        var y = sys.Resolve(sys.Contexts.ForPlant("P1", y0));
        Assert.Equal(3f, y.DefensePercent);
        Assert.Equal(0, y.DefenseFlat);
    }

    [Fact]
    public void CaptureOrGet_keeps_first_baseline()
    {
        var sys = new StatSystem();
        var a = sys.CaptureOrGet("P1", () => new EntityBaseline { Hp = 100, MaxHp = 100, Atk = 1 });
        var b = sys.CaptureOrGet("P1", () => new EntityBaseline { Hp = 999, MaxHp = 999, Atk = 9 });
        Assert.Equal(100, a.Hp);
        Assert.Equal(100, b.Hp);
    }

    [Fact]
    public void Resolve_twice_from_same_y0_is_idempotent()
    {
        var sys = StatSystemBootstrap.CreateDefault();
        var y0 = new EntityBaseline { Hp = 300, MaxHp = 300, Atk = 20 };
        var cfg = new StatsConfig { ApplyStats = true, Plants = { HpPercent = 2f, HpFlat = 20 } };
        var ctx = sys.Contexts.ForPlant("P1", y0, cheatScale: cfg);
        var a = sys.Resolve(ctx);
        var b = sys.Resolve(ctx);
        Assert.Equal(a.Hp, b.Hp);
        Assert.Equal(a.MaxHp, b.MaxHp);
        Assert.Equal(a.Atk, b.Atk);
        // PoE order: (300+20)*2 = 640
        Assert.Equal(640, a.Hp);
    }

    [Fact]
    public void Plugin_order_bands_class_before_cheat()
    {
        var sys = StatSystemBootstrap.CreateDefault();
        var ordered = sys.Plugins.Ordered().Select(p => p.PluginId).ToList();
        Assert.Equal(ClassStatPlugin.Id, ordered[0]);
        Assert.Contains(CheatScaleStatPlugin.Id, ordered);
        Assert.True(ordered.IndexOf(ClassStatPlugin.Id) < ordered.IndexOf(CheatScaleStatPlugin.Id));
        Assert.True(ordered.IndexOf(CheatScaleStatPlugin.Id) < ordered.IndexOf(CheatAbsoluteStatPlugin.Id));
    }

    [Fact]
    public void Contribute_without_withdraw_doubles_flat()
    {
        var bag = new ModifierBag();
        var f = new StatModifierFactory();
        // Bad plugin: Upsert same key twice is idempotent by key — use two SourceIds to show stacking
        // Anti-pattern: Contribute called twice without WithdrawPlugin using distinct keys each call
        var bad = new NonIdempotentPlugin();
        var ctx = new StatContext
        {
            Side = StatSide.Plant,
            EntityKey = "P1",
            Baseline = new EntityBaseline { Hp = 100, MaxHp = 100, Atk = 1 }
        };
        bad.Contribute(ctx, bag);
        bad.Contribute(ctx, bag);
        var sys = new StatSystem();
        // Merge bag via session
        foreach (var m in bag.All) sys.Upsert(m);
        // Clear dirty noise
        sys.ConsumeDirty(out _);
        var y = sys.Composer.Compose(ctx.Baseline, bag, applyStats: true);
        Assert.Equal(120, y.Hp); // +10 twice with distinct keys
    }

    [Fact]
    public void ScaleCurrentHp_preserves_ratio()
    {
        Assert.Equal(50, StatSystem.ScaleCurrentHp(50, 100, 100));
        Assert.Equal(100, StatSystem.ScaleCurrentHp(50, 100, 200));
    }

    [Fact]
    public void CurrentHpForWrite_pushScales_keeps_live_not_composed()
    {
        Assert.Equal(3500, StatSystem.CurrentHpForWrite("cheat.pushScales", 3500, 5000, 5000, 5000));
        Assert.False(StatSystem.PreserveLiveCurrentHp("cheat.absolute"));
        Assert.False(StatSystem.PreserveLiveCurrentHp("debug.spawn"));
        Assert.False(StatSystem.PreserveLiveCurrentHp("start"));
        Assert.False(StatSystem.PreserveLiveCurrentHp("recapture:start"));
        Assert.False(StatSystem.PreserveLiveCurrentHp(null));
        Assert.False(StatSystem.PreserveLiveCurrentHp(""));
        Assert.True(StatSystem.PreserveLiveCurrentHp("cheat.pushScales"));
        Assert.True(StatSystem.PreserveLiveCurrentHp("cheat.reapply"));
        Assert.False(StatSystem.PreserveLiveCurrentHp("cheat.reapply.absolute"));
    }

    [Fact]
    public void CurrentHpForWrite_pushScales_scales_when_max_doubles()
    {
        Assert.Equal(7000, StatSystem.CurrentHpForWrite("cheat.pushScales", 3500, 5000, 10000, 10000));
        Assert.Equal(7000, StatSystem.CurrentHpForWrite("cheat.reapply", 3500, 5000, 9999, 10000));
    }

    [Fact]
    public void CurrentHpForWrite_spawn_and_absolute_use_composed_hp()
    {
        Assert.Equal(5000, StatSystem.CurrentHpForWrite("debug.spawn", 3500, 5000, 5000, 5000));
        Assert.Equal(5000, StatSystem.CurrentHpForWrite("cheat.absolute", 3500, 5000, 5000, 5000));
        Assert.Equal(5000, StatSystem.CurrentHpForWrite(false, 3500, 5000, 5000, 5000));
        Assert.Equal(1, StatSystem.CurrentHpForWrite("debug.spawn", 3500, 5000, 0, 5000));
    }

    [Fact]
    public void CurrentHpForWrite_preserve_clamps_to_new_max_and_floor()
    {
        Assert.Equal(5000, StatSystem.CurrentHpForWrite(true, 6000, 5000, 1000, 5000));
        Assert.Equal(1, StatSystem.CurrentHpForWrite(true, 0, 5000, 5000, 5000));
        Assert.Equal(1, StatSystem.CurrentHpForWrite(true, 3500, 5000, 5000, 0));
    }

    [Fact]
    public void Session_ApplyOwnerKey_filters_resolve_by_entity_and_type()
    {
        var sys = new StatSystem();
        var y0 = new EntityBaseline { Hp = 100, MaxHp = 100, Atk = 10 };
        var f = sys.Modifiers;

        sys.Upsert(f.Flat("t", "effect", "g-match", StatChannels.Atk, 5, applyOwnerKey: "match"));
        sys.Upsert(f.Flat("t", "effect", "g-ent", StatChannels.Atk, 50, applyOwnerKey: "entity:AAA"));
        sys.Upsert(f.Flat("t", "effect", "g-pea", StatChannels.Atk, 7, applyOwnerKey: "plant:0"));

        var elite = sys.Resolve(sys.Contexts.ForPlant("AAA", y0, typeId: 0));
        Assert.Equal(10 + 5 + 50 + 7, elite.Atk);

        var sibling = sys.Resolve(sys.Contexts.ForPlant("BBB", y0, typeId: 0));
        Assert.Equal(10 + 5 + 7, sibling.Atk);

        var wall = sys.Resolve(sys.Contexts.ForPlant("CCC", y0, typeId: 3));
        Assert.Equal(10 + 5, wall.Atk);

        var z = sys.Resolve(sys.Contexts.ForZombie("ZZ", y0, typeId: 0));
        Assert.Equal(10 + 5, z.Atk);
    }

    [Fact]
    public void StatApplyScope_normalize_and_match_wide()
    {
        Assert.Equal("match", StatApplyScope.Normalize(null));
        Assert.Equal("match", StatApplyScope.Normalize(""));
        Assert.Equal("match", StatApplyScope.Normalize("  "));
        Assert.Equal("plant:0", StatApplyScope.Normalize("Plant:0"));
        Assert.Equal("entity:aaa", StatApplyScope.Normalize("entity:0xAAA"));
        Assert.Equal("entity:aaa", StatApplyScope.Normalize("ENTITY:AAA"));
        Assert.True(StatApplyScope.IsMatchWide("match"));
        Assert.True(StatApplyScope.IsMatchWide("player:1"));
        Assert.False(StatApplyScope.IsMatchWide("entity:ABC"));
        Assert.True(StatApplyScope.Matches("entity:AbC", StatSide.Plant, 0, "abc"));
        Assert.True(StatApplyScope.Matches("entity:0xAbC", StatSide.Plant, 0, "ABC"));
        Assert.False(StatApplyScope.Matches("entity:AbC", StatSide.Plant, 0, "def"));
        Assert.False(StatApplyScope.Matches("entity:AbC", StatSide.Plant, 0, null));
        Assert.False(StatApplyScope.IsKnownOwnerKey("foo"));
        Assert.False(StatApplyScope.IsKnownOwnerKey("plant:abc"));
        Assert.True(StatApplyScope.IsKnownOwnerKey("zombie:0"));
    }

    [Fact]
    public void Instance_ownerKey_never_matches_resolve()
    {
        Assert.True(StatApplyScope.IsInstanceOwnerKey("instance:guid-1"));
        Assert.True(StatApplyScope.IsInstanceOwnerKey("INSTANCE:ABC"));
        Assert.False(StatApplyScope.IsInstanceOwnerKey(null));
        Assert.False(StatApplyScope.IsInstanceOwnerKey(""));
        Assert.False(StatApplyScope.IsInstanceOwnerKey("match"));
        Assert.False(StatApplyScope.IsInstanceOwnerKey("entity:AAA"));
        Assert.False(StatApplyScope.IsInstanceOwnerKey("plant:0"));
        Assert.False(StatApplyScope.IsKnownOwnerKey("instance:guid-1"));
        Assert.False(StatApplyScope.IsMatchWide("instance:x"));
        Assert.False(StatApplyScope.Matches("instance:guid-1", StatSide.Plant, 0, "guid-1"));
        Assert.False(StatApplyScope.Matches("instance:guid-1", StatSide.Zombie, 0, "AAA"));
        Assert.False(StatApplyScope.Matches("instance:guid-1", StatSide.Plant, 0, null));
        Assert.True(StatApplyScope.Matches("plant:0", StatSide.Plant, 0, "P"));
        Assert.True(StatApplyScope.Matches("entity:AAA", StatSide.Plant, 0, "AAA"));

        var sys = new StatSystem();
        var y0 = new EntityBaseline { Hp = 100, MaxHp = 100, Atk = 10 };
        var f = sys.Modifiers;
        sys.Upsert(f.Flat("t", "effect", "inst", StatChannels.Atk, 99, applyOwnerKey: "instance:guid-1"));
        Assert.Equal(10, sys.Resolve(sys.Contexts.ForPlant("AAA", y0, typeId: 0)).Atk);
        Assert.Equal(10, sys.Resolve(sys.Contexts.ForPlant("guid-1", y0, typeId: 0)).Atk);
    }

    [Fact]
    public void Session_ApplyOwnerKey_empty_equals_match_on_resolve()
    {
        var sys = new StatSystem();
        var y0 = new EntityBaseline { Hp = 100, MaxHp = 100, Atk = 10 };
        var f = sys.Modifiers;
        sys.Upsert(f.Flat("t", "effect", "g", StatChannels.Atk, 3, applyOwnerKey: ""));
        var y = sys.Resolve(sys.Contexts.ForPlant("P1", y0, typeId: 0));
        Assert.Equal(13, y.Atk);
        Assert.Equal("match", sys.ListSessionMods("effect")[0].ApplyOwnerKey);
    }

    [Fact]
    public void Session_ApplyOwnerKey_zombie_type_not_plant()
    {
        var sys = new StatSystem();
        var y0 = new EntityBaseline { Hp = 100, MaxHp = 100, Atk = 10 };
        var f = sys.Modifiers;
        sys.Upsert(f.Flat("t", "effect", "gz", StatChannels.Atk, 40, applyOwnerKey: "zombie:0"));
        Assert.Equal(50, sys.Resolve(sys.Contexts.ForZombie("Z0", y0, typeId: 0)).Atk);
        Assert.Equal(10, sys.Resolve(sys.Contexts.ForZombie("Z1", y0, typeId: 1)).Atk);
        Assert.Equal(10, sys.Resolve(sys.Contexts.ForPlant("P0", y0, typeId: 0)).Atk);
    }

    [Fact]
    public void Session_ApplyOwnerKey_unknown_does_not_compose()
    {
        var sys = new StatSystem();
        var y0 = new EntityBaseline { Hp = 100, MaxHp = 100, Atk = 10 };
        var f = sys.Modifiers;
        sys.Upsert(f.Flat("t", "effect", "bad", StatChannels.Atk, 99, applyOwnerKey: "foo"));
        Assert.Equal(10, sys.Resolve(sys.Contexts.ForPlant("P", y0, typeId: 0)).Atk);
    }

    [Fact]
    public void Session_ApplyOwnerKey_case_variants_do_not_double_stack()
    {
        var sys = new StatSystem();
        var y0 = new EntityBaseline { Hp = 100, MaxHp = 100, Atk = 10 };
        var f = sys.Modifiers;
        sys.Upsert(f.Flat("t", "effect", "g", StatChannels.Atk, 5, applyOwnerKey: "plant:0"));
        sys.Upsert(f.Flat("t", "effect", "g", StatChannels.Atk, 5, applyOwnerKey: "Plant:0"));
        Assert.Single(sys.ListSessionMods("effect"));
        Assert.Equal(15, sys.Resolve(sys.Contexts.ForPlant("P", y0, typeId: 0)).Atk);
    }

    [Fact]
    public void Session_ApplyOwnerKey_entity_0x_matches_bare_hex()
    {
        var sys = new StatSystem();
        var y0 = new EntityBaseline { Hp = 100, MaxHp = 100, Atk = 10 };
        var f = sys.Modifiers;
        sys.Upsert(f.Flat("t", "effect", "ge", StatChannels.Atk, 20, applyOwnerKey: "entity:0xDEAD"));
        Assert.Equal(30, sys.Resolve(sys.Contexts.ForPlant("DEAD", y0, typeId: 0)).Atk);
        Assert.Equal(10, sys.Resolve(sys.Contexts.ForPlant("BEEF", y0, typeId: 0)).Atk);
    }

    [Fact]
    public void Session_ApplyOwnerKey_same_source_coexists_across_owners()
    {
        var sys = new StatSystem();
        var y0 = new EntityBaseline { Hp = 100, MaxHp = 100, Atk = 10 };
        var f = sys.Modifiers;
        sys.Upsert(f.Flat("t", "effect", "same", StatChannels.Atk, 11, applyOwnerKey: "entity:A"));
        sys.Upsert(f.Flat("t", "effect", "same", StatChannels.Atk, 22, applyOwnerKey: "entity:B"));
        Assert.Equal(2, sys.ListSessionMods("effect").Count);
        Assert.Equal(21, sys.Resolve(sys.Contexts.ForPlant("A", y0, typeId: 0)).Atk);
        Assert.Equal(32, sys.Resolve(sys.Contexts.ForPlant("B", y0, typeId: 0)).Atk);
    }

    [Fact]
    public void Session_ApplyOwnerKey_player_stub_is_match_wide()
    {
        var sys = new StatSystem();
        var y0 = new EntityBaseline { Hp = 100, MaxHp = 100, Atk = 10 };
        var f = sys.Modifiers;
        sys.Upsert(f.Flat("t", "effect", "gp", StatChannels.Atk, 4, applyOwnerKey: "player:9"));
        Assert.Equal(14, sys.Resolve(sys.Contexts.ForPlant("P", y0, typeId: 0)).Atk);
        Assert.Equal(14, sys.Resolve(sys.Contexts.ForZombie("Z", y0, typeId: 0)).Atk);
    }

    sealed class TestIncreasedPlugin : IStatModifierPlugin
    {
        public string PluginId => "test.increased";
        public int Order => 150;
        readonly StatModifierFactory _f = new();
        public void Contribute(StatContext ctx, IModifierBagEditor bag)
        {
            bag.WithdrawPlugin(PluginId);
            bag.Upsert(_f.Increased(PluginId, PluginId, "t", StatChannels.Hp, 0.5));
            bag.Upsert(_f.Increased(PluginId, PluginId, "t", StatChannels.MaxHp, 0.5));
        }
    }

    /// <summary>Intentionally non-idempotent: each Contribute adds a new SourceId Flat.</summary>
    sealed class NonIdempotentPlugin : IStatModifierPlugin
    {
        public string PluginId => "test.bad";
        public int Order => 50;
        int _n;
        readonly StatModifierFactory _f = new();
        public void Contribute(StatContext ctx, IModifierBagEditor bag)
        {
            _n++;
            bag.Upsert(_f.Flat(PluginId, PluginId, "n" + _n, StatChannels.Hp, 10));
            bag.Upsert(_f.Flat(PluginId, PluginId, "n" + _n, StatChannels.MaxHp, 10));
        }
    }
}
