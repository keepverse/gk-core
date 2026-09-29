using FusionRpg.Tools.FileMove;
using Xunit;

namespace FusionRpg.FileMove.Tests;

/// <summary>
/// `solid-remediation` T5.6 — the move tool's contract.
///
/// <para><b>Everything here runs against a SYNTHETIC repo</b>, which the module's spec insists on: the
/// four genuinely misfiled files in this codebase exercise only the base case (each already declares the
/// namespace its destination implies), so building the interesting capabilities against them would
/// leave the tool untested exactly where it is dangerous — caller rewiring, project files, and cycle
/// refusal.</para>
///
/// <para><b>No test asserts how many files in the repo are misfiled.</b> That is a reading that changes
/// whenever code ships; the spec calls it out by name. What is asserted is the tool's behaviour.</para>
/// </summary>
public class FileMoverTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "filemove-" + Guid.NewGuid().ToString("N"));

    public FileMoverTests()
    {
        // A miniature two-assembly repo: Core (referenced by nobody here) and Server (references Core).
        Write("src/FusionRpg.Core/FusionRpg.Core.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>"
            + "<RootNamespace>FusionRpg.Core</RootNamespace></PropertyGroup></Project>");

        Write("src/FusionRpg.Server/FusionRpg.Server.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>"
            + "<RootNamespace>FusionRpg.Server</RootNamespace></PropertyGroup><ItemGroup>"
            + "<ProjectReference Include=\"..\\FusionRpg.Core\\FusionRpg.Core.csproj\" />"
            + "</ItemGroup></Project>");
    }

    public void Dispose()
    {
        // A failed delete is a failure, never a swallowed catch (testing-standard.md) — but this tree is
        // plain files with no connection pool holding handles, so there is nothing to race.
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    FileMover Mover() => new(_root, File.ReadAllText, Directory.EnumerateFiles);

    string Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    string Abs(string relative) => Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));

    // ---- the base case ---------------------------------------------------------------------------

    [Fact]
    public void Moving_within_an_assembly_sets_the_namespace_to_match_the_destination_folder()
    {
        var src = Write("src/FusionRpg.Core/Actions/Thing.cs",
            "namespace FusionRpg.Core.Actions;\n\npublic class Thing { }\n");

        var plan = Mover().Plan(src, Abs("src/FusionRpg.Core/Battle/Thing.cs"));

        Assert.False(plan.IsRefused);
        Assert.Equal("FusionRpg.Core.Actions", plan.OldNamespace);
        Assert.Equal("FusionRpg.Core.Battle", plan.NewNamespace);
        Assert.Contains("namespace FusionRpg.Core.Battle;", plan.Edits[0].After, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_already_declaring_its_destination_namespace_changes_no_callers()
    {
        // This is the four real files, in miniature — and the reason the measurement said a tool was
        // not required for them. The plan is the move and nothing else.
        var src = Write("src/FusionRpg.Core/Actions/BattlePart.cs",
            "namespace FusionRpg.Core.Battle;\n\npublic partial class BattleEngine { }\n");
        Write("src/FusionRpg.Core/Other/Caller.cs",
            "using FusionRpg.Core.Battle;\n\npublic class Caller { }\n");

        var plan = Mover().Plan(src, Abs("src/FusionRpg.Core/Battle/BattlePart.cs"));

        Assert.Equal(plan.OldNamespace, plan.NewNamespace);
        Assert.Single(plan.Edits);
        Assert.Contains("namespace already correct", plan.Edits[0].Reason, StringComparison.Ordinal);
    }

    // ---- caller rewiring -------------------------------------------------------------------------

    [Fact]
    public void Moving_rewires_callers_that_imported_the_old_namespace()
    {
        var src = Write("src/FusionRpg.Core/Actions/Thing.cs",
            "namespace FusionRpg.Core.Actions;\n\npublic class Thing { }\n");
        var caller = Write("src/FusionRpg.Core/Other/Caller.cs",
            "using System;\nusing FusionRpg.Core.Actions;\n\npublic class Caller { }\n");

        var plan = Mover().Plan(src, Abs("src/FusionRpg.Core/Battle/Thing.cs"));
        var callerEdit = plan.Edits.Single(e => string.Equals(e.Path, caller, StringComparison.OrdinalIgnoreCase));

        Assert.Contains("using FusionRpg.Core.Battle;", callerEdit.After, StringComparison.Ordinal);
        Assert.DoesNotContain("using FusionRpg.Core.Actions;", callerEdit.After, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_that_never_imported_the_old_namespace_is_left_alone()
    {
        // Adding a using to a file that did not need one is noise a reviewer must dismiss on every
        // move, which is how a tool stops being trusted.
        var src = Write("src/FusionRpg.Core/Actions/Thing.cs",
            "namespace FusionRpg.Core.Actions;\n\npublic class Thing { }\n");
        var bystander = Write("src/FusionRpg.Core/Other/Bystander.cs",
            "using System;\n\npublic class Bystander { }\n");

        var plan = Mover().Plan(src, Abs("src/FusionRpg.Core/Battle/Thing.cs"));

        Assert.DoesNotContain(plan.Edits, e => string.Equals(e.Path, bystander, StringComparison.OrdinalIgnoreCase));
    }

    // ---- the refusal -----------------------------------------------------------------------------

    [Fact]
    public void A_move_that_would_create_an_assembly_cycle_is_refused_and_the_cycle_is_named()
    {
        // Server already references Core. Moving a file OUT of Server INTO Core would make Core depend
        // on Server's remaining types — closing the loop. It would compile only once someone added the
        // back-reference, which is exactly the kind of wrong that a compiler finds too late.
        var src = Write("src/FusionRpg.Server/Thing.cs",
            "namespace FusionRpg.Server;\n\npublic class Thing { }\n");

        var plan = Mover().Plan(src, Abs("src/FusionRpg.Core/Thing.cs"));

        Assert.True(plan.IsRefused);
        Assert.Contains("cycle", plan.Refusal!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FusionRpg.Server", plan.Refusal!, StringComparison.Ordinal);
        Assert.Contains("FusionRpg.Core", plan.Refusal!, StringComparison.Ordinal);
        Assert.Empty(plan.Edits);
    }

    [Fact]
    public void The_safe_direction_across_the_same_boundary_is_allowed()
    {
        // The mirror, so the refusal is proven specific rather than "any cross-assembly move fails".
        var src = Write("src/FusionRpg.Core/Thing.cs",
            "namespace FusionRpg.Core;\n\npublic class Thing { }\n");

        var plan = Mover().Plan(src, Abs("src/FusionRpg.Server/Thing.cs"));

        Assert.False(plan.IsRefused);
    }

    // ---- dry run ---------------------------------------------------------------------------------

    [Fact]
    public void Planning_writes_nothing()
    {
        var src = Write("src/FusionRpg.Core/Actions/Thing.cs",
            "namespace FusionRpg.Core.Actions;\n\npublic class Thing { }\n");
        var caller = Write("src/FusionRpg.Core/Other/Caller.cs",
            "using FusionRpg.Core.Actions;\n\npublic class Caller { }\n");
        var before = File.ReadAllText(caller);

        Mover().Plan(src, Abs("src/FusionRpg.Core/Battle/Thing.cs"));

        Assert.True(File.Exists(src), "the source file was moved by a dry run");
        Assert.False(File.Exists(Abs("src/FusionRpg.Core/Battle/Thing.cs")));
        Assert.Equal(before, File.ReadAllText(caller));
    }

    [Fact]
    public void Apply_writes_exactly_what_the_plan_printed()
    {
        // The dry run is only worth trusting if applying performs the same change set, which is why
        // Plan computes everything and Apply just writes it.
        var src = Write("src/FusionRpg.Core/Actions/Thing.cs",
            "namespace FusionRpg.Core.Actions;\n\npublic class Thing { }\n");
        var caller = Write("src/FusionRpg.Core/Other/Caller.cs",
            "using FusionRpg.Core.Actions;\n\npublic class Caller { }\n");

        var mover = Mover();
        var plan = mover.Plan(src, Abs("src/FusionRpg.Core/Battle/Thing.cs"));
        mover.Apply(plan);

        foreach (var edit in plan.Edits)
            Assert.Equal(edit.After, File.ReadAllText(edit.Path));

        Assert.False(File.Exists(src), "the source file survived the move");
        Assert.Contains("using FusionRpg.Core.Battle;", File.ReadAllText(caller), StringComparison.Ordinal);
    }

    [Fact]
    public void A_refused_plan_cannot_be_applied()
    {
        var src = Write("src/FusionRpg.Server/Thing.cs",
            "namespace FusionRpg.Server;\n\npublic class Thing { }\n");

        var mover = Mover();
        var plan = mover.Plan(src, Abs("src/FusionRpg.Core/Thing.cs"));

        Assert.Throws<InvalidOperationException>(() => mover.Apply(plan));
        Assert.True(File.Exists(src));
    }
}
