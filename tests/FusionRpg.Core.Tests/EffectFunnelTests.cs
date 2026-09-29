using FusionRpg.Contracts;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Effects.Plugins;
using Xunit;

namespace FusionRpg.Core.Tests;

public class EffectFunnelTests
{
    [Fact]
    public void ResourceDeltaMath_clamps_heal_and_floor()
    {
        Assert.Equal(3500, ResourceDeltaMath.Apply(4500, -1000, 5000));
        Assert.Equal(5000, ResourceDeltaMath.Apply(4500, 1000, 5000));
        Assert.Equal(0, ResourceDeltaMath.Apply(50, -100, 5000));
    }

    [Fact]
    public void One_hundred_mutations_same_target_become_one_fa10()
    {
        var h = new FoundationHarness();
        h.Sink.Items.Clear();
        for (var i = 0; i < 100; i++)
            Assert.True(h.Funnel.EnqueueMutation("entity:aaa", -10, pluginId: "sec.test", grantId: "crit-" + i));
        h.Funnel.Flush();
        var fa10 = h.Sink.Items.Where(a => a.Action == EffectActions.ApplyResourceDelta).ToList();
        Assert.Single(fa10);
        Assert.Equal(-1000L, Convert.ToInt64(fa10[0].Params["amount"]));
        Assert.Equal(100, Convert.ToInt32(fa10[0].Params["mergedCount"]));
        Assert.Equal("aaa", fa10[0].Params["targetPtr"]?.ToString());
    }

    [Fact]
    public void Mutation_packet_is_delta_not_absolute_hp()
    {
        var h = new FoundationHarness();
        h.Sink.Items.Clear();
        // live 4500 is Unity-side; funnel only carries -1000
        h.Funnel.EnqueueMutation("entity:bbb", -1000, pluginId: "sec.test");
        h.Funnel.Flush();
        var item = Assert.Single(h.Sink.Items, a => a.Action == EffectActions.ApplyResourceDelta);
        Assert.False(item.Params.ContainsKey("hp"));
        Assert.False(item.Params.ContainsKey("setHp"));
        Assert.Equal(-1000L, Convert.ToInt64(item.Params["amount"]));
        Assert.Equal(3500, ResourceDeltaMath.Apply(4500, -1000, 5000));
    }

    [Fact]
    public void Ten_distinct_modifier_grants_stay_ten_sources()
    {
        var h = new FoundationHarness();
        const int grantCount = 10;
        for (var i = 0; i < grantCount; i++)
        {
            h.Funnel.EnqueueModifier(new EffectGrantDto
            {
                GrantId = "atk-" + i,
                EffectId = "fx.passive_atk_flat",
                OwnerKey = EffectOwnerKeys.Match,
                PluginId = "sec.gear"
            });
        }

        h.Funnel.Flush();
        var grants = h.Bag.ForOwner(null, EffectOwnerKeys.Match)
            .Where(g => g.PluginId == "sec.gear")
            .ToList();
        // Self-referential (population-pin SE3.3, 2026-09-19): this test enqueued grantCount modifiers
        // itself, so it proves none were lost or duplicated, not a fact about shipped content.
        Assert.Equal(grantCount, grants.Count);
        Assert.True(h.Funnel.Withdraw("atk-0"));
        Assert.Equal(9, h.Bag.ForOwner(null, EffectOwnerKeys.Match).Count(g => g.PluginId == "sec.gear"));
    }

