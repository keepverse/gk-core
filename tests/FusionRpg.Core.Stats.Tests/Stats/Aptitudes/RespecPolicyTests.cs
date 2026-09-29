using FusionRpg.Core.Creatures.Generation;
using FusionRpg.Core.Stats.Aptitudes;
using Xunit;

namespace FusionRpg.Core.Tests.Stats.Aptitudes;

/// <summary>species-build-todo.md T4.1, re-typed EP1.6 (spec-specimen-respec-price.md) —
/// <see cref="RespecPolicy"/> (spec-species-respec.md, read in full this session). Covers the
/// policy's own slice of the spec's testing strategy: free at count zero, strict escalation, the
/// exact linear formula, and the never-refused/never-a-cooldown invariants this policy inherits from
/// class-system-todo.md P6.3's original design.</summary>
public class RespecPolicyTests
{
    static RespecPriceTuning Tuning(long basePrice = 50, long escalationPermille = 500) =>
        new(BasePrice: basePrice, EscalationPermille: escalationPermille, DecayDays: 3);

    /// <summary>EP1.6 test 7 — the species view is byte-identical after the re-type: a
    /// <see cref="SpeciesBuildTuning"/> built with the same three numbers as <see cref="Tuning"/>
    /// prices identically through <see cref="SpeciesBuildTuning.SpeciesRespec"/>.</summary>
    static SpeciesBuildTuning SpeciesTuning(long basePrice = 50, long escalationPermille = 500) => new(
        SchemaVersion: 1, Version: 1,
        ParityFloorPermille: 50, ParityCeilingPermille: 200,
        LeanMinPermille: 350, LeanMaxPermille: 600,
        CrowdingFactor: 633, SecondarySharePermille: 300,
        MaxAptitudesPerSpecies: 5, MinAptitudesPerSpecies: 2,
        RespecBasePrice: basePrice, RespecEscalationPermille: escalationPermille, RespecDecayDays: 3,
        UniqueRespecBasePrice: basePrice, UniqueRespecEscalationPermille: escalationPermille, UniqueRespecDecayDays: 3,
        LeanSignalWeights: LeanSignalWeights.Zero);

    [Fact]
    public void SpeciesRespec_view_prices_byte_identical_to_the_old_whole_record_shape()
    {
        var species = SpeciesTuning(basePrice: 50, escalationPermille: 500);
        for (long count = 0; count <= 4; count++)
            Assert.Equal(RespecPolicy.PriceOf(Tuning(50, 500), count), RespecPolicy.PriceOf(species.SpeciesRespec, count));
    }

    [Fact]
    public void PriceOf_atCountZero_isExactlyBasePrice()
    {
        // The first override is free (spec: "the player expressing a build for the first time"); T4.2
        // owns never CALLING PriceOf for that case, but the policy's own count=0 reading must still be
        // the base price, not zero — free-first-override is a caller-side decision, not this formula's.
        var price = RespecPolicy.PriceOf(Tuning(basePrice: 50), count: 0);
        Assert.Equal(RespecResource.Soul, price.Resource);
        Assert.Equal(50, price.Amount);
    }

    [Fact]
    public void PriceOf_matchesTheLinearFormula_atNamedCounts()
    {
        var tuning = Tuning(basePrice: 50, escalationPermille: 500);
        // price(count) = base + base * count * escalationPermille / 1000
        Assert.Equal(50, RespecPolicy.PriceOf(tuning, 0).Amount);
        Assert.Equal(75, RespecPolicy.PriceOf(tuning, 1).Amount);   // 50 + 50*1*500/1000
        Assert.Equal(100, RespecPolicy.PriceOf(tuning, 2).Amount); // 50 + 50*2*500/1000
        Assert.Equal(150, RespecPolicy.PriceOf(tuning, 4).Amount); // 50 + 50*4*500/1000
    }

    [Fact]
    public void PriceOf_isStrictlyIncreasing_asCountRises()
    {
        // Escalation, not a flat repeated price -- each successive change must cost strictly more.
        var tuning = Tuning();
        var second = RespecPolicy.PriceOf(tuning, 1).Amount;
        var third = RespecPolicy.PriceOf(tuning, 2).Amount;
        var fourth = RespecPolicy.PriceOf(tuning, 3).Amount;

        Assert.True(second > RespecPolicy.PriceOf(tuning, 0).Amount);
        Assert.True(third > second);
        Assert.True(fourth > third);
    }

    [Fact]
    public void PriceOf_isNeverRefused_returnsUnconditionally()
    {
        // "Always available" -- PriceOf has no "cannot respec right now" return path: calling it for
        // any non-negative count always succeeds and always returns a price.
        var tuning = Tuning();
        Assert.True(RespecPolicy.PriceOf(tuning, 0).Amount > 0);
        Assert.True(RespecPolicy.PriceOf(tuning, 1_000).Amount > 0);
    }

    [Fact]
    public void PriceOf_isPure_sameInputsAlwaysGiveTheSamePrice()
    {
        // No hidden cooldown or mutable state -- repeated calls at the same count never drift.
        var tuning = Tuning();
        Assert.Equal(RespecPolicy.PriceOf(tuning, 2), RespecPolicy.PriceOf(tuning, 2));
    }

