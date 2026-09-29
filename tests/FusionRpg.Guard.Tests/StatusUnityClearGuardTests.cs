using Xunit;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// status-rail C5 / B3 — lawn Unity clear matrix: only <c>jala</c> is unclearable;
/// ember/hypno/kelp clear arms exist beside butter/freeze/cold/poison.
/// </summary>
public class StatusUnityClearGuardTests
{
    [Fact]
    public void UnclearableStatuses_is_jala_only()
    {
        var text = ReadInjector("Effects", "InjectorEffectActionSink.cs");
        var start = text.IndexOf("UnclearableStatuses", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var block = text.Substring(start, Math.Min(400, text.Length - start));
        Assert.Contains("\"jala\"", block, StringComparison.Ordinal);
        Assert.DoesNotContain("\"ember\"", block, StringComparison.Ordinal);
        Assert.DoesNotContain("\"hypno\"", block, StringComparison.Ordinal);
        Assert.DoesNotContain("\"kelp\"", block, StringComparison.Ordinal);
    }

    [Fact]
    public void ClearZombieStatus_covers_ember_hypno_kelp()
    {
        var text = ReadInjector("Effects", "InjectorEffectActionSink.cs");
        var start = text.IndexOf("static void ClearZombieStatus", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var block = text.Substring(start, Math.Min(1200, text.Length - start));
        Assert.Contains("SetEmbered(false)", block, StringComparison.Ordinal);
        Assert.Contains("status == \"hypno\"", block, StringComparison.Ordinal);
        Assert.Contains("status == \"kelp\"", block, StringComparison.Ordinal);
        Assert.Contains("UnButtered()", block, StringComparison.Ordinal);
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