    [Fact]
    public void Mode_set_and_absolute_hp_are_rejected()
    {
        var h = new FoundationHarness();
        h.Sink.Items.Clear();
        Assert.False(h.Funnel.EnqueueMutation("entity:ccc", -100, mode: "set"));
        Assert.False(h.Funnel.EnqueueMutation("entity:ccc", -100,
            extra: new Dictionary<string, object?> { ["absoluteHp"] = 4000 }));
        Assert.False(h.Funnel.EnqueueMutation("entity:ccc", -100,
            extra: new Dictionary<string, object?> { ["setHp"] = 4000 }));
        Assert.False(h.Funnel.EnqueueMutation("entity:ccc", -100,
            extra: new Dictionary<string, object?> { ["hp"] = 4000 }));
        Assert.False(h.Funnel.EnqueueMutation("entity:ccc", -100,
            extra: new Dictionary<string, object?> { ["EntityFinal.Hp"] = 4000 }));
        Assert.False(h.Funnel.EnqueueMutation("entity:ccc", -100,
            extra: new Dictionary<string, object?> { ["entityFinalHp"] = 4000 }));
        h.Funnel.Flush();
        Assert.DoesNotContain(h.Sink.Items, a => a.Action == EffectActions.ApplyResourceDelta);
        Assert.Contains(h.Funnel.LastSkipped, s => s.Contains("mode-set", StringComparison.Ordinal));
        Assert.Contains(h.Funnel.LastSkipped, s => s.Contains("absolute-hp", StringComparison.Ordinal));
    }

    [Fact]
    public void Opposite_signs_net_in_one_window()
    {
        var h = new FoundationHarness();
        h.Sink.Items.Clear();
        h.Funnel.EnqueueMutation("entity:ddd", 200);
        h.Funnel.EnqueueMutation("entity:ddd", -1000);
        h.Funnel.Flush();
        var item = Assert.Single(h.Sink.Items, a => a.Action == EffectActions.ApplyResourceDelta);
        Assert.Equal(-800L, Convert.ToInt64(item.Params["amount"]));
        Assert.Equal(2, Convert.ToInt32(item.Params["mergedCount"]));
    }

    [Fact]
    public void BeginMatch_prove_plugins_grant_via_funnel()
    {
        var host = new SimEffectHost();
        SecondaryPluginRegistry.RegisterById(host.Plugins,
            SecondaryPluginRegistry.CreateProve().Select(p => p.PluginId));
        host.BeginMatch("m-funnel");
        Assert.True(host.Bag.HasGrantForEffect("fx.butter_on_hit"));
        Assert.True(host.Bag.HasGrantForEffect("fx.passive_atk_flat"));
        Assert.DoesNotContain(host.Sink.Items, a => a.Action == EffectActions.ApplyResourceDelta);
    }

    [Fact]
    public void Nested_enqueue_during_execute_is_drained_not_dropped()
    {
        var sink = new EnqueueOnExecuteSink();
        var catalog = new InMemoryEffectCatalog();
        catalog.ReplaceAll(EffectSeedCatalog.CreateAll());
        var bag = new EffectBag(catalog, new InMemoryEffectGrantStore(),
            new EffectProcPolicy(new FakeEffectClock(), new SeededEffectRandom(1)), sink);
        var funnel = new EffectFunnel(bag);
        sink.Funnel = funnel;
        funnel.EnqueueMutation("entity:eee", -5);
        funnel.EnqueueMutation("entity:eee", -5);
        funnel.Flush();
        Assert.Equal(2, sink.Amounts.Count);
        Assert.Equal(-10L, sink.Amounts[0]);
        Assert.Equal(-7L, sink.Amounts[1]);
    }

    [Fact]
    public void Empty_modifier_grantId_is_rejected()
    {
        var h = new FoundationHarness();
        Assert.False(h.Funnel.EnqueueModifier(new EffectGrantDto
        {
            GrantId = "  ",
            EffectId = "fx.passive_atk_flat",
            OwnerKey = EffectOwnerKeys.Match,
            PluginId = "sec.gear"
        }));
        h.Funnel.Flush();
        Assert.DoesNotContain(h.Bag.ForOwner(null, EffectOwnerKeys.Match), g => g.PluginId == "sec.gear");
        Assert.Contains(h.Funnel.LastSkipped, s => s.Contains("missing-grantId", StringComparison.Ordinal));
    }

