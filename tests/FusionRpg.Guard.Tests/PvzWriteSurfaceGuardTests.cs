using System.Diagnostics;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// solid-enforcement `pvz-write-surface` (SE2.5, spec-pvz-write-surface.md) — the W2/W3 extension to
/// gk-fusion/scripts/guard-single-writer.py. W2: each of the four allowed writer files may write only its own
/// pinned field list. W3: five RETIRED fields (`attackDamage`, `theAttackDamage`, `theShieldHealth`,
/// `theArmor`, `takeDmgMultiplier` — the 2026-09-16 owner ruling that the RPG's own combat math
/// already pays them) may never be written, in ANY file, allowed files included — the first rule
/// that catches an uncommented retired line INSIDE an allowed file, which the pre-existing W1 rule
/// (file-boundary only) never could.
///
/// Fixtures plant a file NAMED like one of the four allowed writers (matched by filename only, the
/// same way the real guard's own `$allowed -contains $_.Name` check works) under a throwaway
/// gk-fusion/src/FusionRpg.Injector/ tree — never the real files.
/// </summary>
public class PvzWriteSurfaceGuardTests
{
    [Fact]
    public void W3_fails_when_an_allowed_file_uncomments_a_retired_field()
    {
        // The exact incident this rule exists for: W1 never looked inside EntityStatWriter.cs at
        // all (it is on the allowed-files list), so uncommenting line 120's `p.attackDamage = ...`
        // used to pass the old guard outright.
        var fixture = NewFixture();
        try
        {
            WriteInjectorFile(fixture, "EntityStatWriter.cs",
                "namespace X { class EntityStatWriter { static void WritePlant(Plant p, long v) { p.attackDamage = v; } } }\n");

            var (exit, stdout, stderr) = RunGuard(fixture);
            Assert.True(exit != 0, $"expected W3 to fail, got exit=0\n{stdout}\n{stderr}");
            Assert.Contains("W3", stderr, StringComparison.Ordinal);
            Assert.Contains("attackDamage", stderr, StringComparison.Ordinal);
        }
        finally { Cleanup(fixture); }
    }

    [Fact]
    public void W3_a_commented_out_retired_field_reference_still_passes()
    {
        var fixture = NewFixture();
        try
        {
            WriteInjectorFile(fixture, "EntityStatWriter.cs",
                "namespace X { class EntityStatWriter { static void WritePlant(Plant p, long v) { " +
                "// p.attackDamage = v;\n" +
                "p.thePlantHealth = v; } } }\n");

            var (exit, stdout, stderr) = RunGuard(fixture);
            Assert.True(exit == 0, $"a comment must never be scanned as a write\nexit={exit}\n{stdout}\n{stderr}");
        }
        finally { Cleanup(fixture); }
    }

    [Fact]
    public void W2_fails_when_EntityStatWriter_gains_an_unpinned_field()
    {
        var fixture = NewFixture();
        try
        {
            WriteInjectorFile(fixture, "EntityStatWriter.cs",
                "namespace X { class EntityStatWriter { static void WriteZombie(Zombie z, int v) { z.theZombieType = v; } } }\n");

            var (exit, stdout, stderr) = RunGuard(fixture);
            Assert.True(exit != 0, $"expected W2 to fail on a new, un-pinned field\nexit=0\n{stdout}\n{stderr}");
            Assert.Contains("W2", stderr, StringComparison.Ordinal);
            Assert.Contains("theZombieType", stderr, StringComparison.Ordinal);
        }
        finally { Cleanup(fixture); }
    }

    [Fact]
    public void W2_a_field_from_the_pinned_list_passes()
    {
        var fixture = NewFixture();
        try
        {
            WriteInjectorFile(fixture, "EntityStatWriter.cs",
                "namespace X { class EntityStatWriter { static void WritePlant(Plant p, long v) { p.thePlantHealth = v; } } }\n");

            var (exit, stdout, stderr) = RunGuard(fixture);
            Assert.True(exit == 0, $"expected pass, got exit={exit}\n{stdout}\n{stderr}");
        }
        finally { Cleanup(fixture); }
    }

