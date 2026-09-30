using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Server.Tests;

/// <summary>
/// Found live 2026-09-19 (action-skill-tiers ST4.5): a PUBLISHED server imported zero actions, because
/// `Program.cs` reads three brief files straight off its own directory and no `.csproj` rule ever copied
/// them next to the exe. Every in-process test passed, since a test host walks up into the repository and
/// finds the real tree — the exact blind spot that let the dungeon, item and structure trees ship the
/// same way before them (each named in `FusionRpg.Server.csproj`'s own comments).
///
/// <para>These tests read the two files that disagree and require them to agree: a content path
/// `Program.cs` reads at boot must be covered by a copy rule, and the covered files must actually be
/// there. It asserts a CONTRACT (every boot-read path has a rule), never a file count or a list — adding
/// content must not fail it, and deleting a copy rule must.</para>
///
/// <para><b>What it does not cover.</b> Trees a boot reader derives for itself rather than naming as a
/// literal — `PassiveTreeImportRunner`'s `gk-data/packs/fusion/data/generated/passive-tree` — are reached through a walk-up
/// from the exe, so no `Path.Combine(AppContext.BaseDirectory, ...)` names them and this scan cannot see
/// them. They are the same defect class and are recorded separately rather than silently assumed fixed.</para>
///
/// <para><b>The scanner-derived half is now covered too (KS-F1, lane `findings-1`, 2026-09-23).</b>
/// `SeedScanner.OwnedFolders` is the code's own closed list of folders the boot import sweeps, and a
/// folder on that list with no copy rule is a published server that imports a PARTIAL tree and reports
/// `Imported` — `containers`, `curves`, `rarity`, `elements`, `channel-policy`, `channel-pools`,
/// `effects/affixes` and `creatures/species-effects` (12 committed files) had no rule until this case
/// forced them. The join reads the scanner's list, never a maintained copy of it, so adding a swept
/// folder fails here until its rule lands.</para>
/// </summary>
public class BootContentCopyRuleTests
{
    /// <summary>The shipped-content roots a boot read may name. `Program.cs` also resolves runtime state
    /// under its own directory (`data/rpg-hot.sqlite`, `artifacts/`) — that is not shipped content and
    /// correctly has no rule.</summary>
    static readonly string[] ShippedRoots = { "data/seed/", "data/tuning/", "data/generated/", "content/" };

    static string RepoRoot([CallerFilePath] string here = "")
    {
        return KeepverseRoots.Core();
    }

