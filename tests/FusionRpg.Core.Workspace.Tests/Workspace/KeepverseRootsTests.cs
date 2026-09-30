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
        // The three sibling repositories, because Forge(), Web() and Fusion() now REFUSE when the
        // repository they name is absent. That refusal is the point of naming them - but it means a
        // fixture that omits one no longer exercises the happy path, it silently exercises the
        // refusal path instead, and would report a fixture gap as if it were a resolver defect.
        Directory.CreateDirectory(Path.Combine(ws, "gk-forge", "tools"));
        Directory.CreateDirectory(Path.Combine(ws, "gk-web"));
        Directory.CreateDirectory(Path.Combine(ws, "gk-fusion", "src"));
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

    /// <summary>
    /// gk-fusion is a SIBLING of gk-core, which is why this root had to be written rather than
    /// reused: gk-workflow is an ancestor and Workspace() already named it, but nothing named the
    /// Injector, and six tests were reading it through gk-core's own root.
    ///
    /// <para>The assertion that matters is the last one. A sibling is a real dependency, and a
    /// dependency that cannot be satisfied should SAY SO - a resolver that returns a path for a
    /// repository that is not there produces a missing-file error a long way from the cause, which
    /// is how a wrong root reads as an absent file. The pack check already does this for Content();
    /// this is the same discipline applied to the sibling.</para>
    /// </summary>
    [Fact]
    public void The_fusion_root_is_its_own_sibling_and_is_not_confused_with_core()
    {
        var ws = WorkspaceLayout(out var cleanup);
        try
        {
            Assert.Equal(Path.Combine(ws, "gk-fusion"), KeepverseRoots.Fusion(ws));
            Assert.NotEqual(KeepverseRoots.Core(ws), KeepverseRoots.Fusion(ws));

            // And the Injector's source is reachable from it, not from gk-core. The directory is
            // created first: this is a synthetic workspace, and WriteAllText does not create the
            // directories above it - which is the same reason a missing sibling surfaces as a
            // DirectoryNotFoundException rather than as a message about the sibling.
            var injector = Path.Combine(KeepverseRoots.Fusion(ws), "src", "FusionRpg.Injector", "Host");
            Directory.CreateDirectory(injector);
            var host = Path.Combine(injector, "RpgHost.cs");
            File.WriteAllText(host, "// fixture\n");
            Assert.True(File.Exists(host));
            Assert.False(File.Exists(Path.Combine(
                KeepverseRoots.Core(ws), "src", "FusionRpg.Injector", "Host", "RpgHost.cs")),
                "gk-core must not be able to answer for the Injector");

            // A workspace whose gk-fusion is ABSENT must not yield a root that is not there.
            var bare = Path.Combine(cleanup, "..", "no-fusion-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(bare, "gk-core"));
            Directory.CreateDirectory(Path.Combine(bare, "gk-data"));
            try
            {
                  // This used to assert only Assert.False(Directory.Exists(missing)), which is a
                  // statement about the FIXTURE rather than about the resolver: a phantom path into
                  // a directory that does not exist satisfies it, and so does a correct refusal. It
                  // therefore passed no matter what Fusion() did, while its own comment demanded
                  // that "the caller must be told the sibling is absent". Asserting the refusal - and
                  // the two things a caller needs in order to act on it, WHICH repository is missing
                  // and WHICH override redirects it - is what makes this test able to fail.
                  var refused = Assert.Throws<DirectoryNotFoundException>(() => KeepverseRoots.Fusion(bare));
                  Assert.Contains("gk-fusion", refused.Message, StringComparison.Ordinal);
                  Assert.Contains("KEEPVERSE_FUSION_ROOT", refused.Message, StringComparison.Ordinal);
            }
            finally
            {
                Directory.Delete(bare, recursive: true);
            }
        }
        finally
        {
            Directory.Delete(cleanup, recursive: true);
        }
    }

    [Fact]
    public void The_fusion_root_collapses_to_the_repo_root_in_a_legacy_checkout()
    {
        var root = LegacyLayout(out var cleanup);
        try
        {
            // In the monorepo there was one root, so the Injector and the core were the same
            // directory - which is exactly why every read worked before the split and why a hop
            // count was such a convincing way to write one afterwards.
            Assert.Equal(root, KeepverseRoots.Fusion(root));
        }
        finally
        {
            Directory.Delete(cleanup, recursive: true);
        }
    }

    [Fact]
    public void An_env_override_for_the_fusion_root_wins_over_detection()
    {
        var ws = WorkspaceLayout(out var cleanup);
        var previous = Environment.GetEnvironmentVariable("KEEPVERSE_FUSION_ROOT");
        try
        {
            var elsewhere = Path.Combine(cleanup, "elsewhere");
            Directory.CreateDirectory(elsewhere);
            Environment.SetEnvironmentVariable("KEEPVERSE_FUSION_ROOT", elsewhere);
            Assert.Equal(elsewhere, KeepverseRoots.Fusion(ws));
        }
        finally
        {
            Environment.SetEnvironmentVariable("KEEPVERSE_FUSION_ROOT", previous);
            Directory.Delete(cleanup, recursive: true);
        }
    }

    /// <summary>
    /// gk-forge and gk-web are the two remaining sibling repositories a gk-core path can belong to,
    /// and neither had a name until a guard test asked for one. Both assertions are here for the same
    /// reason as Fusion: a sibling needs an accessor, because no ancestor of gk-core contains it, and
    /// a hop count that happens to work today is a statement about where the file used to live.
    ///
    /// <para>The legacy half is asserted too, and it is not a formality. In a legacy checkout every
    /// root collapses to the single repository, which is what makes these accessors safe to keep in
    /// production code that also has to run before the split is finished.</para>
    /// </summary>
    [Fact]
    public void A_workspace_names_the_gkforge_and_gkweb_siblings()
    {
        var ws = WorkspaceLayout(out var cleanup);
        try
        {
            Assert.Equal(Path.Combine(ws, "gk-forge"), KeepverseRoots.Forge(ws));
            Assert.Equal(Path.Combine(ws, "gk-web"), KeepverseRoots.Web(ws));
        }
        finally
        {
            Directory.Delete(cleanup, recursive: true);
        }
    }

    [Fact]
    public void A_legacy_checkout_resolves_forge_and_web_to_the_one_repository()
    {
        var root = LegacyLayout(out var cleanup);
        try
        {
            Assert.Equal(root, KeepverseRoots.Forge(root));
            Assert.Equal(root, KeepverseRoots.Web(root));
        }
        finally
        {
            Directory.Delete(cleanup, recursive: true);
        }
    }

    [Fact]
    public void An_env_override_for_forge_and_web_wins_over_detection()
    {
        var ws = WorkspaceLayout(out var cleanup);
        var prevForge = Environment.GetEnvironmentVariable("KEEPVERSE_FORGE_ROOT");
        var prevWeb = Environment.GetEnvironmentVariable("KEEPVERSE_WEB_ROOT");
        try
        {
            var elsewhere = Path.Combine(cleanup, "elsewhere");
            Directory.CreateDirectory(elsewhere);
            Environment.SetEnvironmentVariable("KEEPVERSE_FORGE_ROOT", elsewhere);
            Environment.SetEnvironmentVariable("KEEPVERSE_WEB_ROOT", elsewhere);
            Assert.Equal(elsewhere, KeepverseRoots.Forge(ws));
            Assert.Equal(elsewhere, KeepverseRoots.Web(ws));
        }
        finally
        {
            Environment.SetEnvironmentVariable("KEEPVERSE_FORGE_ROOT", prevForge);
            Environment.SetEnvironmentVariable("KEEPVERSE_WEB_ROOT", prevWeb);
            Directory.Delete(cleanup, recursive: true);
        }
    }

    /// <summary>
    /// Non-vacuity, and the reason this is a separate test rather than an extra line in the one
    /// above. Asserting each accessor equals its expected sibling still passes if two of them
    /// answered with the same directory, and it passes if one answered with the workspace root - the
    /// path below it simply stops resolving, which is a failure the suite would report as a
    /// confusing FileNotFoundException far from its cause. This asserts the four siblings are
    /// distinct from each other and from the workspace root, which is the question they exist to
    /// answer.
    /// </summary>
    [Fact]
    public void Core_AuthoredContent_Content_Fusion_Forge_and_Web_are_seven_distinct_places()
    {
        var ws = WorkspaceLayout(out var cleanup);
        try
        {
            var all = new[]
            {
                KeepverseRoots.Core(ws),
                KeepverseRoots.AuthoredContent(ws),
                KeepverseRoots.Content(ws),
                KeepverseRoots.Fusion(ws),
                KeepverseRoots.Forge(ws),
                KeepverseRoots.Web(ws),
                KeepverseRoots.Workspace(ws),
            };
            Assert.Equal(all.Length, all.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
        finally
        {
            Directory.Delete(cleanup, recursive: true);
        }
    }

    /// <summary>
    /// The refusal, which is the reason this test exists at all.
    ///
    /// <para>A sibling repository is not reachable by walking upward, so a resolver that merely
    /// concatenated the name would hand back a confident path into a directory that is not there.
    /// In a standalone gk-core clone - the layout ADDITION 9 asks the workspace to support - that is
    /// every call, and the failure it produces surfaces much later as a FileNotFound naming a
    /// directory the caller has no way to create. This asserts the accessor says WHICH repository is
    /// missing and HOW to redirect it: a refusal that only says "not found" is the same dead end
    /// wearing a better coat.</para>
    ///
    /// <para>Each accessor is checked against its own absent repository, and each message is checked
    /// for its own repository name. One accessor refusing correctly while another silently returned a
    /// phantom path would leave the suite green, which is the outcome this is shaped against.</para>
    /// </summary>
    [Fact]
    public void A_sibling_root_that_is_absent_is_refused_by_name()
    {
        foreach (var (folder, accessor, env) in new[]
                 {
                     ("gk-forge", (Func<string, string>)(s => KeepverseRoots.Forge(s)), "KEEPVERSE_FORGE_ROOT"),
                     ("gk-web", (Func<string, string>)(s => KeepverseRoots.Web(s)), "KEEPVERSE_WEB_ROOT"),
                     ("gk-fusion", (Func<string, string>)(s => KeepverseRoots.Fusion(s)), "KEEPVERSE_FUSION_ROOT"),
                 })
        {
            var ws = WorkspaceLayout(out var cleanup);
            try
            {
                Directory.Delete(Path.Combine(ws, folder), recursive: true);

                var ex = Assert.Throws<DirectoryNotFoundException>(() => accessor(ws));
                Assert.Contains(folder, ex.Message, StringComparison.Ordinal);
                Assert.Contains(env, ex.Message, StringComparison.Ordinal);
            }
            finally
            {
                Directory.Delete(cleanup, recursive: true);
            }
        }
    }

    /// <summary>
    /// The counterpart, so the refusal above cannot be satisfied by refusing everything. In a legacy
    /// checkout every repository is the one directory, and the sibling accessors must keep returning
    /// it - which is what makes them safe in production code that also has to run before the split is
    /// finished. A rule that refuses whenever it cannot walk upward would break exactly that case.
    /// </summary>
    [Fact]
    public void A_sibling_accessor_still_resolves_in_a_legacy_checkout_where_everything_is_one_directory()
    {
        var root = LegacyLayout(out var cleanup);
        try
        {
            Assert.Equal(root, KeepverseRoots.Forge(root));
            Assert.Equal(root, KeepverseRoots.Web(root));
            Assert.Equal(root, KeepverseRoots.Fusion(root));
            Assert.Equal(root, KeepverseRoots.AuthoredContent(root));
        }
        finally
        {
            Directory.Delete(cleanup, recursive: true);
        }
    }

}