    [Fact]
    public void W2_UniqueBoundLoadout_own_pinned_list_is_empty_so_any_direct_field_write_fails()
    {
        // UniqueBoundLoadout.cs grants entirely through the RPG-layer Funnel today (measured,
        // spec-pvz-write-surface.md) -- a direct field write there would be a NEW regression back
        // toward the pre-Funnel shape, and the empty list is what catches it.
        var fixture = NewFixture();
        try
        {
            WriteInjectorFile(fixture, "UniqueBoundLoadout.cs",
                "namespace X { class UniqueBoundLoadout { static void Apply(Plant p, long v) { p.thePlantMaxHealth = v; } } }\n");

            var (exit, stdout, stderr) = RunGuard(fixture);
            Assert.True(exit != 0, $"expected W2 to fail, got exit=0\n{stdout}\n{stderr}");
            Assert.Contains("W2", stderr, StringComparison.Ordinal);
        }
        finally { Cleanup(fixture); }
    }

    [Fact]
    public void A_comparison_is_never_mistaken_for_a_write()
    {
        var fixture = NewFixture();
        try
        {
            WriteInjectorFile(fixture, "EntityStatWriter.cs",
                "namespace X { class EntityStatWriter { static bool Check(Zombie z, float x) => z.theSpeed == x; } }\n");

            var (exit, stdout, stderr) = RunGuard(fixture);
            Assert.True(exit == 0, $"a == comparison must never be flagged\nexit={exit}\n{stdout}\n{stderr}");
        }
        finally { Cleanup(fixture); }
    }

    [Fact]
    public void The_real_tree_still_passes()
    {
        var (exit, stdout, stderr) = RunGuard(FindRepoRoot());
        Assert.True(exit == 0, $"exit={exit}\n{stdout}\n{stderr}");
        Assert.Contains("SINGLE-WRITER GUARD OK", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void The_registry_invariant_names_this_guard_not_a_second_one()
    {
        // spec-pvz-write-surface.md's own "Project structure" table names an invariant id
        // `claude-rpg-layer-only` -- the registry's real row for this exact rule (AGENTS.md's "every
        // RPG feature lives in the RPG layer") is `pr-rpg-layer-only`, and it ALREADY lists
        // single-writer among its guards, so R8 needs no new row here. No pvz-write-surface guard
        // row is added either way -- this module extends the existing gate, per its own title.
        var registry = EnforcementRegistry.Load(FindRepoRoot());
        Assert.Contains(registry.Invariants, i => i.Id == "pr-rpg-layer-only" && i.Guards.Contains("single-writer"));
        Assert.False(registry.Guards.ContainsKey("pvz-write-surface"),
            "pvz-write-surface must extend guard-single-writer, never land as a second guard row");
    }

    // ---- fixture plumbing --------------------------------------------------------------------------

    static string NewFixture() =>
        Path.Combine(Path.GetTempPath(), "fusionrpg-pvzwritesurface-" + Guid.NewGuid().ToString("N"));

    static void WriteInjectorFile(string fixtureRoot, string fileName, string csharp)
    {
        var dir = Path.Combine(fixtureRoot, "src", "FusionRpg.Injector", "Somewhere");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), csharp);
    }

    static void Cleanup(string fixture)
    {
        if (!Directory.Exists(fixture)) return;
        Directory.Delete(fixture, recursive: true);
    }

    static (int Exit, string Stdout, string Stderr) RunGuard(string root)
    {
        var repoRoot = FindRepoRoot();
        var script = Path.Combine(repoRoot, "scripts", "guard-single-writer.py");
        var psi = new ProcessStartInfo
        {
            FileName = "python",
            Arguments = $"\"{script}\" --root \"{root}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        return ExternalProcess.Run(psi, 60_000, "guard script timed out");
    }

    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
