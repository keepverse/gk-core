using System;
using System.IO;
using FusionRpg.Data.Seed;
using Xunit;

namespace FusionRpg.Data.Tests.Seed;

/// <summary>
/// <c>SeedImportRunner.FindUp</c> in both repository layouts.
///
/// <para><b>The defect these pin.</b> Before the Keepverse split every root was the repository root, so
/// "walk up looking for <c>gk-data/packs/fusion/data/seed</c>" was a complete answer. After the split
/// <c>gk-data/packs/fusion/data/seed</c> and <c>gk-data/packs/fusion/data/generated</c> live in a <c>gk-data</c> pack while
/// <c>gk-core/data/tuning</c> lives in <c>gk-core</c>, so a program under <c>gk-core/tests/.../bin</c> reached
/// <c>gk-core/data/tuning</c> by luck and <c>gk-data/packs/fusion/data/seed</c> not at all. Measured against the applied workspace
/// with the private siblings absent: the solution built with 0 errors and
/// <c>FusionRpg.E2E.Tests</c> then failed 296 of 296 with
/// <c>FileNotFoundException: gk-data/packs/fusion/data/seed/saves/_registry/new-save-empires.v1.json not found</c>, and all
/// four generators exited 2 with "could not locate gk-core/data/tuning".
///
/// <para><b>Why the fix is a pre-pass and not a rewrite.</b> The original upward walk is still there,
/// so the tests below assert both halves: that the roots are consulted first, and that a monorepo
/// checkout gets byte-identical answers. The second matters more — a resolver that only works in the
/// split layout breaks every working setup to fix a broken one.
///
/// <para>Real temporary directories, because <c>Directory.Exists</c> against a real tree is the entire
/// subject. A failed delete fails the test.
/// </para>
/// </summary>
[Trait("VerificationId", "data.seed-findup")]
public sealed class SeedImportRunnerFindUpTests
{
    private static string LegacyLayout()
    {
        var root = Directory.CreateTempSubdirectory("kv-findup-legacy-").FullName;
        File.WriteAllText(Path.Combine(root, "FusionRpg.slnx"), "<Solution />");
        Directory.CreateDirectory(Path.Combine(root, "data", "seed"));
        Directory.CreateDirectory(Path.Combine(root, "data", "tuning"));
        return root;
    }

    private static string WorkspaceLayout()
    {
        var ws = Directory.CreateTempSubdirectory("kv-findup-ws-").FullName;
        Directory.CreateDirectory(Path.Combine(ws, "gk-core", "data", "tuning"));
        Directory.CreateDirectory(Path.Combine(ws, "gk-data", "packs", "fusion", "data", "seed"));
        Directory.CreateDirectory(Path.Combine(ws, "gk-data", "packs", "fusion", "data", "generated"));
        return ws;
    }

    /// <summary>A path shaped like a test binary's output directory, which is where the walk started.</summary>
    private static string Bin(string root)
    {
        var bin = Path.Combine(root, "gk-core", "tests", "X.Tests", "bin");
        Directory.CreateDirectory(bin);
        return bin;
    }

    [Fact]
    public void Reaches_data_seed_from_a_bin_directory_in_a_workspace_where_the_walk_alone_cannot()
    {
        var ws = WorkspaceLayout();
        try
        {
            // The exact failure: the upward walk from gk-core/.../bin stops at the gk-core checkout and
            // the seed tree is in a sibling pack.
            Assert.Equal(
                Path.Combine(ws, "gk-data", "packs", "fusion", "data", "seed"),
                SeedImportRunner.FindUp(Bin(ws), "data", "seed"));
        }
        finally
        {
            Directory.Delete(ws, recursive: true);
        }
    }

    [Fact]
    public void Reaches_data_tuning_from_a_bin_directory_in_a_workspace()
    {
        var ws = WorkspaceLayout();
        try
        {
            // The walk reaches this one by luck because gk-core owns gk-core/data/tuning. Asserted anyway
            // because it pins the ordering: content is tried FIRST and must fall through rather than
            // shadow core. The pack has no gk-core/data/tuning, which is what makes the fall-through correct.
            Assert.Equal(
                Path.Combine(ws, "gk-core", "data", "tuning"),
                SeedImportRunner.FindUp(Bin(ws), "data", "tuning"));
        }
        finally
        {
            Directory.Delete(ws, recursive: true);
        }
    }

    [Fact]
    public void Reaches_data_generated_from_a_bin_directory_in_a_workspace()
    {
        var ws = WorkspaceLayout();
        try
        {
            Assert.Equal(
                Path.Combine(ws, "gk-data", "packs", "fusion", "data", "generated"),
                SeedImportRunner.FindUp(Bin(ws), "data", "generated"));
        }
        finally
        {
            Directory.Delete(ws, recursive: true);
        }
    }

    [Fact]
    public void Returns_exactly_what_the_old_walk_returned_in_a_legacy_checkout()
    {
        // The regression that matters most: a monorepo checkout must be byte-identical to its
        // pre-resolver behaviour, because that is the layout every existing tool and test runs in.
        var root = LegacyLayout();
        try
        {
            Assert.Equal(Path.Combine(root, "data", "seed"),
                SeedImportRunner.FindUp(root, "data", "seed"));
            Assert.Equal(Path.Combine(root, "data", "tuning"),
                SeedImportRunner.FindUp(root, "data", "tuning"));
            // And from a nested start, the walk still climbs.
            var nested = Path.Combine(root, "tools", "Thing");
            Directory.CreateDirectory(nested);
            Assert.Equal(Path.Combine(root, "data", "seed"),
                SeedImportRunner.FindUp(nested, "data", "seed"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Still_answers_for_a_path_outside_both_roots_by_walking_up()
    {
        // dist/FusionRpg.Server/data is a real call site (AtomImporter, CreatureSpeciesImport). It is
        // in neither root, so the pre-pass misses and the walk must still find it: the roots are a
        // pre-pass, not a replacement, and a rewrite here would have broken those two tools.
        var root = LegacyLayout();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "dist", "FusionRpg.Server", "data"));
            var from = Path.Combine(root, "tools", "Thing");
            Directory.CreateDirectory(from);
            Assert.Equal(
                Path.Combine(root, "dist", "FusionRpg.Server", "data"),
                SeedImportRunner.FindUp(from, "dist", "FusionRpg.Server", "data"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Returns_null_for_a_path_that_is_nowhere_and_never_throws()
    {
        // FindUp is a probe: callers use it to ask "is it there". It must answer null, not throw,
        // including when no workspace can be detected at all.
        var ws = WorkspaceLayout();
        try
        {
            Assert.Null(SeedImportRunner.FindUp(Bin(ws), "data", "no-such-tree"));
        }
        finally
        {
            Directory.Delete(ws, recursive: true);
        }

        var bare = Directory.CreateTempSubdirectory("kv-findup-bare-").FullName;
        try
        {
            Assert.Null(SeedImportRunner.FindUp(bare, "data", "seed"));
        }
        finally
        {
            Directory.Delete(bare, recursive: true);
        }
    }
}
