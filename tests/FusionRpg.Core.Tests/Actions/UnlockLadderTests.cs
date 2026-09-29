using System.Runtime.CompilerServices;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Unlock;
using Xunit;

namespace FusionRpg.Core.Tests.Actions;

/// <summary>
/// T19 (action-todo.md, spec-unlock-ladder.md §1): the ratchet. The chance table is the load-bearing
/// assertion — computed independently (Python, exact floating point, rounded once at the very end)
/// against the shipped tuning row, not copied from the spec's own prose table, so a rounding bug in
/// either the spec or a naive per-step implementation would show up as a mismatch here.
/// </summary>
public class UnlockLadderTests
{
    static string TuningPath([CallerFilePath] string here = "")
    {
        var testsDir = Path.GetDirectoryName(here)!;                            // tests/.../Actions
        var repo = Path.GetFullPath(Path.Combine(testsDir, "..", "..", ".."));   // repo root
        return Path.Combine(repo, "data", "tuning", "action-unlock.v1.json");
    }

    static readonly UnlockTuning Shipped = UnlockTuningLoader.Parse(File.ReadAllText(TuningPath()));

    [Theory]
    [InlineData(0, 500)]   // earn 1
    [InlineData(9, 158)]   // earn 10
    [InlineData(10, 139)]  // earn 11
    [InlineData(19, 44)]   // earn 20
    [InlineData(24, 23)]   // earn 25
    [InlineData(39, 3)]    // earn 40
    [InlineData(49, 1)]    // earn 50 -- AT the floor (0.095% rounds under 0.1%, floor clamps it up)
    public void ChanceMatchesTheSpecTableAtEachNamedEarnCount(long earnCountBeforeRoll, int expectedMilli)
    {
        Assert.Equal(expectedMilli, UnlockLadder.ChanceMilli(earnCountBeforeRoll, Shipped));
    }

    [Fact]
    public void ChanceNeverFallsBelowTheFloorNoMatterHowLargeEarnCountGrows()
    {
        Assert.Equal(Shipped.FloorMilli, UnlockLadder.ChanceMilli(1000, Shipped));
        Assert.Equal(Shipped.FloorMilli, UnlockLadder.ChanceMilli(1_000_000, Shipped));
        Assert.Equal(Shipped.FloorMilli, UnlockLadder.ChanceMilli(long.MaxValue / 2, Shipped));
    }

    [Fact]
    public void ChanceIsMonotonicallyNonIncreasing()
    {
        var previous = UnlockLadder.ChanceMilli(0, Shipped);
        for (long n = 1; n <= 60; n++)
        {
            var current = UnlockLadder.ChanceMilli(n, Shipped);
            Assert.True(current <= previous, $"chance rose from {previous} to {current} at earnCount {n}");
            previous = current;
        }
    }

