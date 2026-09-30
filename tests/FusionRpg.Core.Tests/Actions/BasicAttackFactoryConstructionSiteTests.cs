using System.Runtime.CompilerServices;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.Actions;

/// <summary>
/// `lawn-combat-wire` T8 (spec-lawn-action-bridge.md) success criteria:
/// <list type="bullet">
/// <item>"One public factory in <c>Core.Actions</c>; <c>BattleRunState</c> uses it; a source scan
/// proves no second construction site."</item>
/// <item>"No HTTP, SignalR or SQLite on the construction path" (source-scan test).</item>
/// </list>
/// A source scan, not a design assertion, because the whole point of the extraction is that a future
/// caller (the injector) reaches for <see cref="FusionRpg.Core.Actions.BasicAttackFactory"/> instead
/// of hand-building a second copy — the same dual-compose defect this repo has already overturned
/// once for <c>ActorHub</c> vs <c>BattleStatComposer</c> (AGENTS.md "One ActorHub compose / one read").
/// </summary>
public class BasicAttackFactoryConstructionSiteTests
{
    static string RepoRoot([CallerFilePath] string here = "")
    {
        return KeepverseRoots.Core();
    }

    static string SrcDir() => Path.Combine(RepoRoot(), "src");

    /// <summary>Every non-generated, non-binary .cs file under src/, skipping obj/bin build output.</summary>
    static IEnumerable<string> SourceFiles(string root) =>
        Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    /// <summary>Drops whole-line `//`/`///`/`*` comments before a scan — same discipline
    /// <c>guard-funnel-delta.py</c>'s own <c>Get-CodeLines</c> uses and states the same reason for:
    /// a rule documented IN a comment (this very file explains "no HTTP, no SignalR, no SQLite" and
    /// the sibling files' own doc comments name `new CompiledAction(` while explaining why it moved)
    /// must never itself trip the scan meant to enforce that rule on CODE.</summary>
    static string CodeOnly(string text)
    {
        var lines = text.Split('\n');
        var kept = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            var t = line.TrimStart();
            if (t.StartsWith("//", StringComparison.Ordinal) || t.StartsWith("*", StringComparison.Ordinal)) continue;
            kept.Add(line);
        }
        return string.Join('\n', kept);
    }

    // The literal signature of "building the basic-attack row" — this exact token appears in the
    // hand-built CompiledAction's own ActionId argument. Chosen over a bare `new CompiledAction(`
    // scan because ActionCompiler.cs LEGITIMATELY builds CompiledAction rows for real-content
    // actions (rung/container-based) — a wholly different, still-single construction site this test
    // must not flag. What must never have a second construction site is specifically the
    // basic-attack row's own hand-built shape.
    const string BasicAttackConstructionSignature = "ActionId: BattleEngine.BasicAttackEnvelope.ActionId";

    [Fact]
    public void The_basic_attack_row_is_constructed_in_exactly_one_production_file()
    {
        var src = SrcDir();
        Assert.True(Directory.Exists(src), $"src/ not found at {src}");

        var matches = SourceFiles(src)
            .Where(f => CodeOnly(File.ReadAllText(f)).Contains(BasicAttackConstructionSignature, StringComparison.Ordinal))
            .Select(f => Path.GetFileName(f))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        Assert.True(matches.Count == 1,
            "expected exactly one production construction site for the basic-attack row, found: " +
            string.Join(", ", matches));
        Assert.Equal("BasicAttackFactory.cs", matches[0]);
    }

    [Fact]
    public void BattleRunState_no_longer_hand_builds_the_row_inline()
    {
        var battleRunState = Path.Combine(SrcDir(), "FusionRpg.Core", "Battle", "BattleRunState.cs");
        Assert.True(File.Exists(battleRunState), $"not found: {battleRunState}");

        var text = CodeOnly(File.ReadAllText(battleRunState));
        Assert.DoesNotContain("new CompiledAction(", text, StringComparison.Ordinal);
        Assert.Contains("BasicAttackFactory.Create(", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_factory_file_calls_no_HTTP_SignalR_or_SQLite_API()
    {
        var factory = Path.Combine(SrcDir(), "FusionRpg.Core", "Actions", "BasicAttackFactory.cs");
        Assert.True(File.Exists(factory), $"not found: {factory}");

        var text = CodeOnly(File.ReadAllText(factory));
        var forbidden = new[]
        {
            "HttpClient", "System.Net.Http", "HubConnection", "SignalR",
            "SqliteConnection", "Microsoft.Data.Sqlite", "SqlCommand",
        };
        foreach (var token in forbidden)
            Assert.DoesNotContain(token, text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_injector_side_reader_calls_no_HTTP_SignalR_or_SQLite_API()
    {
        // The injector's own half of "one factory, two callers" (spec-lawn-action-bridge.md) —
        // FusionRpg.Injector is not part of this test project's build graph (interop refs, no CI
        // game dir), so this is a source scan over the .cs text directly rather than a compiled
        // reference, matching this project's own established pattern for cross-project source
        // scans (e.g. guard-*.ps1).
        var reader = Path.Combine(SrcDir(), "FusionRpg.Injector", "Actions", "LawnBasicAttackRow.cs");
        Assert.True(File.Exists(reader), $"not found: {reader}");

        var text = CodeOnly(File.ReadAllText(reader));
        var forbidden = new[]
        {
            "HttpClient", "System.Net.Http", "HubConnection", "SignalR",
            "SqliteConnection", "Microsoft.Data.Sqlite", "SqlCommand",
        };
        foreach (var token in forbidden)
            Assert.DoesNotContain(token, text, StringComparison.Ordinal);
    }
}
