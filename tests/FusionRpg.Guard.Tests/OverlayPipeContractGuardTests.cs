using System.Text.RegularExpressions;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// The overlay pipe contract is duplicated on purpose: the launcher (net8.0-windows WPF) and the
/// injector (net6.0, loaded into the game) share no assembly, so the pipe name and verbs exist as
/// literals on both sides. Nothing at compile time links them — if one drifts, the in-game button
/// simply stops reaching the launcher and hides itself, with no error anywhere. These tests are
/// that missing link. Contract: gk-fusion/docs/launcher/overlay-spec.md §Transport.
/// </summary>
public class OverlayPipeContractGuardTests
{
    const string ServerFile = @"src\FusionRpg.Launcher\Services\OverlayPipeServer.cs";
    const string ClientFile = @"src\FusionRpg.Injector\Hud\OverlaySwitch.cs";

    [Fact]
    public void Both_sides_declare_the_same_pipe_name()
    {
        var server = PipeNameIn(ServerFile);
        var client = PipeNameIn(ClientFile);

        Assert.False(string.IsNullOrEmpty(server), $"no PipeName literal found in {ServerFile}");
        Assert.False(string.IsNullOrEmpty(client), $"no PipeName literal found in {ClientFile}");
        Assert.Equal(server, client);
    }

