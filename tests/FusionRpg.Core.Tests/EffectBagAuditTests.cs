using FusionRpg.Contracts;
using FusionRpg.Core.Effects;
using Xunit;

namespace FusionRpg.Core.Tests;

public class EffectBagAuditTests
{
    static EffectEventDto Dealt(int? targetType = 0, string side = "plant", long tick = 1) => new()
    {
        Trigger = EffectTriggers.OnDamageDealt,
        MatchKey = "m1",
        Side = side,
        ActorPtr = "0xA",
        TargetPtr = "0xB",
        TypeId = 0,
        TargetTypeId = targetType,
        Damage = 20,
        Tick = tick
    };

    [Fact]
    public void Overlay_filter_side_zombie_matches_dealt_damaged_side()
    {
        var h = new FoundationHarness();
        h.Grant(new EffectGrantDto
        {
            GrantId = "gf",
            EffectId = "fx.butter_on_hit",
            Overlay = new Dictionary<string, object?>
            {
                ["icd_ms"] = 0,
                ["filters"] = new Dictionary<string, object?> { ["side"] = "zombie" }
            }
        });
        Assert.Single(h.OnEvent(Dealt()).Actions);
        Assert.Empty(h.OnEvent(Dealt(side: "zombie")).Actions); // attacker zombie → damaged plant
    }

    [Fact]
    public void Overlay_filter_typeId_uses_TargetTypeId_on_dealt()
    {
        var h = new FoundationHarness();
        h.Grant(new EffectGrantDto
        {
            GrantId = "gt",
            EffectId = "fx.butter_on_hit",
            Overlay = new Dictionary<string, object?>
            {
                ["icd_ms"] = 0,
                ["filters"] = new Dictionary<string, object?> { ["typeId"] = 3 }
            }
        });
        Assert.Empty(h.OnEvent(Dealt(targetType: 0)).Actions);
        Assert.Single(h.OnEvent(Dealt(targetType: 3)).Actions);
    }

    [Fact]
    public void ActorIsKiller_match_requires_KillerPtr()
    {
        var h = new FoundationHarness();
        h.Grant(new EffectGrantDto
        {
            GrantId = "gk",
            EffectId = "fx.spawn_zombie_ondeath",
            Overlay = new Dictionary<string, object?>
            {
                ["icd_ms"] = 0,
                ["filters"] = new Dictionary<string, object?> { ["actorIsKiller"] = true }
            }
        });
        var dead = new EffectEventDto
        {
            Trigger = EffectTriggers.OnDeath,
            Side = "zombie",
            ActorPtr = "0xDEAD",
            KillerPtr = null,
            TypeId = 0,
            MatchKey = "m1",
            Tick = 1
        };
        Assert.Empty(h.OnEvent(dead).Actions);
        dead.KillerPtr = "0xKILLER";
        Assert.Single(h.OnEvent(dead).Actions);
    }

    [Fact]
    public void ActorIsKiller_entity_owner_must_match_KillerPtr()
    {
        var h = new FoundationHarness();
        h.Grant(new EffectGrantDto
        {
            GrantId = "ge",
            EffectId = "fx.spawn_zombie_ondeath",
            OwnerKey = EffectOwnerKeys.Entity("0xKILLER"),
            Overlay = new Dictionary<string, object?>
            {
                ["icd_ms"] = 0,
                ["filters"] = new Dictionary<string, object?> { ["actorIsKiller"] = true }
            }
        });
        var dead = new EffectEventDto
        {
            Trigger = EffectTriggers.OnDeath,
            Side = "zombie",
            ActorPtr = "0xDEAD",
            KillerPtr = "0xOTHER",
            TypeId = 0,
            MatchKey = "m1",
            Tick = 1
        };
        // entity owner also requires Actor/Target match — use Killer as actor for owner match via entity on target? 
        // Owner entity matches ActorPtr or TargetPtr; death ActorPtr is dead. Grant owner entity:0xKILLER won't match event unless we set Actor to killer.
        // For this test, use match owner with entity filter only — change owner to match and filter entity via KillerPtr equality.
        h.Withdraw("ge");
        h.Grant(new EffectGrantDto
        {
            GrantId = "ge2",
            EffectId = "fx.spawn_zombie_ondeath",
            OwnerKey = EffectOwnerKeys.Match,
            Overlay = new Dictionary<string, object?>
            {
                ["icd_ms"] = 0,
                ["filters"] = new Dictionary<string, object?> { ["actorIsKiller"] = true }
            }
        });
        dead.KillerPtr = "0xKILLER";
        Assert.Single(h.OnEvent(dead).Actions);

        h.Withdraw("ge2");
        h.Grant(new EffectGrantDto
        {
            GrantId = "ge3",
            EffectId = "fx.spawn_zombie_ondeath",
            OwnerKey = EffectOwnerKeys.Entity("0xKILLER"),
            Overlay = new Dictionary<string, object?>
            {
                ["icd_ms"] = 0,
                ["filters"] = new Dictionary<string, object?> { ["actorIsKiller"] = true }
            }
        });
        // Owner entity:0xKILLER — MatchesEvent needs Actor or Target = killer; put killer on TargetPtr too for match.
        dead.TargetPtr = "0xKILLER";
        dead.KillerPtr = "0xKILLER";
        Assert.Single(h.OnEvent(dead).Actions);
        dead.KillerPtr = "0xOTHER";
        Assert.Empty(h.OnEvent(dead).Actions);
    }