    [Fact]
    public void FloorZeroIsRejectedAtLoadNamingPS8()
    {
        var json = """{ "p1Milli": 500, "deltaMilli": 880, "floorMilli": 0, "heldCap": 10, "rungCap": 10, "discardTaxCoeffMilli": 100 }""";
        var ex = Assert.Throws<UnlockTuningRejection>(() => UnlockTuningLoader.Parse(json));
        Assert.Contains("PS-8", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RungIsDerivedFromEarnCountAloneNeverOccupancy()
    {
        // "A planted occupancy-keyed implementation fails": UnlockLadder.EffectiveRung takes ONLY
        // earnCount -- there is no slot/position parameter it could even read, so an occupancy-keyed
        // variant is not a bug this signature can express, let alone hide. Renamed from `Rung` (A-U1,
        // spec-rung-semantics.md §3.1) -- this is the HOLDER-derived reading, distinct from
        // `ActionRow.Rung`'s authored column.
        Assert.Equal(1, UnlockLadder.EffectiveRung(1, Shipped).Value);
        Assert.Equal(5, UnlockLadder.EffectiveRung(5, Shipped).Value);
        Assert.Equal(10, UnlockLadder.EffectiveRung(10, Shipped).Value);   // at rungCap
        Assert.Equal(10, UnlockLadder.EffectiveRung(11, Shipped).Value);   // past rungCap -- clamped, "arrives at the top rung"
        Assert.Equal(10, UnlockLadder.EffectiveRung(1000, Shipped).Value); // arbitrarily far past rungCap -- still clamped
    }

    [Fact]
    public void RungOfZeroEarnsIsZero()
    {
        Assert.Equal(0, UnlockLadder.EffectiveRung(0, Shipped).Value);
    }

    /// <summary>ST2 (spec-holder-rung-pricing.md contracts 1–3): the window bounds the rung.</summary>
    [Fact]
    public void WindowCeilingBoundsTheRung()
    {
        var window = new RungBand(Floor: 1, Ceiling: 4);
        Assert.Equal(4, UnlockLadder.EffectiveRung(9, Shipped, window).Value); // above ceiling → ceiling
        Assert.Equal(2, UnlockLadder.EffectiveRung(2, Shipped, window).Value); // below ceiling → earnCount
    }

    /// <summary>ST2 contract 2: a null window is unbounded — identical to the two-argument form over a
    /// spread of inputs.</summary>
    [Fact]
    public void NullWindowMatchesTheTwoArgumentForm()
    {
        foreach (var earnCount in new long[] { 0, 1, 5, 10, 11, 100, 1000 })
            Assert.Equal(
                UnlockLadder.EffectiveRung(earnCount, Shipped).Value,
                UnlockLadder.EffectiveRung(earnCount, Shipped, window: null).Value);
    }

    /// <summary>ST2 contract 3: the window's floor never raises the result.</summary>
    [Fact]
    public void WindowFloorNeverRaisesTheResult()
    {
        var window = new RungBand(Floor: 3, Ceiling: 10);
        Assert.Equal(1, UnlockLadder.EffectiveRung(1, Shipped, window).Value);
    }

    [Theory]
    [InlineData(1001)]
    [InlineData(0)]
    [InlineData(-1)]
    public void P1OutOfRangeIsRejectedAtLoad(int badP1)
    {
        var json = $$"""{ "p1Milli": {{badP1}}, "deltaMilli": 880, "floorMilli": 1, "heldCap": 10, "rungCap": 10, "discardTaxCoeffMilli": 100 }""";
        Assert.Throws<UnlockTuningRejection>(() => UnlockTuningLoader.Parse(json));
    }

    [Theory]
    [InlineData(1000)] // never decays
    [InlineData(0)]
    public void DeltaOutOfRangeIsRejectedAtLoad(int badDelta)
    {
        var json = $$"""{ "p1Milli": 500, "deltaMilli": {{badDelta}}, "floorMilli": 1, "heldCap": 10, "rungCap": 10, "discardTaxCoeffMilli": 100 }""";
        Assert.Throws<UnlockTuningRejection>(() => UnlockTuningLoader.Parse(json));
    }

    /// <summary>A-U1 (spec-rung-semantics.md §3.3): the two dials are validated independently now
    /// that they are separate fields — each must individually reject below 1.</summary>
    [Fact]
    public void HeldCapBelowOneIsRejectedAtLoad()
    {
        var json = """{ "p1Milli": 500, "deltaMilli": 880, "floorMilli": 1, "heldCap": 0, "rungCap": 10, "discardTaxCoeffMilli": 100 }""";
        Assert.Throws<UnlockTuningRejection>(() => UnlockTuningLoader.Parse(json));
    }

    [Fact]
    public void RungCapBelowOneIsRejectedAtLoad()
    {
        var json = """{ "p1Milli": 500, "deltaMilli": 880, "floorMilli": 1, "heldCap": 10, "rungCap": 0, "discardTaxCoeffMilli": 100 }""";
        Assert.Throws<UnlockTuningRejection>(() => UnlockTuningLoader.Parse(json));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void DiscardTaxCoeffZeroOrBelowIsRejectedAtLoad(int badCoeff)
    {
        var json = $$"""{ "p1Milli": 500, "deltaMilli": 880, "floorMilli": 1, "heldCap": 10, "rungCap": 10, "discardTaxCoeffMilli": {{badCoeff}} }""";
        Assert.Throws<UnlockTuningRejection>(() => UnlockTuningLoader.Parse(json));
    }
}
