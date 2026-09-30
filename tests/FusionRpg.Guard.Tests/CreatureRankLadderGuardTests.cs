using System.Text.RegularExpressions;
using Xunit;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// spec-species-rank.md §3 (Task 2): the rank ladder's own guard, beside
/// <c>CreatureRarityLadderGuardTests</c> and clause-for-clause the same shape — the existing regexes
/// are <c>CreatureRarity</c>-specific, so <see cref="CreatureRank"/> needs its own.
///
/// <para>A bare <c>(CreatureRank)((int)rank - 1)</c> cast, a bare <c>rank &gt;= CreatureRank.X</c>
/// comparison, or a bare <c>(int)rank</c> ordinal read compiles at ANY enum width and silently changes
/// what fraction of the ten-rung ladder it covers — the exact defect the rarity migration shipped with
/// until its own tests caught it. <c>CreatureRankLadder.cs</c>'s named helpers
/// (OneRungAbove/RungsBelow/AtLeast/AtMost) are the fix; this guard keeps the bare forms from coming
/// back.</para>
///
/// <para><b>Honest scope.</b> The two structural rules — a cast to <see cref="CreatureRank"/> and a
/// relational comparison against a named member — catch the shapes by construction. The third rule
/// (a bare <c>(int)</c> read) is <b>name-based</b>: it fires when the cast operand's identifier
/// carries "rank", so a rank value parked in an unrelated local name is not caught by it. The
/// structural pair is the load-bearing one; the name-based rule exists because
/// <c>(int)rank</c> is the literal form the acceptance names.</para>
///
/// <para>Scoped to <c>src/</c> only, matching <c>CreatureRarityLadderGuardTests</c>' own convention —
/// test fixtures and tooling are not gameplay code and are exempt.</para>
/// </summary>
public class CreatureRankLadderGuardTests
{
    // (CreatureRank) immediately followed by '(' (a nested expression) or an identifier/space —
    // the cast shape the spec names. CreatureRankLadder.cs itself is the one sanctioned exception:
    // its OneRungAbove/RungsBelow bodies cast (int)<->CreatureRank by design.
    static readonly Regex BareCast = new(@"\(CreatureRank\)\s*[\(\w]", RegexOptions.Compiled);

    // CreatureRank.<Member> immediately adjacent (only whitespace between) to a relational operator,
    // either direction. Deliberately excludes ==/!= (equality against a named member is not the
    // landmine — only <,>,<=,>= are, because those are the ones whose MEANING changes when the ladder
    // widens).
    static readonly Regex RelationalCompare = new(
        @"CreatureRank\.\w+\s*(>=|<=|(?<![=!<>])[<>](?!=))|(?<![=!<>])[<>](?!=)\s*CreatureRank\.\w+|(>=|<=)\s*CreatureRank\.\w+",
        RegexOptions.Compiled);

    // A bare (int) read of a rank-typed expression — the literal `(int)rank` form, plus the
    // `(int)CreatureRank.X` spelling. Name-based on purpose (see the class summary).
    static readonly Regex BareIntRead = new(@"\(int\)\s*[A-Za-z_]*[Rr]ank", RegexOptions.Compiled);

    const string LadderHelperFileName = "CreatureRankLadder.cs";

    [Fact]
    public void No_bare_cast_between_int_and_CreatureRank_outside_the_ladder_helper()
    {
        var violations = ScanSrc(BareCast, exemptFileName: LadderHelperFileName);
        Assert.True(violations.Count == 0,
            "bare (CreatureRank) cast(s) found outside CreatureRankLadder.cs — use OneRungAbove/" +
            "RungsBelow/AtLeast/AtMost instead:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void No_relational_comparison_against_a_named_CreatureRank_member()
    {
        // No exemption: CreatureRankLadder.cs's own AtLeast/AtMost compare (int)rank against
        // (int)threshold, never a bare `CreatureRank.X` relational — so the ladder helper itself is
        // clean under this pattern too, and nothing needs to be excused.
        var violations = ScanSrc(RelationalCompare, exemptFileName: null);
        Assert.True(violations.Count == 0,
            "relational comparison against a named CreatureRank member found — use AtLeast/AtMost " +
            "instead:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void No_bare_int_read_of_a_rank_outside_the_ladder_helper()
    {
        var violations = ScanSrc(BareIntRead, exemptFileName: LadderHelperFileName);
        Assert.True(violations.Count == 0,
            "bare (int) read of a rank found outside CreatureRankLadder.cs — use the named ladder " +
            "helpers instead:\n" + string.Join("\n", violations));
    }

    // ---- The scanner is itself exercised directly, so a vacuously-empty src/ sweep can't pass by
    // accident — these pin the scanner catches the exact shapes named in the spec. ----

    [Theory]
    [InlineData("var x = (CreatureRank)((int)rank - 1);")]
    [InlineData("var x = (CreatureRank)value;")]
    public void Scanner_catches_the_bare_cast_shape(string line) =>
        Assert.True(BareCast.IsMatch(line), $"scanner missed: {line}");

    [Theory]
    [InlineData("s.Rank >= CreatureRank.Heirloom")]
    [InlineData("CreatureRank.Heirloom <= rank")]
    [InlineData("rank > CreatureRank.Chaff")]
    [InlineData("rank < CreatureRank.Almanac")]
    public void Scanner_catches_the_relational_comparison_shape(string line) =>
        Assert.True(RelationalCompare.IsMatch(line), $"scanner missed: {line}");

    [Theory]
    [InlineData("var x = (int)rank;")]
    [InlineData("var x = (int)CreatureRank.Fused;")]
    [InlineData("var x = (int)speciesRank;")]
    public void Scanner_catches_the_bare_int_read_shape(string line) =>
        Assert.True(BareIntRead.IsMatch(line), $"scanner missed: {line}");

    [Theory]
    [InlineData("rank == CreatureRank.Almanac")] // equality is not the landmine
    [InlineData("rank != CreatureRank.Chaff")]
    [InlineData("Dictionary<CreatureRank, int> x")] // a generic type arg, not a comparison
    [InlineData("IReadOnlyList<CreatureRank> All")]
    [InlineData(".OrderBy(s => s.Rank)")] // ordering by ordinal is safe at any width
    [InlineData("var t = (int)threshold;")] // an int read that does not name a rank
    [InlineData("var n = (int)rungCount;")]
    public void Scanner_does_not_flag_safe_shapes(string line)
    {
        Assert.False(RelationalCompare.IsMatch(line), $"relational scanner false-positived on: {line}");
        Assert.False(BareIntRead.IsMatch(line), $"int-read scanner false-positived on: {line}");
    }

    static List<string> ScanSrc(Regex pattern, string? exemptFileName)
    {
        var srcRoot = Path.Combine(FindRepoRoot(), "src");
        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (exemptFileName is not null && Path.GetFileName(file) == exemptFileName) continue;

            var lineNum = 0;
            foreach (var line in File.ReadLines(file))
            {
                lineNum++;
                var trimmed = line.TrimStart();
                // Doc/line comments are prose ABOUT the forbidden shape (this guard's own summary),
                // not live code — exempt them.
                if (trimmed.StartsWith("///", StringComparison.Ordinal) ||
                    trimmed.StartsWith("//", StringComparison.Ordinal))
                    continue;

                if (pattern.IsMatch(line))
                    violations.Add($"{Path.GetRelativePath(srcRoot, file)}:{lineNum}: {line.Trim()}");
            }
        }
        return violations;
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Core"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("repo root");
    }
}