    [Fact]
    public void Same_modifier_grantId_last_write_wins()
    {
        var h = new FoundationHarness();
        h.Funnel.EnqueueModifier(new EffectGrantDto
        {
            GrantId = "atk-same",
            EffectId = "fx.passive_atk_flat",
            OwnerKey = EffectOwnerKeys.Match,
            PluginId = "sec.gear",
            Overlay = new Dictionary<string, object?> { ["flat"] = 10 }
        });
        h.Funnel.EnqueueModifier(new EffectGrantDto
        {
            GrantId = "atk-same",
            EffectId = "fx.passive_atk_flat",
            OwnerKey = EffectOwnerKeys.Match,
            PluginId = "sec.gear",
            Overlay = new Dictionary<string, object?> { ["flat"] = 99 }
        });
        h.Funnel.Flush();
        var g = Assert.Single(h.Bag.ForOwner(null, EffectOwnerKeys.Match), x => x.GrantId == "atk-same");
        Assert.Equal(99, Convert.ToInt32(g.Overlay["flat"]));
    }

    [Fact]
    public void Mailbox_cap_skips_new_key_existing_still_sums()
    {
        var h = new FoundationHarness();
        h.Sink.Items.Clear();
        for (var i = 0; i < ResourceDeltaMath.MailboxCap; i++)
            Assert.True(h.Funnel.EnqueueMutation("entity:" + i.ToString("x"), -1));
        Assert.False(h.Funnel.EnqueueMutation("entity:ffff", -1));
        Assert.True(h.Funnel.EnqueueMutation("entity:0", -4));
        h.Funnel.Flush();
        Assert.Contains(h.Funnel.LastSkipped, s => s.Contains("mailbox-cap", StringComparison.Ordinal));
        var first = h.Sink.Items.Single(a => a.Params["targetPtr"]?.ToString() == "0");
        Assert.Equal(-5L, Convert.ToInt64(first.Params["amount"]));
        Assert.Equal(ResourceDeltaMath.MailboxCap, h.Sink.Items.Count(a => a.Action == EffectActions.ApplyResourceDelta));
    }

    [Fact]
    public void Per_event_amount_cap_skips_sibling_still_flushes()
    {
        var h = new FoundationHarness();
        h.Sink.Items.Clear();
        Assert.False(h.Funnel.EnqueueMutation("entity:cap", ResourceDeltaMath.AmountCap + 1));
        Assert.False(h.Funnel.EnqueueMutation("entity:cap", long.MinValue));
        Assert.True(h.Funnel.EnqueueMutation("entity:cap", -10));
        h.Funnel.Flush();
        var item = Assert.Single(h.Sink.Items, a => a.Action == EffectActions.ApplyResourceDelta);
        Assert.Equal(-10L, Convert.ToInt64(item.Params["amount"]));
        Assert.Contains(h.Funnel.LastSkipped, s => s.Contains("amount-cap", StringComparison.Ordinal));
    }

    [Fact]
    public void Merged_sum_over_cap_skips_whole_packet()
    {
        // T3.5 (spec-caps-reconcile.md §2.1): AmountCap is now derived (long.MaxValue/2), not the old
        // 1e9 literal -- each half individually legal, their MERGED sum one past the live cap.
        var half = ResourceDeltaMath.AmountCap / 2 + 1;
        var h = new FoundationHarness();
        h.Sink.Items.Clear();
        Assert.True(h.Funnel.EnqueueMutation("entity:sum", half));
        Assert.True(h.Funnel.EnqueueMutation("entity:sum", half));
        h.Funnel.Flush();
        Assert.DoesNotContain(h.Sink.Items, a => a.Action == EffectActions.ApplyResourceDelta);
        Assert.Contains(h.Funnel.LastSkipped, s => s.Contains("amount-cap", StringComparison.Ordinal));
    }