    [Fact]
    public void Multi_action_grant_allows_shared_row_col_overlay()
    {
        var h = new FoundationHarness();
        var g = h.Grant(new EffectGrantDto
        {
            GrantId = "gg",
            EffectId = "fx.grid_item_cycle",
            Overlay = new Dictionary<string, object?>
            {
                ["icd_ms"] = 0,
                ["row"] = 1,
                ["col"] = 4
            }
        });
        Assert.Equal("gg", g.GrantId);
        var plan = h.OnEvent(Dealt());
        Assert.Equal(2, plan.Actions.Count);
        Assert.Equal(1L, Convert.ToInt64(plan.Actions[0].Params["row"]));
    }

    [Fact]
    public void Max_stacks_clears_on_withdraw_regrant()
    {
        var h = new FoundationHarness();
        h.Grant(new EffectGrantDto
        {
            GrantId = "gs",
            EffectId = "fx.butter_on_hit",
            Overlay = new Dictionary<string, object?> { ["icd_ms"] = 0, ["max_stacks"] = 1 }
        });
        Assert.Single(h.OnEvent(Dealt(tick: 1)).Actions);
        Assert.Empty(h.OnEvent(Dealt(tick: 2)).Actions);
        h.Withdraw("gs");
        h.Grant(new EffectGrantDto
        {
            GrantId = "gs",
            EffectId = "fx.butter_on_hit",
            Overlay = new Dictionary<string, object?> { ["icd_ms"] = 0, ["max_stacks"] = 1 }
        });
        Assert.Single(h.OnEvent(Dealt(tick: 3)).Actions);
    }

    [Fact]
    public void Explicit_icd_zero_allows_back_to_back()
    {
        var h = new FoundationHarness();
        h.Grant(new EffectGrantDto
        {
            GrantId = "gi",
            EffectId = "fx.butter_on_hit",
            Overlay = new Dictionary<string, object?> { ["icd_ms"] = 0 }
        });
        Assert.Single(h.OnEvent(Dealt(tick: 1)).Actions);
        Assert.Single(h.OnEvent(Dealt(tick: 2)).Actions);
    }

    [Fact]
    public void OnDamageTaken_plans_status()
    {
        var catalog = new InMemoryEffectCatalog();
        catalog.Upsert(new EffectDef
        {
            EffectId = "fx.taken_butter",
            EffectType = EffectTypes.Triggered,
            Triggers = new List<string> { EffectTriggers.OnDamageTaken },
            Actions = new List<EffectActionRow>
            {
                new()
                {
                    Seq = 1,
                    Action = EffectActions.ApplyStatus,
                    Params = new Dictionary<string, object?> { ["status"] = "butter" }
                }
            }
        });
        var h = new FoundationHarness().WithCatalog(catalog.All());
        h.Grant(new EffectGrantDto
        {
            GrantId = "gtaken",
            EffectId = "fx.taken_butter",
            Overlay = new Dictionary<string, object?> { ["icd_ms"] = 0 }
        });
        var plan = h.OnEvent(new EffectEventDto
        {
            Trigger = EffectTriggers.OnDamageTaken,
            Side = "zombie",
            TargetPtr = "0xZ",
            TypeId = 0,
            MatchKey = "m1",
            Tick = 1
        });
        Assert.Single(plan.Actions);
        Assert.Equal(EffectActions.ApplyStatus, plan.Actions[0].Action);
    }

    [Fact]
    public void Disabled_def_skipped()
    {
        var catalog = new InMemoryEffectCatalog();
        var def = EffectSeedCatalog.ButterOnHit();
        def = new EffectDef
        {
            EffectId = def.EffectId,
            EffectType = def.EffectType,
            Name = def.Name,
            Enabled = false,
            SourceTag = def.SourceTag,
            Triggers = def.Triggers,
            Actions = def.Actions
        };
        catalog.ReplaceAll(new[] { def });
        var h = new FoundationHarness().WithCatalog(catalog.All());
        h.Grant(new EffectGrantDto
        {
            GrantId = "gd",
            EffectId = "fx.butter_on_hit",
            Overlay = new Dictionary<string, object?> { ["icd_ms"] = 0 }
        });
        Assert.Empty(h.OnEvent(Dealt()).Actions);
    }

