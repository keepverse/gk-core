using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// E2 acceptance (spec-value-spec-and-curve.md). Every row of that spec's testing table has a case
/// here — the roll policies, the stream determinism, and the inclusive-bounds guarantee.
/// </summary>
public class ValueSpecTests
{
    const ulong Seed = 0xC0FFEE;

    [Fact]
    public void Fixed_with_a_range_is_rejected()
    {
        // "fixed" means one number. A spread would silently resolve to Min forever.
        var r = new ValueSpec(10, 20, RollPolicy.Fixed).Validate();

        Assert.Equal(AtomRejectionReason.BadValueSpec, r.Reason);
    }

    [Fact]
    public void Min_above_max_is_rejected()
    {
        Assert.Equal(AtomRejectionReason.BadValueSpec,
            new ValueSpec(20, 10, RollPolicy.OnApply).Validate().Reason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(-3)]
    public void Fixed_resolves_to_its_number_without_a_stream(int value)
    {
        var spec = ValueSpec.Of(value);

        Assert.True(spec.Validate().IsOk);
        Assert.Equal(value, spec.Resolve(null));
    }

    [Fact]
    public void OnInstantiate_with_the_same_roll_seed_reads_identically()
    {
        // Moment 2: the item drops and freezes. Re-reading it must reproduce it exactly.
        var spec = new ValueSpec(1, 1000, RollPolicy.OnInstantiate);

        var first = spec.Resolve(new AtomRandom(instanceRollSeed: 4242));
        var second = spec.Resolve(new AtomRandom(instanceRollSeed: 4242));

        Assert.Equal(first, second);
    }

    [Fact]
    public void OnInstantiate_across_seeds_reaches_both_inclusive_ends()
    {
        // A half-open range would never produce Max. Assert both ends are actually reachable.
        var spec = new ValueSpec(1, 4, RollPolicy.OnInstantiate);
        var seen = new HashSet<int>();

        for (ulong seed = 0; seed < 400; seed++)
            seen.Add(spec.Resolve(new AtomRandom(seed)));

        Assert.Equal(new[] { 1, 2, 3, 4 }, seen.OrderBy(v => v));
    }

    [Fact]
    public void OnApply_stays_inside_inclusive_bounds_over_a_thousand_rolls()
    {
        var spec = ValueSpec.Range(100, 200);
        var rng = new AtomRandom(Seed, AtomStreams.Apply);

        var values = Enumerable.Range(0, 1000).Select(_ => spec.Resolve(rng)).ToList();

        Assert.All(values, v => Assert.InRange(v, 100, 200));
        Assert.Contains(100, values);
        Assert.Contains(200, values);
    }

    [Fact]
    public void OnApply_is_reproducible_for_a_fixed_seed()
    {
        var spec = ValueSpec.Range(1, 1_000_000);

        var a = Roll(new AtomRandom(Seed, AtomStreams.Apply));
        var b = Roll(new AtomRandom(Seed, AtomStreams.Apply));

        Assert.Equal(a, b);
        return;

        List<int> Roll(IAtomRandom rng) =>
            Enumerable.Range(0, 50).Select(_ => spec.Resolve(rng)).ToList();
    }

    [Fact]
    public void Two_atoms_rolling_in_one_hit_consume_the_stream_in_a_defined_order()
    {
        // The whole point of a shared named stream: draw order is part of the contract, so a content
        // change that shifts it is visible in a golden rather than silent.
        var first = ValueSpec.Range(1, 1000);
        var second = ValueSpec.Range(1, 1000);

        var rng = new AtomRandom(Seed, AtomStreams.Apply);
        var a1 = first.Resolve(rng);
        var a2 = second.Resolve(rng);

        var swapped = new AtomRandom(Seed, AtomStreams.Apply);
        var b1 = second.Resolve(swapped);
        var b2 = first.Resolve(swapped);

        // Same stream, same positions: order determines which atom gets which draw.
        Assert.Equal(a1, b1);
        Assert.Equal(a2, b2);
    }

    [Fact]
    public void Named_streams_are_independent()
    {
        // An extra roll in one system must never shift another's sequence.
        var spec = ValueSpec.Range(1, 1_000_000);

        var apply = spec.Resolve(new AtomRandom(Seed, AtomStreams.Apply));
        var proc = spec.Resolve(new AtomRandom(Seed, AtomStreams.Proc));

        Assert.NotEqual(apply, proc);
    }

    [Fact]
    public void A_rolling_spec_without_a_stream_throws_rather_than_defaulting()
    {
        // Silently resolving to Min is the class of no-op this whole layer exists to refuse.
        Assert.Throws<InvalidOperationException>(() => ValueSpec.Range(5, 9).Resolve(null));
    }

    [Fact]
    public void A_curve_scales_both_bounds_before_the_roll()
    {
        // Scaling after the roll would let a value land outside the scaled range.
        var scaled = ValueSpec.Range(100, 200).Scaled(1500);

        Assert.Equal(150, scaled.Min);
        Assert.Equal(300, scaled.Max);

        var rng = new AtomRandom(Seed, AtomStreams.Apply);
        for (var i = 0; i < 200; i++)
            Assert.InRange(scaled.Resolve(rng), 150, 300);
    }

    [Fact]
    public void Resolving_allocates_nothing()
    {
        var spec = ValueSpec.Range(1, 1000);
        var rng = new AtomRandom(Seed, AtomStreams.Apply);

        for (var i = 0; i < 1000; i++) spec.Resolve(rng); // JIT + warm

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100_000; i++) spec.Resolve(rng);
        var after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, after - before);
    }
}
