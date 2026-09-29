using FusionRpg.Core.Battle;
using FusionRpg.Core.Battle.Timeline;
using Xunit;
using Xunit.Abstractions;

namespace FusionRpg.Core.Tests.Battle;

/// <summary>
/// **B34 — the staged sweep**, one configuration per axis rather than two in total.
///
/// <para>`classic-round` → `hybrid-atb` moves several axes at once (advance policy, `W` 1→4,
/// commitment, economy, and since B39 readiness ordering). A single before/after sweep could not say
/// which one moved the result, and the re-bless it feeds is a one-way door. So each axis is added one
/// at a time and its own delta measured.</para>
///
/// <para>⛔ <b>The table must end at the profile production actually runs</b>, and
/// <see cref="TheFinalStageIsTheShippedProfile"/> is what enforces that. B39 added a fifth axis to
/// `hybrid-atb` and this sweep silently stopped describing production until the stage was added — a
/// four-axis table labelled "[hybrid-atb]" that was no longer hybrid-atb. That guard exists so the
/// drift cannot recur quietly.</para>
///
/// <para>This is a TEST rather than a one-off script on purpose: a battle resolves in well under a
/// millisecond, so the whole sweep runs in-suite, and the attribution stops being a number someone
/// recorded once and becomes a property that stays true.</para>
/// </summary>
public class HybridAtbSweepTests
{
    readonly ITestOutputHelper _out;
    public HybridAtbSweepTests(ITestOutputHelper output) => _out = output;

    const int Seeds = 240;

    /// <summary>Squad win rate over a fixed seed band — the only measure this program sweeps on
    /// (spec-residual-fit.md: win rate and nothing else, never fight length or damage).</summary>
    static double WinRate(BattleModeProfile profile)
    {
        var setup = BattleGoldenTests.CloseSetup();
        var wins = 0;
        for (var i = 0; i < Seeds; i++)
        {
            var report = BattleEngine.Resolve(setup, (ulong)(9_000 + i), profile: profile);
            if (report.Outcome == BattleOutcome.Victory) wins++;
        }

        return (double)wins / Seeds;
    }

    [Fact]
    public void TheFourAxesAttributeAndTheDeltasSumToTheTotal()
    {
        var stage0 = BattleModeProfileCatalog.ClassicRound;
        var stage1 = stage0 with { AdvancePolicy = AdvancePolicyKind.FixedIncrement };
        var stage2 = stage1 with { W = 4 };
        var stage3 = stage2 with { DefaultCommitment = Commitment.EarlyBoundWithFallback };
        var stage4 = stage3 with { NewEconomy = static () => new ActionPointsEconomy(2) };
        var stage5 = stage4 with { OrdersBySpeed = true };   // B39 — the axis that makes this hybrid-atb

        var r0 = WinRate(stage0);
        var r1 = WinRate(stage1);
        var r2 = WinRate(stage2);
        var r3 = WinRate(stage3);
        var r4 = WinRate(stage4);
        var r5 = WinRate(stage5);

        _out.WriteLine($"stage 0  classic-round                    {r0:P2}");
        _out.WriteLine($"stage 1  + FixedIncrement                 {r1:P2}   delta {r1 - r0:+0.00%;-0.00%; 0.00%}");
        _out.WriteLine($"stage 2  + W = 4                          {r2:P2}   delta {r2 - r1:+0.00%;-0.00%; 0.00%}");
        _out.WriteLine($"stage 3  + EarlyBoundWithFallback         {r3:P2}   delta {r3 - r2:+0.00%;-0.00%; 0.00%}");
        _out.WriteLine($"stage 4  + ActionPoints(2)                {r4:P2}   delta {r4 - r3:+0.00%;-0.00%; 0.00%}");
        _out.WriteLine($"stage 5  + OrdersBySpeed    [hybrid-atb]  {r5:P2}   delta {r5 - r4:+0.00%;-0.00%; 0.00%}");
        _out.WriteLine($"total    classic-round -> hybrid-atb              delta {r5 - r0:+0.00%;-0.00%; 0.00%}");

        // The acceptance: the per-axis deltas account for the whole move, with no unexplained
        // remainder. Exact rather than tolerant — these are counted outcomes, not sampled statistics.
        var summed = (r1 - r0) + (r2 - r1) + (r3 - r2) + (r4 - r3) + (r5 - r4);
        Assert.Equal(r5 - r0, summed, precision: 10);
    }

