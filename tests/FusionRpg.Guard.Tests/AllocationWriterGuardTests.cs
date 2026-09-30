using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// EP1.9 (spec-specimen-respec-price.md, "One writer") test 9: outside
/// <c>RpgStore.AllocationRespec.cs</c> (the priced gate), the only production writers of the
/// <c>Commander</c>/<c>UniqueCreature</c> allocation scopes are <c>RpgStore.Aptitudes.cs</c> (the
/// <c>SaveAllocation</c>/<c>SaveAllocationUnlocked</c> definitions themselves) and
/// <c>DerivedAuditActor.Seed</c> (an RPG Server Debug fixture — first allocations, free under R18
/// anyway). This is the contract that stops a free respec bypass from reappearing.
///
/// <para>EP1.10 ("Preset activation's commander and unique branches call TryReallocateUnlocked")
/// closed the one temporary allowance this guard shipped with at EP1.9: <c>RpgStore.AptitudePresets.cs</c>
/// now routes its commander/unique preset-activation branches through <c>TryReallocateUnlocked</c>
/// instead of calling <c>SaveAllocationUnlocked</c> directly, so the allowlist below is back to the
/// three permanent entries the spec names.</para>
/// </summary>
public class AllocationWriterGuardTests
{
    static readonly string[] AllowedRelativePaths =
    {
        "src/FusionRpg.Data/Sqlite/RpgStore.AllocationRespec.cs",
        "src/FusionRpg.Data/Sqlite/RpgStore.Aptitudes.cs",
        "src/FusionRpg.Server/DerivedAuditActor.cs",
    };

    [Fact]
    public void No_file_outside_the_allowlist_writes_the_Commander_or_UniqueCreature_scope_directly()
    {
        var root = RepoRoot();
        var src = Path.Combine(root, "src");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (IsBuildOutput(file)) continue;

            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (AllowedRelativePaths.Contains(rel, StringComparer.Ordinal)) continue;

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var writesAllocation = line.Contains("SaveAllocation(", StringComparison.Ordinal)
                    || line.Contains("SaveAllocationUnlocked(", StringComparison.Ordinal);
                if (!writesAllocation) continue;

                var namesGuardedScope = line.Contains("AllocationScope.Commander", StringComparison.Ordinal)
                    || line.Contains("AllocationScope.UniqueCreature", StringComparison.Ordinal);
                if (!namesGuardedScope) continue;

                offenders.Add($"{rel}:{i + 1}: {line.Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "outside the allowlist, no file may write the Commander/UniqueCreature allocation scope " +
            "directly -- go through RpgStore.TryReallocate(Unlocked) instead. Offenders:\n" +
            string.Join("\n", offenders));
    }

    /// <summary>The positive half: the gate itself still exists and still writes both scopes, so the
    /// guard above cannot pass vacuously because the write path was deleted rather than routed
    /// through it.</summary>
    [Fact]
    public void The_gate_itself_still_writes_both_scopes()
    {
        var root = RepoRoot();
        var gate = File.ReadAllText(Path.Combine(root, "src", "FusionRpg.Data", "Sqlite", "RpgStore.AllocationRespec.cs"));
        Assert.Contains("SaveAllocationUnlocked(db, tx, scope, scopeKey, proposed);", gate, StringComparison.Ordinal);
        Assert.Contains("public ReallocationOutcome TryReallocate(", gate, StringComparison.Ordinal);
        Assert.Contains("internal ReallocationOutcome TryReallocateUnlocked(", gate, StringComparison.Ordinal);
    }

    static bool IsBuildOutput(string path)
    {
        var sep = Path.DirectorySeparatorChar;
        return path.Contains($"{sep}bin{sep}", StringComparison.Ordinal)
            || path.Contains($"{sep}obj{sep}", StringComparison.Ordinal);
    }

    static string RepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