    [Fact]
    public void Distinct_targets_stay_two_packets()
    {
        var h = new FoundationHarness();
        h.Sink.Items.Clear();
        h.Funnel.EnqueueMutation("entity:aa", -1);
        h.Funnel.EnqueueMutation("entity:bb", -2);
        h.Funnel.Flush();
        var fa10 = h.Sink.Items.Where(a => a.Action == EffectActions.ApplyResourceDelta).ToList();
        Assert.Equal(2, fa10.Count);
    }

    [Fact]
    public void Entity_0x_prefix_merges_with_bare_hex()
    {
        var h = new FoundationHarness();
        h.Sink.Items.Clear();
        h.Funnel.EnqueueMutation("entity:0xAAA", -3);
        h.Funnel.EnqueueMutation("entity:aaa", -4);
        h.Funnel.Flush();
        var item = Assert.Single(h.Sink.Items, a => a.Action == EffectActions.ApplyResourceDelta);
        Assert.Equal(-7L, Convert.ToInt64(item.Params["amount"]));
        Assert.Equal(2, Convert.ToInt32(item.Params["mergedCount"]));
    }

    [Fact]
    public void Net_zero_emits_no_fa10()
    {
        var h = new FoundationHarness();
        h.Sink.Items.Clear();
        h.Funnel.EnqueueMutation("entity:zero", 100);
        h.Funnel.EnqueueMutation("entity:zero", -100);
        h.Funnel.Flush();
        Assert.DoesNotContain(h.Sink.Items, a => a.Action == EffectActions.ApplyResourceDelta);
        Assert.Empty(h.Fx.Items);
    }

    [Fact]
    public void Non_hp_and_missing_target_are_skipped()
    {
        var h = new FoundationHarness();
        Assert.False(h.Funnel.EnqueueMutation("entity:ch", -1, channel: "sun"));
        Assert.False(h.Funnel.EnqueueMutation("  ", -1));
        Assert.Contains(h.Funnel.LastSkipped, s => s.Contains("channel", StringComparison.Ordinal));
        Assert.Contains(h.Funnel.LastSkipped, s => s.Contains("missing-target", StringComparison.Ordinal));
    }

    [Fact]
    public void OnEvent_triggered_then_fa10_and_funnel_skips_on_plan()
    {
        var h = new FoundationHarness();
        h.Grant(new EffectGrantDto
        {
            GrantId = "golden-butter",
            EffectId = "fx.butter_on_hit",
            OwnerKey = EffectOwnerKeys.Match,
            Overlay = new Dictionary<string, object?> { ["icd_ms"] = 0 }
        });
        Assert.False(h.Funnel.EnqueueMutation("entity:zzz", -1, mode: "set"));
        h.Funnel.EnqueueMutation("entity:zzz", -50);
        var plan = h.OnEvent(new EffectEventDto
        {
            Trigger = EffectTriggers.OnDamageDealt,
            TargetPtr = "zzz",
            Side = "zombie"
        });
        Assert.Equal(EffectActions.ApplyStatus, plan.Actions[0].Action);
        Assert.Equal(EffectActions.ApplyResourceDelta, plan.Actions[^1].Action);
        Assert.Contains(plan.Skipped, s => s.Contains("mode-set", StringComparison.Ordinal));
    }

    [Fact]
    public void Non_recording_sink_plan_includes_fa10()
    {
        var sink = new CountingSink();
        var catalog = new InMemoryEffectCatalog();
        catalog.ReplaceAll(EffectSeedCatalog.CreateAll());
        var bag = new EffectBag(catalog, new InMemoryEffectGrantStore(),
            new EffectProcPolicy(new FakeEffectClock(), new SeededEffectRandom(1)), sink);
        var funnel = new EffectFunnel(bag);
        funnel.EnqueueMutation("entity:nr", -3);
        var plan = bag.OnEvent(new EffectEventDto { Trigger = EffectTriggers.OnDamageDealt, TargetPtr = "nr" });
        Assert.Equal(1, sink.Count);
        var fa10 = Assert.Single(plan.Actions, a => a.Action == EffectActions.ApplyResourceDelta);
        Assert.Equal(-3L, Convert.ToInt64(fa10.Params["amount"]));
    }

