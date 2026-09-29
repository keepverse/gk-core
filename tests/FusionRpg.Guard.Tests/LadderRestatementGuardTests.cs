using System.Text.RegularExpressions;
using Xunit;
using FusionRpg.TestSupport;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// tier-propagation-contract T-2 (spec-tier-propagation-contract.md Testing strategy, Success
/// criteria 3): no C# or Python file outside the declaring site restates a ladder's ids. The
/// rarity ladder is declared by <c>gk-data/packs/fusion/data/seed/rarity/ladder.v1.json</c> (read in C# by the
/// <c>AtomSeedFile.ReadRarity</c> import pipeline and in seedsmith by <c>seedsmith.ladders</c>);
/// the threat ladder by <c>gk-core/data/tuning/creature-threat.v2.json</c> (read in C# by
/// <c>CreatureThreatTuningLoader</c>, in seedsmith by the same leaf module).
///
/// A restatement is any line carrying five or more distinct quoted ids from one ladder — the
/// shape every removed tuple used (five ids per line). Four or fewer stays green on purpose: the
/// legacy→rung forward maps (C#'s own <c>LegacyCreatureRarityIds.ForwardMap</c>, seedsmith's
/// <c>LEGACY_SHARD_FORWARD_MAP</c> pin) name four rung ids as mapping <i>values</i> — consumers of
/// the ladder, not restatements of it — as do the three-id subset vocabularies and single-id
/// membership checks.
///
/// <c>Items/RarityLadder.cs</c> is the one sanctioned exception: it is allowlisted as the
/// boot-time source the seed import validates against (spec Open question 1), and
/// <c>RarityLadderSeedAgreementTests</c> pins it value-for-value against the declaring file.
/// </summary>
public class LadderRestatementGuardTests
{
    static readonly string[] RarityIds =
    {
        "chaff", "sprout", "grafted", "cultivated", "fused",
        "chimeric", "heirloom", "firstseed", "sunwoven", "almanac",
    };

    static readonly string[] ThreatIds =
    {
        "nuisance", "pest", "marauder", "raider", "warden",
        "scourge", "tyrant", "harbinger", "cataclysm", "calamity",
    };

    const string AllowlistedFileName = "RarityLadder.cs";

    [Fact]
    public void No_file_restates_a_ladder_outside_its_declaring_site()
    {
        var violations = Scan();
        Assert.True(violations.Count == 0,
            "ladder restatement(s) found — read the declaring file instead " +
            "(rarity: data/seed/rarity/ladder.v1.json via seedsmith.ladders; threat: " +
            "data/tuning/creature-threat.v2.json):\n" + string.Join("\n", violations));
    }

    // ---- The scanner is itself exercised directly, so a vacuous sweep can't pass by accident. ----

    [Theory]
    [InlineData("    \"chaff\", \"sprout\", \"grafted\", \"cultivated\", \"fused\",")]
    [InlineData("    \"chimeric\", \"heirloom\", \"firstseed\", \"sunwoven\", \"almanac\",")]
    [InlineData("    \"scourge\", \"tyrant\", \"harbinger\", \"cataclysm\", \"calamity\",")]
    [InlineData("RARITY = (\n    \"chaff\", \"sprout\", \"grafted\", \"cultivated\", \"fused\",")]
    public void Scanner_catches_restatement_shapes(string line) =>
        Assert.True(IsRestatement(line), $"scanner missed: {line}");

    [Theory]
    [InlineData("BANDS = (\"firstseed\", \"sunwoven\", \"almanac\")")] // 3-id subset vocabulary, not a ladder
    [InlineData("if band not in (\"firstseed\", \"sunwoven\", \"almanac\"):")]
    [InlineData("rarityId is \"heirloom\" or \"sunwoven\"")] // single-id membership checks
    [InlineData("\"common\": \"chaff\", \"rare\": \"cultivated\", \"epic\": \"heirloom\", \"legendary\": \"sunwoven\",")] // legacy forward map values: consumers, not a restatement
    [InlineData("RARITY = _RARITY_LADDER")] // the declaring-read alias itself
    [InlineData("THREAT_BAND = _THREAT_BAND[6:]  # rungs 7-10")] // a derived slice, never a retype
    [InlineData("// \"chaff\", \"sprout\", \"grafted\", \"cultivated\", \"fused\",")] // comments are prose
    [InlineData("# \"chaff\", \"sprout\", \"grafted\", \"cultivated\", \"fused\",")]
    public void Scanner_does_not_flag_safe_shapes(string line) =>
        Assert.False(IsRestatement(line), $"scanner false-positived on: {line}");

