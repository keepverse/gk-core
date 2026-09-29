using System.Text.RegularExpressions;
using Xunit;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// seed-to-concrete T4.1 (spec-rarity-migration.md §3, §7 step 3): a bare
/// <c>(CreatureRarity)((int)r - 1)</c> cast or a bare <c>r &gt;= CreatureRarity.Rare</c> comparison compiles
/// at ANY enum width and silently changes what fraction of the ten-rung ladder it covers — the exact
/// defect <c>CreatureRecipeCatalog.cs</c> shipped with until this migration's own tests caught it (a
/// static-init-order bug hid a related landmine in the same method). The named helpers in
/// <c>CreatureRarityLadder.cs</c> (OneRungAbove/OneRungBelow/RungsBelow/IsTopRung/IsBottomRung/
/// AtLeast/AtMost) are the fix; this guard is what keeps the bare forms from coming back.
///
/// Scoped to <c>src/</c> only, matching <c>DalGuardTests</c>' own convention — test fixtures and
/// tooling are not gameplay code and are exempt.
/// </summary>
public class CreatureRarityLadderGuardTests
{
    // (CreatureRarity) immediately followed by '(' (a nested expression) or an identifier/space —
    // the cast shape named in the spec. CreatureRarityLadder.cs itself is the one sanctioned exception:
    // its OneRungAbove/OneRungBelow/RungsBelow bodies cast (int)<->CreatureRarity by design.
    static readonly Regex BareCast = new(@"\(CreatureRarity\)\s*[\(\w]", RegexOptions.Compiled);

    // CreatureRarity.<Member> immediately adjacent (only whitespace between) to a relational operator,
    // either direction. Deliberately excludes ==/!= (equality against a named member, e.g.
    // IsTopRung's `rarity == CreatureRarity.Almanac`, is not the landmine — only <,>,<=,>= are, because
    // those are the ones whose MEANING changes when the ladder widens).
    static readonly Regex RelationalCompare = new(
        @"CreatureRarity\.\w+\s*(>=|<=|(?<![=!<>])[<>](?!=))|(?<![=!<>])[<>](?!=)\s*CreatureRarity\.\w+|(>=|<=)\s*CreatureRarity\.\w+",
        RegexOptions.Compiled);

    const string LadderHelperFileName = "CreatureRarityLadder.cs";

    [Fact]
    public void No_bare_cast_between_int_and_CreatureRarity_outside_the_ladder_helper()
    {
        var violations = ScanSrc(BareCast, exemptFileName: LadderHelperFileName);
        Assert.True(violations.Count == 0,
            "bare (CreatureRarity) cast(s) found outside CreatureRarityLadder.cs — use OneRungAbove/" +
            "OneRungBelow/RungsBelow instead:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void No_relational_comparison_against_a_named_CreatureRarity_member()
    {
        // No exemption: CreatureRarityLadder.cs's own AtLeast/AtMost compare (int)rarity against
        // (int)threshold, never a bare `CreatureRarity.X` relational — so the ladder helper itself is
        // clean under this pattern too, and nothing needs to be excused.
        var violations = ScanSrc(RelationalCompare, exemptFileName: null);
        Assert.True(violations.Count == 0,
            "relational comparison against a named CreatureRarity member found — use AtLeast/AtMost/" +
            "IsTopRung/IsBottomRung instead:\n" + string.Join("\n", violations));
    }

    // ---- The scanner is itself exercised directly, so a vacuously-empty src/ sweep can't pass by
    // accident — these two pin the scanner catches the exact shapes named in the spec. ----

    [Theory]
    [InlineData("var x = (CreatureRarity)((int)output.BaseRarity - 1);")]
    [InlineData("var x = (CreatureRarity)value;")]
    public void Scanner_catches_the_bare_cast_shape(string line) =>
        Assert.True(BareCast.IsMatch(line), $"scanner missed: {line}");

    [Theory]
    [InlineData("s.BaseRarity >= CreatureRarity.Rare")]
    [InlineData("CreatureRarity.Rare <= rarity")]
    [InlineData("rarity > CreatureRarity.Chaff")]
    [InlineData("rarity < CreatureRarity.Almanac")]
    public void Scanner_catches_the_relational_comparison_shape(string line) =>
        Assert.True(RelationalCompare.IsMatch(line), $"scanner missed: {line}");

    [Theory]
    [InlineData("rarity == CreatureRarity.Almanac")] // equality is not the landmine
    [InlineData("rarity != CreatureRarity.Chaff")]
    [InlineData("Dictionary<CreatureRarity, int> x")] // a generic type arg, not a comparison
    [InlineData("IReadOnlyList<CreatureRarity> All")]
    [InlineData(".OrderBy(s => s.BaseRarity)")] // spec §3: ordering by ordinal is safe at any width
    public void Scanner_does_not_flag_safe_shapes(string line) =>
        Assert.False(RelationalCompare.IsMatch(line), $"scanner false-positived on: {line}");

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
                // Doc/line comments are prose ABOUT the forbidden shape (this guard's own summary,
                // CreatureRecipeCatalog.cs's "never a bare cast" note), not live code — exempt them.
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
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Injector"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("repo root");
    }
}
