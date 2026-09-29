using FusionRpg.Contracts;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Effects.Plugins;
using Xunit;

namespace FusionRpg.Core.Tests;

public class EffectPluginLifecycleTests
{
    static void RegisterProvePlugins(SimEffectHost host) =>
        SecondaryPluginRegistry.RegisterById(host.Plugins, SecondaryPluginRegistry.CreateProve()
            .Select(p => p.PluginId));

    [Fact]
    public void Default_registry_has_patron_only_until_prove_plugins_registered()
    {
        var host = new SimEffectHost();
        Assert.Single(host.Plugins.Plugins);
        Assert.Contains(host.Plugins.Plugins, p => p.PluginId == "sec.patron.aura");
        RegisterProvePlugins(host);
        Assert.Equal(3, host.Plugins.Plugins.Count);
    }

    [Fact]
    public void Prove_plugins_grant_butter_and_passive_on_match_start()
    {
        var host = new SimEffectHost();
        RegisterProvePlugins(host);
        host.BeginMatch("m-sim");
        var snap = host.Snapshot();
        var butter = Assert.Single(snap.Grants, g => g.GrantId == "golden-butter");
        Assert.Equal("fx.butter_on_hit", butter.EffectId);
        Assert.Equal("sec.match.butter", butter.PluginId);
        var atk = Assert.Single(snap.Grants, g => g.GrantId == "sec-passive-atk");
        Assert.Equal("fx.passive_atk_flat", atk.EffectId);
        Assert.Equal("sec.match.passive_atk", atk.PluginId);
    }

    [Fact]
    public void EndMatch_withdraws_prove_plugin_grants()
    {
        var host = new SimEffectHost();
        RegisterProvePlugins(host);
        host.BeginMatch("m-sim");
        host.EndMatch();
        Assert.Empty(host.Snapshot().Grants);
        Assert.False(host.Bag.HasGrantForEffect("fx.butter_on_hit"));
        Assert.False(host.Bag.HasGrantForEffect("fx.passive_atk_flat"));
    }

    [Fact]
    public void NotifyRemoved_withdraws_only_matching_pluginId()
    {
        var host = new SimEffectHost();
        RegisterProvePlugins(host);
        host.BeginMatch("m-sim");
        host.Grant(new EffectGrantDto
        {
            GrantId = "manual-keep",
            EffectId = "fx.freeze_on_hit",
            OwnerKey = EffectOwnerKeys.Match,
            PluginId = "debug"
        });
        host.Plugins.NotifyRemoved(host.MatchKey);
        var snap = host.Snapshot();
        Assert.Contains(snap.Grants, g => g.GrantId == "manual-keep");
        Assert.DoesNotContain(snap.Grants, g => g.GrantId == "golden-butter");
        Assert.DoesNotContain(snap.Grants, g => g.GrantId == "sec-passive-atk");
    }

    [Fact]
    public void Throwing_plugin_does_not_block_remaining_plugins()
    {
        var h = new FoundationHarness();
        var plugins = new EffectPluginHost(h.Bag);
        plugins.Register(new ThrowingStartPlugin());
        plugins.Register(new MatchButterSecondaryPlugin());
        plugins.NotifyMatchStart("m-iso");
        Assert.True(h.Bag.HasGrantForEffect("fx.butter_on_hit"));
    }

    [Fact]
    public void ClearAll_skips_plugin_OnRemoved_EndMatch_calls_it()
    {
        var host = new SimEffectHost();
        var spy = new LifecycleSpyPlugin();
        host.Plugins.Register(spy);
        host.BeginMatch("m-spy");
        Assert.Equal(0, spy.RemovedCount);
        host.ClearAll();
        Assert.Equal(0, spy.RemovedCount);
        host.BeginMatch("m-spy");
        host.EndMatch();
        Assert.True(spy.RemovedCount >= 1);
    }

    [Fact]
    public void BeginMatch_uses_passed_matchKey_not_stale_host_key()
    {
        var host = new SimEffectHost(matchKey: "stale");
        RegisterProvePlugins(host);
        host.MatchKey = "stale";
        host.BeginMatch("custom-key");
        Assert.Equal("custom-key", host.MatchKey);
        Assert.True(host.Bag.HasGrantForEffect("fx.butter_on_hit"));
    }