    [Fact]
    public void PriceOf_negativeCount_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RespecPolicy.PriceOf(Tuning(), -1));
    }

    [Fact]
    public void PriceOf_nullTuning_throws()
    {
        Assert.Throws<ArgumentNullException>(() => RespecPolicy.PriceOf(null!, 0));
    }

    // ---- Quote (EP1.6) -----------------------------------------------------------------------

    [Fact]
    public void Quote_wraps_PriceOf_and_carries_the_free_stock_it_was_given()
    {
        var tuning = Tuning(50, 500);
        var quote = RespecPolicy.Quote(tuning, effectiveCount: 2, freeStock: 3);
        Assert.Equal(RespecPolicy.PriceOf(tuning, 2), quote.Souls);
        Assert.Equal(3, quote.FreeStock);
        Assert.True(quote.FreeAvailable);
    }

    [Fact]
    public void Quote_zero_free_stock_reads_FreeAvailable_false()
    {
        var quote = RespecPolicy.Quote(Tuning(), effectiveCount: 0, freeStock: 0);
        Assert.False(quote.FreeAvailable);
    }

    [Fact]
    public void Quote_negative_free_stock_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RespecPolicy.Quote(Tuning(), 0, -1));
    }

    // ---- IsRespec (EP1.6 test 1) ---------------------------------------------------------------

    [Fact]
    public void IsRespec_additions_only_is_false()
    {
        var current = AptitudeAllocation.Single(AllocationScope.UniqueCreature, "Might", 10);
        var proposed = current + AptitudeAllocation.Single(AllocationScope.UniqueCreature, "Vigor", 5);
        Assert.False(RespecPolicy.IsRespec(AllocationScope.UniqueCreature, current, proposed));
    }

    [Fact]
    public void IsRespec_a_single_decrease_is_true()
    {
        var current = AptitudeAllocation.Single(AllocationScope.UniqueCreature, "Might", 10);
        var proposed = AptitudeAllocation.Single(AllocationScope.UniqueCreature, "Might", 9);
        Assert.True(RespecPolicy.IsRespec(AllocationScope.UniqueCreature, current, proposed));
    }

    [Fact]
    public void IsRespec_empty_to_anything_is_false_a_first_allocation_is_free()
    {
        var proposed = AptitudeAllocation.Single(AllocationScope.UniqueCreature, "Might", 1000);
        Assert.False(RespecPolicy.IsRespec(AllocationScope.UniqueCreature, AptitudeAllocation.Empty, proposed));
    }

    [Fact]
    public void IsRespec_nonEmpty_to_empty_is_true_clearing_is_a_respec()
    {
        var current = AptitudeAllocation.Single(AllocationScope.UniqueCreature, "Might", 10);
        Assert.True(RespecPolicy.IsRespec(AllocationScope.UniqueCreature, current, AptitudeAllocation.Empty));
    }

    [Fact]
    public void IsRespec_only_looks_at_the_given_scope()
    {
        // A decrease in a DIFFERENT scope must not read as a respec of this one.
        var current = AptitudeAllocation.Single(AllocationScope.Commander, "Might", 10);
        var proposed = AptitudeAllocation.Single(AllocationScope.Commander, "Might", 9)
            + AptitudeAllocation.Single(AllocationScope.UniqueCreature, "Might", 5);
        Assert.False(RespecPolicy.IsRespec(AllocationScope.UniqueCreature, current, proposed));
        Assert.True(RespecPolicy.IsRespec(AllocationScope.Commander, current, proposed));
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(10, 0, 10, 5)]
    [InlineData(10, 5, 3, 5)]
    [InlineData(1000, 0, 999, 1)]
    public void IsRespec_property_true_exactly_when_some_aptitude_decreases(
        long mightBefore, long vigorBefore, long mightAfter, long vigorAfter)
    {
        var current = AptitudeAllocation.Single(AllocationScope.UniqueCreature, "Might", mightBefore)
            + AptitudeAllocation.Single(AllocationScope.UniqueCreature, "Vigor", vigorBefore);
        var proposed = AptitudeAllocation.Single(AllocationScope.UniqueCreature, "Might", mightAfter)
            + AptitudeAllocation.Single(AllocationScope.UniqueCreature, "Vigor", vigorAfter);

        var expected = mightAfter < mightBefore || vigorAfter < vigorBefore;
        Assert.Equal(expected, RespecPolicy.IsRespec(AllocationScope.UniqueCreature, current, proposed));
    }

    [Fact]
    public void IsRespec_nullArguments_throw()
    {
        Assert.Throws<ArgumentNullException>(
            () => RespecPolicy.IsRespec(AllocationScope.UniqueCreature, null!, AptitudeAllocation.Empty));
        Assert.Throws<ArgumentNullException>(
            () => RespecPolicy.IsRespec(AllocationScope.UniqueCreature, AptitudeAllocation.Empty, null!));
    }
}
