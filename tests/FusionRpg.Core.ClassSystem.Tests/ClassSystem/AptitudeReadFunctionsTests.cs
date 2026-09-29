using FusionRpg.Core.Stats.Aptitudes;
using Xunit;

namespace FusionRpg.Core.Tests.ClassSystem;

/// <summary>class-system-todo.md P2.2 — AptitudeReadFunctions, the one shared `k · share^γ · scale`
/// implementation (ssot-power-scale.md §4.6, PS-3). Contest is Θ-free and bounded (PS-8 exemption,
/// `double` throughout); Magnitude reads `P(Θ)` and stays `long`-safe (docs/architecture/numeric-types.md's overflow table) —
/// checked, widened before multiply, one rounding division at the end.</summary>
public class AptitudeReadFunctionsTests
{
    // ── Contest: k * share^gamma * spanPoints, Theta-free ──────────────────────────────────────────

    [Fact]
    public void Contest_gammaOne_isLinearInShare()
    {
        // k=2.2, share=0.5, gamma=1.0, span=100 -> 2.2 * 0.5 * 100 = 110.0
        var v = AptitudeReadFunctions.Contest(kMilli: 2200, share: 0.5, shareExponentMilli: 1000, spanPointsMilli: 100_000);
        Assert.Equal(110.0, v, 9);
    }

    [Fact]
    public void Contest_zeroShare_isZero()
    {
        var v = AptitudeReadFunctions.Contest(kMilli: 5000, share: 0.0, shareExponentMilli: 1000, spanPointsMilli: 100_000);
        Assert.Equal(0.0, v, 9);
    }

    [Fact]
    public void Contest_fullShare_equalsKTimesSpan()
    {
        // share=1.0 -> share^gamma = 1 regardless of gamma -> k * span
        var v = AptitudeReadFunctions.Contest(kMilli: 3000, share: 1.0, shareExponentMilli: 2500, spanPointsMilli: 100_000);
        Assert.Equal(3.0 * 100.0, v, 9);
    }

    [Fact]
    public void Contest_gammaTwo_isQuadraticInShare()
    {
        // k=1.0, share=0.5, gamma=2.0, span=100 -> 1.0 * 0.25 * 100 = 25.0
        var v = AptitudeReadFunctions.Contest(kMilli: 1000, share: 0.5, shareExponentMilli: 2000, spanPointsMilli: 100_000);
        Assert.Equal(25.0, v, 9);
    }