    [Fact]
    public void RpgEffectEvent_dispatches_modifier_and_mutation()
    {
        var h = new FoundationHarness();
        h.Sink.Items.Clear();
        Assert.True(h.Funnel.Enqueue(new RpgEffectEvent
        {
            Family = RpgEffectFamily.Modifier,
            GrantId = "rpg-atk",
            EffectId = "fx.passive_atk_flat",
            OwnerKey = EffectOwnerKeys.Match,
            PluginId = "sec.rpg"
        }));
        Assert.True(h.Funnel.Enqueue(new RpgEffectEvent
        {
            Family = RpgEffectFamily.Mutation,
            TargetKey = "entity:rpg",
            Amount = -8,
            PluginId = "sec.rpg"
        }));
        h.Funnel.Flush();
        Assert.Contains(h.Bag.ForOwner(null, EffectOwnerKeys.Match), g => g.GrantId == "rpg-atk");
        Assert.Single(h.Sink.Items, a => a.Action == EffectActions.ApplyResourceDelta);
    }

    [Fact]
    public void ResourceDeltaMath_amount_cap_and_negative_max()
    {
        Assert.True(ResourceDeltaMath.ExceedsAmountCap(ResourceDeltaMath.AmountCap + 1));
        Assert.True(ResourceDeltaMath.ExceedsAmountCap(long.MinValue));
        Assert.False(ResourceDeltaMath.ExceedsAmountCap(ResourceDeltaMath.AmountCap));
        Assert.Equal(0, ResourceDeltaMath.Apply(10, 5, -1));
    }

    [Fact]
    public void Nested_flush_during_execute_is_noop()
    {
        var sink = new ReentrantSink();
        var catalog = new InMemoryEffectCatalog();
        catalog.ReplaceAll(EffectSeedCatalog.CreateAll());
        var bag = new EffectBag(catalog, new InMemoryEffectGrantStore(),
            new EffectProcPolicy(new FakeEffectClock(), new SeededEffectRandom(1)), sink);
        var funnel = new EffectFunnel(bag);
        sink.Funnel = funnel;
        funnel.EnqueueMutation("entity:eee", -5);
        funnel.EnqueueMutation("entity:eee", -5);
        funnel.Flush();
        Assert.True(funnel.FlushCallCount >= 2);
        Assert.Equal(1, sink.ExecuteCount);
        Assert.Equal(-10L, Convert.ToInt64(sink.LastAmount));
    }

    [Fact]
    public void One_hundred_mutations_same_target_become_one_neutral_present()
    {
        var h = new FoundationHarness();
        h.Fx.Items.Clear();
        for (var i = 0; i < 100; i++)
            Assert.True(h.Funnel.EnqueueMutation("entity:aaa", -10, pluginId: "sec.test", grantId: "crit-" + i));
        h.Funnel.Flush();
        var fx = Assert.Single(h.Fx.Items);
        Assert.Equal("aaa", fx.TargetPtr);
        Assert.Equal(-1000L, fx.Amount);
        Assert.Equal(100, fx.MergedCount);
        Assert.Equal(DamageFxTag.Neutral, fx.Tag);
        Assert.Equal("float", fx.Fx);
    }

    [Fact]
    public void Heal_mutation_presents_heal_tag()
    {
        var h = new FoundationHarness();
        h.Fx.Items.Clear();
        Assert.True(h.Funnel.EnqueueMutation("entity:heal", 100, pluginId: "sec.test"));
        h.Funnel.Flush();
        var fx = Assert.Single(h.Fx.Items);
        Assert.Equal(100L, fx.Amount);
        Assert.Equal(DamageFxTag.Heal, fx.Tag);
    }

