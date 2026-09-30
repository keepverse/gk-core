using System;
using System.Collections.Generic;
using System.IO;
using FusionRpg.Core.Workspace;
using FusionRpg.TestSupport;
using Xunit;

namespace FusionRpg.Core.Tests.Workspace;

/// <summary>
/// Resolver contract (tasks/keepverse-split-plan.md "Resolver contract"): legacy layout, Keepverse
/// workspace layout — asserted for <b>both</b> resolvers, because there are two and they must agree.
///
/// <para><b>Why the production one exists.</b> <c>gk-core/tests/Shared/KeepverseRoots.cs</c> was the whole of
/// the contract for weeks, and that is the defect this file now also covers. The Keepverse split gave
/// MSBuild the workspace roots — <c>GkDataRoot</c> and friends in <c>Directory.Build.props</c> — and
/// gave the running program none, so the solution built with 0 errors and then
/// <c>FusionRpg.E2E.Tests</c> failed 296 of 296 at runtime with
/// <c>FileNotFoundException: gk-data/packs/fusion/data/seed/saves/_registry/new-save-empires.v1.json not found</c> on a
/// path that demonstrably existed. A resolver only the tests can see is a resolver the product cannot
/// use. <see cref="KeepverseRoots"/> is the production half, and these tests hold it to the same
/// contract the test-support resolver already met.
///
/// <para><b>Disk is the thing under test</b> — the entire job is <c>Directory.Exists</c> against a real
/// tree — so real temporary directories, and a failed delete fails the test rather than being
/// swallowed. <c>Directory.CreateTempSubdirectory</c> is used because it hands back a fresh directory
/// rather than a name that has to be made unique by hand.
/// </para>
/// </summary>
[Trait("VerificationId", "core.keepverse-roots")]
public sealed class KeepverseRootsTests
{
    /// <summary>A legacy checkout: FusionRpg.slnx next to gk-data/packs/fusion/data/seed, with gk-core/data/tuning beside it.</summary>
    private static string LegacyLayout(out string cleanup)
    {
        var root = Directory.CreateTempSubdirectory("kv-legacy-").FullName;
        cleanup = root;
        File.WriteAllText(Path.Combine(root, "FusionRpg.slnx"), "<Solution />");
        Directory.CreateDirectory(Path.Combine(root, "data", "seed"));
        Directory.CreateDirectory(Path.Combine(root, "data", "tuning"));
        return root;
    }

    /// <summary>A Keepverse workspace: gk-core beside gk-data, content in the pack, tuning in core.</summary>
    private static string WorkspaceLayout(out string cleanup)
    {
        var ws = Directory.CreateTempSubdirectory("kv-ws-").FullName;
        cleanup = ws;
        Directory.CreateDirectory(Path.Combine(ws, "gk-core", "data", "tuning"));
        Directory.CreateDirectory(Path.Combine(ws, "gk-data", "packs", "fusion", "data", "seed"));
        Directory.CreateDirectory(Path.Combine(ws, "gk-data", "packs", "fusion", "data", "generated"));
        // gk-content holds the authored content/ tree. It is easy to leave out of a fixture, and
        // leaving it out is exactly how the fourth root went missing from this resolver in the
        // first place: the repository has one file in it, so nothing else refers to it.
        Directory.CreateDirectory(Path.Combine(ws, "gk-content", "content", "display"));
        return ws;
    }

    // ---- the test-support resolver, the contract's original half ---------------------------------

    [Fact]
    public void Legacy_layout_resolves_every_root_to_the_repo()
    {
        // This used to read the AMBIENT layout - ContentRoot.Resolve() with no argument - and
        // assert the other two agreed with it. That passes in the monorepo, where the ambient
        // layout happens to be legacy, and fails in a workspace, where it legitimately is not:
        // Content is the pack and Core is gk-core, so they are SUPPOSED to differ. A test whose
        // pass/fail depends on where its own binary sits is not testing what its name says.
        //
        // So it builds the layout it claims to test, the way every other test in this file
        // already passes an explicit start, and asserts against that. This is also why
        // LegacyLayout exists at all - it was written for this test, and this test stopped
        // calling it.
        var root = LegacyLayout(out var cleanup);
        try
        {
            Assert.True(Directory.Exists(Path.Combine(root, "data", "seed")));
            Assert.Equal(root, ContentRoot.Resolve(root));
            Assert.Equal(root, CoreRoot.Resolve(root));
            Assert.Equal(root, WorkspaceRoot.Resolve(root));
        }
        finally
        {
            Directory.Delete(cleanup, recursive: true);
        }
    }

