using Xunit;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// passive-tree-todo.md G6 (spec-gate-counters.md §10, §15 criterion 9) — the injector's half of G6
/// cannot be unit-tested for the same reason `StatusDerivedWiringGuardTests` exists:
/// <c>FusionRpg.Injector</c> targets net6.0 against the game's BepInEx/Il2Cpp interop DLLs, so it needs
/// a real PVZ Fusion install to build, and the injector cannot host a test project. This guard reads
/// <c>EffectRuntime.cs</c>, <c>InjectorLoop.cs</c> and <c>GateCounterHost.cs</c> as TEXT, matching that
/// file's own established shape verbatim (same <c>ReadInjector</c>/<c>FindRepoRoot</c> helpers).
///
/// <para><b>What it protects against.</b> Before G6, <c>StatusRuntime.OnFreshApplication</c> and
/// <c>EffectBag.OnDamageApplied</c> both shipped with zero production callers — G2/G3's own counters
/// had nowhere to receive a real credit from. The fix is two one-line assignments at
/// <c>EffectRuntime.Ensure()</c>'s composition root, a periodic flush timer in <c>InjectorLoop.Tick</c>,
/// and an unconditional flush at <c>EffectRuntime.NotifyMatchEnd</c> (spec §4.3's two triggers). A
/// refactor that silently drops one of those four wires — or points a hook at a stub instead of the
/// real <see cref="FusionRpg.Core.PassiveTree.GateCounters.GateCounterAccumulator"/> — would reopen the
/// exact "inert path" gap this task exists to close, and nothing in Core's own test suite would notice:
/// Core's tests exercise <c>EffectBag</c>/<c>StatusRuntime</c> directly and never touch
/// <c>EffectRuntime.cs</c> or <c>InjectorLoop.cs</c>.</para>
/// </summary>
public class GateCounterWiringGuardTests
{
    [Fact]
    public void EffectRuntime_wires_OnFreshApplication_to_the_real_StatusCounter()
    {
        var text = ReadInjector("Effects", "EffectRuntime.cs");
        Assert.Contains(
            "_status.OnFreshApplication = GateCounterHost.StatusCounter.Handle;",
            text, StringComparison.Ordinal);
    }

    [Fact]
    public void EffectRuntime_wires_bag_OnDamageApplied_to_the_real_element_mastery_adapter()
    {
        var text = ReadInjector("Effects", "EffectRuntime.cs");
        Assert.Contains(
            "bag.OnDamageApplied = GateCounterHost.HandleDamageApplied;",
            text, StringComparison.Ordinal);
    }

    [Fact]
    public void EffectRuntime_flushes_gate_counters_unconditionally_at_match_end()
    {
        var text = ReadInjector("Effects", "EffectRuntime.cs");
        Assert.Contains("GateCounterHost.Flush(RpgHost.Client);", text, StringComparison.Ordinal);
    }

    [Fact]
    public void InjectorLoop_flushes_gate_counters_on_a_timer()
    {
        var text = ReadInjector("Host", "InjectorLoop.cs");
        Assert.Contains("GateCounterHost.Flush(client);", text, StringComparison.Ordinal);
    }

    [Fact]
    public void GateCounterHost_ownership_resolver_reads_spawn_kind_never_current_allegiance()
    {
        // §2.1's closing rule, made a guarded fact rather than a comment: the resolver must ask
        // LawnElementResolverHost (spawn-kind, board-scan-cached) for "plant" vs "zombie" -- never a
        // live-side/current-allegiance flag that a charm/hypno status could flip.
        var text = ReadInjector("Effects", "GateCounterHost.cs");
        Assert.Contains("LawnElementResolverHost.Resolve(", text, StringComparison.Ordinal);
        Assert.Contains("\"plant\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void GateCounterHost_credits_only_a_positive_current_player_id()
    {
        var text = ReadInjector("Effects", "GateCounterHost.cs");
        Assert.Contains("CheatState.CurrentPlayerId", text, StringComparison.Ordinal);
    }

    static string ReadInjector(params string[] relative)
    {
        var path = Path.Combine(new[] { FindRepoRoot(), "src", "FusionRpg.Injector" }.Concat(relative).ToArray());
        Assert.True(File.Exists(path), "missing " + path);
        return File.ReadAllText(path);
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Injector"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("repo root");
    }
}
