using FusionRpg.Core.Combat;
using Xunit;

namespace FusionRpg.Core.Tests.Combat;

/// <summary>spec-evasion-chain.md §2 (T5.2) — the shared clamp+delta helper, extracted from
/// ShieldMath.AbsorbLayer without moving a single shield golden.</summary>
public class ClampedContestTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(100)]
    [InlineData(999)]
    [InlineData(123457)]
    public void HelperMatchesShieldMathExactly(long input)
    {
        // Property test over the same golden matrix ShieldMathTests.Invariants_hold_across_grid uses
        // (the test that caught T5.2's own extraction bug): ClampedContest.Apply, called with the
        // exact arguments AbsorbLayer passes it, must equal a hand-reimplemented copy of the ORIGINAL
        // pre-extraction formula -- bounds against raw `input`, delta term against `input + elemMod`
        // -- not the spec's own simplified single-"base" pseudocode, which this grid would fail
        // against (that mismatch IS the bug T5.2 found and fixed).
        foreach (var rel in new long[] { -1000, -300, 0, 700, 1000 })
        foreach (var breaker in new long[] { -10 * input, -5, 0, 5, 10 * input })
        foreach (var hits in new long[] { 1, 3 })
        {
            var elemMod = RoundDivSigned(rel * 250 /* ShieldPolicy.MatchupShareKPm, default */ * input, 1_000_000);
            var deltaBase = input + elemMod;

            var expectedRaw = deltaBase + hits * breaker;
            var expectedFloor = CeilDiv(100 * input, 1000);   // ShieldPolicy.ChipFloorKPm default
            var expectedCap = 3000 * input / 1000;            // ShieldPolicy.PenCapKPm default
            var expected = Math.Clamp(expectedRaw, expectedFloor, expectedCap);

            var actual = ClampedContest.Apply(deltaBase, breaker, hits, boundsBase: input, floorKPm: 100, capKPm: 3000);

            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void BoundsScaleAgainstBoundsBaseNotDeltaBase()
    {
        // The exact discrepancy T5.2's extraction first got wrong: floor/cap must scale against
        // boundsBase, never deltaBase, even when the two differ (a nonzero elemMod). Direct, minimal
        // proof using inputs large enough to actually hit the cap (so a wrong wiring changes the
        // result, not just fails to clamp at all).
        var smallDeltaBase = ClampedContest.Apply(deltaBase: 100, delta: 1000, hitCount: 1, boundsBase: 100, floorKPm: 100, capKPm: 3000);
        var largeDeltaBase = ClampedContest.Apply(deltaBase: 500, delta: 1000, hitCount: 1, boundsBase: 100, floorKPm: 100, capKPm: 3000);
        // raw = 1100 and 1500 respectively, both clamp to the SAME cap (3000*100/1000 = 300) --
        // deltaBase moved from 100 to 500, but the cap did not, because it reads boundsBase (100).
        Assert.Equal(300, smallDeltaBase);
        Assert.Equal(300, largeDeltaBase);

        // Now hold deltaBase/delta fixed and change ONLY boundsBase -- the cap DOES move.
        var largerBoundsBase = ClampedContest.Apply(deltaBase: 100, delta: 1000, hitCount: 1, boundsBase: 200, floorKPm: 100, capKPm: 3000);
        Assert.Equal(600, largerBoundsBase); // cap = 3000*200/1000 = 600; raw = 1100 clamps to it
    }

    static long CeilDiv(long num, long div) => (num + div - 1) / div;

    static long RoundDivSigned(long num, long div) =>
        num >= 0 ? (num + div / 2) / div : -((-num + div / 2) / div);
}
