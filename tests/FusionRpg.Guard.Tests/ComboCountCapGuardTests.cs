using System.Text.RegularExpressions;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// strain-splice-host SSH6.5 + SSH8.6 — the two R12 source scans that keep the retired per-actor
/// combination cap retired and keep every price blind to what the actor wears.
///
/// <para><b>SSH6.5 (`no_combination_count_cap_exists`).</b> R12 (spec-combo-budget) retired
/// `maxCombosPerActor` / `SocketCombinationCap`: scarcity is circuit geometry plus price, never a
/// backstop count. A source scan is the only enforcement that survives a later "just add a cap" —
/// the tuning's own current revision cannot carry the key because `SOCKETS_OWNED_KEYS` pins the
/// owned-key set, but nothing stopped a new C# field.</para>
///
/// <para><b>SSH8.6 (`no_price_reads_the_actors_worn_combinations`).</b> The workbench's cost path
/// prices the TARGET item and the RECIPE (spec-socket-pricing §5): bore / imbue / insert resolve
/// through <c>RecipeContextFor(playerId, target)</c>, so the price of opening a socket cannot depend
/// on how many words the actor already wears. A loadout / equipped-set / combination-count read in
/// this file would re-introduce exactly the coupling R12 removed — a price that changes with the
/// actor's other items.</para>
///
/// <para>Both scans reuse one walker and skip comment lines, because a negative assertion cannot be
/// written without naming what it excludes: the allowlists below are the history tests that must
/// name the retired identifier to prove its absence.</para>
/// </summary>
public class ComboCountCapGuardTests
{
    /// <summary>This guard's own path — it must name every identifier to scan for one.</summary>
    const string ThisFile = "tests/FusionRpg.Guard.Tests/ComboCountCapGuardTests.cs";

    /// <summary>R12's retired identifiers, in every spelling they shipped under.</summary>
    static readonly string[] CapIdentifiers =
        { "MaxCombosPerActor", "maxCombosPerActor", "max_combos_per_actor", "SocketCombinationCap" };

    /// <summary>
    /// The files allowed to name a retired identifier, each because it is the NEGATIVE assertion that
    /// keeps the retirement true — an ownership key list or a "carries no cap" history test. A
    /// production reader is never allowed here.
    /// </summary>
    static readonly HashSet<string> CapAllowlist = new(StringComparer.Ordinal)
    {
        // This guard itself: a scan cannot name what it excludes without naming it.
        "tests/FusionRpg.Guard.Tests/ComboCountCapGuardTests.cs",
        // The ownership check by name: `SOCKETS_OWNED_KEYS` and its C# mirror must list the key to
        // guarantee the migration path keeps ignoring it.
        "tools/seedsmith/seedsmith/adapters/items/combogen/tuning.py",
        "tests/FusionRpg.Core.Items.Tests/Items/StrainSpliceGridTests.cs",
        // The two R12 history tests: each asserts the key is ABSENT from the current revision.
        "tests/FusionRpg.Core.Items.Tests/Items/SocketGeometryTests.cs",
        "tools/seedsmith/tests/test_strain_splice_gen.py",
    };

    /// <summary>The workbench cost path (spec-socket-pricing §5): the ONE file that resolves bore,
    /// imbue and insert prices for a craft.</summary>
    const string WorkbenchCostPath = "src/FusionRpg.Server/ItemWorkbench.cs";

    /// <summary>What "the actor's worn combinations" looks like as source: a loadout or equipped-set
    /// read, or a combination count. None may reach a price.</summary>
    static readonly string[] WornIdentifiers =
        { "Loadout", "Equipped", "EquippedBoundAtoms", "CombinationResult", "GetComboRecipes", "ComboCount" };

