using System.Text.RegularExpressions;
using FusionRpg.Core.Stats.Derived;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.ClassSystem;

/// <summary>
/// class-system-todo.md Phase 0 (C0.1, C0.2) — "architecture changes that lock behavior need
/// decisions.md first" (AGENTS.md) is a hard boundary. These are the two mechanical gates: a row
/// exists, and it is not stale prose — its headline numbers agree with the code and the roster.
/// Mirrors SpecChannelClaimTests' own pattern (parse the real file, assert on the real text).
/// </summary>
public class DecisionsGateTests
{
    static readonly Regex RowPattern = new(@"^\|\s*(.+?)\s*\|\s*(.+?)\s*\|$", RegexOptions.Multiline | RegexOptions.Compiled);

    [Fact]
    public void DecisionsRowExists_forClassSystem()
    {
        var decisionsPath = Path.Combine(KeepverseRoots.Workspace(), "docs", "architecture", "decisions.md");
        var text = ReadNormalized(decisionsPath);
        var row = FindRow(text, "Class system");

        Assert.True(row is not null, "decisions.md has no 'Class system' row — AGENTS.md requires one before this program's architecture changes lock behavior.");
        // The index proves the ROW exists; the category file the row links to carries the RULE. Asserting
        // rule text against the index asserts against a file defined not to hold it — AGENTS.md: "The two
        // lock files are indexes, not documents. Each row is one line; the rule text lives in the category
        // file the row links to." Measured: all nine needles are absent from the index and present in
        // decisions/progression.md, whose line 21 carries the whole row.
        var rule = CategoryTextFor(row!, decisionsPath);
        Assert.Contains("free build", rule, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Zomboss AI patterns", rule, StringComparison.Ordinal);
        Assert.Contains("Twelve aptitudes", rule, StringComparison.Ordinal);
        Assert.Contains("sources, not registered channels", rule, StringComparison.Ordinal);
        Assert.Contains("sum of four scopes", rule, StringComparison.Ordinal);
        Assert.Contains("Win rate is the metric", rule, StringComparison.Ordinal);
        Assert.Contains("HARD and blocks the build", rule, StringComparison.Ordinal);
        Assert.Contains("SOFT and reports", rule, StringComparison.Ordinal);
        Assert.Contains("No aptitude cap and no respec cap", rule, StringComparison.Ordinal);
    }

    [Fact]
    public void ResourceModelRow_readsSixAndAgreesWithCodeAndRoster()
    {
        var repoRoot = FindRepoRoot();
        var decisionsPath = Path.Combine(KeepverseRoots.Workspace(), "docs", "architecture", "decisions.md");
        var decisionsText = ReadNormalized(decisionsPath);
        var row = FindRow(decisionsText, "Resource model");
        Assert.True(row is not null, "decisions.md has no 'Resource model' row.");

        // Same two-step gate as the row above: the index for the row, the linked category file for the
        // substance. Neither is inferred from the other.
        var rule = CategoryTextFor(row!, decisionsPath);
        Assert.Contains("Six actor resources", rule, StringComparison.Ordinal);
        Assert.Contains("`poise`", rule, StringComparison.Ordinal);
        Assert.Contains("no longer claims guard", rule, StringComparison.Ordinal);

        // The row's own headline number must equal the code's registered list -- not a separately
        // maintained count that could drift the moment either side changes.
        Assert.Equal(6, DerivedStatChannels.ResourceIds.Count);
        Assert.Contains("poise", DerivedStatChannels.ResourceIds);

        var rosterPath = Path.Combine(KeepverseRoots.Content(), "data", "seed", "resources", "roster.json");
        var rosterIds = ExtractRosterIdsInOrdinalOrder(rosterPath);
        Assert.Equal(DerivedStatChannels.ResourceIds, rosterIds);
    }

    // decisions.md ships CRLF -- a bare `$` under RegexOptions.Multiline matches immediately before
    // `\n`, so a row ending "...|\r\n" leaves `\r` as the last character before that boundary and a
    // pattern ending in a literal `\|$` never matches. Normalizing once here is cheaper than teaching
    // every pattern below about `\r?$`.
    static string ReadNormalized(string path) => File.ReadAllText(path).Replace("\r\n", "\n");

    static string? FindRow(string tableText, string topicPrefix)
    {
        foreach (System.Text.RegularExpressions.Match m in RowPattern.Matches(tableText))
        {
            var topic = m.Groups[1].Value.Trim();
            // Topic cells carry a trailing "(YYYY-MM-DD[, note])" — compare the stable prefix only.
            var bareTopic = Regex.Replace(topic, @"\s*\(.*\)\s*$", "").Trim();
            if (string.Equals(bareTopic, topicPrefix, StringComparison.Ordinal))
                return m.Value;
        }
        return null;
    }

    /// <summary>
    /// The category file an index row links to, resolved relative to the index.
    ///
    /// <para><b>Why this exists.</b> <c>decisions.md</c> is an INDEX: one row per decision, with the rule
    /// text in <c>decisions/&lt;category&gt;.md</c>. AGENTS.md is explicit — "The two lock files are
    /// indexes, not documents. Each row is one line; the rule text lives in the category file the row
    /// links to" — and the two gates in this class asserted rule text IN the index. Measured: all nine
    /// needles are absent from the index and present in <c>decisions/progression.md</c>, whose line 21
    /// carries the whole row including "**Free build: the player has no class.**"
    ///
    /// <para>So each gate is now two checks and both matter: the index must carry the row — that is the
    /// hard boundary, "architecture changes that lock behavior need decisions.md first" — AND the file the
    /// row links to must carry the substance. Neither is inferred from the other, and a row pointing at a
    /// category file that does not exist is a refusal rather than a silent pass.
    /// </para></summary>
    static string CategoryTextFor(string indexRow, string decisionsPath)
    {
        var link = Regex.Match(indexRow, @"\]\((?<path>[^)]+\.md)\)");
        Assert.True(link.Success, "the index row carries no link to a category file: " + indexRow);
        var indexDir = Path.GetDirectoryName(decisionsPath)!;
        var categoryPath = Path.GetFullPath(Path.Combine(indexDir, link.Groups["path"].Value));
        Assert.True(File.Exists(categoryPath),
                    "the index row links to a category file that does not exist: " + categoryPath
                    + " (from " + indexRow.Trim() + ")");
        return ReadNormalized(categoryPath);
    }

    static List<string> ExtractRosterIdsInOrdinalOrder(string rosterPath)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(rosterPath));
        var entries = doc.RootElement.GetProperty("entries").EnumerateArray()
            .Select(e => (Id: e.GetProperty("id").GetString()!, Ordinal: e.GetProperty("ordinal").GetInt32()))
            .OrderBy(e => e.Ordinal)
            .Select(e => e.Id)
            .ToList();
        return entries;
    }

    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
