using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Fusion;
using Xunit;

namespace FusionRpg.Core.Tests.Creatures;

/// <summary>F3: fusion rolls — pick-one guaranteed, rest seeded from the combined input pool,
/// deterministic per seed (correlation replays reproduce the outcome).</summary>
public class FusionRollerTests
{
    static readonly CreatureSpeciesDef Species = CreatureSpeciesCatalog.All
        .First(s => s.BaseRarity == CreatureRarity.Heirloom && s.Acquisition != CreatureAcquisition.CaptureOnly);

    static readonly string[] CombinedPool = { "swift", "guardian", "berserker", "loyal" };

    [Fact]
    public void Picked_trait_leads_and_rolls_are_deterministic()
    {
        var a = FusionRoller.Roll(Species, CreatureRarity.Heirloom, "guardian", CombinedPool, 42);
        var b = FusionRoller.Roll(Species, CreatureRarity.Heirloom, "guardian", CombinedPool, 42);

        Assert.Equal(a.TraitIds, b.TraitIds);
        Assert.Equal(a.Variant, b.Variant);
        Assert.Equal("guardian", a.TraitIds[0]);
        Assert.Equal(2, a.TraitIds.Count); // epic = 2 slots
        Assert.Equal(a.TraitIds.Count, a.TraitIds.Distinct(StringComparer.Ordinal).Count());
        Assert.All(a.TraitIds, t => Assert.Contains(t, CombinedPool));
    }

    [Fact]
    public void Different_seeds_can_diverge_and_variants_include_shiny()
    {
        var variants = new HashSet<string>(StringComparer.Ordinal);
        for (ulong seed = 0; seed < 200; seed++)
            variants.Add(FusionRoller.Roll(Species, CreatureRarity.Heirloom, "swift", CombinedPool, seed).Variant);
        Assert.Contains("normal", variants);
        Assert.Contains("shiny", variants); // 1/64 — 200 seeds make a miss astronomically unlikely
    }

    [Fact]
    public void Pick_outside_the_combined_pool_rejects()
    {
        Assert.Throws<ArgumentException>(() =>
            FusionRoller.Roll(Species, CreatureRarity.Heirloom, "immortal", CombinedPool, 1));
        Assert.Throws<ArgumentException>(() =>
            FusionRoller.Roll(Species, CreatureRarity.Heirloom, "no-such-trait", CombinedPool, 1));
    }

    [Fact]
    public void Exhausted_pools_yield_fewer_traits_never_padding()
    {
        var result = FusionRoller.Roll(Species, CreatureRarity.Sunwoven, "swift", new[] { "swift" }, 7);
        Assert.Equal(new[] { "swift" }, result.TraitIds); // 3 slots wanted, pool had 1
    }

    [Fact]
    public void Promotion_keeps_existing_traits_and_rolls_only_new_slots()
    {
        var existing = new[] { "swift", "guardian" };
        var a = FusionRoller.RollPromotionTraits(Species, existing, newSlotCount: 3, seed: 9);
        var b = FusionRoller.RollPromotionTraits(Species, existing, newSlotCount: 3, seed: 9);

        Assert.Equal(a, b);
        Assert.Equal(3, a.Count);
        Assert.Equal("swift", a[0]);
        Assert.Equal("guardian", a[1]);
        Assert.Contains(a[2], Species.TraitPool); // the new slot comes from the species pool
        Assert.Equal(a.Count, a.Distinct(StringComparer.Ordinal).Count());

        // Already at or above the slot count: unchanged.
        Assert.Equal(existing, FusionRoller.RollPromotionTraits(Species, existing, 2, 9));
    }
}
