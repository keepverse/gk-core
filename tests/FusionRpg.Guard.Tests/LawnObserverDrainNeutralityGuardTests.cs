using System.Text.RegularExpressions;
using Xunit;
using FusionRpg.Core.Workspace;

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
        var host = ReadInjectorFile("src/FusionRpg.Injector/Effects/EventDrainHost.cs");
        Assert.Contains("static bool Active => Enabled && !DebugRuntime.SessionActive;", host, StringComparison.Ordinal);
    }

    [Fact]
    public void Drain_flags_have_only_their_known_writers()
    {
        var sessionWriters = new List<string>();
        var enabledWriters = new List<string>();
        var scanned = 0;
        foreach (var (rel, file) in DrainFlagSourceFiles())
        {
            var text = File.ReadAllText(file);
            scanned++;
            foreach (Match m in Regex.Matches(text, @"(?<![\w.])(DebugRuntime\.)?SessionActive\s*=(?!=)"))
                if (!rel.StartsWith("src/FusionRpg.Core/", StringComparison.Ordinal) && !rel.StartsWith("src/FusionRpg.Server/", StringComparison.Ordinal))
                    sessionWriters.Add(rel);
            if (Regex.IsMatch(text, @"EventDrainHost\.Enabled\s*=(?!=)"))
                enabledWriters.Add(rel);
        }

        // A guard that quietly scans nothing passes forever, so the scan is anchored on BOTH the volume
        // and the owner. The volume alone would pass on a small tree; the owner alone passed on an empty
        // list, which is the failure this had.
        Assert.True(scanned > 50, $"expected to scan both repositories' sources, saw {scanned} files");
        Assert.All(sessionWriters, w => Assert.Equal("src/FusionRpg.Injector/DebugRuntime.cs", w));
        Assert.True(sessionWriters.Count > 0,
            "no file writes SessionActive, so the single-writer assertion above proved nothing");
        Assert.Equal(new[] { "src/FusionRpg.Injector/Host/InjectorLoop.cs" }, enabledWriters.Distinct().ToArray());
        Assert.True(enabledWriters.Count > 0,
            "no file writes EventDrainHost.Enabled, so the single-writer assertion above proved nothing");

        var debugRuntime = ReadInjectorFile("src/FusionRpg.Injector/DebugRuntime.cs");
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
        // The observer tool is gk-FUSION's, and it was being looked for under gk-core.
        foreach (var file in SourceFiles(KeepverseRoots.Fusion(), "tools/LawnCombatObserver"))
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
        var endpoints = Read(KeepverseRoots.Core(), "src/FusionRpg.Server/DebugEndpoints.cs");

        var snapshot = MethodBody(endpoints, "g.MapGet(\"/snapshot\"");
        var sends = Regex.Matches(snapshot, @"Send\(hub,\s*inbox,\s*""([^""]+)""").Select(m => m.Groups[1].Value).ToArray();
        Assert.Equal(new[] { "debug.snapshot" }, sends);

        Assert.Contains("g.MapGet(\"/session\", () => Results.Ok(DebugSessionState.Snapshot()));", endpoints, StringComparison.Ordinal);
    }

    [Fact]
    public void Injector_debug_snapshot_handler_only_self_reports()
    {
        var runner = ReadInjectorFile("src/FusionRpg.Injector/CheatCommandRunner.cs");
        var at = runner.IndexOf("case \"debug.snapshot\":", StringComparison.Ordinal);
        Assert.True(at >= 0, "missing debug.snapshot handler");
        var end = runner.IndexOf("break;", at, StringComparison.Ordinal);
        var handler = runner.Substring(at, end - at);
        Assert.Contains("DebugRuntime.Emit(\"debug.snapshot\", DebugRuntime.Snapshot());", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("Session", handler.Replace("DebugRuntime.Snapshot()", ""), StringComparison.Ordinal);

        var snapshot = MethodBody(ReadInjectorFile("src/FusionRpg.Injector/DebugRuntime.cs"), "public static Dictionary<string, object> Snapshot()");
        AssertWritesNoFlag(snapshot, "DebugRuntime.Snapshot()");
    }

    // One row is the Injector's bridge and the other is gk-core's own observer, so the
    // repository is a parameter of the theory rather than a property of the file. Choosing it by
    // the path prefix would work until someone adds a third row, and would be the same implicit
    // prefix under another name.
    [Theory]
    [InlineData("src/FusionRpg.Injector/Effects/LawnCombatObserverBridge.cs", true)]
    [InlineData("src/FusionRpg.Core/Combat/Observability/LawnCombatObserver.cs", false)]
    public void Observer_bridge_and_core_observer_write_no_drain_or_session_flag(string path, bool inFusion)
    {
        var text = inFusion ? ReadInjectorFile(path) : Read(KeepverseRoots.Core(), path);
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

    /// <summary>Every .cs file under <c>&lt;root&gt;/&lt;relativeDir&gt;</c>, build output excluded.</summary>
    /// <param name="root">The repository to scan.</param>
    /// <param name="relativeDir">The directory below it.</param>
    /// <remarks>
    /// The root is a parameter because the trees a caller needs span repositories. This used to scan
    /// gk-core's <c>src/</c> for every caller, and the drain-flag test then asserted that the only writer
    /// of <c>SessionActive</c> is <c>src/FusionRpg.Injector/DebugRuntime.cs</c> — a gk-FUSION file the scan
    /// could never have reached, so the writer list it compared was empty and the equality passed for the
    /// wrong reason. An empty list satisfies <c>Assert.All</c>; that is what made this invisible rather
    /// than red.
    /// </remarks>
    static IEnumerable<string> SourceFiles(string root, string relativeDir) =>
        Directory.EnumerateFiles(Path.Combine(root, relativeDir), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                        && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar));

    /// <summary>
    /// Every source file in the repositories that own the drain/session flag writers, as
    /// (repository-relative path, absolute path).
    /// </summary>
    /// <remarks>
    /// TWO repositories, because the test's own exclusions say so: it filters out
    /// <c>src/FusionRpg.Core/</c> and <c>src/FusionRpg.Server/</c>, and those prefixes only mean anything
    /// if gk-core is in the scanned set. The writers themselves are gk-fusion's. Scanning one repository
    /// answers neither half, so both are scanned and the relative path is what the assertions compare —
    /// a path whose owner is not named in the result would be a defect this shape cannot express.
    /// </remarks>
    static IEnumerable<(string Rel, string Abs)> DrainFlagSourceFiles()
    {
        foreach (var root in new[] { KeepverseRoots.Core(), KeepverseRoots.Fusion() })
        {
            foreach (var file in SourceFiles(root, "src"))
            {
                yield return (Path.GetRelativePath(root, file).Replace('\\', '/'), file);
            }
        }
    }

    /// <summary>Reads a file under an EXPLICIT root. This helper used to prepend gk-core's
    /// root to every path, which is why four Injector reads and one Server read all went
    /// through the same prefix: the Injector ones were asking gk-core for a file in gk-fusion,
    /// and the Server one was accidentally right. The root is now a parameter, so each call
    /// site states which repository it means and neither kind is right by accident.</summary>
    static string Read(string root, string relative)
    {
        var path = Path.Combine(root, relative);
        Assert.True(File.Exists(path), "missing " + path);
        return File.ReadAllText(path);
    }

    static string ReadInjectorFile(string relative) => Read(KeepverseRoots.Fusion(), relative);

    static string RepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
