using Xunit;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// lawn-combat-wire L-N31 (found by L-N29, live 2026-09-15): <c>GameHooks.Emit</c> enqueued an event only after
/// <c>MatchHost.Apply</c> and <c>EffectRuntime.OnCapture</c>. Those emit their own events and clear <c>MatchKey</c> on
/// <c>match.result</c>/<c>board.end</c>, so effects reached the server before their cause and no live run was ever closed
/// or given a result (1 of the last 100 runs closed, and that one was a web match). Source scan: the Injector has no
/// CI-runnable unit tests.
/// </summary>
public class CaptureEnqueueOrderGuardTests
{
    static readonly string Source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "FusionRpg.Injector", "GameHooks.cs"));

    [Fact]
    public void Emit_enqueues_after_the_stamps_and_before_the_capture_side_effects()
    {
        var body = MethodBody(Source, "internal static void Emit(string kind, object payload)");

        var stamp = body.IndexOf("dict[\"lifecycleOccurrence\"] = Interlocked.Increment(ref _lifecycleOccurrence);", StringComparison.Ordinal);
        var enqueue = body.IndexOf("RpgHost.Client?.Enqueue(kind, payload, MatchKey ?? _runKey);", StringComparison.Ordinal);
        var apply = body.IndexOf("Match.MatchHost.Apply(kind, dict);", StringComparison.Ordinal);
        var capture = body.IndexOf("Effects.EffectRuntime.OnCapture(kind, dict);", StringComparison.Ordinal);

        Assert.True(stamp >= 0 && enqueue > stamp, "the payload stamps must be written before the event is enqueued");
        Assert.True(apply > enqueue, "MatchHost.Apply must run after the enqueue");
        Assert.True(capture > enqueue, "EffectRuntime.OnCapture must run after the enqueue");
        Assert.Equal(body.IndexOf("RpgHost.Client?.Enqueue(", StringComparison.Ordinal), body.LastIndexOf("RpgHost.Client?.Enqueue(", StringComparison.Ordinal));
    }

    [Fact]
    public void The_run_key_lives_from_board_awake_until_board_end_is_emitted()
    {
        var awake = MethodBody(Source.Substring(Source.IndexOf("public static class BoardAwake", StringComparison.Ordinal)),
            "public static void Postfix(Board __instance)");
        Assert.True(awake.IndexOf("_runKey = MatchKey;", StringComparison.Ordinal) > awake.IndexOf("MatchKey = Guid.NewGuid().ToString();", StringComparison.Ordinal),
            "Board.Awake must set the run key from the new match key");

        var die = Source.Substring(Source.IndexOf("public static class BoardDie", StringComparison.Ordinal));
        die = MethodBody(die, "public static void Postfix()");
        var end = die.IndexOf("Emit(\"board.end\"", StringComparison.Ordinal);
        var clear = die.IndexOf("_runKey = null;", StringComparison.Ordinal);
        Assert.True(end >= 0 && clear > end, "the run key must be cleared only after board.end is emitted");
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