    [Fact]
    public void Two_presents_same_ptr_and_tag_sum()
    {
        var h = new FoundationHarness();
        h.Fx.Items.Clear();
        Assert.True(h.Funnel.EnqueuePresent(new DamageFxDto
        {
            TargetPtr = "entity:zzz",
            Amount = 40,
            Tag = DamageFxTag.Crit
        }));
        Assert.True(h.Funnel.EnqueuePresent(new DamageFxDto
        {
            TargetPtr = "zzz",
            Amount = 60,
            Tag = DamageFxTag.Crit
        }));
        h.Funnel.Flush();
        var fx = Assert.Single(h.Fx.Items);
        Assert.Equal(100L, fx.Amount);
        Assert.Equal(2, fx.MergedCount);
        Assert.Equal(DamageFxTag.Crit, fx.Tag);
        Assert.Equal("zzz", fx.TargetPtr);
    }

    [Fact]
    public void Distinct_tags_on_same_ptr_are_two_floaters()
    {
        var h = new FoundationHarness();
        h.Fx.Items.Clear();
        Assert.True(h.Funnel.EnqueuePresent(new DamageFxDto
        {
            TargetPtr = "entity:zzz",
            Amount = 50,
            Tag = DamageFxTag.Crit
        }));
        Assert.True(h.Funnel.EnqueuePresent(new DamageFxDto
        {
            TargetPtr = "entity:zzz",
            Amount = 0,
            Tag = DamageFxTag.Dodge
        }));
        h.Funnel.Flush();
        Assert.Equal(2, h.Fx.Items.Count);
        Assert.Contains(h.Fx.Items, x => x.Tag == DamageFxTag.Crit && x.Amount == 50);
        Assert.Contains(h.Fx.Items, x => x.Tag == DamageFxTag.Dodge && x.Amount == 0);
    }

    [Fact]
    public void Explicit_present_skips_default_fa10_present_for_same_target()
    {
        var h = new FoundationHarness();
        h.Fx.Items.Clear();
        Assert.True(h.Funnel.EnqueuePresent(new DamageFxDto
        {
            TargetPtr = "entity:mix",
            Amount = 250,
            Tag = DamageFxTag.Crit
        }));
        Assert.True(h.Funnel.EnqueueMutation("entity:mix", -250, pluginId: "sec.test"));
        h.Funnel.Flush();
        var fx = Assert.Single(h.Fx.Items);
        Assert.Equal(DamageFxTag.Crit, fx.Tag);
        Assert.Equal(250L, fx.Amount);
    }

    [Fact]
    public void Present_only_sets_HasPending_until_flush()
    {
        var h = new FoundationHarness();
        Assert.False(h.Funnel.HasPending);
        Assert.True(h.Funnel.EnqueuePresent(new DamageFxDto
        {
            TargetPtr = "entity:p",
            Amount = 0,
            Tag = DamageFxTag.Dodge
        }));
        Assert.True(h.Funnel.HasPending);
        h.Funnel.Flush();
        Assert.False(h.Funnel.HasPending);
        var fx = Assert.Single(h.Fx.Items);
        Assert.Equal(0L, fx.Amount);
        Assert.Equal(DamageFxTag.Dodge, fx.Tag);
    }

    [Fact]
    public void Present_missing_target_is_skipped()
    {
        var h = new FoundationHarness();
        Assert.False(h.Funnel.EnqueuePresent(new DamageFxDto { Amount = 1, Tag = DamageFxTag.Crit }));
        Assert.Contains(h.Funnel.LastSkipped, s => s.Contains("missing-target", StringComparison.Ordinal));
        h.Funnel.Flush();
        Assert.Empty(h.Fx.Items);
    }

    [Fact]
    public void Nested_flush_does_not_show_presents_until_outer()
    {
        var fx = new RecordingDamageFxSink();
        var sink = new ReentrantPresentSink { Fx = fx };
        var funnel = FunnelWith(sink, fx);
        sink.Funnel = funnel;
        funnel.EnqueueMutation("entity:eee", -5);
        funnel.Flush();
        Assert.Equal(0, sink.ShownDuringExecute);
        var shown = Assert.Single(fx.Items);
        Assert.Equal(DamageFxTag.Neutral, shown.Tag);
        Assert.Equal(-5L, shown.Amount);
    }