    static bool IsRestatement(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("///", StringComparison.Ordinal) ||
            trimmed.StartsWith("//", StringComparison.Ordinal) ||
            trimmed.StartsWith("#", StringComparison.Ordinal) ||
            trimmed.StartsWith("\"\"\"", StringComparison.Ordinal))
            return false;

        return CountDistinctQuotedIds(line, RarityIds) >= 5 ||
               CountDistinctQuotedIds(line, ThreatIds) >= 5;
    }

    static int CountDistinctQuotedIds(string line, string[] ids)
    {
        var count = 0;
        foreach (var id in ids)
        {
            // A quoted occurrence: "..." or '...' — unquoted prose mentions don't count.
            if (line.Contains($"\"{id}\"", StringComparison.Ordinal) ||
                line.Contains($"'{id}'", StringComparison.Ordinal))
                count++;
        }
        return count;
    }

    static List<string> Scan()
    {
        var root = FindRepoRoot();
        var violations = new List<string>();
        foreach (var (dir, pattern) in new[]
                 {
                     (Path.Combine(root, "src"), "*.cs"),
                     (Path.Combine(root, "tools", "seedsmith"), "*.py"),
                 })
        {
            foreach (var file in EnumerateSourceFiles(dir, pattern))
            {
                if (Path.GetFileName(file) == AllowlistedFileName) continue;
                var lineNum = 0;
                foreach (var line in File.ReadLines(file))
                {
                    lineNum++;
                    if (IsRestatement(line))
                        violations.Add($"{Path.GetRelativePath(root, file)}:{lineNum}: {line.Trim()}");
                }
            }
        }
        return violations;
    }

    /// <summary>
    /// Directories that are not source and must never be scanned: build output, dependency trees, and
    /// the dot-prefixed throwaways a local tool run leaves behind (<c>.venv</c>, <c>.pytest_cache</c>,
    /// <c>tools/seedsmith/.tmp-*</c>). All are gitignored.
    /// </summary>
    static readonly string[] NotSourceDirs = { "obj", "bin", "node_modules", "__pycache__", "artifacts" };

    /// <summary>
    /// Source files under <paramref name="dir"/>, skipping everything in <see cref="NotSourceDirs"/> and
    /// every dot-prefixed directory.
    ///
    /// <para>⚠️ This replaced a plain <c>EnumerateFiles(dir, pattern, SearchOption.AllDirectories)</c>,
    /// which had two faults. It scanned directories that are not source at all, so a stray local
    /// artifact could fail a guard about the power ladder. And a single unreadable directory took the
    /// whole test down rather than being skipped: a real
    /// <c>UnauthorizedAccessException: Access to the path 'tools/seedsmith/.tmp-seedsmith-pytest-audit'
    /// is denied</c> (a gitignored pytest leftover whose ACL denies even <c>icacls</c>) failed this
    /// guard from 2026-09-10 until it was found on 2026-09-17. A guard that a leftover directory can
    /// veto is not guarding anything — it is reporting on the filesystem.</para>
    /// </summary>
    static IEnumerable<string> EnumerateSourceFiles(string dir, string pattern)
    {
        if (!Directory.Exists(dir)) yield break;

        foreach (var file in SafeFiles(dir, pattern))
            yield return file;

        foreach (var sub in SafeDirs(dir))
        {
            var name = Path.GetFileName(sub);
            if (name.StartsWith(".", StringComparison.Ordinal)) continue;
            if (NotSourceDirs.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            foreach (var file in EnumerateSourceFiles(sub, pattern))
                yield return file;
        }
    }

    static string[] SafeFiles(string dir, string pattern)
    {
        try { return Directory.GetFiles(dir, pattern); }
        catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
        catch (DirectoryNotFoundException) { return Array.Empty<string>(); }
    }

    static string[] SafeDirs(string dir)
    {
        try { return Directory.GetDirectories(dir); }
        catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
        catch (DirectoryNotFoundException) { return Array.Empty<string>(); }
    }

    static string FindRepoRoot() => CoreRoot.Path;
}
