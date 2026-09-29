using Xunit;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// Debug telemetry must be strictly session-scoped: per-hit LogDamage is a lag source
/// (perf baseline 00-baseline.md), so a session may enable it but MUST disable it on end —
/// and nothing may ship a default-on per-hit telemetry flag (2026-08-21 incident: the old
/// StatsConfig default=true made every player pay per-hit emission).
/// </summary>
public class DebugSessionGuardTests
{
    [Fact]
    public void EndSession_clears_LogDamage_it_enabled()
    {
        var text = ReadSource("src", "FusionRpg.Injector", "DebugRuntime.cs");

        var start = text.IndexOf("public static void StartSession", StringComparison.Ordinal);
        var end = text.IndexOf("public static void EndSession", StringComparison.Ordinal);
        var disarm = text.IndexOf("public static void DisarmAll", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start && disarm > end, "DebugRuntime session methods not found in expected order");

        var startBody = text.Substring(start, end - start);
        var endBody = text.Substring(end, disarm - end);

        // StartSession turns per-hit telemetry on for the lab…
        Assert.Contains("LogDamage = true", startBody, StringComparison.Ordinal);
        // …and EndSession must turn it back off, or telemetry lag persists until game restart.
        Assert.Contains("LogDamage = false", endBody, StringComparison.Ordinal);
    }

    [Fact]
    public void StatsConfig_LogDamage_defaults_off()
    {
        var text = ReadSource("src", "FusionRpg.Contracts", "Dtos.cs");
        var idx = text.IndexOf("public bool LogDamage", StringComparison.Ordinal);
        Assert.True(idx >= 0, "StatsConfig.LogDamage not found");
        var line = text.Substring(idx, Math.Min(80, text.Length - idx));
        Assert.DoesNotContain("= true", line, StringComparison.Ordinal);
    }

    static string ReadSource(params string[] parts)
    {
        var path = Path.Combine(new[] { FindRepoRoot() }.Concat(parts).ToArray());
        Assert.True(File.Exists(path), "missing " + path);
        return File.ReadAllText(path);
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "FusionRpg.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
