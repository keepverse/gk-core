using Xunit;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// EP1.14 (spec-default-build.md, testing 5) — every production reader of the
/// <c>UniqueCreature</c> scope calls <c>RpgStore.EffectiveUniqueAllocation(Unlocked)</c>, never
/// <c>LoadAllocation(Unlocked)?</c> directly. The allowlist is the resolver's own definition
/// (<c>RpgStore.Aptitudes.cs</c>), the allocate write path (<c>RpgStore.AllocationRespec.cs</c>,
/// which reads the CURRENT persisted allocation to decide free-vs-priced before writing —
/// reading the raw value there is correct, not a bypass), and the RPG Server Debug fixture
/// (<c>DerivedAuditActor.cs</c>, first allocations only, free under R18 anyway).
/// </summary>
[Trait("VerificationId", "guard.unique-allocation-reader")]
public class UniqueAllocationReaderGuardTests
{
    static readonly string[] AllowedRelativePaths =
    {
        "src/FusionRpg.Data/Sqlite/RpgStore.Aptitudes.cs",
        "src/FusionRpg.Data/Sqlite/RpgStore.AllocationRespec.cs",
        "src/FusionRpg.Server/DerivedAuditActor.cs",
    };

    [Fact]
    public void No_file_outside_the_allowlist_reads_the_UniqueCreature_scope_directly()
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
                var readsAllocation = line.Contains("LoadAllocation(", StringComparison.Ordinal)
                    || line.Contains("LoadAllocationUnlocked(", StringComparison.Ordinal);
                if (!readsAllocation) continue;

                var namesGuardedScope = line.Contains("AllocationScope.UniqueCreature", StringComparison.Ordinal);
                if (!namesGuardedScope) continue;

                offenders.Add($"{rel}:{i + 1}: {line.Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "outside the allowlist, no file may read the UniqueCreature allocation scope directly " +
            "-- go through RpgStore.EffectiveUniqueAllocation(Unlocked) instead. Offenders:\n" +
            string.Join("\n", offenders));
    }

    /// <summary>The positive half: the resolver itself still exists and still reads the raw scope, so
    /// the guard above cannot pass vacuously because the read path was deleted rather than routed
    /// through it.</summary>
    [Fact]
    public void The_resolver_itself_still_reads_the_raw_scope()
    {
        var root = RepoRoot();
        var resolver = File.ReadAllText(Path.Combine(root, "src", "FusionRpg.Data", "Sqlite", "RpgStore.Aptitudes.cs"));
        Assert.Contains("LoadAllocationUnlocked(db, AllocationScope.UniqueCreature, instanceId);", resolver, StringComparison.Ordinal);
        Assert.Contains("public EffectiveAllocation EffectiveUniqueAllocation(", resolver, StringComparison.Ordinal);
        Assert.Contains("internal EffectiveAllocation EffectiveUniqueAllocationUnlocked(", resolver, StringComparison.Ordinal);
    }

    static bool IsBuildOutput(string path)
    {
        var sep = Path.DirectorySeparatorChar;
        return path.Contains($"{sep}bin{sep}", StringComparison.Ordinal)
            || path.Contains($"{sep}obj{sep}", StringComparison.Ordinal);
    }

    static string RepoRoot()
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
