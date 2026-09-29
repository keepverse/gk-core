using System.Text.RegularExpressions;
using FusionRpg.Core.Stats.Derived;
using Xunit;

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
        var text = ReadNormalized(Path.Combine(FindRepoRoot(), "docs", "architecture", "decisions.md"));
        var row = FindRow(text, "Class system");

        Assert.True(row is not null, "decisions.md has no 'Class system' row — AGENTS.md requires one before this program's architecture changes lock behavior.");
        Assert.Contains("free build", row, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Zomboss AI patterns", row, StringComparison.Ordinal);
        Assert.Contains("Twelve aptitudes", row, StringComparison.Ordinal);
        Assert.Contains("sources, not registered channels", row, StringComparison.Ordinal);
        Assert.Contains("sum of four scopes", row, StringComparison.Ordinal);
        Assert.Contains("Win rate is the metric", row, StringComparison.Ordinal);
        Assert.Contains("HARD and blocks the build", row, StringComparison.Ordinal);
        Assert.Contains("SOFT and reports", row, StringComparison.Ordinal);
        Assert.Contains("No aptitude cap and no respec cap", row, StringComparison.Ordinal);
    }

    [Fact]
    public void ResourceModelRow_readsSixAndAgreesWithCodeAndRoster()
    {
        var repoRoot = FindRepoRoot();
        var decisionsText = ReadNormalized(Path.Combine(repoRoot, "docs", "architecture", "decisions.md"));
        var row = FindRow(decisionsText, "Resource model");
        Assert.True(row is not null, "decisions.md has no 'Resource model' row.");

        Assert.Contains("Six actor resources", row, StringComparison.Ordinal);
        Assert.Contains("`poise`", row, StringComparison.Ordinal);
        Assert.Contains("no longer claims guard", row, StringComparison.Ordinal);

        // The row's own headline number must equal the code's registered list -- not a separately
        // maintained count that could drift the moment either side changes.
        Assert.Equal(6, DerivedStatChannels.ResourceIds.Count);
        Assert.Contains("poise", DerivedStatChannels.ResourceIds);

        var rosterPath = Path.Combine(repoRoot, "data", "seed", "resources", "roster.json");
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
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Injector"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("repo root");
    }
}
