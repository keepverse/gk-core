using Xunit;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// lawn-combat-wire L-N16 (T9 "a dead/dying target absorbs no delta and triggers no second Die()"):
/// the Core rules live in <c>EntityLiveness</c> / <c>DeferredForgetQueue</c> and are unit-tested there;
/// this pins the Injector call sites that make them load-bearing. Source scan: the Injector has no
/// CI-runnable unit tests.
/// </summary>
public class EntityLivenessWiringGuardTests
{
    [Fact]
    public void Resource_delta_sink_asks_liveness_before_any_write()
    {
        var body = MethodBody(ReadInjector("Effects/InjectorEffectActionSink.cs"), "static bool ExecApplyResourceDelta(");

        var gate = body.IndexOf("EventDrainHost.Liveness.AdmitsDelta(targetPtr)", StringComparison.Ordinal);
        Assert.True(gate >= 0, "ExecApplyResourceDelta must refuse a dead target");
        foreach (var write in new[] { "EntityStatWriter.AddZombieHp(", "EntityStatWriter.AddPlantHp(", "ResourcePools" })
        {
            var at = body.IndexOf(write, StringComparison.Ordinal);
            Assert.True(at > gate, $"'{write}' must come after the liveness gate");
        }
    }

    [Theory]
    [InlineData("public static class PlantStart", "EventDrainHost.MarkSpawned(")]
    [InlineData("public static class ZombieStart", "NoteZombieSpawned(")]
    [InlineData("public static class ZombieInitHealth", "NoteZombieSpawned(")]
    public void Every_real_spawn_hook_clears_the_dead_mark_before_registering(string hookClass, string clear)
    {
        var body = MethodBody(ReadInjector("GameHooks.cs"), hookClass);

        var clearAt = body.IndexOf(clear, StringComparison.Ordinal);
        var addAt = body.IndexOf("InjectorEntityRegistry.Add(", StringComparison.Ordinal);
        Assert.True(clearAt >= 0, $"{hookClass} must call {clear}");
        Assert.True(addAt > clearAt, $"{hookClass} must clear the dead mark before registering");
    }

    [Fact]
    public void Zombie_spawn_edge_clears_both_the_death_latch_and_the_liveness_mark()
    {
        var body = MethodBody(ReadInjector("GameHooks.cs"), "static void NoteZombieSpawned(IntPtr p)");

        Assert.Contains("DeadZombies.Remove(p)", body, StringComparison.Ordinal);
        Assert.Contains("EventDrainHost.MarkSpawned(p)", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Registry_resync_never_revives_a_ptr()
    {
        // Resync re-adds every object FindObjectsOfType returns, dying-but-not-destroyed ones included.
        Assert.DoesNotContain("MarkSpawned", ReadInjector("Effects/InjectorEntityRegistry.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void Drain_host_runs_deferred_forgets_from_tick_and_drops_them_at_match_end()
    {
        var host = ReadInjector("Effects/EventDrainHost.cs");

        Assert.Contains("_pendingForgets.RunDue(", MethodBody(host, "public static void Tick(float unscaledDeltaTime)"), StringComparison.Ordinal);
        var reset = MethodBody(host, "public static void FlushAllAndReset()");
        Assert.Contains("Liveness.Clear()", reset, StringComparison.Ordinal);
        Assert.Contains("_pendingForgets.Clear()", reset, StringComparison.Ordinal);
    }

    static string MethodBody(string text, string signature)
    {
        var at = text.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, "missing " + signature);
        var open = text.IndexOf('{', at);
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}' && --depth == 0) return text.Substring(open, i - open + 1);
        }
        throw new InvalidOperationException("unbalanced braces after " + signature);
    }

    static string ReadInjector(string relative)
    {
        var path = Path.Combine(FindRepoRoot(), "src", "FusionRpg.Injector", relative);
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
