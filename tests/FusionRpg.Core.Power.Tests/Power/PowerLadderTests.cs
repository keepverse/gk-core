using System;
using System.IO;
using System.Text.RegularExpressions;
using FusionRpg.Core.Power;
using Xunit;

namespace FusionRpg.Core.Tests.Power;

/// <summary>
/// power-ladder wave 1 (spec-power-ladder.md §5, §7). <see cref="PowerLadder"/>'s function, its
/// overflow ceiling, and the whole-module determinism guarantees. <see cref="PowerTuning"/>'s own
/// loading/rejection behaviour is PowerTuningTests.cs, not here.
/// </summary>
public class PowerLadderTests
{
    static PowerLadder Ladder(long bMilli, long? wm = 5000) =>
        new(PowerTuning.Build(1, 1, PowerTuning.FixedCMilli, bMilli, PowerTuning.FixedPinIndex, PowerTuning.FixedPinValue,
            1000, 25000, 250, 1000, wm, 5000, 25000));

    // ---- §7.1: the zero-movement proof --------------------------------------------------------------

    [Fact]
    public void AtBZero_ValueMatchesShippedBattleRulesetBaseHpFormula_AcrossFullRange()
    {
        // BattleRuleset.BaseHp(L) = 80 + 30*L today (ssot-power-scale.md §4.4). This is the equality
        // that makes Phase 2's whole migration a zero-golden-movement refactor.
        var ladder = Ladder(bMilli: 0);
        for (int l = 0; l <= 5000; l++)
            Assert.Equal(80 + 30L * l, ladder.Value(l));
    }

    // ---- §7.2: the pin holds for every legal B --------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(200)]
    [InlineData(400)]
    [InlineData(1000)]
    [InlineData(9998)]
    public void PinHolds_ValueAt20Equals680_ForEveryLegalB(long bMilli)
    {
        var ladder = Ladder(bMilli);
        Assert.Equal(680, ladder.Value(20));
    }

