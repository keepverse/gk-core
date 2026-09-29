using System;
using System.IO;
using System.Linq;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Tests.Battle;
using Xunit;

namespace FusionRpg.Core.Tests.Actions;

/// <summary>
/// combat-ai `stance-wiring` (CAI3.1, spec-stance-wiring.md): the anti-drift half of the row's stance
/// acceptance. It proves the seam is the ONLY place `gk-core/src/FusionRpg.Core/Battle/**` names
/// <c>NoStanceHeld.Instance</c>, which is what stops a future call site from quietly reintroducing the
/// hardcoded literal the seam replaced.
///
/// <para><b>One deliberate reading of the row's wording, stated so it is reviewable.</b> The row says a
/// scan should find "zero <c>NoStanceHeld.Instance</c> literals in <c>gk-core/src/FusionRpg.Core/Battle/**</c>
/// outside <c>Actions/IAffordabilityCheck.cs</c>'s declaration". Taken literally that excludes the seam
/// too — but the seam's whole signature is "one <c>IStanceCheck</c>, DEFAULT <c>NoStanceHeld.Instance</c>",
/// and a defaulted property needs that literal. So the scan asserts EXACTLY ONE code occurrence and that
/// it is the seam's own initializer: a second one anywhere else in `Battle/**` fails this test, which is
/// the anti-drift property the row is after.</para>
///
/// <para><b>The two behavioural tests are HERE now, and how they became possible is worth recording.</b>
/// They were previously gated on a decision to make <c>BattleRunState</c> internal or to add a fixture
/// seam, because the run state is a PRIVATE nested class inside <c>BattleEngine</c> and
/// `BattleRunState.cs` records at its own lines 20-31 that the nesting was chosen precisely so no
/// visibility change would be needed. Re-reading the seam showed a smaller answer: the property had a
/// reader and <b>no writer at all</b> — `grep` found zero assignments to <c>BattleRunState.Stance</c>
/// anywhere — so gate 0 was inert by construction rather than by content, and the tests were
/// un-writable for that reason. This commit adds the writer (`BattleEngine.Resolve`'s trailing optional
/// `IStanceCheck? stance = null`, forwarded to the run state) and the tests drive a REAL battle through
/// it. No visibility changed, and the default path is byte-identical.</para>
public class StanceSeamTests
{
    [Fact]
    public void Every_policy_construction_reads_the_run_states_one_stance_seam()
    {
        var battleDir = Path.Combine(RepoRoot(), "src", "FusionRpg.Core", "Battle");
        Assert.True(Directory.Exists(battleDir), $"battle source dir not found: {battleDir}");

        // Line numbers are deliberately NOT pinned: an absolute line here would go stale on the next
        // edit to the file, which is the drift this repo's citation discipline exists to avoid. What is
        // pinned is the INVARIANT -- exactly one code occurrence, in the run state, on the seam's own
        // declaration -- and a housekeeping move inside the file cannot fake that.
        var occurrences = Directory.GetFiles(battleDir, "*.cs", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal)
            .SelectMany(file => File.ReadAllLines(file)
                .Select((line, index) => (File: Path.GetFileName(file), Line: index + 1, Text: line))
                // Comments are stripped: the seam's own doc comment NAMES the instance on purpose, and
                // the criterion is about code.
                .Where(x => !x.Text.TrimStart().StartsWith("//", StringComparison.Ordinal))
                .Where(x => x.Text.Split("//")[0].Contains("NoStanceHeld.Instance", StringComparison.Ordinal)))
            .ToList();

        var only = Assert.Single(occurrences);
        Assert.Equal("BattleRunState.cs", only.File);
        Assert.Contains("IStanceCheck Stance", only.Text.Split("//")[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// The identity half: with NO stance supplied, the run state keeps its default
    /// (<c>NoStanceHeld.Instance</c>) and gate 0 refuses nothing, so a won battle stays won. The fixture is
    /// `BattleGoldenTests.StompSetup()` — level-10 squad against level-2 wave — reused rather than
    /// re-authored, the same "reuse the existing fixture" discipline the row applies to `FixedStance`.
    /// </summary>
    [Fact]
    public void A_run_state_with_no_stance_assigned_refuses_nothing()
    {
        var report = BattleEngine.Resolve(BattleGoldenTests.StompSetup(), 1001);

        Assert.Equal(BattleOutcome.Victory, report.Outcome);
    }

    /// <summary>
    /// The seam half: a <see cref="FixedStance"/> that refuses reaches gate 0 <b>through a live run
    /// state</b>, and its refusal changes the battle — the squad can no longer act, so the wave it stomps
    /// above is still standing when the round cap arrives.
    ///
    /// <para><b>Why this is the strong form of the row's criterion rather than a proxy for it.</b> Gate 0
    /// is unexempted (`UsabilityEvaluator`: <i>"six gates, cheapest first, short-circuiting: stance →
    /// bound → cooldown → afford → range → condition"</i>), and the run state hands its one
    /// <see cref="IStanceCheck"/> to the fallback source it builds itself (`BasicAttack.cs:177`,
    /// <c>new StubIntentSource(view, state.Cooldowns, state.Stance, state.CostLedger)</c>). So the fake
    /// cannot be consulted by anything except that seam. The <c>UsabilityReason.StanceHeld</c> that a
    /// refusal PRODUCES is pinned one gate level down in
    /// `ActionUsabilityEvaluatorTests.Gate0_stance_refuses_first`; this test pins that the seam's value
    /// arrives.</para>
    /// </summary>
    [Fact]
    public void A_supplied_stance_check_reaches_gate_zero_through_the_run_state()
    {
        var refusing = new FixedStance(UsabilityResult.Refuse(UsabilityReason.StanceHeld, "stance-seam-test"));

        var refused = BattleEngine.Resolve(BattleGoldenTests.StompSetup(), 1001, stance: refusing);
        var defaulted = BattleEngine.Resolve(BattleGoldenTests.StompSetup(), 1001);

        // Both halves are asserted: the refusal CHANGED the battle (so the fake was reached and used),
        // and it changed it in the one way a stance explanation predicts -- nobody can attack, so no side
        // can wipe the other and the round cap ends it.
        Assert.NotEqual(defaulted.Outcome, refused.Outcome);
        Assert.Equal(BattleOutcome.Stalemate, refused.Outcome);
    }

    static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "")
    {
        var testsDir = Path.GetDirectoryName(here)!;
        return Path.GetFullPath(Path.Combine(testsDir, "..", "..", ".."));
    }
}
