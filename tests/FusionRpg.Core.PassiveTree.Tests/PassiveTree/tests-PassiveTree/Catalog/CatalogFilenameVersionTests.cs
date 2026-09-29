using FusionRpg.Core.PassiveTree.Catalog;
using Xunit;

namespace FusionRpg.Core.Tests.PassiveTree.Catalog;

/// <summary>Task C5 — R6's "classes.v2.json trap" (spec-tree-catalog.md §4):
/// `PassiveTreeCatalogLoader.CheckFilenameVersion` asserts a committed file's own `vN` agrees with
/// its document's `catalogVersion` field. Pure logic, no JSON parsing involved.</summary>
public class CatalogFilenameVersionTests
{
    [Fact] // filename_version_equals_catalogVersion_field
    public void A_matching_version_and_filename_returns_no_refusal()
    {
        Assert.Null(PassiveTreeCatalogLoader.CheckFilenameVersion("might.v1.json", catalogVersion: 1));
    }

    [Fact] // the classes.v2.json trap itself, reproduced: filename says v2, document says 4
    public void A_mismatched_version_is_refused_naming_the_file()
    {
        var refusal = PassiveTreeCatalogLoader.CheckFilenameVersion("classes.v2.json", catalogVersion: 4);

        Assert.NotNull(refusal);
        Assert.Contains("classes.v2.json", refusal);
        Assert.Contains("4", refusal);
    }

    [Fact] // "where applicable" -- a name carrying no vN token at all has nothing to check
    public void A_filename_with_no_version_token_returns_no_refusal()
    {
        Assert.Null(PassiveTreeCatalogLoader.CheckFilenameVersion("might.json", catalogVersion: 1));
        Assert.Null(PassiveTreeCatalogLoader.CheckFilenameVersion("might", catalogVersion: 1));
    }

    [Fact]
    public void A_multi_digit_version_is_parsed_correctly()
    {
        Assert.Null(PassiveTreeCatalogLoader.CheckFilenameVersion("aptitudes.v12.json", catalogVersion: 12));
        Assert.NotNull(PassiveTreeCatalogLoader.CheckFilenameVersion("aptitudes.v12.json", catalogVersion: 2));
    }

    [Fact]
    public void Case_of_the_v_token_does_not_matter()
    {
        Assert.Null(PassiveTreeCatalogLoader.CheckFilenameVersion("might.V3.json", catalogVersion: 3));
    }

    [Fact]
    public void A_path_prefix_before_the_filename_is_fine()
    {
        Assert.Null(PassiveTreeCatalogLoader.CheckFilenameVersion(
            "data/generated/passive-tree/might.v1.json", catalogVersion: 1));
    }

    [Fact]
    public void Null_filename_is_refused()
    {
        Assert.Throws<ArgumentNullException>(() => PassiveTreeCatalogLoader.CheckFilenameVersion(null!, 1));
    }
}
