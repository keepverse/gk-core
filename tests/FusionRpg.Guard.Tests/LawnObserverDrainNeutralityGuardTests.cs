using System.Text.RegularExpressions;
using Xunit;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// lawn-combat-wire T0 / L-N10: "A test or assertion proves the collection path does not alter
/// <c>EventDrainHost.Active</c>". <c>Active</c> is <c>Enabled &amp;&amp; !DebugRuntime.SessionActive</c>, so the
/// proof is a closed chain over the code: (1) <c>Active</c> reads only those two flags; (2) each flag has
/// only its known writers; (3) the only way to reach <c>SessionActive</c>'s writers is a
/// <c>debug.session</c> / scenario command; (4) no part of the collection path — the observer tool's
/// HTTP surface, the server routes it hits, the injector handler they relay to, the injector bridge and
/// the Core observer — sends such a command or writes either flag. Source scan: the Injector has no
/// CI-runnable unit tests, and the observer tool talks to a live server.
/// </summary>
public class LawnObserverDrainNeutralityGuardTests
{
    static readonly Regex Assign = new(@"\b(SessionActive|Enabled|SessionMode|LogDamage|HitCapture)\s*=(?!=)", RegexOptions.Compiled);

    [Fact]
    public void Drain_Active_reads_only_Enabled_and_SessionActive()
    {
        var host = Read("src/FusionRpg.Injector/Effects/EventDrainHost.cs");
        Assert.Contains("static bool Active => Enabled && !DebugRuntime.SessionActive;", host, StringComparison.Ordinal);
    }

    [Fact]
    public void Drain_flags_have_only_their_known_writers()
    {
        var sessionWriters = new List<string>();
        var enabledWriters = new List<string>();
        foreach (var file in SourceFiles("src"))
        {
            var text = File.ReadAllText(file);
            var rel = Path.GetRelativePath(RepoRoot(), file).Replace('\\', '/');
            foreach (Match m in Regex.Matches(text, @"(?<![\w.])(DebugRuntime\.)?SessionActive\s*=(?!=)"))
                if (!rel.StartsWith("src/FusionRpg.Core/", StringComparison.Ordinal) && !rel.StartsWith("src/FusionRpg.Server/", StringComparison.Ordinal))
                    sessionWriters.Add(rel);
            if (Regex.IsMatch(text, @"EventDrainHost\.Enabled\s*=(?!=)"))
                enabledWriters.Add(rel);
        }

        Assert.All(sessionWriters, w => Assert.Equal("src/FusionRpg.Injector/DebugRuntime.cs", w));
        Assert.Equal(new[] { "src/FusionRpg.Injector/Host/InjectorLoop.cs" }, enabledWriters.Distinct().ToArray());

        var debugRuntime = Read("src/FusionRpg.Injector/DebugRuntime.cs");
        var outsideSessionMethods = debugRuntime
            .Replace(MethodBody(debugRuntime, "public static void StartSession("), "")
            .Replace(MethodBody(debugRuntime, "public static void EndSession()"), "");
        Assert.DoesNotMatch(@"\bSessionActive\s*=(?!=)", outsideSessionMethods);
    }

    [Fact]
    public void Observer_tool_calls_only_its_four_read_routes_with_GET()
    {
        var allowed = new[] { "/api/perf/recent", "/api/debug/snapshot", "/api/events", "/api/debug/session" };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in SourceFiles("tools/LawnCombatObserver"))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotMatch(@"\b(PostAsync|PutAsync|PatchAsync|DeleteAsync|SendAsync)\b", text);
            foreach (Match m in Regex.Matches(text, "\"(/api/[A-Za-z0-9/_-]+)"))
                seen.Add(m.Groups[1].Value);
        }

        Assert.NotEmpty(seen);
        Assert.All(seen, route => Assert.Contains(route, allowed));
    }

    [Fact]
    public void Server_routes_the_observer_hits_relay_only_debug_snapshot()
    {
        var endpoints = Read("src/FusionRpg.Server/DebugEndpoints.cs");

        var snapshot = MethodBody(endpoints, "g.MapGet(\"/snapshot\"");
        var sends = Regex.Matches(snapshot, @"Send\(hub,\s*inbox,\s*""([^""]+)""").Select(m => m.Groups[1].Value).ToArray();
        Assert.Equal(new[] { "debug.snapshot" }, sends);

        Assert.Contains("g.MapGet(\"/session\", () => Results.Ok(DebugSessionState.Snapshot()));", endpoints, StringComparison.Ordinal);
    }

    [Fact]
    public void Injector_debug_snapshot_handler_only_self_reports()
    {
        var runner = Read("src/FusionRpg.Injector/CheatCommandRunner.cs");
        var at = runner.IndexOf("case \"debug.snapshot\":", StringComparison.Ordinal);
        Assert.True(at >= 0, "missing debug.snapshot handler");
        var end = runner.IndexOf("break;", at, StringComparison.Ordinal);
        var handler = runner.Substring(at, end - at);
        Assert.Contains("DebugRuntime.Emit(\"debug.snapshot\", DebugRuntime.Snapshot());", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("Session", handler.Replace("DebugRuntime.Snapshot()", ""), StringComparison.Ordinal);

        var snapshot = MethodBody(Read("src/FusionRpg.Injector/DebugRuntime.cs"), "public static Dictionary<string, object> Snapshot()");
        AssertWritesNoFlag(snapshot, "DebugRuntime.Snapshot()");
    }

    [Theory]
    [InlineData("src/FusionRpg.Injector/Effects/LawnCombatObserverBridge.cs")]
    [InlineData("src/FusionRpg.Core/Combat/Observability/LawnCombatObserver.cs")]
    public void Observer_bridge_and_core_observer_write_no_drain_or_session_flag(string path)
    {
        var text = Read(path);
        // The bridge's own kill switch is its own `Enabled { get; set; } = ...` initializer — not a drain flag.
        var withoutOwnSwitch = Regex.Replace(text, @"public static bool Enabled \{ get; set; \} =", "");
        AssertWritesNoFlag(withoutOwnSwitch, path);
    }

    static void AssertWritesNoFlag(string code, string where)
    {
        var hits = Assign.Matches(code).Select(m => m.Value).ToArray();
        Assert.True(hits.Length == 0, $"{where} writes a drain/session flag: {string.Join(", ", hits)}");
        foreach (var call in new[] { "StartSession(", "EndSession(", "SetToggle(", "\"debug.session" })
            Assert.DoesNotContain(call, code, StringComparison.Ordinal);
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

    static IEnumerable<string> SourceFiles(string relativeDir) =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), relativeDir), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                        && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar));

    static string Read(string relative)
    {
        var path = Path.Combine(RepoRoot(), relative);
        Assert.True(File.Exists(path), "missing " + path);
        return File.ReadAllText(path);
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