    [Fact]
    public void Workspace_layout_resolves_pack_core_and_workspace()
    {
        // Disk is the thing under test here (directory discovery); a failed delete fails the test.
        var ws = Directory.CreateTempSubdirectory("kv-roots-").FullName;
        try
        {
            var start = Directory.CreateDirectory(Path.Combine(ws, "gk-core", "tests")).FullName;
            var pack = Directory.CreateDirectory(Path.Combine(ws, "gk-data", "packs", "fusion", "data", "seed")).Parent!.Parent!.FullName;

            Assert.Equal(Path.Combine(ws, "gk-core"), CoreRoot.Resolve(start));
            Assert.Equal(ws, WorkspaceRoot.Resolve(start));
            Assert.Equal(pack, ContentRoot.Resolve(start));
        }
        finally
        {
            Directory.Delete(ws, recursive: true);
        }
    }

    [Fact]
    public void No_layout_above_the_start_throws()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        Assert.Throws<DirectoryNotFoundException>(() => CoreRoot.Resolve(root));
    }

    // ---- the production resolver, and the agreement between the two -------------------------------

    [Fact]
    public void The_production_resolver_agrees_with_the_test_support_resolver_in_a_legacy_checkout()
    {
        var root = LegacyLayout(out var cleanup);
        try
        {
            var found = KeepverseRoots.Detect(root);
            Assert.NotNull(found);
            var (legacy, detected) = found!.Value;
            Assert.True(legacy, "FusionRpg.slnx next to data/seed is a legacy checkout");
            Assert.Equal(Path.GetFullPath(root), detected);
            // The collapse is the load-bearing property: before the split all three roots were one
            // directory, and that is what kept every existing data/... literal valid with no caller
            // changing. If this stops holding, a monorepo checkout breaks.
            Assert.Equal(root, KeepverseRoots.Content(root));
            Assert.Equal(root, KeepverseRoots.Core(root));
            Assert.Equal(root, KeepverseRoots.Workspace(root));
            Assert.Single(KeepverseRoots.Roots(root));
        }
        finally
        {
            Directory.Delete(cleanup, recursive: true);
        }
    }

    [Fact]
    public void The_production_resolver_agrees_with_the_test_support_resolver_in_a_workspace()
    {
        var ws = WorkspaceLayout(out var cleanup);
        try
        {
            var start = Path.Combine(ws, "gk-core", "tests", "X.Tests", "bin");
            Directory.CreateDirectory(start);

            var found = KeepverseRoots.Detect(start);
            Assert.NotNull(found);
            var (legacy, detected) = found!.Value;
            Assert.False(legacy, "gk-core beside gk-data is a Keepverse workspace");
            Assert.Equal(Path.GetFullPath(ws), detected);

            // Same answers as ContentRoot/CoreRoot/WorkspaceRoot, resolved from the same deep start.
            Assert.Equal(ContentRoot.Resolve(start), KeepverseRoots.Content(start));
            Assert.Equal(CoreRoot.Resolve(start), KeepverseRoots.Core(start));
            Assert.Equal(WorkspaceRoot.Resolve(start), KeepverseRoots.Workspace(start));

            var pack = Path.Combine(ws, "gk-data", "packs", "fusion");
            Assert.True(Directory.Exists(Path.Combine(KeepverseRoots.Content(start), "data", "seed")),
                "the content root is the pack, so a data/seed literal still resolves");
        }
        finally
        {
            Directory.Delete(cleanup, recursive: true);
        }
    }

    [Fact]
    public void Roots_offers_both_candidates_in_a_workspace_because_a_caller_cannot_always_tell_which_root_owns_a_data_path()
    {
        var ws = WorkspaceLayout(out var cleanup);
        try
        {
            var roots = KeepverseRoots.Roots(ws);
            // Three, not one: SeedImportRunner.FindUp is handed gk-data/packs/fusion/data/seed and gk-core/data/tuning by different
            // callers and they live in different repositories after the split, so it cannot be told
            // which root owns a path. Trying them all lets the filesystem decide. Authored content
            // (gk-content) is in the list because gk-content/content/display/en.json lives there and is NOT
            // inside the gk-data pack, despite sharing the word.
            Assert.Equal(3, roots.Count);
            Assert.Contains(Path.Combine(ws, "gk-content"), roots);
            Assert.Contains(Path.Combine(ws, "gk-data", "packs", "fusion"), roots);
            Assert.Contains(Path.Combine(ws, "gk-core"), roots);
            // Authored content is offered BEFORE the derived pack, so a caller asking for "content"
            // is handed the authored tree rather than the corpus that shares the word.
            Assert.Equal(Path.Combine(ws, "gk-content"), roots[0]);
            // Deduplicated: content-first ordering must not offer the same directory twice in a
            // legacy checkout, where both roots are one path.
            Assert.Equal(roots.Count, new HashSet<string>(roots, StringComparer.OrdinalIgnoreCase).Count);
        }
        finally
        {
            Directory.Delete(cleanup, recursive: true);
        }
    }

    [Fact]
    public void Roots_is_empty_and_Never_guessed_when_no_layout_is_above_the_start()
    {
        // A bare temp directory is neither shape. The old walk returned null here, so this pre-pass
        // must return nothing rather than throwing: a probe must not be the thing that fails.
        var bare = Directory.CreateTempSubdirectory("kv-bare-").FullName;
        try
        {
            Assert.Empty(KeepverseRoots.Roots(bare));
            Assert.Null(KeepverseRoots.Detect(bare));
            // Naming a root with nothing to name is a different question, and it is answered loudly.
            Assert.Throws<DirectoryNotFoundException>(() => KeepverseRoots.Content(bare));
            Assert.Throws<DirectoryNotFoundException>(() => KeepverseRoots.Core(bare));
        }
        finally
        {
            Directory.Delete(bare, recursive: true);
        }
    }

    [Fact]
    public void A_pack_that_does_not_exist_throws_rather_than_naming_a_root_that_is_not_there()
    {
        var ws = Directory.CreateTempSubdirectory("kv-nopack-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(ws, "gk-core"));
            Directory.CreateDirectory(Path.Combine(ws, "gk-data", "packs"));   // no fusion/ inside
            var ex = Assert.Throws<DirectoryNotFoundException>(() => KeepverseRoots.Content(ws));
            Assert.Contains("does not exist", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(ws, recursive: true);
        }
    }

    [Fact]
    public void An_env_override_wins_over_detection_and_is_honoured_before_it()
    {
        var ws = WorkspaceLayout(out var cleanup);
        var elsewhere = Directory.CreateTempSubdirectory("kv-override-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(elsewhere, "data", "seed"));
            Environment.SetEnvironmentVariable("KEEPVERSE_CONTENT_ROOT", elsewhere);
            try
            {
                Assert.Equal(elsewhere, KeepverseRoots.Content(ws));
                // Roots honours the override first and returns without detecting, so a caller that
                // disagrees with the detection cannot be overruled by it.
                Assert.Equal(elsewhere, Assert.Single(KeepverseRoots.Roots(ws)));
            }
            finally
            {
                Environment.SetEnvironmentVariable("KEEPVERSE_CONTENT_ROOT", null);
            }
        }
        finally
        {
            Directory.Delete(cleanup, recursive: true);
            Directory.Delete(elsewhere, recursive: true);
        }
    }

    [Fact]
    public void Authored_content_is_its_own_root_and_is_not_inside_the_content_pack()
    {
        var ws = WorkspaceLayout(out var cleanup);
        try
        {
            // The defect this root was added for: gk-content/content/display/en.json lives in gk-content, and
            // Content() returns the gk-data pack, so a caller handed the pack for a "content" path
            // was looking one repository away from the file. 178 test failures named a path under
            // the workspace root before this root existed.
            Assert.Equal(Path.Combine(ws, "gk-content"), KeepverseRoots.AuthoredContent(ws));
            Assert.NotEqual(KeepverseRoots.Content(ws), KeepverseRoots.AuthoredContent(ws));

            // And the file is reachable from the authored root, not from the pack.
            var file = Path.Combine(KeepverseRoots.AuthoredContent(ws), "content", "display", "en.json");
            File.WriteAllText(file, "{}\n");
            Assert.True(File.Exists(file));
            Assert.False(File.Exists(Path.Combine(
                KeepverseRoots.Content(ws), "content", "display", "en.json")),
                "the pack must not be able to answer for the authored tree");
        }
        finally
        {
            Directory.Delete(cleanup, recursive: true);
        }
    }

    [Fact]
    public void Authored_content_collapses_to_the_repo_root_in_a_legacy_checkout()
    {
        var root = LegacyLayout(out var cleanup);
        try
        {
            Assert.Equal(root, KeepverseRoots.AuthoredContent(root));
        }
        finally
        {
            Directory.Delete(cleanup, recursive: true);
        }
    }

}
