using System.Diagnostics;
using System.Reflection;
using FusionRpg.SquadHarness.Tests.TestSupport;
using FusionRpg.Tools.SquadHarness;
using Xunit;

namespace FusionRpg.SquadHarness.Tests;

/// <summary>
/// spec-squad-harness.md "Testing strategy": "Determinism is the hard requirement, so it is asserted
/// three ways, not one." All tests here run over a small SYNTHETIC roster (three real corner builds,
/// not the full 91/23) at a tiny trial count -- spec's own words for the in-process variant: "run twice
/// in one process at a small --trials." Using the shipped <see cref="BuildFactory"/>/<see cref="SquadMatch"/>
/// machinery on a tiny slice keeps these fast without weakening what they prove: the seed function and
/// the aggregation are the same code path the full 91/23 rosters use.
/// </summary>
public class DeterminismTests
{
    static IReadOnlyList<RosterEntry> TinyRoster() => new[]
    {
        new RosterEntry("Might", new[] { BuildFactory.Build("Might") }),
        new RosterEntry("Agility", new[] { BuildFactory.Build("Agility") }),
        new RosterEntry("Bulwark", new[] { BuildFactory.Build("Bulwark") }),
    };

    static RunSpec TinySpec(ulong seed) => new(Theta: 60, Trials: 3, RunSeed: seed);

    [Fact]
    public void Every_run_repeats_byte_identically_in_process()
    {
        var run1 = Sweep.Run("tiny", TinyRoster(), TinySpec(20260906), parallel: false);
        var run2 = Sweep.Run("tiny", TinyRoster(), TinySpec(20260906), parallel: false);
        Assert.Equal(DeterminismHash.Hash(run1), DeterminismHash.Hash(run2));
        Assert.Equal(DeterminismHash.CanonicalJson(run1), DeterminismHash.CanonicalJson(run2));
    }

    [Fact]
    public void Parallel_and_serial_agree()
    {
        var serial = Sweep.Run("tiny", TinyRoster(), TinySpec(777), parallel: false);
        var parallel = Sweep.Run("tiny", TinyRoster(), TinySpec(777), parallel: true);
        Assert.Equal(DeterminismHash.Hash(serial), DeterminismHash.Hash(parallel));
    }

    [Fact]
    public void Reordering_the_roster_does_not_move_a_cell()
    {
        var spec = TinySpec(555);
        var inOrder = TinyRoster();
        var shuffled = new[] { inOrder[2], inOrder[0], inOrder[1] }; // same SET, different enumeration order

        var runA = Sweep.Run("tiny", inOrder, spec, parallel: false);
        var runB = Sweep.Run("tiny", shuffled, spec, parallel: false);

        // Same set of ids -> the seed-index assignment (Id-sorted position) is identical either way,
        // so every surviving (attacker, defender) cell reports the identical outcome.
        Assert.Equal(DeterminismHash.Hash(runA), DeterminismHash.Hash(runB));
    }

    [Fact]
    public void Reordering_that_changes_which_ids_exist_does_move_which_cells_exist_but_not_their_values()
    {
        var spec = TinySpec(333);
        var full = TinyRoster();
        var subset = new[] { full[0], full[1] }; // drop "Bulwark" entirely

        var fullRun = Sweep.Run("tiny", full, spec, parallel: false);
        var subsetRun = Sweep.Run("tiny", subset, spec, parallel: false);

        // The surviving (Might, Agility) cell must report the identical counts in both runs.
        var fromFull = fullRun.Pairs.Single(p => p.AttackerId == "Might" && p.DefenderId == "Agility");
        var fromSubset = subsetRun.Pairs.Single(p => p.AttackerId == "Might" && p.DefenderId == "Agility");
        Assert.Equal(fromFull, fromSubset);
    }

