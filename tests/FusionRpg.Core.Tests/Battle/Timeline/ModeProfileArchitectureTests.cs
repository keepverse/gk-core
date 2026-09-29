using System.Runtime.CompilerServices;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Timeline;

/// <summary>
/// B11 / T4a — the no-branch architecture test, written and green **before any profile row
/// exists** (this file's own acceptance line). "Adding a mode adds a row, never a branch in the
/// kernel" (battle-timeline-map.md T4) means two concrete things a source scan can actually check:
/// no kernel file names a profile id string literal, and no kernel file switches or equality-
/// branches on <see cref="FusionRpg.Core.Battle.Timeline.AdvancePolicyKind"/> — <c>= AdvancePolicyKind.X</c>
/// (a plain default-value assignment, which <c>BattleModeProfile.cs</c> itself legitimately does)
/// stays legal; <c>case AdvancePolicyKind.X</c> and <c>== / != AdvancePolicyKind.X</c> do not.
///
/// Independent of <see cref="KernelPurityScan"/> rather than extending it: that scanner's model has
/// no per-token exemption, and this guard genuinely needs one once B12 lands (the file that DEFINES
/// <c>classic-round</c>/<c>galaxy-sync</c>/<c>hybrid-atb</c> as data must itself be allowed to hold
/// those literals — only kernel MECHANISM files may not). Same caveat as that scanner states about
/// itself: a line-based heuristic, not a proof — it skips whole-line comments but is not fully
/// comment- or string-aware, which is an acceptable gap for tokens this code-shaped.
/// </summary>
public class ModeProfileArchitectureTests
{
    /// <summary>Ids named in battle-timeline-map.md T4. Adding a fourth mode adds a row to THIS
    /// array too — the same "the map is the closed inventory" discipline the power ladder and the
    /// atom vocabulary already use elsewhere in this repo.</summary>
    static readonly string[] KnownProfileIds = { "classic-round", "galaxy-sync", "hybrid-atb", "siege", "delve" };

    static readonly string[] BannedTokens =
    {
        "case AdvancePolicyKind.",
        "== AdvancePolicyKind.",
        "!= AdvancePolicyKind."
    };

    /// <summary>The one file allowed to hold a profile-row definition once B12 lands. Named, not
    /// wildcarded — the same narrow-exemption discipline <c>KernelPurityScan.DiagnosticsExemptFromTickPath</c>
    /// already uses, for the same reason: a pattern-based exemption grows silently.</summary>
    static readonly string[] ProfileDefinitionFiles = { "BattleModeProfile.cs" };

    static string TimelineDir([CallerFilePath] string here = "")
    {
        var testsDir = Path.GetDirectoryName(here)!;
        var repo = Path.GetFullPath(Path.Combine(testsDir, "..", "..", "..", ".."));
        return Path.Combine(repo, "src", "FusionRpg.Core", "Battle", "Timeline");
    }

    static List<string> Scan(string dir)
    {
        var offences = new List<string>();
        foreach (var file in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            var name = Path.GetRelativePath(dir, file).Replace('\\', '/');
            var idExempt = ProfileDefinitionFiles.Contains(name, StringComparer.Ordinal);
            var lines = File.ReadAllLines(file);

            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal)) continue; // whole-line comments only

                foreach (var token in BannedTokens)
                    if (lines[i].Contains(token, StringComparison.Ordinal))
                        offences.Add($"{name}:{i + 1} → {token}");

                if (idExempt) continue;
                foreach (var id in KnownProfileIds)
                    if (lines[i].Contains($"\"{id}\"", StringComparison.Ordinal))
                        offences.Add($"{name}:{i + 1} → profile id literal \"{id}\"");
            }
        }

        return offences;
    }

    [Fact]
    public void No_profile_id_or_branch_exists_in_the_kernel_yet()
    {
        var dir = TimelineDir();
        Assert.True(Directory.Exists(dir), $"kernel source dir not found: {dir}");

        var offences = Scan(dir);
        Assert.True(offences.Count == 0,
            "mode-profile architecture violated before B12 has even shipped a row:\n" + string.Join("\n", offences));
    }

    [Theory]
    [InlineData("switch (p.AdvancePolicy) { case AdvancePolicyKind.NextEvent: break; }", "case AdvancePolicyKind.")]
    [InlineData("if (p.AdvancePolicy == AdvancePolicyKind.FixedIncrement) { }", "== AdvancePolicyKind.")]
    [InlineData("if (p.AdvancePolicy != AdvancePolicyKind.NextEvent) { }", "!= AdvancePolicyKind.")]
    [InlineData("var s = \"classic-round\";", "profile id literal \"classic-round\"")]
    [InlineData("var s = \"galaxy-sync\";", "profile id literal \"galaxy-sync\"")]
    [InlineData("var s = \"hybrid-atb\";", "profile id literal \"hybrid-atb\"")]
    public void The_guard_actually_detects_a_planted_violation(string badLine, string expectedFragment)
    {
        // A guard that cannot fail is decoration — proven here against a temp directory, the same
        // discipline TimelinePurityGuardTests already applies to KernelPurityScan.
        var tmp = Path.Combine(Path.GetTempPath(), "fusionrpg-modeprofile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            File.WriteAllLines(Path.Combine(tmp, "Offender.cs"),
                new[] { "namespace X;", "sealed class Offender", "{", "    void M() { " + badLine + " }", "}" });

            var offences = Scan(tmp);
            Assert.Contains(offences, o => o.Contains(expectedFragment, StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }

    [Fact]
    public void A_default_value_assignment_is_not_a_branch()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "fusionrpg-modeprofile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            File.WriteAllLines(Path.Combine(tmp, "Harmless.cs"),
                new[] { "namespace X;", "sealed class Harmless", "{", "    AdvancePolicyKind P = AdvancePolicyKind.NextEvent;", "}" });

            Assert.Empty(Scan(tmp));
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }

    [Fact]
    public void The_profile_definition_file_is_exempt_from_the_id_literal_ban_but_not_the_branch_ban()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "fusionrpg-modeprofile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            File.WriteAllLines(Path.Combine(tmp, "BattleModeProfile.cs"),
                new[]
                {
                    "namespace X;",
                    "sealed class Row",
                    "{",
                    "    string Id = \"classic-round\";",          // legal here — a data row, not a branch
                    "    void M() { if (X == AdvancePolicyKind.NextEvent) {} }" // still a branch — still banned
                });

            var offences = Scan(tmp);
            Assert.DoesNotContain(offences, o => o.Contains("classic-round", StringComparison.Ordinal));
            Assert.Contains(offences, o => o.Contains("== AdvancePolicyKind.", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }
}
