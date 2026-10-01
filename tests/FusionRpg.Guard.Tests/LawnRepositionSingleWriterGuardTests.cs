using System.Diagnostics;
using FusionRpg.Core.Workspace;
using Xunit;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// A-M2 lawn-reposition (spec-lawn-reposition.md §4.3) — the extended
/// gk-fusion/scripts/guard-single-writer.py gains five new patterns (thePlantRow/thePlantColumn/
/// theZombieRow/transform.position/.localPosition), an EntityPositionWriter.cs allow-list entry,
/// and two new path exemptions (Fx/, Hud/). SingleWriterGuardTests already proves the extended
/// guard exits 0 against the real, clean tree — these cases prove the planted-violation and
/// exemption shape the spec calls out by name, each its own test so CI catches a regression in
/// either direction (a missing exemption failing the clean tree, or an exemption swallowed too
/// wide and hiding a real actor write).
/// </summary>
public class LawnRepositionSingleWriterGuardTests
{
    [Fact]
    public void PlantedViolation_thePlantRow_assignment_outside_writer_fails_and_names_the_file()
    {
        var script = FindScript();
        var fixture = NewFixture("thePlantRow");
        try
        {
            var injectorDir = Path.Combine(fixture, "src", "FusionRpg.Injector", "Somewhere");
            Directory.CreateDirectory(injectorDir);
            File.WriteAllText(
                Path.Combine(injectorDir, "BadRowWrite.cs"),
                "namespace X { class Bad { void Move(Plant p) { p.thePlantRow = 3; } } }\n");

            var (exit, stdout, stderr) = RunScript(script, fixture);
            Assert.True(exit != 0, $"expected fail exit, got 0\nstdout:\n{stdout}\nstderr:\n{stderr}");
            Assert.Contains("BadRowWrite.cs", stderr, StringComparison.Ordinal);
            Assert.Contains("thePlantRow", stderr, StringComparison.Ordinal);
        }
        finally { Cleanup(fixture); }
    }

    [Fact]
    public void PlantedViolation_transformPosition_assignment_outside_writer_and_Fx_Hud_fails_and_names_the_file()
    {
        var script = FindScript();
        var fixture = NewFixture("transformPosition");
        try
        {
            // Deliberately NOT under Fx/ or Hud/ — an actor-shaped write anywhere else must still fail.
            var injectorDir = Path.Combine(fixture, "src", "FusionRpg.Injector", "Somewhere");
            Directory.CreateDirectory(injectorDir);
            File.WriteAllText(
                Path.Combine(injectorDir, "BadTeleport.cs"),
                "namespace X { class Bad { void Move(Zombie z) { z.transform.position = world; } } }\n");

            var (exit, stdout, stderr) = RunScript(script, fixture);
            Assert.True(exit != 0, $"expected fail exit, got 0\nstdout:\n{stdout}\nstderr:\n{stderr}");
            Assert.Contains("BadTeleport.cs", stderr, StringComparison.Ordinal);
            Assert.Contains("transform", stderr, StringComparison.OrdinalIgnoreCase);
        }
        finally { Cleanup(fixture); }
    }

    [Fact]
    public void PlantedViolation_localPosition_assignment_outside_writer_fails()
    {
        var script = FindScript();
        var fixture = NewFixture("localPosition");
        try
        {
            var injectorDir = Path.Combine(fixture, "src", "FusionRpg.Injector", "Somewhere");
            Directory.CreateDirectory(injectorDir);
            File.WriteAllText(
                Path.Combine(injectorDir, "BadLocalPos.cs"),
                "namespace X { class Bad { void Move(Plant p) { p.transform.localPosition = local; } } }\n");

            var (exit, stdout, stderr) = RunScript(script, fixture);
            Assert.True(exit != 0, $"expected fail exit, got 0\nstdout:\n{stdout}\nstderr:\n{stderr}");
            Assert.Contains("BadLocalPos.cs", stderr, StringComparison.Ordinal);
        }
        finally { Cleanup(fixture); }
    }

    /// <summary>
    /// The inverse test spec §4.3 names explicitly: "the guard run against the clean tree with
    /// Fx/ and Hud/ present exits 0 — this is the test that would have caught the ADR's original
    /// omission." A fixture with the SAME shape the real Fx/Hud files have (transform.position and
    /// .localPosition writes, under Fx/ and Hud/ directories) must pass — proving the exemption
    /// actually exempts, not just that an empty tree is silent.
    /// </summary>
    [Fact]
    public void InverseTest_guard_exits_0_with_Fx_and_Hud_present_shaped_like_the_real_VFX_and_HUD_writers()
    {
        var script = FindScript();
        var fixture = NewFixture("inverse-fx-hud");
        try
        {
            var fxDir = Path.Combine(fixture, "src", "FusionRpg.Injector", "Fx");
            var hudDir = Path.Combine(fixture, "src", "FusionRpg.Injector", "Hud");
            Directory.CreateDirectory(fxDir);
            Directory.CreateDirectory(hudDir);

            File.WriteAllText(
                Path.Combine(fxDir, "AuraPool.cs"),
                "namespace X { class AuraPool { void Pulse() { lease.Go.transform.position = world; } } }\n");
            File.WriteAllText(
                Path.Combine(fxDir, "BurstPool.cs"),
                "namespace X { class BurstPool { void Play() { slot.Go.transform.position = world; } } }\n");
            File.WriteAllText(
                Path.Combine(hudDir, "ActorHudPool.cs"),
                "namespace X { class ActorHudPool { void Show() { slot.Root.transform.position = crown; } " +
                "void Row() { t.localPosition = new Vector3(p.x, y, p.z); } } }\n");

            var (exit, stdout, stderr) = RunScript(script, fixture);
            Assert.True(exit == 0, $"expected OK exit, got {exit}\nstdout:\n{stdout}\nstderr:\n{stderr}");
            Assert.Contains("SINGLE-WRITER GUARD OK", stdout, StringComparison.Ordinal);
        }
        finally { Cleanup(fixture); }
    }