    [Fact]
    public void The_pipe_name_still_matches_the_spec()
    {
        Assert.Equal("FusionRpg.Overlay", PipeNameIn(ServerFile));

        // The spec is gk-FUSION's. This read went to KeepverseRoots.Workspace() on the reasoning that
        // the launcher contract is a document and documents are gk-workflow's. Measured per FILE, the
        // workspace root's docs/ holds development documentation and does not carry this one; gk-fusion's
        // docs/ does. So this was a missing file, and a directory-level check gets it backwards for the
        // same reason it gets data/seed/creatures/_registry backwards.
        var spec = ReadFusionFile(@"docs\launcher\overlay-spec.md");
        Assert.Contains(@"\\.\pipe\FusionRpg.Overlay", spec, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_verb_the_client_sends_is_one_the_server_accepts()
    {
        var server = ReadFusionFile(ServerFile);
        var client = ReadFusionFile(ClientFile);

        // Verbs the client actually puts on the wire: Send("toggle", ...) / Send("ping", ...)
        var sent = Regex.Matches(client, @"Send\(""(?<verb>[a-z]+)""")
            .Select(m => m.Groups["verb"].Value)
            .Distinct()
            .ToList();

        Assert.NotEmpty(sent);
        foreach (var verb in sent)
        {
            Assert.True(
                server.Contains($"\"{verb}\" => OverlayPipeCommand.", StringComparison.Ordinal),
                $"the injector sends \"{verb}\" but the launcher's ParseCommand does not map it — " +
                "the button would silently do nothing");
        }
    }

    [Fact]
    public void The_launcher_stays_the_default_host()
    {
        // overlayHost=injector must stay opt-in until the in-game view is proven live.
        var mode = ReadRepoFile(KeepverseRoots.Core(), @"src\FusionRpg.Core\Overlay\OverlayHostSelection.cs");
        Assert.Contains("Launcher = 0", mode, StringComparison.Ordinal);
        Assert.Contains("return OverlayHostMode.Launcher;", mode, StringComparison.Ordinal);

        foreach (var host in new[]
                 {
                     @"src\FusionRpg.Injector.BepInEx\Plugin.cs",
                     @"src\FusionRpg.Injector\Host\FileRpgConfig.cs"
                 })
        {
            var text = ReadFusionFile(host);
            Assert.True(
                text.Contains("\"launcher\"", StringComparison.Ordinal),
                $"{host} should default OverlayHost to \"launcher\"");
        }
    }

    [Fact]
    public void The_in_game_view_is_torn_down_by_both_hosts()
    {
        // A browser process outliving the game is the spike's stated no-go.
        foreach (var host in new[]
                 {
                     @"src\FusionRpg.Injector.BepInEx\Plugin.cs",
                     @"src\FusionRpg.Injector.MelonLoader\MelonFusionRpgMod.cs"
                 })
        {
            var text = ReadFusionFile(host);
            Assert.True(
                text.Contains("OnApplicationQuit", StringComparison.Ordinal)
                && text.Contains("OverlaySwitch.Shutdown", StringComparison.Ordinal),
                $"{host} must tear the overlay view down on quit");
        }
    }

    /// <summary>
    /// Every project that compiles the shared injector sources, discovered rather than listed:
    /// the game x loader matrix grows a cell at a time, and a host added later would otherwise
    /// miss the package and fail to compile the moment someone builds that cell.
    /// </summary>
    static IEnumerable<string> InjectorHostProjects()
    {
        // gk-FUSION's src, for the same reason as the timeScale scan below: the projects being
        // discovered are Injector hosts, and the shared-source glob it keys on is an Injector path.
        // Enumerating gk-core's src found no host project at all, so the "discovered rather than
        // listed" claim was true of a set that was empty.
        var root = KeepverseRoots.Fusion();
        foreach (var proj in Directory.EnumerateFiles(
                     Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(proj);
            // The shared-source glob is what makes a project an injector host.
            if (text.Contains(@"..\FusionRpg.Injector\**\*.cs", StringComparison.Ordinal))
                yield return proj;
        }
    }

    [Fact]
    public void Every_injector_host_carries_the_webview_payload_rules()
    {
        // Everything referenced here lands in the player's game folder. Missing the package on a
        // host is not a soft failure: the shared sources use WebView2, so that cell stops building.
        var hosts = InjectorHostProjects().ToList();
        Assert.True(hosts.Count >= 3,
            $"expected at least the BepInEx + two MelonLoader hosts, found {hosts.Count}");

        foreach (var proj in hosts)
        {
            var text = File.ReadAllText(proj);
            var name = Path.GetFileName(proj);
            // Match the reference itself: the trim targets name the WPF/WinForms assemblies, so a
            // bare Contains("Microsoft.Web.WebView2") stays true even with the package removed.
            Assert.True(
                Regex.IsMatch(text, @"PackageReference\s+Include=""Microsoft\.Web\.WebView2"""),
                $"{name} compiles the shared injector sources but has no WebView2 package reference");
            Assert.True(text.Contains("TrimWebView2Payload", StringComparison.Ordinal),
                $"{name} would ship WPF/WinForms wrappers and XML docs into the game folder");
            Assert.True(text.Contains("TrimWebView2Natives", StringComparison.Ordinal),
                $"{name} would ship wrong-architecture natives, or bury the loader under runtimes\\");
        }
    }

    [Fact]
    public void The_in_game_view_keeps_a_way_out()
    {
        // The view covers the game, so the button that opened it is underneath it. Wave 1 has Esc,
        // WPF chrome and a launcher-registered hotkey; the in-game host has none of those, so the
        // key handler is the only exit. Losing it strands the player in a covered lawn.
        var host = ReadFusionFile(@"src\FusionRpg.Injector\Hud\OverlayViewHost.cs");

        Assert.True(
            host.Contains("AcceleratorKeyPressed", StringComparison.Ordinal),
            "OverlayViewHost must handle keys, or the view cannot be closed from inside");
        Assert.True(
            host.Contains("OverlayViewPolicy.IsCloseKey", StringComparison.Ordinal),
            "the close-key rule belongs to OverlayViewPolicy, where it is unit-tested");
        Assert.True(
            host.Contains("OverlayViewPolicy.ShouldAutoHide", StringComparison.Ordinal),
            "a topmost, non-alt-tabbable window must hide when the game loses foreground");
    }

    [Fact]
    public void Time_scale_keeps_exactly_one_writer()
    {
        // CheatActions.TickContinuous asserts the timescale every frame (G-TIMEFREEZE / G-TIMESCALE).
        // A second writer anywhere would be silently overwritten on the next frame whenever a speed
        // setting is active — precisely how "pause does not work with a speed cheat on" bugs appear.
        // OverlayPause therefore decides and CheatActions applies.
        // Scan gk-FUSION, which is where the Injector and its single writer live. This walked
        // gk-core's `src`, and that made the guard VACUOUS IN THE DANGEROUS DIRECTION rather than
        // merely wrong: `Time.timeScale =` is written only by the Injector, so scanning a tree with
        // none of them made `offenders.Count == 0` true by construction — a second writer would have
        // been invisible. Measured, gk-core has 0 such writes and gk-fusion has 4, all in the declared
        // owner CheatActions.cs (which is gk-fusion's, so the walk could never have found it).
        // The guard's own anchor is what caught this: `scanned > 50` was reading gk-core's 40-odd
        // files, and `writesInTheOwner > 0` was reading zero. That anchor is load-bearing, not
        // ceremony — a guard that quietly scans nothing passes forever, and this one nearly did.
        var offenders = new List<string>();
        var scanned = 0;
        var writesInTheOwner = 0;
        var separator = Path.DirectorySeparatorChar;

        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(KeepverseRoots.Fusion(), "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{separator}obj{separator}", StringComparison.Ordinal)) continue;
            if (file.Contains($"{separator}bin{separator}", StringComparison.Ordinal)) continue;

            var isOwner = Path.GetFileName(file)
                .Equals("CheatActions.cs", StringComparison.OrdinalIgnoreCase);
            scanned++;

            foreach (var raw in File.ReadAllLines(file))
            {
                var line = raw.Trim();
                if (line.StartsWith("//", StringComparison.Ordinal)) continue;
                if (!Regex.IsMatch(line, @"Time\.timeScale\s*=")) continue;

                if (isOwner) writesInTheOwner++;
                else offenders.Add(Path.GetFileName(file) + ": " + line);
            }
        }

        // A guard that quietly scans nothing passes forever. Anchor it on both counts.
        Assert.True(scanned > 50, $"expected to scan the injector sources, saw {scanned} files");
        Assert.True(writesInTheOwner > 0,
            "CheatActions.cs no longer writes Time.timeScale - this guard watches the wrong file");

        Assert.True(offenders.Count == 0,
            "Time.timeScale must only be assigned in CheatActions.TickContinuous: " +
            string.Join(" | ", offenders));
    }

    /// <summary>Both ends of the pipe live in gk-fusion - the Launcher's server and the
    /// Injector's client - so every caller of this is reading the host repository.</summary>
    static string PipeNameIn(string relativePath)
    {
        var text = ReadFusionFile(relativePath);
        var match = Regex.Match(text, @"PipeName\s*=\s*""(?<name>[^""]+)""");
        return match.Success ? match.Groups["name"].Value : "";
    }

    /// <summary>Reads a file under an EXPLICIT root. This helper used to prepend gk-core's
    /// root to every path, and its call sites reach TWO repositories: gk-fusion for the
    /// Launcher and Injector ends of the pipe, and gk-core for the host-selection policy.
    /// Under one implicit prefix the gk-fusion reads failed identically, as a missing file,
    /// which told a reader nothing about which repository was actually being asked. Each
    /// call site now names it.</summary>
    /// <remarks>
    /// A THIRD repository was named here and was wrong: the comment claimed the spec came from
    /// gk-workflow. Resolved per file, <c>docs/launcher/overlay-spec.md</c> is gk-fusion's — the
    /// workspace root's <c>docs/</c> is the development documentation tree and does not carry it.
    /// The call site now reads it through <see cref="ReadFusionFile"/>, and the helper that made
    /// the wrong claim is gone rather than left for the next caller to trust.
    /// </remarks>
    static string ReadRepoFile(string root, string relativePath)
    {
        var path = Path.Combine(root, relativePath);
        Assert.True(File.Exists(path), $"missing {path} - the owner repository does not carry {relativePath}");
        return File.ReadAllText(path);
    }

    static string ReadFusionFile(string relativePath) => ReadRepoFile(KeepverseRoots.Fusion(), relativePath);
}
