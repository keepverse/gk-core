using System.Reflection;
using FusionRpg.Core.ActorSurface;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Status;
using Xunit;

namespace FusionRpg.Core.Tests.ActorSurface;

/// <summary>
/// D19 (combat-math-dedup Task 13). The surface catalog's element / status-category / action-family
/// vocabularies are REFERENCES to their declaring constants, not a second literal copy. `omni` is the
/// one deliberate extra (the surface's own non-specific variant) and stays an explicit literal.
/// </summary>
public sealed class DerivedStatSurfaceVocabularyTests
{
    static HashSet<string> PrivateSet(string fieldName)
    {
        var field = typeof(DerivedStatSurfaceCatalogLoader)
            .GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        return (HashSet<string>)field!.GetValue(null)!;
    }

    [Fact]
    public void The_surface_vocabularies_are_the_referenced_constants()
    {
        var expectedElements = ElementRoster.Concrete.Select(e => e.ToElementId())
            .Append(ElementRoster.OmniId)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(expectedElements, PrivateSet("ElementLeafIds"));

        var expectedStatus = new HashSet<string>(StringComparer.Ordinal)
        {
            "omni", StatusL2bCategory.Dot, StatusL2bCategory.Cc, StatusL2bCategory.Contagion
        };
        Assert.Equal(expectedStatus, PrivateSet("StatusCategoryIds"));

        var expectedAction = new HashSet<string>(StringComparer.Ordinal)
        {
            DerivedStatChannels.SkillCooldownPrefix,
            DerivedStatChannels.SkillEffectivenessPrefix
        };
        Assert.Equal(expectedAction, PrivateSet("ActionCategoryFamilyIds"));
    }

    /// <summary>
    /// The negative guard D19 asks for: no element leaf, status L2b category, or action-family id is
    /// re-literalised in the surface catalog. `omni` is deliberately excluded — it is the one explicit
    /// addition, called out in the code comment beside each use. It bites on the pre-dedup shape (the
    /// three literal sets this replaced, and the inline <c>"skill.cooldown"</c>/<c>"skill.effectiveness"</c>
    /// comparison).
    /// </summary>
    [Fact]
    public void The_surface_catalog_declares_no_reliteralised_vocabulary_of_its_own()
    {
        var path = Path.Combine(RepoRoot(), "src", "FusionRpg.Core", "ActorSurface", "DerivedStatSurfaceCatalog.cs");
        Assert.True(File.Exists(path), "missing " + path);
        var text = File.ReadAllText(path);

        var forbidden = ElementRoster.Concrete.Select(e => e.ToElementId())
            .Concat(new[]
            {
                StatusL2bCategory.Dot, StatusL2bCategory.Cc, StatusL2bCategory.Contagion,
                DerivedStatChannels.SkillCooldownPrefix, DerivedStatChannels.SkillEffectivenessPrefix
            })
            .Distinct(StringComparer.Ordinal);

        foreach (var id in forbidden)
            Assert.DoesNotContain("\"" + id + "\"", text, StringComparison.Ordinal);
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