    [Fact]
    public void Unknown_effect_id_throws()
    {
        var h = new FoundationHarness();
        Assert.Throws<InvalidOperationException>(() =>
            h.Grant(new EffectGrantDto { EffectId = "fx.nope" }));
    }

    [Fact]
    public void Higher_priority_grant_fires_first()
    {
        var catalog = new InMemoryEffectCatalog();
        catalog.Upsert(new EffectDef
        {
            EffectId = "fx.a",
            EffectType = EffectTypes.Triggered,
            Triggers = new List<string> { EffectTriggers.OnDamageDealt },
            Actions = new List<EffectActionRow>
            {
                new() { Seq = 1, Action = EffectActions.Economy, Params = new Dictionary<string, object?> { ["amount"] = 1 } }
            }
        });
        catalog.Upsert(new EffectDef
        {
            EffectId = "fx.b",
            EffectType = EffectTypes.Triggered,
            Triggers = new List<string> { EffectTriggers.OnDamageDealt },
            Actions = new List<EffectActionRow>
            {
                new() { Seq = 1, Action = EffectActions.Economy, Params = new Dictionary<string, object?> { ["amount"] = 2 } }
            }
        });
        var h = new FoundationHarness().WithCatalog(catalog.All());
        h.Grant(new EffectGrantDto
        {
            GrantId = "low",
            EffectId = "fx.a",
            Priority = 1,
            Overlay = new Dictionary<string, object?> { ["icd_ms"] = 0 }
        });
        h.Grant(new EffectGrantDto
        {
            GrantId = "high",
            EffectId = "fx.b",
            Priority = 10,
            Overlay = new Dictionary<string, object?> { ["icd_ms"] = 0 }
        });
        var plan = h.OnEvent(Dealt());
        Assert.Equal(2, plan.Actions.Count);
        Assert.Equal("high", plan.Actions[0].GrantId);
        Assert.Equal("low", plan.Actions[1].GrantId);
    }

    [Fact]
    public void Owner_zombie_and_entity_keys()
    {
        var h = new FoundationHarness();
        h.Grant(new EffectGrantDto
        {
            GrantId = "gz",
            EffectId = "fx.butter_on_hit",
            OwnerKey = EffectOwnerKeys.ZombieType(0),
            Overlay = new Dictionary<string, object?> { ["icd_ms"] = 0 }
        });
        // dealt side=plant attacker — zombie owner should not match plant attacker
        Assert.Empty(h.OnEvent(Dealt()).Actions);

        h.Withdraw("gz");
        h.Grant(new EffectGrantDto
        {
            GrantId = "gent",
            EffectId = "fx.butter_on_hit",
            OwnerKey = EffectOwnerKeys.Entity("0xA"),
            Overlay = new Dictionary<string, object?> { ["icd_ms"] = 0 }
        });
        Assert.Single(h.OnEvent(Dealt()).Actions);
    }

    [Fact]
    public void Executor_stop_omits_second_action()
    {
        var clock = new FakeEffectClock();
        var rng = new SeededEffectRandom(1);
        var sink = new RecordingEffectSink();
        var catalog = new InMemoryEffectCatalog();
        catalog.ReplaceAll(EffectSeedCatalog.CreateAll());
        var bag = new EffectBag(catalog, new InMemoryEffectGrantStore(), new EffectProcPolicy(clock, rng), sink);
        bag.Grant(new EffectGrantDto
        {
            GrantId = "g2",
            EffectId = "fx.spawn_plant_bullet",
            Overlay = new Dictionary<string, object?> { ["icd_ms"] = 0 }
        });
        sink.Items.Clear();
        sink.Fired.Clear();
        sink.FailNext = true;
        var plan = bag.OnEvent(Dealt());
        Assert.Single(plan.Actions);
        Assert.DoesNotContain(plan.Actions, a => a.Seq == 2);
        Assert.Equal(EffectActions.SpawnEntity, plan.Actions[0].Action);
    }

    [Fact]
    public void Board_cherry_seed_op_is_cherry()
    {
        var h = new FoundationHarness();
        h.Grant(new EffectGrantDto
        {
            GrantId = "gc",
            EffectId = "fx.board_cherry",
            Overlay = new Dictionary<string, object?> { ["icd_ms"] = 0 }
        });
        var plan = h.OnEvent(Dealt());
        Assert.Equal("cherry", plan.Actions[0].Params["op"]?.ToString());
        Assert.StartsWith("effect:", plan.Actions[0].SourceTag);
    }

    [Fact]
    public void ClearAll_removes_grants()
    {
        var h = new FoundationHarness();
        h.Grant(new EffectGrantDto
        {
            GrantId = "gx",
            EffectId = "fx.butter_on_hit",
            Overlay = new Dictionary<string, object?> { ["icd_ms"] = 0 }
        });
        h.Bag.ClearAll();
        Assert.False(h.Bag.HasAnyGrant());
        Assert.Empty(h.OnEvent(Dealt()).Actions);
    }
}