    static string ProgramSource() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "src", "FusionRpg.Server", "Program.cs"));

    static string CsprojSource() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "src", "FusionRpg.Server", "FusionRpg.Server.csproj"));

    /// <summary>Every `Path.Combine(AppContext.BaseDirectory, "a", "b", ...)` literal in `Program.cs`,
    /// as a repo-relative slash path, restricted to shipped content. Multi-line calls are matched too,
    /// which matters: several of the reads this guards are wrapped across lines.</summary>
    public static IReadOnlyList<string> BootReadPaths(string programSource)
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match call in Regex.Matches(
                     programSource,
                     @"Path\.Combine\(\s*AppContext\.BaseDirectory\s*(?<segments>(?:,\s*""[^""]*"")+)",
                     RegexOptions.Singleline))
        {
            var segments = Regex.Matches(call.Groups["segments"].Value, @"""(?<s>[^""]*)""")
                .Select(m => m.Groups["s"].Value)
                .Where(s => s.Length > 0);
            var path = string.Join('/', segments);
            if (ShippedRoots.Any(root => path.StartsWith(root, StringComparison.Ordinal))) found.Add(path);
        }
        return found.ToList();
    }

    /// <summary>Each rule's coverage as (base directory, exact file). A glob rule covers its directory;
    /// a rule naming one file covers only that file.
    ///
    /// <para><b>Coverage is the path NEXT TO THE EXE, so this reads a rule's <c>&lt;Link&gt;</c> when it
    /// has one.</b> The two attributes are different coordinate systems: <c>Include</c> is a source path
    /// and <c>Link</c> is the output path, and the only thing a boot reader ever touches is the output
    /// path. After the split, <c>Include</c> became <c>$(GkDataRoot)packs\fusion\data\seed\...</c> - an
    /// MSBuild property this has no way to resolve - while <c>Link</c> stayed the plain
    /// <c>data\seed\...</c> the guard compares against. Comparing the wrong one made every rewritten rule
    /// invisible, and 17 swept folders plus 23 boot-read paths reported as uncovered while the sibling test
    /// confirmed the files really were being copied. That test is the reason this is a parser fix and not a
    /// missing-rules fix.</para>
    ///
    /// <para>A rule with no <c>&lt;Link&gt;</c> still uses its <c>Include</c>, which is what every rule in
    /// the pre-split tree had. The contract this class asserts is unchanged by either: a boot-read path with
    /// no covering rule is still uncovered, so deleting a copy rule still fails.</para></summary>
    public static IReadOnlyList<(string BaseDir, string? Exact)> CopyRules(string csprojSource)
    {
        var rules = new List<(string, string?)>();
        foreach (Match rule in Regex.Matches(csprojSource, @"<Content\s+Include=""(?<inc>[^""]+)"""))
        {
            // The element's own body, up to its close. A <Link> is a child of the <Content> that
            // declared it, so pairing them needs the element boundary rather than a global search -
            // otherwise one rule's Link would be credited to the rule above it.
            var tail = rule.Length < csprojSource.Length ? csprojSource[rule.Length..] : string.Empty;
            var close = tail.IndexOf("</Content>", StringComparison.Ordinal);
            var selfClose = tail.IndexOf("/>", StringComparison.Ordinal);
            if (close < 0 || (selfClose >= 0 && selfClose < close)) close = selfClose;
            var body = close < 0 ? tail : tail[..close];
            var link = Regex.Match(body, @"<Link>(?<l>[^<]+)</Link>");

            var spec = link.Success
                ? link.Groups["l"].Value
                : rule.Groups["inc"].Value;
            var path = spec.Replace('\\', '/').TrimStart('.', '/');
            var wildcard = path.IndexOfAny(new[] { '*', '%' });
            if (wildcard < 0) rules.Add((path, path));
            else rules.Add((path[..wildcard].TrimEnd('/'), null));
        }
        return rules;
    }

    /// <summary>A differential for the guard's own parser, run in the same process on the same string.
    ///
    /// <para>There are two things a reader must not have to guess: which bases <see cref="CopyRules"/>
    /// produces from the real csproj, and whether that list is what the arithmetic says it should be. This
    /// asserts both, and when they disagree it names the first rule that differs rather than printing two
    /// lists and leaving the comparison to the reader.</para>
    ///
    /// <para>The reference below is deliberately the same arithmetic, written out again. That is not a
    /// second opinion about what the parser SHOULD do - it is a check that the deployed function does what
    /// its own source says, on the actual file, in the actual test host. A model compared against a model
    /// can only prove they agree with each other; a model compared against the deployed function, here,
    /// can prove a divergence - which is the only reason the thirteen uncovered folders have stayed
    /// unexplained across three passes.</para>
    ///
    /// <para>Asserting equality of two lists is not a population pin: it fails when the parser's behaviour
    /// changes, and never fails because content was added.</para></summary>
    [Fact]
    public void CopyRules_agrees_with_the_same_arithmetic_written_out_on_the_real_csproj()
    {
        var csproj = CsprojSource();
        var actual = CopyRules(csproj).Select(r => r.BaseDir).ToList();

        var expected = new List<string>();
        foreach (Match rule in Regex.Matches(csproj, @"<Content\s+Include=""(?<inc>[^""]+)"""))
        {
            var tail = csproj[rule.Length..];
            var close = tail.IndexOf("</Content>", StringComparison.Ordinal);
            var selfClose = tail.IndexOf("/>", StringComparison.Ordinal);
            if (close < 0 || (selfClose >= 0 && selfClose < close)) close = selfClose;
            var body = close < 0 ? tail : tail[..close];
            var link = Regex.Match(body, @"<Link>(?<l>[^<]+)</Link>");
            var spec = link.Success ? link.Groups["l"].Value : rule.Groups["inc"].Value;
            var path = spec.Replace('\\', '/').TrimStart('.', '/');
            var wildcard = path.IndexOfAny(new[] { '*', '%' });
            expected.Add(wildcard < 0 ? path : path[..wildcard].TrimEnd('/'));
        }

        var firstDiff = expected.Zip(actual, (e, a) => (e, a))
            .Select((pair, i) => (pair, i))
            .FirstOrDefault(x => x.pair.e != x.pair.a);
        Assert.True(
            firstDiff.pair.e == firstDiff.pair.a,
            $"CopyRules diverges from its own arithmetic at rule #{firstDiff.i}: "
            + $"deployed produced '{firstDiff.pair.a}', the same arithmetic gives '{firstDiff.pair.e}'. "
            + $"deployed bases: {string.Join(" | ", actual)}");
    }

    /// <summary>Reports what <see cref="CopyRules"/> actually parses, from the real csproj, through the
    /// same reader the coverage checks use.
    ///
    /// <para>This exists because a second, independent re-implementation of this parser - same Include/Link
    /// pairing, same normalisation, same wildcard truncation - says all seventeen swept folders are covered,
    /// while this class says thirteen are not. Two implementations of one function disagreeing is not
    /// something to resolve by modelling the function a third time. It is resolved by having the function
    /// say what it produced, and this assertion is written so that message lands in the failure output: if
    /// <c>gk-data/packs/fusion/data/seed/atoms</c> is absent from the parsed bases, the parser disagrees with the model and the
    /// printed list is the evidence; if it is present, the parser is right and <see cref="Covered"/> is what
    /// disagrees.</para>
    ///
    /// <para>It asserts a real property (this tree's dungeon rule is a directory rule over a known base) and
    /// not a count, so it does not become the population-pin this repo forbids.</para></summary>
    [Fact]
    public void The_parsed_rule_bases_are_what_the_coverage_check_compares_against()
    {
        var rules = CopyRules(CsprojSource());
        var bases = rules.Select(r => r.BaseDir).ToList();
        Assert.True(
            bases.Contains("data/seed/dungeon"),
            "parsed rule bases: " + string.Join(" | ", bases));
    }

    /// <summary>The guard still bites: a rule that is not there is still uncovered. This exists because
    /// the fix above makes the parser able to see rules written as MSBuild properties, and a parser that
    /// suddenly matches more is exactly the kind of change that can also stop noticing a deletion. It is
    /// asserted against a synthetic csproj rather than by editing the real one, so the proof is permanent
    /// and does not depend on anyone remembering to break the build.</summary>
    [Fact]
    public void A_rule_that_is_absent_is_still_reported_uncovered()
    {
        const string csproj = """
            <Project>
              <ItemGroup>
                <Content Include="$(GkDataRoot)packs\fusion\data\seed\dungeon\**\*.json">
                  <Link>data\seed\dungeon\%(RecursiveDir)%(Filename)%(Extension)</Link>
                </Content>
              </ItemGroup>
            </Project>
            """;
        var rules = CopyRules(csproj);

        // Present: covered, by the Link rather than by the unresolvable Include.
        Assert.True(Covered("data/seed/dungeon/rooms", rules), "the rule that exists must cover its own folder");
        // Absent: a deleted rule's folder is uncovered.
        Assert.False(Covered("data/seed/items/charms", rules), "a deleted rule must not be reported as covering");
        // And the prefix semantics the class documents still hold: a rule covering a directory covers
        // files beneath it, which is what makes a whole-tree rule sufficient.
        Assert.True(Covered("data/seed/dungeon/_registry/room-kinds.v1.json", rules),
                    "a directory rule must still cover files beneath it");
    }

    /// <summary>A boot read names either a file or a whole directory (`gk-data/packs/fusion/data/seed/loot`), so a rule covers
    /// it when the rule's own base is that path or an ancestor of it. That equality is what makes this
    /// guard catch the live defect: before ST4.5 no rule had `gk-data/packs/fusion/data/seed/actions` as its base at all.</summary>
    static bool Covered(string path, IReadOnlyList<(string BaseDir, string? Exact)> rules) =>
        rules.Any(r => r.Exact is { } exact
            ? string.Equals(path, exact, StringComparison.Ordinal)
            : string.Equals(path, r.BaseDir, StringComparison.Ordinal)
              || path.StartsWith(r.BaseDir + "/", StringComparison.Ordinal));

    [Fact]
    public void Every_content_path_the_server_reads_at_boot_is_covered_by_a_copy_rule()
    {
        var bootReads = BootReadPaths(ProgramSource());
        Assert.NotEmpty(bootReads);   // a regex that stopped matching would otherwise pass vacuously

        var rules = CopyRules(CsprojSource());
        var uncovered = bootReads.Where(p => !Covered(p, rules)).ToList();

        Assert.True(uncovered.Count == 0,
                    "these paths are read at boot but no <Content> rule copies them next to the exe, so a "
                    + "published server silently boots without them: " + string.Join(", ", uncovered));
    }

    [Fact]
    public void Every_seed_folder_the_boot_import_sweeps_is_covered_by_a_copy_rule()
    {
        // The join, not a list: `SeedScanner.OwnedFolders` is the atom import's own closed input set
        // (`AtomRoots` sweeps `AtomFolders`; the dungeon loaders read `DungeonFolders`), so a folder it
        // names with no rule is content the boot cannot see beside its own exe.
        var swept = FusionRpg.Data.Seed.SeedScanner.OwnedFolders.Select(f => "data/seed/" + f).ToList();
        Assert.NotEmpty(swept);   // a scanner that stopped naming folders would otherwise pass vacuously

        var rules = CopyRules(CsprojSource());
        var uncovered = swept.Where(p => !Covered(p, rules)).ToList();

        Assert.True(uncovered.Count == 0,
                    "these folders are swept by the boot import but no <Content> rule copies them next to "
                    + "the exe, so a published server imports a partial tree and reports Imported: "
                    + string.Join(", ", uncovered));
    }

    [Fact]
    public void Every_swept_seed_folder_that_exists_in_the_repo_is_present_next_to_this_test_host()
    {
        // The rule landed and is effective (a wrong Link or a swallowing Exclude fails here).
        var root = RepoRoot();
        var missing = new List<string>();

        foreach (var folder in FusionRpg.Data.Seed.SeedScanner.OwnedFolders)
        {
            var relative = "data/seed/" + folder;
            var repoPath = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(repoPath)) continue;   // not on this checkout
            // A folder holding no *.json has nothing to copy (`gk-data/packs/fusion/data/seed/curves` is a README today).
            if (!Directory.EnumerateFiles(repoPath, "*.json", SearchOption.AllDirectories).Any()) continue;
            var copied = Path.Combine(AppContext.BaseDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(copied)) missing.Add(relative);
        }

        Assert.True(missing.Count == 0,
                    "these swept seed folders exist in the repo but were not copied next to the test host: "
                    + string.Join(", ", missing));
    }

    [Fact]
    public void Every_boot_read_path_that_exists_in_the_repo_is_present_next_to_this_test_host()
    {
        // The second half of the contract, on the one output this test can inspect: the copy rule
        // actually landed. A rule that is present but ineffective (a wrong Link, an Exclude that
        // swallows everything) fails here and nowhere else.
        var root = RepoRoot();
        var missing = new List<string>();

        foreach (var path in BootReadPaths(ProgramSource()))
        {
            var repoPath = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(repoPath) && !File.Exists(repoPath)) continue;   // not on this checkout

            var copied = Path.Combine(AppContext.BaseDirectory, path.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(repoPath) && !File.Exists(copied)) missing.Add(path);
            else if (Directory.Exists(repoPath) && !Directory.Exists(copied)) missing.Add(path);
        }

        Assert.True(missing.Count == 0,
                    "these boot-read paths exist in the repo but were not copied next to the test host: "
                    + string.Join(", ", missing));
    }
}
