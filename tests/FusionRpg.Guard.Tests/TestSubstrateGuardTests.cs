using System.Diagnostics;
using Xunit;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// Runs gk-core/scripts/guard-test-substrate.py and proves it both passes on the current tree and FAILS on
/// a planted violation. Standard: docs/contributing/testing-standard.md. Added 2026-09-12 with the
/// data-test-substrate program (the leak that hid 65.5 GB of temp dirs).
/// </summary>
public class TestSubstrateGuardTests
{
    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not find repo root with Directory.Build.props");
    }

    static (int exit, string stdout, string stderr) RunGuard(string root, string? baselinePath = null)
    {
        // The tool always lives in the REAL repo; only the tree it scans and the ratchet it reads are
        // redirected, which is what lets a planted violation stay out of the real tests/ tree.
        //
        // This is ALSO the exemption that makes the suite possible: the guard skips
        // `TestSubstrateGuardTests.cs` by name, because a test that proves the guard fires has to
        // contain the banned shape itself. The guard's contract test asserts the exemption is exactly
        // one entry, so it cannot widen.
        // FINDINGS ARE READ FROM `stdout + stderr` THROUGHOUT THIS FILE, and that is the point rather
        // than a convenience. The guard reports its findings on STDERR - the port standard, so a caller
        // reading stdout alone gets the verdict and nothing that could be mistaken for it - while the
        // PowerShell original printed everything through `Write-Host`, which is stdout. Five assertions
        // below were reading the wrong stream the moment the port landed and went red saying so, with an
        // empty string where the finding should have been.
        var script = Path.Combine(RepoRoot(), "scripts", "guard-test-substrate.py");
        var baseline = baselinePath is null ? "" : $" --baseline-path \"{baselinePath}\"";
        var psi = new ProcessStartInfo
        {
            FileName = "python",
            Arguments = $"\"{script}\" --root \"{root}\"{baseline}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        return ExternalProcess.Run(psi, 120_000, "test-substrate guard timed out");
    }

    /// <summary>
    /// A throwaway tree with a `gk-core/tests/FusionRpg.Data.Tests/` the guard will scan, and an empty ratchet.
    ///
    /// <para><b>Why not plant in the real tree.</b> These two tests used to write their probe into the
    /// repo's own <c>gk-core/tests/FusionRpg.Data.Tests/</c>. While that file existed, EVERY parallel test that
    /// runs this gate saw a real violation — <c>test_fast.py</c> runs it as its first step, so
    /// <c>VerificationBoundaryWorkflowTests.Test_fast_requires_an_explicit_scope</c> failed whenever
    /// the two overlapped, in different classes and therefore in parallel by default. And a crashed run
    /// left the probe behind for good, poisoning every later run until someone deleted it by hand.</para>
    /// </summary>
    sealed class ProbeTree : IDisposable
    {
        public string Root { get; }
        public string BaselinePath { get; }

        public ProbeTree()
        {
            Root = Path.Combine(Path.GetTempPath(), "substrate-probe-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Root, "tests", "FusionRpg.Data.Tests"));

            // Empty, not the real ratchet: against a tree holding one file, every real baseline entry
            // would report as "no longer violated" and drown the assertion it is meant to prove.
            BaselinePath = Path.Combine(Root, "baseline.txt");
            File.WriteAllText(BaselinePath, string.Empty);
        }

        public void Plant(string fileName, string content) =>
            File.WriteAllText(Path.Combine(Root, "tests", "FusionRpg.Data.Tests", fileName), content);

        public void Dispose()
        {
            // A failed temp-delete is a failure, never a swallowed catch — the standard this very
            // guard enforces, applied to the guard's own test.
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    [Fact]
    public void Guard_exits_zero_on_the_current_tree()
    {
        var root = RepoRoot();
        var (exit, stdout, stderr) = RunGuard(root);
        Assert.True(exit == 0, $"guard failed exit={exit}\nstdout:\n{stdout}\nstderr:\n{stderr}");
        Assert.Contains("TEST SUBSTRATE GUARD OK", stdout + stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Guard_fails_on_a_planted_swallowed_delete_and_temp_store()
    {
        using var tree = new ProbeTree();
        tree.Plant("ZzGuardProbeTests.cs", """
            using Xunit;
            namespace FusionRpg.Data.Tests;
            public class ZzGuardProbeTests
            {
                [Fact]
                public void Swallowed()
                {
                    var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "probe");
                    System.IO.Directory.CreateDirectory(dir);
                    new RpgStore(dir).Init();
                    try { System.IO.Directory.Delete(dir, true); } catch { /* temp */ }
                }
            }
            """);

        var (exit, stdout, stderr) = RunGuard(tree.Root, tree.BaselinePath);

        Assert.True(exit != 0, $"expected a failing exit, got 0\nstdout:\n{stdout}");
        Assert.Contains("TEST SUBSTRATE GUARD FAILED", stdout + stderr, StringComparison.Ordinal);
        Assert.Contains("swallowed-delete", stdout + stderr, StringComparison.Ordinal);
        Assert.Contains("temp-store", stdout + stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Guard_fails_on_a_planted_swallowed_File_Delete()
    {
        // solid-enforcement tuning-immutability SE2.3: swallowed-delete now also matches File.Delete,
        // not only Directory.Delete -- the exact call shape SE2.2's own incident used to leak four
        // throwaway tuning files into gk-core/data/tuning/ (a crashed run never reached the swallowed catch's
        // "cleanup", and the files stayed committed until this program found them).
        using var tree = new ProbeTree();
        tree.Plant("ZzGuardFileDeleteProbeTests.cs", """
            using Xunit;
            namespace FusionRpg.Data.Tests;
            public class ZzGuardFileDeleteProbeTests
            {
                [Fact]
                public void Swallowed()
                {
                    var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "probe.json");
                    System.IO.File.WriteAllText(path, "{}");
                    try { System.IO.File.Delete(path); } catch { /* best effort */ }
                }
            }
            """);

        var (exit, stdout, stderr) = RunGuard(tree.Root, tree.BaselinePath);

        Assert.True(exit != 0, $"expected a failing exit, got 0\nstdout:\n{stdout}");
        Assert.Contains("TEST SUBSTRATE GUARD FAILED", stdout + stderr, StringComparison.Ordinal);
        Assert.Contains("swallowed-delete", stdout + stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Guard_fails_on_a_planted_untagged_file_backed_store()
    {
        // The third rule — `untagged-file-store`, added 2026-09-23 by `diskw-fix`: a tests/** file
        // that constructs a file-backed store with the memory plan nowhere in sight and no
        // `[Trait("Category", "DiskSemantics")]` opens a real file on EVERY default-profile run.
        // The gate's own self-test was hook-protected when that lane ran, so the rule shipped with
        // the throwaway-probe proof only; this is the in-repo case.
        using var tree = new ProbeTree();
        tree.Plant("ZzGuardUntaggedStoreProbeTests.cs", """
            using Xunit;
            namespace FusionRpg.Data.Tests;
            public class ZzGuardUntaggedStoreProbeTests
            {
                [Fact]
                public void Untagged()
                {
                    // DataTestStore.CreateFileBacked() is the helper's own file plan, and this file
                    // names no memory URI and carries no DiskSemantics trait.
                    using var store = DataTestStore.CreateFileBacked();
                    Assert.NotNull(store.Store);
                }
            }
            """);

        var (exit, stdout, stderr) = RunGuard(tree.Root, tree.BaselinePath);

        Assert.True(exit != 0, $"expected a failing exit, got 0\nstdout:\n{stdout}");
        Assert.Contains("TEST SUBSTRATE GUARD FAILED", stdout + stderr, StringComparison.Ordinal);
        Assert.Contains("untagged-file-store", stdout + stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Guard_passes_the_same_file_backed_store_once_it_carries_the_DiskSemantics_trait()
    {
        // The rule is a CONTRACT, not a ban on file-backed stores: the trait IS the default
        // profile's filter, so the identical construction with the tag is legitimate — which is why
        // this is a shape the code owns rather than a file list. (The companion case above proves
        // the same text is refused without the tag; this one proves the tag is what clears it.)
        using var tree = new ProbeTree();
        tree.Plant("ZzGuardTaggedStoreProbeTests.cs", """
            using Xunit;
            namespace FusionRpg.Data.Tests;
            [Trait("Category", "DiskSemantics")]
            public class ZzGuardTaggedStoreProbeTests
            {
                [Fact]
                public void Tagged()
                {
                    using var store = DataTestStore.CreateFileBacked();
                    Assert.NotNull(store.Store);
                }
            }
            """);

        var (exit, stdout, stderr) = RunGuard(tree.Root, tree.BaselinePath);

        Assert.True(exit == 0, $"a DiskSemantics-tagged file-backed store must not be flagged\nstdout:\n{stdout}");
    }

    [Fact]
    public void Guard_fails_on_a_planted_corpus_copied_into_a_temp_dir()
    {
        // The fourth rule — `temp-corpus`, added 2026-09-23 by `diskw-fix2`: a tests/** file that copies
        // the shipped seed corpus into a temp directory to get a corpus it can add rows to. Eight files did
        // exactly that (six in Data.Tests, two in gk-core/tests/FusionRpg.Server.Tests), on every default-profile
        // run times every lane, and none of the three rules above could see them. The corpus is data:
        // `StructureCorpus.FromRows`/`WithRows` is the entrance that replaced the copy.
        using var tree = new ProbeTree();
        tree.Plant("ZzGuardCorpusCopyProbeTests.cs", """
            using Xunit;
            namespace FusionRpg.Data.Tests;
            public class ZzGuardCorpusCopyProbeTests
            {
                [Fact]
                public void Copies()
                {
                    var realRoot = Path.Combine(ContentRoot.Path, "data", "seed", "structures");
                    var tmp = Path.Combine(Path.GetTempPath(), "cargo-probe-test-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tmp);
                    foreach (var path in Directory.EnumerateFiles(realRoot, "*.json", SearchOption.AllDirectories))
                    {
                        var dest = Path.Combine(tmp, Path.GetRelativePath(realRoot, path));
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        File.Copy(path, dest);
                    }
                    StructureCatalog.Configure(StructureCorpus.Load(tmp));
                }
            }
            """);

        var (exit, stdout, stderr) = RunGuard(tree.Root, tree.BaselinePath);

        Assert.True(exit != 0, $"expected a failing exit, got 0\nstdout:\n{stdout}");
        Assert.Contains("TEST SUBSTRATE GUARD FAILED", stdout + stderr, StringComparison.Ordinal);
        Assert.Contains("temp-corpus", stdout + stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Guard_passes_a_fixture_that_appends_corpus_rows_in_memory_instead()
    {
        // The same fixture without the copy — the real corpus read in place, the suite's own rows appended
        // by `WithRows`. That is the shape all eight files use now, and the reason the rule is a contract
        // about a COPY rather than a ban on a suite needing rows the shipped corpus does not carry.
        // (The planted text is scanned, never compiled or run.)
        using var tree = new ProbeTree();
        tree.Plant("ZzGuardCorpusOverlayProbeTests.cs", """
            using Xunit;
            namespace FusionRpg.Data.Tests;
            public class ZzGuardCorpusOverlayProbeTests
            {
                [Fact]
                public void Appends()
                {
                    var real = StructureCorpus.Load(Path.Combine(ContentRoot.Path, "data", "seed", "structures"));
                    StructureCatalog.Configure(real.WithRows(real.Rows.ToArray()));
                }
            }
            """);

        var (exit, stdout, stderr) = RunGuard(tree.Root, tree.BaselinePath);

        Assert.True(exit == 0, $"a fixture that reads the shipped corpus and appends rows in memory must not be flagged\nstdout:\n{stdout}");
    }

    [Fact]
    public void Guard_fails_on_a_planted_corpus_written_into_a_temp_dir()
    {
        // The fifth rule — `temp-corpus-write`, added 2026-09-23 by `diskw-fix2` (BU9): the write-shaped
        // sibling of `temp-corpus`. Six Core.Tests wonder/import suites wrote a corpus JSON into `%TEMP%`
        // and loaded it back on every run; the corpus is data, so they now hand their rows to
        // `StructureCorpus.FromJson` and write nothing. Distinct from the copy rule on purpose — one copies
        // the shipped corpus, this one authors its own rows — and it keeps the same `DiskSemantics` escape
        // hatch for a test whose subject really is the loader's file contract.
        using var tree = new ProbeTree();
        tree.Plant("ZzGuardCorpusWriteProbeTests.cs", """
            using Xunit;
            namespace FusionRpg.Data.Tests;
            public class ZzGuardCorpusWriteProbeTests
            {
                [Fact]
                public void Writes()
                {
                    var tmp = Path.Combine(Path.GetTempPath(), "probe-corpus-" + Guid.NewGuid());
                    Directory.CreateDirectory(tmp);
                    File.WriteAllText(Path.Combine(tmp, "corpus.json"), "{ \"kind\": \"structure-anchor\", \"entries\": [] }");
                    StructureCatalog.Configure(StructureCorpus.Load(tmp));
                }
            }
            """);

        var (exit, stdout, stderr) = RunGuard(tree.Root, tree.BaselinePath);

        Assert.True(exit != 0, $"expected a failing exit, got 0\nstdout:\n{stdout}");
        Assert.Contains("TEST SUBSTRATE GUARD FAILED", stdout + stderr, StringComparison.Ordinal);
        Assert.Contains("temp-corpus-write", stdout + stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Guard_passes_the_same_rows_handed_over_in_memory()
    {
        // `StructureCorpus.FromJson` is the entrance the six fixed suites use: the same document text,
        // parsed in memory, no temp path and no write.
        using var tree = new ProbeTree();
        tree.Plant("ZzGuardCorpusInMemoryProbeTests.cs", """
            using Xunit;
            namespace FusionRpg.Data.Tests;
            public class ZzGuardCorpusInMemoryProbeTests
            {
                [Fact]
                public void InMemory()
                {
                    var corpus = StructureCorpus.FromJson("{ \"kind\": \"structure-anchor\", \"entries\": [] }");
                    StructureCatalog.Configure(corpus);
                }
            }
            """);

        var (exit, stdout, stderr) = RunGuard(tree.Root, tree.BaselinePath);

        Assert.True(exit == 0, $"a corpus handed over in memory must not be flagged\nstdout:\n{stdout}");
    }

    [Fact]
    public void Guard_does_not_flag_a_rethrow_cleanup()
    {
        using var tree = new ProbeTree();
        tree.Plant("ZzGuardCleanProbeTests.cs", """
            using System.IO;
            using Xunit;
            namespace FusionRpg.Data.Tests;
            public class ZzGuardCleanProbeTests
            {
                [Fact]
                public void Clean()
                {
                    var dir = Path.Combine(Path.GetTempPath(), "probe");
                    Directory.CreateDirectory(dir);
                    try { Directory.Delete(dir, recursive: true); }
                    catch (IOException ex) { throw new InvalidOperationException("cleanup failed", ex); }
                }
            }
            """);

        var (exit, stdout, stderr) = RunGuard(tree.Root, tree.BaselinePath);

        Assert.True(exit == 0, $"a rethrow cleanup must not be flagged\nstdout:\n{stdout}");
    }

    [Fact]
    public void test_substrate_guard_is_gated_by_its_owning_phase()
    {
        var root = RepoRoot();
        GuardWiring.AssertGuardReachableInItsOwningPhase(root, "test-substrate");
    }
}
