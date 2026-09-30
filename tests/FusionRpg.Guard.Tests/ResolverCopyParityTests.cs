using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// The workspace root resolver exists as THREE byte-copies on disk - gk-core's
/// <c>scripts/lib/keepverse_roots.py</c>, gk-fusion's, and seedsmith's
/// <c>workspace_roots.py</c> - and nothing held them together. An audit found all three carrying the
/// same seven accessors and the same <c>RootNotFound</c>, 31 lines apart, with the validated-override
/// check present in one and absent from the other two: so a guard could be silenced by a bad
/// <c>KEEPVERSE_*_ROOT</c> in gk-fusion and not in gk-core, and the divergence had been invisible
/// because no test compared them.
///
/// Copying is the right shape here - a Python module cannot be shared across repositories without a
/// package, and three repositories must run their guards from a standalone clone - which is exactly
/// why the copy needs a test. A contract with three implementations and no parity check is three
/// contracts.
///
/// These assert the CONTRACT, not a byte count: identical content, and the refusal behaviour that the
/// divergence actually broke.
/// </summary>
[Trait("VerificationId", "guard.resolver-copy-parity")]
public sealed class ResolverCopyParityTests
{
    private static string Core() => KeepverseRoots.Core();

    private static string Fusion() => KeepverseRoots.Fusion();

    private static string Forge() => KeepverseRoots.Forge();

    private static string Workspace() => KeepverseRoots.Workspace();

    /// <summary>Every copy of the contract, wherever this workspace happens to keep it.</summary>
    private static IReadOnlyList<(string Label, string Path)> Copies()
    {
        var found = new List<(string, string)>
        {
            ("gk-core scripts/lib", Path.Combine(Core(), "scripts", "lib", "keepverse_roots.py")),
            ("gk-fusion scripts/lib", Path.Combine(Fusion(), "scripts", "lib", "keepverse_roots.py")),
            ("gk-forge seedsmith", Path.Combine(Forge(), "tools", "seedsmith", "seedsmith", "workspace_roots.py")),
        };
        return found;
    }

    private static string Sha256(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(stream));
    }

    [Fact]
    public void W1_every_copy_on_disk_is_byte_identical_to_gk_cores()
    {
        var canonical = Path.Combine(Core(), "scripts", "lib", "keepverse_roots.py");
        Assert.True(File.Exists(canonical), $"missing the canonical resolver: {canonical}");

        var expected = Sha256(canonical);
        var compared = 0;
        foreach (var (label, path) in Copies())
        {
            if (!File.Exists(path))
            {
                // A repository that is not checked out is not a divergence; it is an absent sibling,
                // and the resolver refuses for that case on purpose. Asserting its absence here would
                // make a partial workspace fail a test about copies.
                continue;
            }

            Assert.True(Sha256(path) == expected,
                $"{label} ({path}) has drifted from gk-core's copy. The three copies are one contract; "
              + "a fix applied to one and not the others is how a guard ends up silenceable in one "
              + "repository and not in another. Converge the copies rather than editing one.");
            compared++;
        }

        Assert.True(compared >= 1, "no resolver copy was found to compare, so this test proved nothing");
    }

    [Fact]
    public void W2_the_copies_expose_the_same_seven_accessors()
    {
        // Byte parity already implies this, so the test is on the CONTRACT rather than the file: it
        // names what a caller is entitled to call, so that ADDING an accessor to one copy without the
        // others fails here with a readable message rather than as a byte-hash mismatch.
        string[] expected =
        {
            "authored_content_root", "content_root", "core_root", "forge_root",
            "fusion_root", "web_root", "workspace_root",
        };

        foreach (var (label, path) in Copies())
        {
            if (!File.Exists(path)) continue;
            var text = File.ReadAllText(path);
            foreach (var accessor in expected)
            {
                Assert.True(text.Contains($"def {accessor}("),
                    $"{label} does not define {accessor}(); the copies have diverged in their surface");
            }
        }
    }

    /// <summary>
    /// The behaviour the divergence actually broke. <c>_env</c> used to return a
    /// <c>KEEPVERSE_*_ROOT</c> override unchecked, which made the override a way to switch the
    /// refusal off: a guard received a confident path to a directory that was not there, carried on,
    /// and reported the ABSENCE as findings against real code. This runs the resolver rather than
    /// reading its source, because a source-text assertion on a docstring is not a check of behaviour.
    /// </summary>
    [Fact]
    public void W3_an_override_naming_a_directory_that_does_not_exist_is_refused_not_handed_back()
    {
        var absent = Path.Combine(Path.GetTempPath(), "keepverse-resolver-parity-absent-" + Guid.NewGuid().ToString("N"));
        var probes = new (string Env, string Accessor)[]
        {
            ("KEEPVERSE_FUSION_ROOT", "fusion_root"),
            ("KEEPVERSE_FORGE_ROOT", "forge_root"),
            ("KEEPVERSE_CONTENT_ROOT", "content_root"),
            ("KEEPVERSE_WORKSPACE_ROOT", "workspace_root"),
        };

        foreach (var (envName, accessor) in probes)
        {
            var libDir = Path.Combine(Core(), "scripts", "lib");
            var psi = new ProcessStartInfo("python")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = Core(),
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(
                "import sys; sys.path.insert(0, r'" + libDir + "'); import keepverse_roots as k; "
              + $"print(k.{accessor}())");
            psi.Environment[envName] = absent;

            // Concurrent drain via the approved helper. Reading stdout to completion and THEN stderr
            // is a pipe deadlock: whichever stream fills its buffer first stops the child writing, so
            // the child never exits and WaitForExit never returns. SubprocessPipeDrainGuardTests
            // caught exactly that in the first version of this test, which is the guard working.
            var (exit, stdout, stderr) = ExternalProcess.Run(psi, 120_000,
                $"{accessor}() did not finish; the override probe must not hang the suite");

            Assert.True(exit != 0,
                $"{accessor}() returned {stdout.Trim()} for {envName} naming a directory that does not "
              + "exist. An unchecked override hands the caller a path to nothing, and the failure then "
              + "surfaces several frames later as findings about files the caller never supplied.");
            Assert.True(stderr.Contains("does not name a directory"),
                $"{accessor}() failed for the wrong reason, so this test is not exercising the check it "
              + $"claims to. stderr was: {stderr.Trim()}");
        }
    }
}
