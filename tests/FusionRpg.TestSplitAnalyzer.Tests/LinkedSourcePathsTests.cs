using Xunit;

namespace FusionRpg.Tools.TestSplitAnalyzer.Tests;

/// <summary>
/// `CsprojReader.LinkedSourcePaths` — TVB-F18's second cause. The analyzer compiles a project's
/// DIRECTORY, so the sources it links in from outside (`Directory.Build.props`' `gk-core/tests/Shared/
/// KeepverseRoots.cs`, the shared props' four files) were never parsed; the project's own
/// previously-built DLL used to supply those types by accident. Measured 2026-09-23: excluding that
/// DLL without parsing the links made the map WORSE (11 erroring projects -> 16, the residual 16 -> 57
/// errors). These cases pin the replacement.
///
/// <para>The disk is the thing under test here — the reader's whole job is reading files — so a temp
/// tree is legitimate; every case deletes it in a `finally` with the throwing `Directory.Delete` this
/// repo mandates (`docs/contributing/testing-standard.md`).</para>
/// </summary>
public class LinkedSourcePathsTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "tvb-linked-" + Guid.NewGuid().ToString("N"));

    public LinkedSourcePathsTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    string Write(string relativePath, string text)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
        return full;
    }

    [Fact]
    public void A_link_in_the_project_file_is_found_and_a_plain_or_wildcard_include_is_not()
    {
        Write("shared/Linked.cs", "namespace X; internal static class Linked { }");
        Write("shared/Plain.cs", "namespace X; internal static class Plain { }");
        var csproj = Write("proj/Proj.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <Compile Include="..\shared\Linked.cs" Link="Linked.cs" />
                <Compile Include="..\shared\Plain.cs" />
                <Compile Include="..\shared\*.generated.cs" Link="Generated.cs" />
              </ItemGroup>
            </Project>
            """);

        var found = CsprojReader.LinkedSourcePaths(csproj);

        var single = Assert.Single(found);
        Assert.EndsWith("Linked.cs", single, StringComparison.Ordinal);
    }

    [Fact]
    public void A_link_declared_by_an_imported_props_file_is_found_transitively()
    {
        Write("shared/Inner.cs", "namespace X; internal static class Inner { }");
        Write("shared/Outer.cs", "namespace X; internal static class Outer { }");
        Write("shared/Inner.props", """
            <Project>
              <ItemGroup>
                <Compile Include="$(MSBuildThisFileDirectory)Inner.cs" Link="Inner.cs" />
              </ItemGroup>
            </Project>
            """);
        Write("shared/Outer.props", """
            <Project>
              <Import Project="$(MSBuildThisFileDirectory)Inner.props" />
              <ItemGroup>
                <Compile Include="$(MSBuildThisFileDirectory)Outer.cs" Link="Outer.cs" />
              </ItemGroup>
            </Project>
            """);
        var csproj = Write("proj/Proj.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <Import Project="..\shared\Outer.props" />
            </Project>
            """);

        var names = CsprojReader.LinkedSourcePaths(csproj).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        Assert.Equal(new[] { "Inner.cs", "Outer.cs" }, names);
    }

    [Fact]
    public void The_Directory_Build_props_walk_stops_at_the_first_one_found()
    {
        // The bug this pins, measured on a worktree: worktrees live under
        // `<main>/.claude/worktrees/<lane>/`, so a walk that continued past the worktree root parsed
        // the MAIN checkout's own `Directory.Build.props` too and compiled a second copy of
        // `gk-core/tests/Shared/KeepverseRoots.cs` from a different absolute path — duplicate definitions in
        // every project. MSBuild stops at the first file; so must this.
        Write("Outer.cs", "namespace X; internal static class Outer { }");
        Write("Directory.Build.props", """
            <Project>
              <ItemGroup>
                <Compile Include="$(MSBuildThisFileDirectory)Outer.cs" Link="Outer.cs" />
              </ItemGroup>
            </Project>
            """);
        Write("proj/Inner.cs", "namespace X; internal static class Inner { }");
        Write("proj/Directory.Build.props", """
            <Project>
              <ItemGroup>
                <Compile Include="$(MSBuildThisFileDirectory)Inner.cs" Link="Inner.cs" />
              </ItemGroup>
            </Project>
            """);
        var csproj = Write("proj/Proj.csproj", """<Project Sdk="Microsoft.NET.Sdk"></Project>""");

        var names = CsprojReader.LinkedSourcePaths(csproj).Select(Path.GetFileName).ToArray();

        Assert.Equal(new[] { "Inner.cs" }, names);
    }
}
