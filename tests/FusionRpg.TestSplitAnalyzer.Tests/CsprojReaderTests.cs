using FusionRpg.Tools.TestSplitAnalyzer;
using Xunit;

namespace FusionRpg.TestSplitAnalyzer.Tests;

/// <summary>
/// core-split-analyzer: `CsprojReader` reads a `.csproj` as text (no MSBuild/Workspaces), the input
/// `ReferenceGraph` (TVB1.7) will classify. Every case parses a synthetic in-memory string — no file
/// I/O, no temp directories (`testing-standard.md` R1).
/// </summary>
public sealed class CsprojReaderTests
{
    // ── A8: a Compile item with a Link is a linked file (classified shared by ReferenceGraph later) ──

    [Fact]
    public void A8_a_compile_item_with_link_is_reported_as_linked()
    {
        const string csproj = """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <Compile Include="..\FusionRpg.Core.Tests\DataTestStore.cs" Link="DataTestStore.cs" />
                <Compile Include="Battle\BattleGoldenTests.cs" />
              </ItemGroup>
            </Project>
            """;

        var reader = CsprojReader.Parse(csproj);

        Assert.Equal(2, reader.CompileItems.Count);
        var linked = Assert.Single(reader.LinkedItems);
        Assert.Equal(@"..\FusionRpg.Core.Tests\DataTestStore.cs", linked.Include);
        Assert.Equal("DataTestStore.cs", linked.Link);
    }

    [Fact]
    public void A_plain_compile_item_with_no_link_is_not_linked()
    {
        const string csproj = """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <Compile Include="World\WorldTests.cs" />
              </ItemGroup>
            </Project>
            """;

        var reader = CsprojReader.Parse(csproj);

        Assert.Single(reader.CompileItems);
        Assert.Empty(reader.LinkedItems);
    }

    // ── ProjectReference extraction (the same shape FileMove's AssemblyGraph already reads) ────────

    [Fact]
    public void ProjectReferences_are_extracted_by_path()
    {
        const string csproj = """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <ProjectReference Include="..\..\src\FusionRpg.Core\FusionRpg.Core.csproj" />
                <ProjectReference Include="..\..\src\FusionRpg.Data\FusionRpg.Data.csproj" />
              </ItemGroup>
            </Project>
            """;

        var reader = CsprojReader.Parse(csproj);

        Assert.Equal(
            new[] { @"..\..\src\FusionRpg.Core\FusionRpg.Core.csproj", @"..\..\src\FusionRpg.Data\FusionRpg.Data.csproj" },
            reader.ProjectReferences);
    }

    // ── None content items (fixtures/Goldens paths a project ships as data) ────────────────────────

    [Fact]
    public void None_content_items_are_extracted()
    {
        const string csproj = """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <None Include="fixtures\combat\example.json" CopyToOutputDirectory="PreserveNewest" />
              </ItemGroup>
            </Project>
            """;

        var reader = CsprojReader.Parse(csproj);

        Assert.Equal(new[] { @"fixtures\combat\example.json" }, reader.NoneItems);
    }

    [Fact]
    public void An_empty_project_reports_nothing()
    {
        const string csproj = """<Project Sdk="Microsoft.NET.Sdk"></Project>""";

        var reader = CsprojReader.Parse(csproj);

        Assert.Empty(reader.CompileItems);
        Assert.Empty(reader.ProjectReferences);
        Assert.Empty(reader.NoneItems);
    }
}
