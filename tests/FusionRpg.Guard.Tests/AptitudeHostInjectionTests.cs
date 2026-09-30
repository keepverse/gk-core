using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Guard.Tests;

/// <summary>class-system-todo.md P2.3 — host injection. Neither composition root is practically
/// callable from a unit test: `Program.cs` is top-level statements wired to a live `WebApplication`,
/// and `RpgHost.Initialize` pulls in BepInEx/Harmony types this test assembly does not reference —
/// `FusionRpg.Guard.Tests` is deliberately reference-free (text-scanning only), so this file proves
/// only the structural half here: each host's source literally contains the real wiring line, and
/// both hosts wire it identically (a divergence would be spec-aptitude-tuning.md §1 rule 2's "one
/// config, two consumers" failing at the host layer). The behavioral half — a malformed file's
/// rejection names the missing key — is already covered where it can actually call the parser:
/// <c>AptitudeTuningTests.MissingTopLevelBlock_rejectsNamingIt("grant")</c> in
/// <c>FusionRpg.Core.Tests</c>, which exercises the identical
/// <c>AptitudeTuningLoader.Parse(File.ReadAllText(...))</c> chain both hosts call.</summary>
public class AptitudeHostInjectionTests
{
    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }

    const string WiringNeedle = "AptitudeTuningHub.Configure(";
    const string LoaderNeedle = "AptitudeTuningLoader.Parse(";
    const string FileNeedle = "aptitudes.v2.json"; // class-system-todo.md P8.2/P8.3 (2026-08-27): v1 -> v2

    [Fact]
    public void InjectorHost_wiresAptitudeTuningHub()
    {
        var path = Path.Combine(FindRepoRoot(), "src", "FusionRpg.Injector", "Host", "RpgHost.cs");
        var text = File.ReadAllText(path);
        Assert.Contains(WiringNeedle, text, StringComparison.Ordinal);
        Assert.Contains(LoaderNeedle, text, StringComparison.Ordinal);
        Assert.Contains(FileNeedle, text, StringComparison.Ordinal);
    }

    [Fact]
    public void ServerHost_wiresAptitudeTuningHub()
    {
        var path = Path.Combine(FindRepoRoot(), "src", "FusionRpg.Server", "Program.cs");
        var text = File.ReadAllText(path);
        Assert.Contains(WiringNeedle, text, StringComparison.Ordinal);
        Assert.Contains(LoaderNeedle, text, StringComparison.Ordinal);
        Assert.Contains(FileNeedle, text, StringComparison.Ordinal);
    }

    [Fact]
    public void BothHosts_useTheIdenticalWiringPattern()
    {
        // Not just "each host mentions aptitudes.v2.json somewhere" -- the exact
        // Configure(Loader.Parse(ReadAllText(...))) chain, character-for-character, so a future edit
        // to one host cannot silently diverge from the other (spec-aptitude-tuning.md §1 rule 2:
        // "one config, two consumers" -- divergent wiring is the same failure at the host layer).
        var injectorText = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "FusionRpg.Injector", "Host", "RpgHost.cs"));
        var serverText = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "FusionRpg.Server", "Program.cs"));

        string ExtractWiringLine(string text)
        {
            var idx = text.IndexOf(WiringNeedle, StringComparison.Ordinal);
            Assert.True(idx >= 0);
            // Version-AGNOSTIC terminator (2026-09-02): this was pinned to the literal
            // "aptitudes.v2.json" and so broke the moment Phase 0 published v3 -- the guard's job is
            // that the two hosts wire IDENTICALLY, not that they sit on a particular version, and a
            // version literal here just means every future bump edits a guard for no reason.
            var m = System.Text.RegularExpressions.Regex.Match(
                text[idx..], @"aptitudes\.v\d+\.json""\)\)\)");
            Assert.True(m.Success);
            var end = idx + m.Index;
            var snippet = text[idx..(end + m.Length)];
            // Normalise the version so v{n} vs v{n+1} on one host mid-bump is a LOUD mismatch on the
            // shape comparison below rather than a silent pass.
            snippet = System.Text.RegularExpressions.Regex.Replace(snippet, @"aptitudes\.v\d+\.json", "aptitudes.v{n}.json");
            // Strip the two hosts' own path-prefix differences (System.IO. qualification, _pluginDir
            // vs AppContext.BaseDirectory) -- what must match is the Configure/Parse/ReadAllText/file
            // chain shape, not incidental host-local spelling.
            snippet = snippet.Replace("System.IO.", "", StringComparison.Ordinal);
            // Host-local comments naturally mention different version history. They are not
            // executable wiring and must not make the shape guard fail on a documentation-only
            // edit; compare the code after removing line comments.
            snippet = System.Text.RegularExpressions.Regex.Replace(snippet, @"//[^\r\n]*", "");
            return snippet;
        }

        var injectorWiring = ExtractWiringLine(injectorText);
        var serverWiring = ExtractWiringLine(serverText);
        Assert.Equal(
            System.Text.RegularExpressions.Regex.Replace(injectorWiring, @"\s+", " "),
            System.Text.RegularExpressions.Regex.Replace(serverWiring, @"\s+", " "));
    }
}
