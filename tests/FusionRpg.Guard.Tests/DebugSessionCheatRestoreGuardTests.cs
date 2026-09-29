using Xunit;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// 2026-09-15 live (lawn-combat-wire L-N22): a lab scenario's wave-freeze, plant attack x0, zombie count x0 and
/// silenced vanilla damage outlived its debug session, so the next shipped-configuration board spawned no
/// zombies. <c>DebugRuntime</c> must snapshot user-set cheat entries when a session starts and restore them
/// (and re-resolve living entities) when it ends. Source scan: the Injector has no CI-runnable unit tests.
/// </summary>
public class DebugSessionCheatRestoreGuardTests
{
    static string Runtime() => File.ReadAllText(Path.Combine(RepoRoot(), "src", "FusionRpg.Injector", "DebugRuntime.cs"));

    [Fact]
    public void StartSession_snapshots_cheats_only_when_no_session_is_already_active()
    {
        var body = MethodBody(Runtime(), "public static void StartSession(string? scenarioId)");
        var guard = body.IndexOf("if (!SessionActive)", StringComparison.Ordinal);
        var snap = body.IndexOf("_preSessionCheats = JsonSerializer.Serialize(CheatState.Snapshot());", StringComparison.Ordinal);
        var activate = body.IndexOf("SessionActive = true;", StringComparison.Ordinal);
        Assert.True(guard >= 0 && snap > guard, "the snapshot must be taken only when a session is not already active");
        Assert.True(activate > snap, "the snapshot must be taken before the session flips active");
    }

    [Fact]
    public void EndSession_restores_the_snapshot_and_reapplies_living_entities()
    {
        var text = Runtime();
        Assert.Contains("RestorePreSessionCheats()", MethodBody(text, "public static void EndSession()"), StringComparison.Ordinal);
        var restore = MethodBody(text, "static int RestorePreSessionCheats()");
        Assert.Contains("CheatState.ApplySnapshot(doc.RootElement);", restore, StringComparison.Ordinal);
        Assert.Contains("CheatActions.ReapplyAllLiving();", restore, StringComparison.Ordinal);
        Assert.Contains("_preSessionCheats = null;", restore, StringComparison.Ordinal);
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

    static string RepoRoot()
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
