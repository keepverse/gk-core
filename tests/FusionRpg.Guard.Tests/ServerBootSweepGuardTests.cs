using Xunit;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// E20/C1 (completeness-audit.md): the server boot sweep, in <c>Program.cs</c>, is not unit-testable
/// directly (top-level statements) — reads it as text, matching every other injector/server-wiring
/// guard in this project.
///
/// <para><b>What this protects.</b> <c>RpgStore.LoadContentIntoRuntime</c>,
/// <c>ClearSessionScopedBindings</c> and <c>CountOrphanInstances</c> all had zero production callers
/// before E20/C1. A future edit that removes one of these calls while refactoring boot order would
/// silently reopen that exact gap — the loader stops loading, or a crash-restart starts accumulating
/// stale <c>entity:</c> bindings forever, and nothing in Core's own tests would notice, because those
/// tests build their own <see cref="FusionRpg.Data.RpgStore"/> and call these methods directly.</para>
/// </summary>
public class ServerBootSweepGuardTests
{
    [Fact]
    public void The_content_loader_runs_at_boot()
    {
        var text = ReadServer("Program.cs");

        Assert.Contains("LoadContentIntoRuntime()", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_session_scoped_binding_sweep_runs_at_boot()
    {
        var text = ReadServer("Program.cs");

        Assert.Contains("ClearSessionScopedBindings()", text, StringComparison.Ordinal);
        Assert.Contains("CountOrphanInstances()", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_binding_sweep_runs_after_the_content_loader_not_before()
    {
        // LoadContentIntoRuntime does not depend on the binding sweep, but keeping the order
        // documented here means a reordering that swaps them is a deliberate edit, not a drive-by.
        var text = ReadServer("Program.cs");

        var loaderIdx = text.IndexOf("LoadContentIntoRuntime()", StringComparison.Ordinal);
        var sweepIdx = text.IndexOf("ClearSessionScopedBindings()", StringComparison.Ordinal);
        Assert.True(loaderIdx >= 0 && sweepIdx >= 0 && loaderIdx < sweepIdx,
            "expected LoadContentIntoRuntime() before ClearSessionScopedBindings() in Program.cs");
    }

    static string ReadServer(string relative)
    {
        var path = Path.Combine(FindRepoRoot(), "src", "FusionRpg.Server", relative);
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