    /// <summary>Same inverse shape, but the write is OUTSIDE Fx/Hud this time (a sibling dir) —
    /// proves the exemption is scoped by path, not by matching "looks like VFX/HUD code".</summary>
    [Fact]
    public void InverseTest_the_same_shaped_write_outside_Fx_and_Hud_still_fails()
    {
        var script = FindScript();
        var fixture = NewFixture("inverse-outside-fx-hud");
        try
        {
            var badDir = Path.Combine(fixture, "src", "FusionRpg.Injector", "NotFx");
            Directory.CreateDirectory(badDir);
            File.WriteAllText(
                Path.Combine(badDir, "AuraPool.cs"), // same filename as the real Fx file, wrong dir
                "namespace X { class AuraPool { void Pulse() { lease.Go.transform.position = world; } } }\n");

            var (exit, stdout, stderr) = RunScript(script, fixture);
            Assert.True(exit != 0, $"expected fail exit, got 0\nstdout:\n{stdout}\nstderr:\n{stderr}");
        }
        finally { Cleanup(fixture); }
    }

    /// <summary>Mechanical guard against the exact 2026-08 incident (spec §6 hazard 1): the guard's
    /// own text must still name Fx/ and Hud/, each with its own comment, not just an empty
    /// implementation that happens to pass today's fixtures.</summary>
    [Fact]
    public void Guard_script_text_carries_named_Fx_and_Hud_exemptions_with_comments()
    {
        var script = FindScript();
        var text = File.ReadAllText(script);

        Assert.Contains("Fx[\\\\/]", text, StringComparison.Ordinal);
        Assert.Contains("Hud[\\\\/]", text, StringComparison.Ordinal);
        Assert.Contains("EntityPositionWriter.cs", text, StringComparison.Ordinal);
        Assert.Contains("thePlantRow", text, StringComparison.Ordinal);
        Assert.Contains("thePlantColumn", text, StringComparison.Ordinal);
        Assert.Contains("theZombieRow", text, StringComparison.Ordinal);
        Assert.Contains("transform\\.position", text, StringComparison.Ordinal);
        Assert.Contains("localPosition", text, StringComparison.Ordinal);
    }

    static string NewFixture(string tag) =>
        Path.Combine(Path.GetTempPath(), "fusionrpg-lawn-singlewriter-" + tag + "-" + Guid.NewGuid().ToString("N"));

    static void Cleanup(string fixture)
    {
        try { Directory.Delete(fixture, recursive: true); } catch { /* temp */ }
    }

    static (int Exit, string Stdout, string Stderr) RunScript(string script, string root)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "python",
            Arguments = $"\"{script}\" --root \"{root}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        return ExternalProcess.Run(psi, 60_000, "guard script timed out");
    }

    /// <summary>The guard script is gk-FUSION's, and a walk up cannot reach it.</summary>
    ///
    /// <para>This walked up from the test host's own directory looking for
    /// <c>scripts/guard-single-writer.py</c>. The only ancestors of a gk-core test's bin output are gk-core
    /// and the workspace root; gk-fusion is a SIBLING, which no number of <c>..</c> hops reaches. That is
    /// stated in the shared resolver's own documentation as the reason every "walk up until you find it"
    /// loop for a sibling fails, and the refusal is what six of these tests reported:
    /// <c>DirectoryNotFoundException : Could not find the guard-single-writer.py script</c>.</para>
    ///
    /// <para>The comment at the top of this file already names the correct post-split path -
    /// <c>gk-fusion/scripts/guard-single-writer.py</c> - so the prose was right and the code beside it was
    /// not. That is the seventh instance of that shape in this program.</para>
    static string FindScript()
    {
        var script = Path.Combine(KeepverseRoots.Fusion(), "scripts", "guard-single-writer.py");
        if (File.Exists(script)) return script;
        // Name where it was looked for. A refusal that cannot say where is the opposite of a named refusal,
        // and this one previously named nothing at all.
        throw new DirectoryNotFoundException(
            $"guard-single-writer.py not found at {script} - it is gk-fusion's, and this is a gk-core test, "
            + "so the walk up this used to do could never have reached it");
    }
}