    [Fact]
    public void Scenario_matchStart_uses_scenario_matchKey()
    {
        var goldenRoot = Path.GetFullPath(Path.Combine(FindScenariosDir(), ".."));
        var dto = new EffectScenarioDto
        {
            Id = "custom-key-secondary",
            MatchKey = "custom-key",
            Steps =
            {
                new EffectScenarioStepDto
                {
                    Op = "grant",
                    Grant = new EffectGrantDto
                    {
                        GrantId = "golden-butter",
                        EffectId = "fx.butter_on_hit",
                        OwnerKey = EffectOwnerKeys.Match,
                        Overlay = new Dictionary<string, object?> { ["icd_ms"] = 0 }
                    }
                },
                new EffectScenarioStepDto { Op = "hit", TypeId = 0, TargetTypeId = 0 },
                new EffectScenarioStepDto { Op = "expectPlan", Golden = "butter_on_hit.plan.json" }
            }
        };
        var result = EffectScenarioRunner.Run(dto, goldenRoot);
        Assert.True(result.Ok, result.Error);
    }

    [Fact]
    public void Second_matchStart_after_matchEnd_regrants()
    {
        var host = new SimEffectHost();
        RegisterProvePlugins(host);
        host.BeginMatch("m-a");
        host.EndMatch();
        Assert.False(host.Bag.HasGrantForEffect("fx.butter_on_hit"));
        host.BeginMatch("m-b");
        Assert.True(host.Bag.HasGrantForEffect("fx.butter_on_hit"));
        Assert.Equal("m-b", host.MatchKey);
        Assert.Contains(host.Snapshot().Grants, g => g.GrantId == "sec-passive-atk");
    }

    /// <summary>Mirrors MatchHost.Apply effect hook order (no Unity / GameHooks).</summary>
    [Fact]
    public void Live_order_auto_end_before_start_records_Removed_then_Start()
    {
        var host = new SimEffectHost();
        RegisterProvePlugins(host);
        var spy = new LifecycleSpyPlugin();
        host.Plugins.Register(spy);

        LiveStart(host, "old-key");
        LiveEndThenStart(host, "new-key");

        Assert.Equal(new[] { "start:old-key", "removed:old-key", "start:new-key" }, spy.Calls);
        Assert.Equal("new-key", host.MatchKey);
        Assert.True(host.Bag.HasGrantForEffect("fx.butter_on_hit"));
    }

    static void LiveStart(SimEffectHost host, string matchKey)
    {
        host.ClearAll();
        host.MatchKey = matchKey;
        host.Plugins.NotifyMatchStart(matchKey);
    }

    static void LiveEndThenStart(SimEffectHost host, string newKey)
    {
        var endingKey = host.MatchKey;
        host.Plugins.NotifyRemoved(endingKey);
        host.ClearAll();
        LiveStart(host, newKey);
    }

    static string FindScenariosDir()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            var candidate = Path.Combine(dir, "fixtures", "effects", "scenarios");
            if (Directory.Exists(candidate)) return candidate;
            var up = Path.GetFullPath(Path.Combine(dir, "..", "..", "..", "..", "fixtures", "effects", "scenarios"));
            if (Directory.Exists(up)) return up;
            dir = Path.GetFullPath(Path.Combine(dir, ".."));
        }

        throw new DirectoryNotFoundException("fixtures/effects/scenarios");
    }

    sealed class LifecycleSpyPlugin : IEffectGrantPlugin
    {
        public string PluginId => "spy.lifecycle";
        public List<string> Calls { get; } = new();
        public int RemovedCount { get; private set; }

        public void OnMatchStart(EffectPluginContext ctx) => Calls.Add("start:" + ctx.MatchKey);
        public void OnLoadoutChanged(EffectPluginContext ctx) { }
        public void OnOwnerChanged(EffectPluginContext ctx) { }

        public void OnRemoved(EffectPluginContext ctx)
        {
            RemovedCount++;
            Calls.Add("removed:" + ctx.MatchKey);
        }
    }

    sealed class ThrowingStartPlugin : IEffectGrantPlugin
    {
        public string PluginId => "throw.start";
        public void OnMatchStart(EffectPluginContext ctx) => throw new InvalidOperationException("boom");
        public void OnLoadoutChanged(EffectPluginContext ctx) { }
        public void OnOwnerChanged(EffectPluginContext ctx) { }
        public void OnRemoved(EffectPluginContext ctx) { }
    }
}
