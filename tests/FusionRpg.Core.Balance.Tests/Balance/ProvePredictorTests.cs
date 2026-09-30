using System.Linq;
using System.Text.RegularExpressions;
using FusionRpg.Core.Tests.TestSupport;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.Balance;

/// <summary>
/// `gk-core/tools/ProvePredictor` — the cross-check that `FusionRpg.Core.Balance.Analytic.Predictor` (the port)
/// still agrees with `gk-core/tools/CombatSim`'s `Analytic.Predict` (the reference it was ported from).
///
/// <para><b>Why this test exists (solid-remediation T1.8, register entry G3).</b> The tool shipped with
/// no test that would notice it breaking. Under `AGENTS.md` an unmapped production path is itself a
/// verification-boundary defect, and the module spec is explicit that mapping a path to tests which
/// cannot fail for it is <i>worse</i> than leaving it unmapped, because it reports confidence it has not
/// earned. So the boundary and the test land together: `gk-core/tools/ProvePredictor/**` now selects this file,
/// and this file actually runs the tool.</para>
///
/// <para><b>What the first run found, and how it ended.</b> Three of the tool's four checks passed. The
/// fourth — actions-plus-status — did not: the FORCE/BASTION matchup diverged by ~9.2e-4 against the
/// tool's own 1e-4 bound. It went unseen because nothing ever ran the tool, which is the exact gap G3
/// names.</para>
///
/// <para><b>CLOSED 2026-09-17.</b> The divergence was a real defect in the port, and an internal
/// inconsistency rather than a difference of opinion with the reference:
/// <c>Predictor.Predict</c> carried each side's DoT into <c>dealtMean*</c> and therefore into the damage
/// RATE, while feeding the raw <c>swing*.Mean</c> into <c>ShieldEffectiveHp</c> and
/// <c>RecoveryPerRound</c>. The same fight counted a DoT as damage when deciding how fast HP fell and
/// pretended it did not exist when deciding how long the shield lasted and how much regen had to
/// out-heal. A DoT tick is incoming damage on every one of those paths. Fixed by passing
/// <c>dealtMean*</c> to all four sites; actions+status went 9.222E-004 → <b>8.836E-007</b>, which is
/// exactly the actions-only figure — the status term now composes consistently, so it contributes no
/// divergence of its own.</para>
///
/// <para>The tripwire below did its job in the direction that is easy to get wrong: it went red because
/// the tool went GREEN, and its own comment said the correct response was to assert <c>exit == 0</c>
/// rather than loosen anything. That is what happened.</para>
///
/// <para>⚠️ This test asserts the tool's <b>verdicts</b> and never its printed diffs. Those are readings
/// from a floating-point pipeline: they move when tuning is republished or a curve is re-derived, and
/// pinning one would make an ordinary balance change fail here with "bump the literal" as its fix.</para>
/// </summary>
[Trait("VerificationId", "core.prove-predictor")]
public class ProvePredictorTests
{
    static string RepoRoot()
    {
        return KeepverseRoots.Core();
    }

    [Fact]
    public void The_ported_predictor_matches_the_reference_on_every_check()
    {
        var (exit, stdout, stderr) = ToolProcess.Run(RepoRoot(), "ProvePredictor", string.Empty, 300_000);
        var output = stdout + stderr;

        // All four checks pass, and each is asserted by name rather than inferred from the exit code --
        // a single `exit == 0` would not notice a check being silently deleted from the tool.
        Assert.Contains("PASS -- the port matches the reference within long-rounding tolerance.", output, StringComparison.Ordinal);
        Assert.Contains("PASS -- Theta-invariant within long-rounding tolerance.", output, StringComparison.Ordinal);
        Assert.Contains("Actions-only PASS threshold (1e-4, same as core path): PASS", output, StringComparison.Ordinal);
        Assert.Contains("Actions+status PASS threshold (1e-4): PASS", output, StringComparison.Ordinal);

        // Still a tripwire, now in one direction: ANY check going red fails this test. The fix is the
        // defect, never the bound -- the 1e-4 gate is the tool's own and is not to be loosened here.
        var failures = Regex.Matches(output, "^.*FAIL.*$", RegexOptions.Multiline)
            .Select(m => m.Value.Trim())
            .ToArray();

        Assert.True(failures.Length == 0,
            $"the predictor cross-check regressed; {failures.Length} check(s) now FAIL:{Environment.NewLine}" +
            string.Join(Environment.NewLine, failures));

        // The exit code is the tool's own summary of those four checks, so it must agree with them.
        Assert.True(exit == 0,
            $"the tool's exit code no longer agrees with its printed checks (exit {exit}){Environment.NewLine}{output}");
    }
}
