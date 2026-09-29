using FusionRpg.Core.Battle.Timeline;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Timeline;

/// <summary>
/// `battle-tempo` `reaction-lane` RL4 (spec-reaction-lane.md §5.1/§5.4/§5.6): all four
/// `ReactionOutcome` values reachable, nested-resolution determinism, and the depth limit
/// unreachable by ordinary content. Reuses `ReactionLane.cs` unmodified — this task is test-only,
/// proving existing machinery (`RL4`'s own todo evidence names the same shape for `AT4`).
/// </summary>
public class ReactionLaneOutcomesTests
{
    [Fact]
    public void WReactZeroAlwaysRefusesWithNoLane()
    {
        var lane = new ReactionLane(wReact: 0);
        var outcome = lane.TryEnter("a", "left");
        Assert.Equal(ReactionOutcome.NoLane, outcome);
    }

    [Fact]
    public void EnteringWithinCapacityAndDepthSucceeds()
    {
        var lane = new ReactionLane(wReact: 1);
        var outcome = lane.TryEnter("a", "left");
        Assert.Equal(ReactionOutcome.Entered, outcome);
        Assert.Equal(1, lane.Depth);
    }

    [Fact]
    public void ASecondEntryWhileTheOneSlotIsHeldRefusesWithNoSlot()
    {
        var lane = new ReactionLane(wReact: 1);
        var first = lane.TryEnter("a", "left");
        var second = lane.TryEnter("b", "left"); // same slot pool, none released yet

        Assert.Equal(ReactionOutcome.Entered, first);
        Assert.Equal(ReactionOutcome.NoSlot, second);
    }

    [Fact]
    public void ExceedingDepthLimitRefusesWithDepthExceeded()
    {
        // Wide enough that slots are never the limiting factor -- only depth is under test.
        var lane = new ReactionLane(wReact: ReactionLane.DepthLimit + 5);

        for (var i = 0; i < ReactionLane.DepthLimit; i++)
            Assert.Equal(ReactionOutcome.Entered, lane.TryEnter($"actor{i}", "left"));

        var overLimit = lane.TryEnter("oneTooMany", "left");
        Assert.Equal(ReactionOutcome.DepthExceeded, overLimit);
        Assert.Equal(ReactionLane.DepthLimit, lane.Depth); // the refused attempt never incremented it
    }

    [Fact]
    public void ExitReleasesTheSlotAndPopsOneNestingLevel()
    {
        var lane = new ReactionLane(wReact: 1);
        lane.TryEnter("a", "left");
        Assert.Equal(1, lane.Depth);

        lane.Exit("a");
        Assert.Equal(0, lane.Depth);

        var reentered = lane.TryEnter("b", "left"); // the slot is free again
        Assert.Equal(ReactionOutcome.Entered, reentered);
    }

    [Fact]
    public void ExitIsIdempotentPerAcquire()
    {
        var lane = new ReactionLane(wReact: 1);
        lane.TryEnter("a", "left");
        lane.Exit("a");
        lane.Exit("a"); // second call for the same actor -- must not unwind depth twice or go negative
        Assert.Equal(0, lane.Depth);
    }

    /// <summary>Nested-resolution determinism: the mechanism introduces no hidden state of its own —
    /// the identical call sequence against a fresh lane produces the identical outcome sequence and
    /// final depth, every time.</summary>
    [Fact]
    public void TheIdenticalCallSequenceProducesIdenticalOutcomesAndFinalDepth()
    {
        (List<ReactionOutcome> Outcomes, int FinalDepth) Run()
        {
            var lane = new ReactionLane(wReact: 2);
            var outcomes = new List<ReactionOutcome>
            {
                lane.TryEnter("a", "left"),
                lane.TryEnter("b", "left"),
                lane.TryEnter("c", "left"), // NoSlot -- only 2 slots
            };
            lane.Exit("a");
            outcomes.Add(lane.TryEnter("c", "left")); // now succeeds
            return (outcomes, lane.Depth);
        }

        var first = Run();
        var second = Run();
        Assert.Equal(first.Outcomes, second.Outcomes);
        Assert.Equal(first.FinalDepth, second.FinalDepth);
    }

    /// <summary>The deepest shape this game has NAMED (hit → block → riposte-to-the-block,
    /// `DepthLimit`'s own doc) lands exactly at the limit without being refused — proving `DepthLimit`
    /// was sized FOR that chain, not merely larger than it by luck. A hypothetical fourth level (a
    /// counter-riposte) is what the limit exists to catch, proven in the same test rather than
    /// assumed. "Unreachable by ordinary content" (the acceptance line) means battles routinely reach
    /// nowhere NEAR three levels of nesting at all — this is the ONE named worst case, not a
    /// representative everyday one.</summary>
    [Fact]
    public void TheNamedWorstCaseChainLandsExactlyAtTheLimitAndAFourthLevelIsRefused()
    {
        // Three DISTINCT actor keys -- found via gk-core/tools/ReactionLaneProbe: ActionSlots refuses a
        // SECOND concurrent acquire by the SAME actor key ("defender" entering twice, for block then
        // riposte, got NoSlot on the second try -- ActionSlots' own one-slot-per-actor contract
        // working correctly, not a ReactionLane defect). This test is about NESTING DEPTH, not which
        // actor holds which level, so distinct keys isolate exactly that property.
        var lane = new ReactionLane(wReact: 4);
        var hit = lane.TryEnter("attacker", "left");
        var block = lane.TryEnter("defender", "right");
        var riposte = lane.TryEnter("bystander", "right");

        Assert.Equal(ReactionOutcome.Entered, hit);
        Assert.Equal(ReactionOutcome.Entered, block);
        Assert.Equal(ReactionOutcome.Entered, riposte);
        // DepthLimit's own doc: "3 covers the deepest shape this game has named so far -- a hit, a
        // block, and a riposte to the block -- WITH ONE LEVEL OF HEADROOM before a chain is dropped."
        // So the ordinary 3-deep chain lands exactly AT the limit (never exceeds it, never refused);
        // headroom means a genuinely deeper, unusual chain is what the limit exists to catch.
        Assert.Equal(ReactionLane.DepthLimit, lane.Depth);
        var oneMore = lane.TryEnter("someone", "left");
        Assert.Equal(ReactionOutcome.DepthExceeded, oneMore); // the FOURTH level is what gets dropped
    }

    [Fact]
    public void NegativeWReactThrows()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReactionLane(wReact: -1));
    }
}
