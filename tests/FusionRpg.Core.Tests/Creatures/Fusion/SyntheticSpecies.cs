using FusionRpg.Core.Creatures;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Tests.Creatures.Fusion;

/// <summary>Minimal, hand-built <see cref="CreatureSpeciesDef"/> fixtures for tests that need a small,
/// human-checkable roster instead of the real ~829-species corpus (<see cref="RealCorpusFixture"/>)
/// — shared by `FusionRecipeDistributionIndexTests` (T8.1) and `FusionRecipeReconcileTests`
/// (T8.3) so the same handful of required-but-irrelevant fields (`Side`, `GameTypeId`, ...) are
/// filled in exactly once.</summary>
internal static class SyntheticSpecies
{
    public static CreatureSpeciesDef Make(
        string id, CreatureRarity rarity, CreatureAcquisition acquisition, int creatureTypeId,
        ElementTypeId elementPrimary = ElementTypeId.Fire, CreatureRank? rank = null) => new()
    {
        SpeciesId = id,
        Name = id,
        Side = "plant",
        GameTypeId = creatureTypeId,
        CreatureTypeId = creatureTypeId,
        ElementPrimary = elementPrimary,
        BaseRarity = rarity,
        Acquisition = acquisition,
        // spec-species-rank.md §6's gate tests need a species' rank as a first-class fixture field;
        // null (the default) is the skipped case, which every gate maps to the bottom rung.
        Rank = rank,
    };
}
