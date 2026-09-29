using Xunit;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// lawn-combat-wire L-N20: the debug-spawn max-HP pin is a Hub input (Core <c>SpawnHpPin.ApplyTo</c>,
/// unit-tested in Core.Tests), never a write after the Hub. A post-write re-assert replaced the composed
/// max HP and erased Hub max-HP bonuses on that ptr. Source scan: the Injector has no CI-runnable tests.
/// </summary>
public class SpawnHpPinHubInputGuardTests
{
    [Theory]
    [InlineData("public static void RunPlant(", "CheatState.BuildPlantAbsolute()", "CheatState.ActorHub.Resolve(ctx)")]
    [InlineData("public static void RunZombie(", "CheatState.BuildZombieAbsolute()", "CheatState.ActorHub.Resolve(ctx)")]
    public void Entity_apply_feeds_the_pin_into_the_Hub_resolve_and_never_writes_it_afterwards(
        string method, string globalAbsolute, string resolve)
    {
        var body = MethodBody(ReadInjector("Stats/EntityApply.cs"), method);

        var pinInput = body.IndexOf("InjectorSpawnHpPin.Store.ApplyTo(key, includeAbsolute ? " + globalAbsolute + " : null)", StringComparison.Ordinal);
        var resolveAt = body.IndexOf(resolve, StringComparison.Ordinal);
        Assert.True(pinInput >= 0, method + " must pass the pin into the absolute input regardless of includeAbsolute");
        Assert.True(resolveAt > pinInput, method + " must apply the pin before the Hub resolve");
        Assert.DoesNotContain("PreserveRatio(", body, StringComparison.Ordinal);
        Assert.DoesNotContain("InjectorSpawnHpPin.TryGet", body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_writer_has_no_post_Hub_pin_re_assert()
    {
        Assert.DoesNotContain("MaxHpPreserveRatio", ReadInjector("Stats/EntityStatWriter.cs"), StringComparison.Ordinal);
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
