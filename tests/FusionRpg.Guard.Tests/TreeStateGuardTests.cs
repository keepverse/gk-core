using Xunit;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// Task C8 — spec-tree-state.md §2.3 (D25's soft economic bound, PS-8), §6 (the single-key loader
/// must never be looped by battle), §7 (row-count discipline). Text-scan guards matching
/// `SpeciesAllocationSeamTests`'/`DalGuardTests`' own established rigor.
/// </summary>
public class TreeStateGuardTests
{
    static readonly string[] PriceAndBudgetFiles =
    {
        "TreeUnlockCost.cs", "RpgStore.PassiveTree.cs", "TreeRespecPolicy.cs",
    };

    static IEnumerable<string> AllSourceFiles()
    {
        var repoRoot = FindRepoRoot();
        var srcRoot = Path.Combine(repoRoot, "src");
        foreach (var file in Directory.GetFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
            yield return file;
        }
    }

    [Fact]
    public void No_Math_Min_ever_clamps_the_tree_unlock_price_or_budget()
    {
        // PS-8: no hard progression ceiling. A budget/price a balance dial can only ever grow is not
        // a cap -- Math.Min silently converting "the wallet is huge" into "the wallet is exactly X"
        // is the shape D25's own "soft economic bound, provably not a ceiling" verdict forbids. A
        // Math.Min bounding something STRUCTURAL (never a price/budget magnitude) is allowed only when
        // it says so in a "PS-8 exempt" comment beside it (AGENTS.md's own exemption convention) --
        // scanned within the 500 characters immediately before the call, not just anywhere in the file.
        var violations = new List<string>();
        foreach (var file in AllSourceFiles())
        {
            var name = Path.GetFileName(file);
            if (Array.IndexOf(PriceAndBudgetFiles, name) < 0) continue;
            var text = File.ReadAllText(file);

            var idx = 0;
            while ((idx = text.IndexOf("Math.Min", idx, StringComparison.Ordinal)) >= 0)
            {
                var lookbackStart = Math.Max(0, idx - 500);
                var context = text[lookbackStart..idx];
                if (!context.Contains("PS-8 exempt", StringComparison.Ordinal))
                    violations.Add(file);
                idx += "Math.Min".Length;
            }
        }
        Assert.True(violations.Count == 0,
            "Math.Min found clamping a tree price/budget quantity with no 'PS-8 exempt' comment " +
            "beside it (PS-8 forbids a silent cap — see spec-tree-state.md §2.3). Offending file(s): " +
            string.Join(", ", violations));
    }

    [Fact]
    public void No_narrowing_int_cast_truncates_the_tree_budget_or_price()
    {
        // A `(int)` cast on a `long` budget/price silently reintroduces the int.MaxValue ceiling
        // docs/architecture/numeric-types.md's overflow table measures at Theta ~103,557 for whole-unit magnitudes -- exactly
        // the ceiling PS-8 forbids reintroducing through the back door of a narrowing cast.
        var violations = new List<string>();
        foreach (var file in AllSourceFiles())
        {
            var name = Path.GetFileName(file);
            if (Array.IndexOf(PriceAndBudgetFiles, name) < 0) continue;
            var text = File.ReadAllText(file);
            if (text.Contains("(int)", StringComparison.Ordinal))
                violations.Add(file);
        }
        Assert.True(violations.Count == 0,
            "a narrowing (int) cast was found on a tree price/budget file -- PS-8 requires `long` " +
            "throughout (docs/architecture/numeric-types.md's overflow table). Offending file(s): " + string.Join(", ", violations));
    }

    [Fact]
    public void No_CanUnlock_method_exists_that_can_ever_return_false()
    {
        // D25/PS-8: "never refused" mirrors RespecPolicy's own discipline -- a `CanUnlock` predicate
        // that can return false is the exact shape a hard progression ceiling takes when it hides
        // behind a boolean gate instead of a number. None ships today; this guards the day one is
        // added carelessly.
        var violations = new List<string>();
        foreach (var file in AllSourceFiles())
        {
            if (!file.Contains($"{Path.DirectorySeparatorChar}PassiveTree{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && Path.GetFileName(file) != "RpgStore.PassiveTree.cs")
                continue;
            var text = File.ReadAllText(file);
            if (text.Contains("CanUnlock", StringComparison.Ordinal))
                violations.Add(file);
        }
        Assert.True(violations.Count == 0,
            "a CanUnlock method was introduced in the passive-tree module -- D25/PS-8 forbids a " +
            "boolean unlock gate (see RespecPolicy.cs's 'never refused' discipline, mirrored at " +
            "spec-tree-state.md §5). Offending file(s): " + string.Join(", ", violations));
    }

    [Fact]
    public void No_file_other_than_RpgStorePassiveTree_calls_the_single_key_LoadTreeState()
    {
        // spec-tree-state.md §6: LoadTreeState(scope, scopeKey) is the EDITING SURFACE ONLY.
        // Battle setup must use LoadTreeStateBatch instead, or a 6-actor squad becomes 234
        // lock-serialised queries before the first turn. No production caller exists yet (this
        // guards the day one is added carelessly as a per-actor loop instead of a batch call).
        var violations = new List<string>();
        foreach (var file in AllSourceFiles())
        {
            var name = Path.GetFileName(file);
            if (name == "RpgStore.PassiveTree.cs") continue; // the store itself both defines and reads it

            var text = File.ReadAllText(file);
            // Match the single-key call `LoadTreeState(` but not `LoadTreeStateBatch(` or its
            // own name appearing only in a doc comment/string.
            var idx = 0;
            while ((idx = text.IndexOf("LoadTreeState(", idx, StringComparison.Ordinal)) >= 0)
            {
                violations.Add(file);
                break;
            }
        }
        Assert.True(violations.Count == 0,
            "LoadTreeState(scope, scopeKey) (the single-key editing-surface loader) called outside " +
            "RpgStore.PassiveTree.cs -- battle/squad paths must use LoadTreeStateBatch instead. " +
            "Offending file(s): " + string.Join(", ", violations));
    }

    [Fact]
    public void ListCreatureRoster_does_not_join_tree_state()
    {
        // The unpaged roster query must never grow an implicit join onto rpg_tree_node_state -- that
        // would turn a roster listing into an O(actors x owned nodes) query with no pagination to
        // bound it.
        var repoRoot = FindRepoRoot();
        var path = Path.Combine(repoRoot, "src", "FusionRpg.Data", "Sqlite", "RpgStore.Creatures.cs");
        Assert.True(File.Exists(path), "missing " + path);
        var text = File.ReadAllText(path);
        Assert.DoesNotContain("rpg_tree_node_state", text, StringComparison.Ordinal);
        Assert.DoesNotContain("LoadTreeState", text, StringComparison.Ordinal);
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var scripts = Path.Combine(dir.FullName, "Directory.Build.props");
            if (File.Exists(scripts)) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not find repo root with Directory.Build.props");
    }
}
