using System.Diagnostics;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// Runs scripts/guard-class-system.ps1 (class-system-todo.md V2) — G1 aptitude ids collision-free,
/// G2 every edge channel registered, G3 no aptitude reaches atk twice, G4 every null unitClass carries
/// a note, G5 at most one AptitudeReadFunctions, G6 DominantPosture never called from a resolve path,
/// G7 Balance/Analytic's damage-computing files reference a shipped combat symbol (added P4.1).
/// Mirrors StatTaxonomyGuardTests'/PowerGuardTests' fixture shape.
///
/// <para>G2/G3 key off the SHIPPED <c>data/tuning/aptitudes.v*.json</c>. That file did not exist when
/// this suite was written, so this note used to say "P2.1, not yet built" and "nothing to check" —
/// stale since P2.1 landed, and five versions have shipped since (v1..v5, corrected 2026-09-02). The
/// planted-violation tests still supply their own copy inside the fixture, which is what keeps each
/// rule provable independently of whatever the real tree currently holds; the REAL tree is covered
/// separately by <see cref="ClassSystemGuard_script_exitsZeroOnTheRealTree"/>, which pins it to exit 0
/// now that `retire-atk` R3 published `aptitudes.v9` without the retired channel. Decision 12's
/// "permanently red" state is closed by the owner's 2026-09-18 ruling, not by editing the tuning to
/// silence it — and the G3 RULE stays in the guard unchanged, as the falsifier below proves.</para>
/// </summary>
public class ClassSystemGuardTests
{
    [Fact]
    public void ClassSystemGuard_script_exitsZeroOnTheRealTree()
    {
        // class-system-todo.md P1.5/P1.6 (reader census) closed G4's 29 real-tree findings on
        // 2026-08-26 -- every null unitClass now carries a note.
        //
        // This test used to be `..._exitsOneOnTheRealTree_onlyG3_permanentlyByDesign`, pinning
        // class-system-plan.md decision 12's deliberate red: G3 (Might/Ferocity feed both
        // combat.power.* and progression.bonus.atk) stayed failing "until battle-adoption ships or the
        // design changes". The design changed -- the owner retired `progression.bonus.atk` on
        // 2026-09-18 (solid-enforcement `retire-atk`), and R3 published `aptitudes.v9` without it. The
        // shipped tuning was NOT edited to silence the guard; the channel the guard complained about is
        // gone.
        //
        // The G3 RULE is unchanged and still fires:
        // `G3_fails_when_one_source_feeds_both_power_and_bonus_atk` below plants exactly the violation
        // it was written for, so this green is a closed decision rather than a disabled check.
        var (exit, stdout, stderr) = Run(FindRepoRoot(), FindRepoRoot());
        Assert.True(exit == 0, $"expected exit=0, got exit={exit}\nstdout:\n{stdout}\nstderr:\n{stderr}");
        foreach (var rule in new[] { "G1 ", "G2 ", "G3 ", "G4 ", "G5 ", "G6 ", "G7 " })
            Assert.DoesNotContain(rule, stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void class_system_guard_is_gated_by_its_owning_phase()
    {
        var repoRoot = FindRepoRoot();
        GuardWiring.AssertGuardReachableInItsOwningPhase(repoRoot, "class-system");
    }

    [Fact]
    public void G1_fails_on_a_duplicate_aptitude_id()
    {
        var fixture = NewFixture();
        try
        {
            WriteAptitudeRoster(fixture, """
                {"entries":[
                  {"id":"Might","posture":"force","ordinal":0},
                  {"id":"Might","posture":"finesse","ordinal":1}
                ]}
                """);
            WriteCatalog(fixture, MinimalCatalog());

        var (exit, stdout, stderr) = Run(fixture, FindRepoRoot());
            Assert.True(exit != 0, "expected G1 to fail on a duplicate id");
            Assert.Contains("G1 Might", stderr, StringComparison.Ordinal);
        }
        finally { Cleanup(fixture); }
    }

    [Fact]
    public void G1_fails_when_an_aptitude_id_collides_with_a_channel_family()
    {
        var fixture = NewFixture();
        try
        {
            WriteAptitudeRoster(fixture, """
                {"entries":[
                  {"id":"combat.power","posture":"force","ordinal":0}
                ]}
                """);
            WriteCatalog(fixture, MinimalCatalog());

        var (exit, stdout, stderr) = Run(fixture, FindRepoRoot());
            Assert.True(exit != 0, "expected G1 to fail on a channel-family collision");
            Assert.Contains("G1 combat.power", stderr, StringComparison.Ordinal);
        }
        finally { Cleanup(fixture); }
    }

    [Fact]
    public void G2_fails_on_an_edge_channel_not_in_the_catalog()
    {
        var fixture = NewFixture();
        try
        {
            WriteAptitudeRoster(fixture, SingleAptitudeRoster("Might"));
            WriteCatalog(fixture, MinimalCatalog());
            WriteShippedTuning(fixture, "aptitudes.v1.json", """
                {"edges":[{"channel":"combat.totallyInvented.omni","source":"Might","kMilli":1000}]}
                """);

        var (exit, stdout, stderr) = Run(fixture, FindRepoRoot());
            Assert.True(exit != 0, "expected G2 to fail on an unregistered edge channel");
            Assert.Contains("G2 Might -> combat.totallyInvented.omni", stderr, StringComparison.Ordinal);
        }
        finally { Cleanup(fixture); }
    }

    [Fact]
    public void G2_passes_on_an_edge_channel_that_resolves_by_family_prefix()
    {
        var fixture = NewFixture();
        try
        {
            WriteAptitudeRoster(fixture, SingleAptitudeRoster("Might"));
            WriteCatalog(fixture, MinimalCatalog()); // declares family "combat.power"
            WriteShippedTuning(fixture, "aptitudes.v1.json", """
                {"edges":[{"channel":"combat.power.omni","source":"Might","kMilli":1000}]}
                """);

            var (exit, stdout, stderr) = Run(fixture, FindRepoRoot());
            Assert.True(exit == 0, $"expected pass, got exit={exit}\n{stdout}\n{stderr}");
        }
        finally { Cleanup(fixture); }
    }

    [Fact]
    public void G3_fails_when_one_source_feeds_both_power_and_bonus_atk()
    {
        var fixture = NewFixture();
        try
        {
            WriteAptitudeRoster(fixture, SingleAptitudeRoster("Might"));
            WriteCatalog(fixture, MinimalCatalog());
            WriteShippedTuning(fixture, "aptitudes.v1.json", """
                {"edges":[
                  {"channel":"combat.power.omni","source":"Might","kMilli":2000},
                  {"channel":"progression.bonus.atk","source":"Might","kMilli":1000}
                ]}
                """);

        var (exit, stdout, stderr) = Run(fixture, FindRepoRoot());
            Assert.True(exit != 0, "expected G3 to fail on a double-counted atk source");
            Assert.Contains("G3 Might", stderr, StringComparison.Ordinal);
        }
        finally { Cleanup(fixture); }
    }

    [Fact]
    public void G4_fails_on_a_null_unitClass_with_no_note()
    {
        var fixture = NewFixture();
        try
        {
            WriteAptitudeRoster(fixture, SingleAptitudeRoster("Might"));
            WriteCatalog(fixture, """
                {"entries":[
                  {"family":"combat.mystery","statClass":"Pool","cap":null,"unitClass":null}
                ]}
                """);

        var (exit, stdout, stderr) = Run(fixture, FindRepoRoot());
            Assert.True(exit != 0, "expected G4 to fail on an unnoted null unitClass");
            Assert.Contains("G4 combat.mystery", stderr, StringComparison.Ordinal);
        }
        finally { Cleanup(fixture); }
    }

    [Fact]
    public void G4_passes_when_the_null_unitClass_carries_a_note()
    {
        var fixture = NewFixture();
        try
        {
            WriteAptitudeRoster(fixture, SingleAptitudeRoster("Might"));
            WriteCatalog(fixture, """
                {"entries":[
                  {"family":"combat.mystery","statClass":"Pool","cap":null,"unitClass":null,
                   "unitClassNote":"documented reason"}
                ]}
                """);

            var (exit, stdout, stderr) = Run(fixture, FindRepoRoot());
            Assert.True(exit == 0, $"expected pass, got exit={exit}\n{stdout}\n{stderr}");
        }
        finally { Cleanup(fixture); }
    }

    [Fact]
    public void G5_fails_on_two_AptitudeReadFunctions_implementations()
    {
        var fixture = NewFixture();
        try
        {
            WriteAptitudeRoster(fixture, SingleAptitudeRoster("Might"));
            WriteCatalog(fixture, MinimalCatalog());
            WriteSrcFile(fixture, "One.cs", "namespace X { public static class AptitudeReadFunctions { } }");
            WriteSrcFile(fixture, "Two.cs", "namespace Y { public static class AptitudeReadFunctions { } }");

        var (exit, stdout, stderr) = Run(fixture, FindRepoRoot());
            Assert.True(exit != 0, "expected G5 to fail on a duplicate implementation");
            Assert.Contains("G5:", stderr, StringComparison.Ordinal);
        }
        finally { Cleanup(fixture); }
    }

    [Fact]
    public void G5_passes_on_a_single_AptitudeReadFunctions_implementation()
    {
        var fixture = NewFixture();
        try
        {
            WriteAptitudeRoster(fixture, SingleAptitudeRoster("Might"));
            WriteCatalog(fixture, MinimalCatalog());
            WriteSrcFile(fixture, "One.cs", "namespace X { public static class AptitudeReadFunctions { } }");

            var (exit, stdout, stderr) = Run(fixture, FindRepoRoot());
            Assert.True(exit == 0, $"expected pass, got exit={exit}\n{stdout}\n{stderr}");
        }
        finally { Cleanup(fixture); }
    }

    [Fact]
    public void G6_fails_when_a_resolve_shaped_file_calls_DominantPosture()
    {
        var fixture = NewFixture();
        try
        {
            WriteAptitudeRoster(fixture, SingleAptitudeRoster("Might"));
            WriteCatalog(fixture, MinimalCatalog());
            WriteSrcFile(fixture, "AptitudeResolve.cs",
                "class AptitudeResolve { void Go() { var p = DominantPosture.Of(alloc); } }");

        var (exit, stdout, stderr) = Run(fixture, FindRepoRoot());
            Assert.True(exit != 0, "expected G6 to fail on a resolve-path call to DominantPosture");
            Assert.Contains("G6 ", stderr, StringComparison.Ordinal);
        }
        finally { Cleanup(fixture); }
    }

    [Fact]
    public void G6_passes_when_only_a_non_resolve_file_calls_DominantPosture()
    {
        var fixture = NewFixture();
        try
        {
            WriteAptitudeRoster(fixture, SingleAptitudeRoster("Might"));
            WriteCatalog(fixture, MinimalCatalog());
            WriteSrcFile(fixture, "ActorSheetView.cs",
                "class ActorSheetView { void Render() { var p = DominantPosture.Of(alloc); } }");

            var (exit, stdout, stderr) = Run(fixture, FindRepoRoot());
            Assert.True(exit == 0, $"expected pass, got exit={exit}\n{stdout}\n{stderr}");
        }
        finally { Cleanup(fixture); }
    }

    [Fact]
    public void G7_fails_when_StrikeMixture_has_no_shipped_combat_symbol_reference()
    {
        var fixture = NewFixture();
        try
        {
            WriteAptitudeRoster(fixture, SingleAptitudeRoster("Might"));
            WriteCatalog(fixture, MinimalCatalog());
            // A re-derived sigmoid -- no reference to CombatProbability/ClampedContest/
            // OverlayCombatCalculator/etc. -- exactly the "second combat SSOT" spec-deterministic-
            // core.md §2 forbids.
            WriteAnalyticFile(fixture, "StrikeMixture.cs",
                "class StrikeMixture { static double Sigmoid(double d) => 1.0 / (1.0 + Math.Exp(-d)); }");

        var (exit, stdout, stderr) = Run(fixture, FindRepoRoot());
            Assert.True(exit == 1, $"expected fail, got exit={exit}\n{stdout}\n{stderr}");
            Assert.Contains("G7 ", stderr, StringComparison.Ordinal);
            Assert.Contains("StrikeMixture.cs", stderr, StringComparison.Ordinal);
        }
        finally { Cleanup(fixture); }
    }

    [Fact]
    public void G7_passes_when_StrikeMixture_calls_a_shipped_combat_symbol()
    {
        var fixture = NewFixture();
        try
        {
            WriteAptitudeRoster(fixture, SingleAptitudeRoster("Might"));
            WriteCatalog(fixture, MinimalCatalog());
            WriteAnalyticFile(fixture, "StrikeMixture.cs",
                "class StrikeMixture { static double Hit(double d) => CombatProbability.Sigmoid(d, 100); }");

            var (exit, stdout, stderr) = Run(fixture, FindRepoRoot());
            Assert.True(exit == 0, $"expected pass, got exit={exit}\n{stdout}\n{stderr}");
        }
        finally { Cleanup(fixture); }
    }

    [Fact]
    public void G7_ignores_pure_statistics_files_that_call_no_combat_symbol()
    {
        // FirstPassage/Race are generic probability math over numbers StrikeMixture already produced
        // -- they legitimately reference no combat symbol at all, and G7 must not false-positive here.
        var fixture = NewFixture();
        try
        {
            WriteAptitudeRoster(fixture, SingleAptitudeRoster("Might"));
            WriteCatalog(fixture, MinimalCatalog());
            WriteAnalyticFile(fixture, "FirstPassage.cs",
                "class FirstPassage { static double Mean(double h, double mu) => h / mu; }");

            var (exit, stdout, stderr) = Run(fixture, FindRepoRoot());
            Assert.True(exit == 0, $"expected pass, got exit={exit}\n{stdout}\n{stderr}");
        }
        finally { Cleanup(fixture); }
    }

    // ---- fixture plumbing --------------------------------------------------------------------------

    static string MinimalCatalog() => """
        {"entries":[
          {"family":"combat.power","statClass":"Contest","counterpart":"combat.defense","cap":null,"unitClass":"GameUnits"}
        ]}
        """;

    static string SingleAptitudeRoster(string id) => $$"""
        {"entries":[{"id":"{{id}}","posture":"force","ordinal":0}]}
        """;

    static string NewFixture()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fusionrpg-classsystemguard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(KeepverseRoots.Content(), "data", "seed", "aptitudes"));
        Directory.CreateDirectory(Path.Combine(KeepverseRoots.Content(), "data", "seed", "derived-stats"));
        return dir;
    }