    [Theory]
    [InlineData(-0.0001)]
    [InlineData(1.0001)]
    [InlineData(double.NaN)]
    public void Contest_shareOutOfRange_rejects(double share)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AptitudeReadFunctions.Contest(kMilli: 1000, share: share, shareExponentMilli: 1000, spanPointsMilli: 100_000));
    }

    [Fact]
    public void Contest_negativeK_rejects()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AptitudeReadFunctions.Contest(kMilli: -1, share: 0.5, shareExponentMilli: 1000, spanPointsMilli: 100_000));
    }

    [Fact]
    public void Contest_nonPositiveGamma_rejects()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AptitudeReadFunctions.Contest(kMilli: 1000, share: 0.5, shareExponentMilli: 0, spanPointsMilli: 100_000));
    }

    // ── Magnitude: k * share^gamma * P(Theta), reads the ladder ────────────────────────────────────

    [Fact]
    public void Magnitude_gammaOne_isLinearInShare_exact()
    {
        // k=2.2, share=0.5, gamma=1.0, P(Theta)=1000 -> 2.2 * 0.5 * 1000 = 1100, exactly.
        var v = AptitudeReadFunctions.Magnitude(kMilli: 2200, share: 0.5, shareExponentMilli: 1000, pTheta: 1000);
        Assert.Equal(1100L, v);
    }

    [Fact]
    public void Magnitude_fullShare_equalsKTimesPTheta_exact()
    {
        // share=1.0, gamma=1.0, k=1.0 -> P(Theta) unchanged.
        var v = AptitudeReadFunctions.Magnitude(kMilli: 1000, share: 1.0, shareExponentMilli: 1000, pTheta: 123_456_789);
        Assert.Equal(123_456_789L, v);
    }

    [Fact]
    public void Magnitude_zeroShare_isZero()
    {
        var v = AptitudeReadFunctions.Magnitude(kMilli: 9000, share: 0.0, shareExponentMilli: 1000, pTheta: 1_000_000);
        Assert.Equal(0L, v);
    }

    [Fact]
    public void Magnitude_roundsHalfAwayFromZero()
    {
        // k=1.5, share=1.0, gamma=1.0, P(Theta)=1 -> exact value 1.5 -> rounds to 2, not 1.
        var v = AptitudeReadFunctions.Magnitude(kMilli: 1500, share: 1.0, shareExponentMilli: 1000, pTheta: 1);
        Assert.Equal(2L, v);
    }

    [Fact]
    public void Magnitude_subMilliContribution_roundsDownToZero_notNegative()
    {
        // k=0.001, share=1.0, P(Theta)=1 -> exact value 0.001 -> rounds to 0.
        var v = AptitudeReadFunctions.Magnitude(kMilli: 1, share: 1.0, shareExponentMilli: 1000, pTheta: 1);
        Assert.Equal(0L, v);
    }

    [Fact]
    public void Magnitude_gammaTwo_isQuadraticInShare_exact()
    {
        // k=1.0, share=0.5, gamma=2.0, P(Theta)=1000 -> share^2=0.25 -> 1.0 * 0.25 * 1000 = 250.
        var v = AptitudeReadFunctions.Magnitude(kMilli: 1000, share: 0.5, shareExponentMilli: 2000, pTheta: 1000);
        Assert.Equal(250L, v);
    }

    [Fact]
    public void Magnitude_scalesWithHugePTheta_noPrecisionLoss()
    {
        // Deliberately past double's exact-integer ceiling (2^53 ~ 9.007e15) -- docs/architecture/numeric-types.md's own
        // worked example: P(Theta) can legitimately reach into the quintillions under `long`, and a
        // double-based multiply would silently lose precision here. share=1, k=1 -> pTheta unchanged.
        const long huge = 4_611_686_018_427_387_904L; // 2^62, far past double's exact range
        var v = AptitudeReadFunctions.Magnitude(kMilli: 1000, share: 1.0, shareExponentMilli: 1000, pTheta: huge);
        Assert.Equal(huge, v);
    }

    [Fact]
    public void Magnitude_overflow_throwsNeverWraps()
    {
        // k=2.0, share=1.0, pTheta=long.MaxValue -- the TRUE answer (2 * pTheta) genuinely exceeds
        // long.MaxValue, so this must throw. (A large kMilli alone does not overflow this: see
        // Magnitude_largeKAndHugePTheta_stillFits_noSpuriousOverflow for the case that must NOT throw.)
        Assert.Throws<OverflowException>(() =>
            AptitudeReadFunctions.Magnitude(kMilli: 2000, share: 1.0, shareExponentMilli: 1000, pTheta: long.MaxValue));
    }

    [Fact]
    public void Magnitude_largeKAndHugePTheta_stillFits_noSpuriousOverflow()
    {
        // Regression for a real bug caught in this file's own test run: an earlier implementation
        // multiplied the two per-mille factors together BEFORE multiplying by pTheta, which overflowed
        // `checked long` on the intermediate product even though the true final answer (k=2.0 * pTheta)
        // comfortably fits in long. k=2.0, pTheta well under long.MaxValue/2 -- must NOT throw.
        const long pTheta = 2_000_000_000_000_000_000L; // 2e18, true answer 4e18 < long.MaxValue (~9.223e18)
        var v = AptitudeReadFunctions.Magnitude(kMilli: 2000, share: 1.0, shareExponentMilli: 1000, pTheta: pTheta);
        Assert.Equal(4_000_000_000_000_000_000L, v);
    }

    [Theory]
    [InlineData(-0.0001)]
    [InlineData(1.0001)]
    [InlineData(double.NaN)]
    public void Magnitude_shareOutOfRange_rejects(double share)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AptitudeReadFunctions.Magnitude(kMilli: 1000, share: share, shareExponentMilli: 1000, pTheta: 1000));
    }

    [Fact]
    public void Magnitude_negativeK_rejects()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AptitudeReadFunctions.Magnitude(kMilli: -1, share: 0.5, shareExponentMilli: 1000, pTheta: 1000));
    }

    [Fact]
    public void Magnitude_negativePTheta_rejects()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AptitudeReadFunctions.Magnitude(kMilli: 1000, share: 0.5, shareExponentMilli: 1000, pTheta: -1));
    }

    [Fact]
    public void Magnitude_nonPositiveGamma_rejects()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AptitudeReadFunctions.Magnitude(kMilli: 1000, share: 0.5, shareExponentMilli: -1, pTheta: 1000));
    }

    // ── spec-aptitude-tuning.md §6 tests 6-8: the invariance proof's premises, as assertions ───────

    [Fact]
    public void ContestRead_isThetaFree_byConstruction()
    {
        // Test 6. Contest's signature carries no Theta/P(Theta) parameter at all -- Theta-freedom is
        // enforced by the TYPE, not just by behavior. This is the runtime half of that guarantee: the
        // same (k, share, gamma, span) inputs always produce the same output, independent of whatever
        // ladder value a caller happens to be resolving alongside it.
        var a = AptitudeReadFunctions.Contest(kMilli: 1700, share: 0.62, shareExponentMilli: 1000, spanPointsMilli: 100_000);
        var b = AptitudeReadFunctions.Contest(kMilli: 1700, share: 0.62, shareExponentMilli: 1000, spanPointsMilli: 100_000);
        Assert.Equal(a, b, 12);
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(1_000L)]
    [InlineData(1_000_000_000L)]
    public void MagnitudeRead_isProportionalToPTheta_doublingPThetaDoublesTheValue(long pTheta)
    {
        // Test 7. share fixed below 1.0 so share^gamma stays a genuine fraction, not the share=1
        // identity case already covered elsewhere.
        var once = AptitudeReadFunctions.Magnitude(kMilli: 3000, share: 0.4, shareExponentMilli: 1000, pTheta: pTheta);
        var twice = AptitudeReadFunctions.Magnitude(kMilli: 3000, share: 0.4, shareExponentMilli: 1000, pTheta: pTheta * 2);
        Assert.Equal(once * 2, twice);
    }

    [Fact]
    public void GammaOne_isLinearInShare_twoHalvesEqualOneWhole()
    {
        // Test 8. At gamma=1, splitting one full allocation into two half-shares and summing their
        // separately-read contributions must equal reading the whole share at once -- the additivity
        // that makes "share" a meaningful linear currency instead of merely a bounded ratio.
        const long k = 2200, pTheta = 1_000_000;
        var half = AptitudeReadFunctions.Magnitude(kMilli: k, share: 0.5, shareExponentMilli: 1000, pTheta: pTheta);
        var wholeFromTwoHalves = half + half;
        var whole = AptitudeReadFunctions.Magnitude(kMilli: k, share: 1.0, shareExponentMilli: 1000, pTheta: pTheta);
        Assert.Equal(whole, wholeFromTwoHalves);
    }

    [Fact]
    public void GammaTwo_isNotLinearInShare_twoHalvesUnderShootOneWhole()
    {
        // The contrapositive of the above, at gamma=2: superlinear concentration means two half-shares
        // sum to LESS than one whole share -- proves the linearity in GammaOne_isLinear... is actually
        // exercising gamma, not an accident of the arithmetic.
        const long k = 1000, pTheta = 1_000_000;
        var half = AptitudeReadFunctions.Magnitude(kMilli: k, share: 0.5, shareExponentMilli: 2000, pTheta: pTheta);
        var wholeFromTwoHalves = half + half;
        var whole = AptitudeReadFunctions.Magnitude(kMilli: k, share: 1.0, shareExponentMilli: 2000, pTheta: pTheta);
        Assert.True(wholeFromTwoHalves < whole, $"expected two half-shares ({wholeFromTwoHalves}) < one whole share ({whole}) at gamma=2");
    }

    // ── one implementation only (structural echo of guard-class-system.ps1 G5) ─────────────────────

    [Fact]
    public void OnlyOneAptitudeReadFunctionsTypeExists_inFusionRpgCore()
    {
        var asm = typeof(AptitudeReadFunctions).Assembly;
        var hits = asm.GetTypes().Where(t => t.Name == nameof(AptitudeReadFunctions)).ToList();
        Assert.Single(hits);
    }
}