    [Fact]
    public void The_hash_excludes_provenance()
    {
        // HarnessRun carries no provenance field at all (no `at`/`environmentStamp`/`wallClockMs`) --
        // this is asserted structurally: every field on the hashed records is enumerated and none of
        // them is a timestamp-shaped string. A determinism hash is only "portable" if nothing
        // machine/run-specific ever reaches it.
        var properties = typeof(HarnessRun).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Concat(typeof(PairResult).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Select(p => p.Name.ToLowerInvariant())
            .ToList();
        foreach (var banned in new[] { "at", "environmentstamp", "wallclockms", "timestamp" })
            Assert.DoesNotContain(banned, properties);
    }

    // A reflection test banning float/double from the hashed record graph lived here; it was removed
    // by the 2026-09-15 owner ruling (floating point is allowed for any quantity; determinism of a
    // double in a hashed golden is handled by the platform stamp, ssot-power-scale.md §10.7). The
    // byte-identical repeat tests above and the second-process test below still prove determinism by
    // value.

    /// <summary>F1's acceptance bullet: "A second process reproduces the hash." A fresh OS process
    /// (not just a second in-process run) catches static state carried across runs an in-process repeat
    /// cannot see. Scoped to <c>--roster squad --limit 4</c> (an F1-internal cost knob, see Modes.cs) so
    /// the check stays fast: the point is proving determinism, not re-measuring the full 506-cell
    /// matrix.</summary>
    [Fact]
    public void A_second_process_reproduces_the_hash()
    {
        var (exit1, stdout1, stderr1) = Run("verify --seed 20260906 --theta 60 --trials 2 --roster squad --limit 4");
        var (exit2, stdout2, stderr2) = Run("verify --seed 20260906 --theta 60 --trials 2 --roster squad --limit 4");
        Assert.True(exit1 == 0, $"exit {exit1}\n{stdout1}\n{stderr1}");
        Assert.True(exit2 == 0, $"exit {exit2}\n{stdout2}\n{stderr2}");
        Assert.Equal(stdout1, stdout2);
    }

    [Fact]
    public void A_missing_seed_is_a_refusal_naming_it()
    {
        var (exit, stdout, stderr) = Run("verify --theta 60 --trials 2");
        Assert.NotEqual(0, exit);
        Assert.Contains("seed", (stdout + stderr), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Launches the referenced SquadHarness as a real separate process, WITHOUT an implicit build.
    ///
    /// <para><b>Why not <c>dotnet run --project</c>.</b> That form rebuilds SquadHarness — which
    /// references <c>FusionRpg.Core</c> — in a child process while the parent <c>dotnet test</c> still
    /// holds Core's output, so the child dies on a locked file (CS2012, VBCSCompiler). The signature is
    /// unmistakable and was measured here on 2026-09-17: <b>red inside a full-suite run, green when the
    /// test is run alone</b>, and <c>A_second_process_reproduces_the_hash</c> spawns two of them. It
    /// looked like a determinism break in a test named for determinism, which is the worst place for a
    /// build race to hide.</para>
    ///
    /// <para>SquadHarness is already a <c>ProjectReference</c> of this test project, so this test
    /// host's own build produces its apphost beside the test dll; launching that directly removes the
    /// child build at the root. Same fix, same reason, as <c>ToolProcess</c> in
    /// <c>FusionRpg.Core.Tests</c> and the convention <c>FusionRpg.AtomImporter.Tests</c> established
    /// before it.</para>
    /// </summary>
    static (int Exit, string Stdout, string Stderr) Run(string args)
    {
        var repoRoot = FindRepoRoot();
        var dir = AppContext.BaseDirectory;
        var apphost = Path.Combine(dir, OperatingSystem.IsWindows() ? "SquadHarness.exe" : "SquadHarness");
        var dll = Path.Combine(dir, "SquadHarness.dll");

        Assert.True(File.Exists(apphost) || File.Exists(dll),
            $"SquadHarness was not built beside the test dll ({dir}); the ProjectReference should have produced it");

        var psi = File.Exists(apphost)
            ? new ProcessStartInfo
            {
                FileName = apphost,
                Arguments = args,
                CreateNoWindow = true,
                WorkingDirectory = repoRoot,
            }
            : new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"\"{dll}\" {args}",
                CreateNoWindow = true,
                WorkingDirectory = repoRoot,
            };

        return ExternalProcess.Run(psi, 180_000, "SquadHarness invocation timed out");
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Core"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("could not locate repo root above " + AppContext.BaseDirectory);
    }
}
