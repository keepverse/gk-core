using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

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
        var dir = new DirectoryInfo(Path.GetDirectoryName(here)!);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Server"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("could not find the repo root above the test sources");
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
    /// a rule naming one file covers only that file.</summary>
    public static IReadOnlyList<(string BaseDir, string? Exact)> CopyRules(string csprojSource)
    {
        var rules = new List<(string, string?)>();
        foreach (Match rule in Regex.Matches(csprojSource, @"<Content\s+Include=""(?<inc>[^""]+)"""))
        {
            var include = rule.Groups["inc"].Value.Replace('\\', '/').TrimStart('.', '/');
            var wildcard = include.IndexOfAny(new[] { '*', '%' });
            if (wildcard < 0) rules.Add((include, include));
            else rules.Add((include[..wildcard].TrimEnd('/'), null));
        }
        return rules;
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