    [Fact]
    public void Present_during_execute_skips_default_and_shows_after_outer_flush()
    {
        var fx = new RecordingDamageFxSink();
        var sink = new PresentOnExecuteSink { Fx = fx };
        var funnel = FunnelWith(sink, fx);
        sink.Funnel = funnel;
        funnel.EnqueueMutation("entity:mix", -50);
        funnel.Flush();
        Assert.Equal(0, sink.ShownDuringExecute);
        var shown = Assert.Single(fx.Items);
        Assert.Equal(DamageFxTag.Crit, shown.Tag);
        Assert.Equal(50L, shown.Amount);
        Assert.DoesNotContain(fx.Items, x => x.Tag == DamageFxTag.Neutral);
    }

    [Fact]
    public void Default_then_crit_same_ptr_is_one_crit()
    {
        var fx = new RecordingDamageFxSink();
        var sink = new DefaultThenCritSink();
        var funnel = FunnelWith(sink, fx);
        sink.Funnel = funnel;
        funnel.EnqueueMutation("entity:t1", -10);
        funnel.EnqueueMutation("entity:t2", -10);
        funnel.Flush();
        var crit = Assert.Single(fx.Items, x => x.Tag == DamageFxTag.Crit);
        Assert.Equal(250L, crit.Amount);
        Assert.DoesNotContain(fx.Items, x => x.Tag == DamageFxTag.Neutral && x.TargetPtr == crit.TargetPtr);
    }

    [Fact]
    public void Present_mailbox_cap_skips_new_key_existing_still_sums()
    {
        var h = new FoundationHarness();
        h.Fx.Items.Clear();
        for (var i = 0; i < ResourceDeltaMath.MailboxCap; i++)
            Assert.True(h.Funnel.EnqueuePresent(new DamageFxDto
            {
                TargetPtr = "entity:" + i.ToString("x"),
                Amount = 1,
                Tag = DamageFxTag.Neutral
            }));
        Assert.False(h.Funnel.EnqueuePresent(new DamageFxDto
        {
            TargetPtr = "entity:fffff",
            Amount = 1,
            Tag = DamageFxTag.Neutral
        }));
        Assert.True(h.Funnel.EnqueuePresent(new DamageFxDto
        {
            TargetPtr = "entity:0",
            Amount = 4,
            Tag = DamageFxTag.Neutral
        }));
        h.Funnel.Flush();
        Assert.Contains(h.Funnel.LastSkipped, s => s.Contains("mailbox-cap", StringComparison.Ordinal));
        Assert.Equal(ResourceDeltaMath.MailboxCap, h.Fx.Items.Count);
        Assert.Equal(5L, h.Fx.Items.Single(x => x.TargetPtr == "0").Amount);
    }

    [Fact]
    public void Mutation_mailbox_full_still_accepts_a_present()
    {
        var h = new FoundationHarness();
        for (var i = 0; i < ResourceDeltaMath.MailboxCap; i++)
            Assert.True(h.Funnel.EnqueueMutation("entity:" + i.ToString("x"), -1));
        Assert.True(h.Funnel.EnqueuePresent(new DamageFxDto
        {
            TargetPtr = "entity:fx",
            Amount = 0,
            Tag = DamageFxTag.Dodge
        }));
        Assert.True(h.Funnel.HasPending);
    }