    /// <summary>
    /// ⭐ The finding the staging exists to produce: **every axis but one is inert in a batch resolver,
    /// and the entire delta belongs to the economy.**
    ///
    /// <para>Each has a documented reason, verified rather than assumed: `AdvancePolicy` has no frames
    /// to step in a batch resolve; `W` cannot bind without wind-up (`ActionSlots`' own doc: "under
    /// next-event advance with a strict total order and atomic resolution, a battle is already
    /// serialized regardless of W"); and `Commitment` is deliberately unwired by B37, which deferred
    /// early binding to the migration that first selects a profile using it. **B39's readiness ordering
    /// joins that list for a content reason rather than a structural one**: it reorders a round only
    /// when speeds *differ*, and no shipped content authors a `turn.speed`, so every comparison ties and
    /// falls through to the same initiative jitter. That zero is the one most likely to stop being zero
    /// — the day a content pass authors speed, this assertion goes red, and it should.</para>
    ///
    /// <para><b>This is what makes B35's predicted delta writable at all.</b> A migration whose whole
    /// effect is one named axis is a delta someone can actually predict and review.</para>
    /// </summary>
    [Fact]
    public void EveryAxisButTheEconomyIsInert_andTheEconomyOwnsTheWholeDelta()
    {
        var stage0 = BattleModeProfileCatalog.ClassicRound;
        var stage1 = stage0 with { AdvancePolicy = AdvancePolicyKind.FixedIncrement };
        var stage2 = stage1 with { W = 4 };
        var stage3 = stage2 with { DefaultCommitment = Commitment.EarlyBoundWithFallback };
        var stage4 = stage3 with { NewEconomy = static () => new ActionPointsEconomy(2) };
        var stage5 = stage4 with { OrdersBySpeed = true };

        var r0 = WinRate(stage0);
        Assert.Equal(r0, WinRate(stage1), precision: 10);   // advance policy: no frames in a batch resolve
        Assert.Equal(r0, WinRate(stage2), precision: 10);   // W: cannot bind without wind-up
        Assert.Equal(r0, WinRate(stage3), precision: 10);   // commitment: deliberately unwired by B37

        var r4 = WinRate(stage4);
        Assert.NotEqual(r0, r4);                            // economy: the one axis that moves anything
        Assert.Equal(r4, WinRate(stage5), precision: 10);   // readiness ordering: inert while no content authors speed
    }

    /// <summary>
    /// ⛔ **The sweep must end where production begins.** Every stage above is a `with`-derived
    /// measurement profile, so nothing forces the last one to still equal the shipped `hybrid-atb` —
    /// and when B39 added an axis, it silently stopped doing so. A table that no longer describes what
    /// the game runs is worse than no table, because it still reads as evidence.
    ///
    /// <para>Compared on the measured RESULT rather than on the record's fields: the question this
    /// sweep answers is "does the migration land where the table says", and two profiles that resolve
    /// 240 battles identically answer it, whatever their unrelated fields hold.</para>
    /// </summary>
    [Fact]
    public void TheFinalStageIsTheShippedProfile()
    {
        var shipped = BattleModeProfileCatalog.HybridAtb;
        var finalStage = BattleModeProfileCatalog.ClassicRound with
        {
            AdvancePolicy = AdvancePolicyKind.FixedIncrement,
            W = 4,
            DefaultCommitment = Commitment.EarlyBoundWithFallback,
            NewEconomy = static () => new ActionPointsEconomy(2),
            OrdersBySpeed = true
        };

        Assert.Equal(WinRate(shipped), WinRate(finalStage), precision: 10);

        // And the axes the table names really are the shipped ones, so a silent profile change cannot
        // leave the sweep measuring a configuration nobody plays.
        Assert.Equal(AdvancePolicyKind.FixedIncrement, shipped.AdvancePolicy);
        Assert.Equal(4, shipped.W);
        Assert.Equal(Commitment.EarlyBoundWithFallback, shipped.DefaultCommitment);
        Assert.True(shipped.OrdersBySpeed);
    }
}
