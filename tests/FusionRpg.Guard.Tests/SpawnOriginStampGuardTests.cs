using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// live-probe Task 18: every Injector-command spawn marks its entity's origin, and both die payloads
/// carry <c>spawnOrigin</c>, so a soul earned from a debug- or cheat-spawned kill is attributable
/// (<c>gk-fusion/tools/ProveLiveProbe</c> <c>SoulProvenance</c>, tested offline there). Source scan: the Injector has
/// no CI-runnable unit tests.
/// </summary>
public class SpawnOriginStampGuardTests
{
    [Theory]
    [InlineData("DebugActions.cs", "public static bool SpawnPlant(JsonElement p)", "SpawnOriginTags.Mark(plant.Pointer, Match.SpawnOriginTags.Debug)")]
    [InlineData("DebugActions.cs", "public static bool SpawnZombie(JsonElement p)", "SpawnOriginTags.Mark(z.Pointer, Match.SpawnOriginTags.Debug)")]
    [InlineData("DebugActions.cs", "public static void IceRoad(JsonElement p)", "SpawnOriginTags.Mark(z.Pointer, Match.SpawnOriginTags.Debug)")]
    [InlineData("CheatActions.cs", "public static void SpawnPlant(int type)", "SpawnOriginTags.Mark(plant.Pointer, Match.SpawnOriginTags.Cheat)")]
    [InlineData("CheatActions.cs", "public static void SpawnZombie(int type, bool mindControl)", "SpawnOriginTags.Mark(z.Pointer, Match.SpawnOriginTags.Cheat)")]
    public void Every_command_spawn_marks_its_origin(string file, string method, string mark) =>
        Assert.Contains(mark, MethodBody(ReadInjector(file), method), StringComparison.Ordinal);

    [Theory]
    [InlineData("public static void Postfix(Plant __instance, Plant.DieReason reason)", "Emit(\"plant.die\"", "SpawnOriginTags.TakeOnDeath(ptr)")]
    [InlineData("static void NoteZombieDead(Zombie z, int reason)", "Emit(\"zombie.die\"", "SpawnOriginTags.TakeOnDeath(p)")]
    public void Both_die_payloads_carry_spawnOrigin_before_they_are_emitted(string method, string emit, string take)
    {
        var body = MethodBody(ReadInjector("GameHooks.cs"), method);
        var stamp = body.IndexOf("[\"spawnOrigin\"] = Match." + take, StringComparison.Ordinal);
        Assert.True(stamp >= 0, method + " must stamp spawnOrigin");
        Assert.True(body.IndexOf(emit, StringComparison.Ordinal) > stamp, method + " must stamp before emitting");
    }

    [Fact]
    public void Marks_are_dropped_on_forget_and_at_match_end()
    {
        var hooks = ReadInjector("GameHooks.cs");
        Assert.Contains("Match.SpawnOriginTags.Forget(ptr)", MethodBody(hooks, "public static void ForgetEntity(IntPtr ptr)"), StringComparison.Ordinal);
        Assert.Contains("Match.SpawnOriginTags.Clear()", hooks, StringComparison.Ordinal);
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
        var path = Path.Combine(KeepverseRoots.Fusion(), "src", "FusionRpg.Injector", relative);
        Assert.True(File.Exists(path), "missing " + path);
        return File.ReadAllText(path);
    }

    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