    [Fact]
    public void no_combination_count_cap_exists()
    {
        var root = RepoRoot();
        var offenders = new List<string>();
        var allowlistedWithHits = new HashSet<string>(StringComparer.Ordinal);

        foreach (var top in new[] { "src", "tools", "tests" })
            foreach (var file in EnumerateSourceFiles(Path.Combine(root, top)))
            {
                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                var hits = IdentifierHits(file, rel, CapIdentifiers).ToList();
                if (CapAllowlist.Contains(rel))
                {
                    if (hits.Count > 0) allowlistedWithHits.Add(rel);
                    continue;
                }
                offenders.AddRange(hits);
            }

        Assert.True(offenders.Count == 0,
            "R12 retired the per-actor combination cap (SSH4.1/SSH4.2); these name it again:\n" +
            string.Join("\n", offenders));

        // A file the allowlist still names but no longer needs is itself a defect: the allowlist must
        // never grow into a licence, so an entry with no hit fails here.
        var stale = CapAllowlist
            .Where(f => f != ThisFile && !allowlistedWithHits.Contains(f))
            .ToList();
        Assert.True(stale.Count == 0,
            "these files no longer name a retired combination-cap identifier; remove them from the " +
            "allowlist:\n" + string.Join("\n", stale));
    }

    [Fact]
    public void no_price_reads_the_actors_worn_combinations()
    {
        var root = RepoRoot();
        var path = Path.Combine(root, WorkbenchCostPath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"{WorkbenchCostPath} is missing — the workbench cost path moved");

        var offenders = IdentifierHits(path, WorkbenchCostPath, WornIdentifiers).ToList();
        Assert.True(offenders.Count == 0,
            "a workbench price must depend only on the target item and the recipe " +
            "(spec-socket-pricing §5); these read what the actor wears:\n" +
            string.Join("\n", offenders));

        // …and the cost path's own shape says so: the ONE context builder every workbench price goes
        // through takes exactly TWO inputs — the paying player and the TARGET — so a later edit cannot
        // slip a loadout or equipped-set parameter in without this failing.
        var text = File.ReadAllText(path);
        var declaration = Regex.Match(text, @"RecipeContext\s+RecipeContextFor\(([^)]*)\)");
        Assert.True(declaration.Success, "RecipeContextFor(...) not found in the workbench cost path");
        Assert.Equal(2, declaration.Groups[1].Value.Split(',').Length);
    }

    static IEnumerable<string> IdentifierHits(string file, string rel, IReadOnlyList<string> identifiers)
    {
        var lines = File.ReadAllLines(file);
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("//", StringComparison.Ordinal) ||
                trimmed.StartsWith("///", StringComparison.Ordinal)) continue;
            foreach (var identifier in identifiers)
                if (lines[i].Contains(identifier, StringComparison.Ordinal))
                    yield return $"{rel}:{i + 1}: {lines[i].Trim()}";
        }
    }

    /// <summary>Every C# and Python file under <paramref name="rootDir"/>, skipping <c>bin</c>,
    /// <c>obj</c> and every dot-directory, and any unreadable directory — an access denial is a
    /// filesystem fact, never a reader naming a retired identifier.</summary>
    static IEnumerable<string> EnumerateSourceFiles(string rootDir)
    {
        var pending = new Stack<string>();
        pending.Push(rootDir);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            string[] files;
            try { files = Directory.GetFiles(dir, "*.cs").Concat(Directory.GetFiles(dir, "*.py")).ToArray(); }
            catch (UnauthorizedAccessException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            catch (IOException) { continue; }

            foreach (var file in files)
            {
                var rel = file.Replace('\\', '/');
                if (rel.Contains("/bin/", StringComparison.Ordinal) ||
                    rel.Contains("/obj/", StringComparison.Ordinal)) continue;
                yield return file;
            }

            string[] subdirs;
            try { subdirs = Directory.GetDirectories(dir); }
            catch (UnauthorizedAccessException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            catch (IOException) { continue; }

            foreach (var sub in subdirs)
            {
                var name = Path.GetFileName(sub);
                if (name.StartsWith(".", StringComparison.Ordinal)) continue;
                pending.Push(sub);
            }
        }
    }

    static string RepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
