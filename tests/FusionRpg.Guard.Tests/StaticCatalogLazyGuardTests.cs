using System.Text.RegularExpressions;
using Xunit;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// T4.7 (`catalog-runtime` §3a): an eager `static readonly X = Build()` field runs its initializer
/// once, at first touch of the type — before `CreatureSpeciesCatalog.Configure` is guaranteed to have
/// run, and (once T4.8 flips the source) at a point that could hold a stale roster forever. This guard
/// is the "cannot return" half of that fix: it fails loudly the moment the eager pattern reappears,
/// rather than relying on every future editor to remember why it must not.
///
/// <para><b>Reads source as text</b>, matching every other guard in this project — `Guard.Tests`
/// carries no project reference to `Core`, on purpose, so a guard cannot accidentally start exercising
/// the thing it is meant to police from the outside.</para>
/// </summary>
public class StaticCatalogLazyGuardTests
{
    static readonly Regex EagerBuildField =
        new(@"static\s+readonly\s+\S.*=\s*Build\(\)\s*;", RegexOptions.Compiled);

    [Theory]
    [InlineData("Battle", "WaveCatalog.cs")]
    [InlineData("Creatures", "Fusion", "CreatureRecipeCatalog.cs")]
    [InlineData("Creatures", "CreatureMaterialCatalog.cs")]
    public void The_three_downstream_catalogs_carry_no_eager_static_readonly_Build(params string[] relativeUnderCore)
    {
        var text = ReadCore(relativeUnderCore);

        Assert.False(EagerBuildField.IsMatch(text),
            $"{string.Join("/", relativeUnderCore)} still has an eager `static readonly ... = Build()` " +
            "field — first touch must happen lazily, after CreatureSpeciesCatalog.Configure runs, not at " +
            "an unpredictable point tied to class-load order (catalog-runtime §3a).");
    }

    [Fact]
    public void No_other_file_under_src_reads_CreatureSpeciesCatalog_through_an_eager_static_readonly_Build()
    {
        // The three known offenders are checked by name above; this is the repo-wide regression net —
        // a FOURTH catalog built the same eager way, reading the species roster, would reintroduce the
        // exact split-brain hazard this task exists to close.
        var srcRoot = Path.Combine(FindRepoRoot(), "src");
        var offenders = new List<string>();

        foreach (var file in Directory.GetFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            if (!text.Contains("CreatureSpeciesCatalog", StringComparison.Ordinal)) continue;
            if (EagerBuildField.IsMatch(text)) offenders.Add(Path.GetRelativePath(srcRoot, file));
        }

        Assert.Empty(offenders);
    }

    static string ReadCore(params string[] relativeUnderCore)
    {
        var path = Path.Combine(new[] { FindRepoRoot(), "src", "FusionRpg.Core" }.Concat(relativeUnderCore).ToArray());
        Assert.True(File.Exists(path), "missing " + path);
        return File.ReadAllText(path);
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