    static void WriteAptitudeRoster(string fixtureRoot, string json) =>
        File.WriteAllText(Path.Combine(fixtureRoot, "data", "seed", "aptitudes", "roster.json"), json);

    static void WriteCatalog(string fixtureRoot, string json) =>
        File.WriteAllText(Path.Combine(fixtureRoot, "data", "seed", "derived-stats", "catalog.json"), json);

    static void WriteShippedTuning(string fixtureRoot, string fileName, string json)
    {
        var dir = Path.Combine(fixtureRoot, "data", "tuning");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), json);
    }

    static void WriteSrcFile(string fixtureRoot, string fileName, string csharp)
    {
        var dir = Path.Combine(fixtureRoot, "src");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), csharp);
    }

    static void WriteAnalyticFile(string fixtureRoot, string fileName, string csharp)
    {
        var dir = Path.Combine(fixtureRoot, "src", "FusionRpg.Core", "Balance", "Analytic");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), csharp);
    }

    /// <summary>Runs guard-class-system.py FROM the real repo but pointed AT the fixture directory via
    /// --root, exactly like StatTaxonomyGuardTests'/PowerGuardTests' own pattern. The verdict goes to
    /// stdout and the findings to stderr; PowerShell's Write-Host put both on stdout, so any assertion
    /// that reads a finding has to name the stream it now lives on.</summary>
    static (int Exit, string Stdout, string Stderr) Run(string fixtureRoot, string repoRoot)
    {
        var script = Path.Combine(repoRoot, "scripts", "guard-class-system.py");
        var psi = new ProcessStartInfo
        {
            FileName = "python",
            Arguments = $"\"{script}\" --root \"{fixtureRoot}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        return ExternalProcess.Run(psi, 60_000, "guard script timed out");
    }

    static void Cleanup(string fixture)
    {
        try { Directory.Delete(fixture, recursive: true); } catch { /* temp */ }
    }

    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
