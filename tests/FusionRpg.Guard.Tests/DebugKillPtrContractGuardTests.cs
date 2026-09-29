using Xunit;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// `debug.kill` / `debug.kill-plant` with a request <c>ptr</c> must kill exactly that entity or report an
/// error — never fall back to the current selection. Before this contract, <c>DebugActions.Kill</c>
/// ignored <c>ptr</c> and called <c>OneShotSelected</c>, so a live "double-kill falsifier" read back a
/// death it never targeted (lawn-combat-wire audit 2026-09-15, L-N5a). Source scan: the Injector has no
/// CI-runnable unit tests.
/// </summary>
public class DebugKillPtrContractGuardTests
{
    [Fact]
    public void Kill_reads_ptr_before_any_selection_fallback()
    {
        var body = MethodBody(ReadInjector("DebugActions.cs"), "public static void Kill(JsonElement p, bool plants)");

        var ptrRead = body.IndexOf("Str(p, \"ptr\")", StringComparison.Ordinal);
        var byPtr = body.IndexOf("KillByPtr(", StringComparison.Ordinal);
        var selectionFallback = body.IndexOf("OneShotSelected()", StringComparison.Ordinal);

        Assert.True(ptrRead >= 0, "Kill must read the request ptr");
        Assert.True(byPtr > ptrRead, "Kill must route an explicit ptr to KillByPtr");
        Assert.True(selectionFallback > byPtr, "the selection fallback must come after the explicit-ptr branch");
    }

    [Fact]
    public void KillByPtr_reports_a_missing_ptr_and_never_uses_the_selection()
    {
        var body = MethodBody(ReadInjector("DebugActions.cs"), "static void KillByPtr(string ptrHex, bool plants)");

        Assert.Contains("debug.kill: no living zombie ptr=", body, StringComparison.Ordinal);
        Assert.Contains("debug.kill-plant: no living plant ptr=", body, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectedPtr", body, StringComparison.Ordinal);
        Assert.DoesNotContain("OneShotSelected", body, StringComparison.Ordinal);
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