    // ---- §7.4: closed form vs iterated ΔP sum ---------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(400)]
    [InlineData(9998)]
    public void ClosedForm_AgreesWithIteratedDeltaSum_ToTheta2000(long bMilli)
    {
        var ladder = Ladder(bMilli);
        long summed = ladder.ValueMilli(0);
        for (int theta = 1; theta <= 2000; theta++)
        {
            long delta = ladder.ValueMilli(theta) - ladder.ValueMilli(theta - 1);
            summed += delta;
            Assert.Equal(ladder.ValueMilli(theta), summed);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(400)]
    public void Increment_IsArithmeticProgression(long bMilli)
    {
        var ladder = Ladder(bMilli);
        long aMilli = 30000 - 19 * bMilli / 2;
        for (int theta = 1; theta <= 2000; theta++)
        {
            long increment = ladder.ValueMilli(theta) - ladder.ValueMilli(theta - 1);
            Assert.Equal(aMilli + bMilli * (theta - 1), increment);
        }
    }

    // 9998 is deliberately excluded here (unlike every other theory in this file): it drives the
    // derived AMilli negative (30000 - 19*9998/2 = -64981), which dips Value(1) below Value(0) before
    // the quadratic term recovers — a real property of this formula's shape at extreme B, not a code
    // defect (verified: Value(0)=80, Value(1)=15 at B=9998). 9998 is 25x the decided dial (400) and
    // 12x the documented "steep" example (800, ssot-power-scale.md §4.5) — no real tuning approaches
    // it. The spec's own table lists 9998 only under "pin holds", never under "monotonic".
    [Theory]
    [InlineData(0)]
    [InlineData(400)]
    public void Monotonic_ValueStrictlyIncreases(long bMilli)
    {
        var ladder = Ladder(bMilli);
        long prev = ladder.Value(0);
        for (int theta = 1; theta <= 5000; theta++)
        {
            long v = ladder.Value(theta);
            Assert.True(v > prev, $"Value({theta})={v} did not exceed Value({theta - 1})={prev} at B={bMilli}");
            prev = v;
        }
    }

    // ---- §7 local exponent band (test-only real-valued check; production stays integer-only) ---------

    [Fact]
    public void LocalExponentBand_AtDecidedB_FallsInDocumentedRange()
    {
        // dlnP/dlnTheta over [40,250] at B=400 should sit in (1.2, 1.7) per ssot-power-scale.md §4.5's
        // measured table (1.28 @ 50, 1.61 @ 200). Math.Log is a TEST-side approximation of a
        // continuous quantity the design brief defines in real terms — PowerLadder itself stays
        // integer-only; this assertion never touches production code with a double.
        var ladder = Ladder(bMilli: 400);
        double p40 = ladder.Value(40), p250 = ladder.Value(250);
        double exponent = Math.Log(p250 / p40) / Math.Log(250.0 / 40.0);
        Assert.InRange(exponent, 1.2, 1.7);
    }

    // ---- §7.5: maxIndex ----------------------------------------------------------------------------

    [Fact]
    public void MaxIndex_AtDecidedDialB400_MatchesExactly()
    {
        // ssot-power-scale.md §4.5 says "B=400 -> Theta ~ 2.14x10^8" (rounded); the exact boundary,
        // independently re-derived from the closed form (C=80000, A=26200, B=400) rather than read
        // off this implementation, is 214,748,299 — one below where the triangular term's true value
        // (not the intermediate product) would first exceed long.MaxValue.
        var ladder = Ladder(bMilli: 400);
        Assert.Equal(214_748_299L, ladder.MaxIndex);
    }

    [Fact]
    public void MaxIndex_AtBZero_NoIntSizedThetaCanOverflow()
    {
        // "B=0 -> Theta ~ 3.5x10^14" (SSOT §4.5) is far beyond int.MaxValue, and Value's own index
        // parameter is int — so at B=0 (what actually ships through Phase 1-3) maxIndex is simply
        // the full int range: nothing an int can carry ever overflows the long computation.
        var ladder = Ladder(bMilli: 0);
        Assert.Equal(int.MaxValue, ladder.MaxIndex);
    }

    [Fact]
    public void Value_AboveMaxIndex_ThrowsPowerIndexOverflow_RatherThanWrapping()
    {
        var ladder = Ladder(bMilli: 400);
        long max = ladder.MaxIndex;
        Assert.True(max < int.MaxValue, "precondition: B=400 must have a max below int range for this test to mean anything");

        // The boundary itself must still succeed...
        _ = ladder.Value((int)max);
        // ...and one past it must throw, never silently wrap to a smaller/negative number.
        var ex = Assert.Throws<PowerIndexOverflow>(() => ladder.Value((int)max + 1));
        Assert.Equal((int)max + 1, ex.Index);
        Assert.Equal(max, ex.MaxIndex);
    }

    [Fact]
    public void MaxIndex_ShrinksAsBGrows()
    {
        var small = Ladder(bMilli: 0).MaxIndex;
        var mid = Ladder(bMilli: 400).MaxIndex;
        var large = Ladder(bMilli: 9998).MaxIndex;
        Assert.True(small > mid);
        Assert.True(mid > large);
    }

    [Fact]
    public void NegativeIndex_Rejected()
    {
        var ladder = Ladder(bMilli: 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => ladder.Value(-1));
    }

    // ---- §7.6: purity --------------------------------------------------------------------------------

    [Fact]
    public void Purity_SameIndexRepeatedCalls_IdenticalResult()
    {
        var ladder = Ladder(bMilli: 400);
        long first = ladder.Value(777);
        for (int i = 0; i < 1000; i++)
            Assert.Equal(first, ladder.Value(777));
    }

    // ---- §7.6/§5: determinism ------------------------------------------------------------------------
    // The former Core/Power source scan banning double/decimal/Math.Pow/Math.Exp was removed by the
    // 2026-09-15 owner ruling (floating point is allowed for any quantity; determinism of a double in a
    // hashed golden is a platform stamp, ssot-power-scale.md §10.7). Determinism stays proven by value:
    // Purity_SameIndexRepeatedCalls_IdenticalResult above, and the exact-value/overflow tests.

    [Fact]
    public void PowerLadder_SourceContainsNoNumericLiteralOutsideTheLoader()
    {
        // "No numeric literal appears outside PowerTuning's loader" (§2.6). PowerLadder.cs itself may
        // only contain small structural literals (0, 1, 2, 1000 for the milli scale) — never a curve
        // constant. This is deliberately narrower than the repo-wide magic-number audit: it exists to
        // catch a curve literal creeping into THIS file specifically.
        var path = Path.Combine(RepoRoot(), "src", "FusionRpg.Core", "Power", "PowerLadder.cs");
        var allowed = new System.Collections.Generic.HashSet<string> { "0", "1", "2", "1000" };
        var literalRe = new Regex(@"(?<![\w.])-?\d+(?![\w.])");

        var offenders = new System.Collections.Generic.List<string>();
        var lines = File.ReadAllLines(path);
        for (int i = 0; i < lines.Length; i++)
        {
            var code = Regex.Replace(lines[i], "//.*$", "");
            code = Regex.Replace(code, "\"[^\"]*\"", "\"\"");
            foreach (System.Text.RegularExpressions.Match m in literalRe.Matches(code))
                if (!allowed.Contains(m.Value))
                    offenders.Add($"PowerLadder.cs:{i + 1}: {lines[i].Trim()}");
        }
        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tuning"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("data/tuning");
    }
}