    [Fact]
    public void Null_fx_sink_flush_does_not_throw()
    {
        var sink = new CountingSink();
        var catalog = new InMemoryEffectCatalog();
        catalog.ReplaceAll(EffectSeedCatalog.CreateAll());
        var bag = new EffectBag(catalog, new InMemoryEffectGrantStore(),
            new EffectProcPolicy(new FakeEffectClock(), new SeededEffectRandom(1)), sink);
        var funnel = new EffectFunnel(bag);
        Assert.True(funnel.EnqueuePresent(new DamageFxDto
        {
            TargetPtr = "entity:n",
            Amount = 3,
            Tag = DamageFxTag.Crit
        }));
        Assert.True(funnel.EnqueueMutation("entity:n", -3));
        funnel.Flush();
        Assert.Equal(1, sink.Count);
    }

    static EffectFunnel FunnelWith(IEffectActionSink sink, RecordingDamageFxSink fx)
    {
        var catalog = new InMemoryEffectCatalog();
        catalog.ReplaceAll(EffectSeedCatalog.CreateAll());
        var bag = new EffectBag(catalog, new InMemoryEffectGrantStore(),
            new EffectProcPolicy(new FakeEffectClock(), new SeededEffectRandom(1)), sink);
        return new EffectFunnel(bag, fx);
    }

    sealed class ReentrantSink : IEffectActionSink
    {
        public EffectFunnel? Funnel { get; set; }
        public int ExecuteCount { get; private set; }
        public object? LastAmount { get; private set; }

        public bool Execute(EffectExecuteContext ctx, EffectActionPlanItem item)
        {
            ExecuteCount++;
            LastAmount = item.Params.GetValueOrDefault("amount");
            Funnel?.Flush();
            return true;
        }
    }

    sealed class EnqueueOnExecuteSink : IEffectActionSink
    {
        public EffectFunnel? Funnel { get; set; }
        public List<long> Amounts { get; } = new();

        public bool Execute(EffectExecuteContext ctx, EffectActionPlanItem item)
        {
            Amounts.Add(Convert.ToInt64(item.Params.GetValueOrDefault("amount")));
            if (Amounts.Count == 1)
                Funnel?.EnqueueMutation("entity:eee", -7);
            Funnel?.Flush();
            return true;
        }
    }

    sealed class CountingSink : IEffectActionSink
    {
        public int Count { get; private set; }

        public bool Execute(EffectExecuteContext ctx, EffectActionPlanItem item)
        {
            Count++;
            return true;
        }
    }

    sealed class ReentrantPresentSink : IEffectActionSink
    {
        public EffectFunnel? Funnel { get; set; }
        public RecordingDamageFxSink Fx { get; set; } = null!;
        public int ShownDuringExecute { get; private set; }

        public bool Execute(EffectExecuteContext ctx, EffectActionPlanItem item)
        {
            Funnel?.Flush();
            ShownDuringExecute = Fx.Items.Count;
            return true;
        }
    }

    sealed class PresentOnExecuteSink : IEffectActionSink
    {
        public EffectFunnel? Funnel { get; set; }
        public RecordingDamageFxSink Fx { get; set; } = null!;
        public int ShownDuringExecute { get; private set; }

        public bool Execute(EffectExecuteContext ctx, EffectActionPlanItem item)
        {
            Funnel?.EnqueuePresent(new DamageFxDto
            {
                TargetPtr = ctx.Event.TargetPtr ?? "",
                Amount = 50,
                Tag = DamageFxTag.Crit
            });
            Funnel?.Flush();
            ShownDuringExecute = Fx.Items.Count;
            return true;
        }
    }

    sealed class DefaultThenCritSink : IEffectActionSink
    {
        public EffectFunnel? Funnel { get; set; }
        int _n;
        string _first = "";

        public bool Execute(EffectExecuteContext ctx, EffectActionPlanItem item)
        {
            _n++;
            if (_n == 1)
                _first = ctx.Event.TargetPtr ?? "";
            if (_n == 2)
            {
                Funnel?.EnqueuePresent(new DamageFxDto
                {
                    TargetPtr = _first,
                    Amount = 250,
                    Tag = DamageFxTag.Crit
                });
            }

            return true;
        }
    }
}
