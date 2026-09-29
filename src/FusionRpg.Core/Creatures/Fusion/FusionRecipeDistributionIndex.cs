namespace FusionRpg.Core.Creatures.Fusion;

/// <summary>
/// One rarity rung's fusion-pairing capacity: `creature-seed` module 17 (`fusion-recipe-generator`,
/// spec §1 `distribution-index`)'s own per-rung report row.
/// </summary>
public readonly record struct RungCapacity(
    CreatureRarity Rarity, int OutputCount, CreatureRarity? NearestBelow, int BelowCount, long MaxPairs)
{
    /// <summary>The exact capacity ceiling `CreatureRecipeCatalog.Build()` hits live: this rung's
    /// eligible outputs exceed how many distinct unordered pairs its own input pool can produce.</summary>
    public bool Shortfall => OutputCount > MaxPairs;

    /// <summary>How many outputs the deterministic pass alone cannot cover — zero when there is no
    /// shortfall.</summary>
    public long Deficit => Shortfall ? OutputCount - MaxPairs : 0;
}

/// <summary>
/// `fusion-recipe-generator` (creature-seed module 17) §1 `distribution-index` — deterministic, no model
/// calls. Reused directly by both the CLI (`gk-forge/tools/CreatureRecipeDistributionIndex`) and its own tests, so
/// neither can silently drift from what `Build()` itself actually does: every rung's "nearest populated
/// rung below" comes from <see cref="CreatureRecipeCatalog.NearestPopulatedRungBelow"/>, the exact search
/// `Build()` uses — never a second, parallel walk-down.
/// </summary>
public static class FusionRecipeDistributionIndex
{
    /// <summary>Requires <see cref="CreatureSpeciesCatalog"/> already configured (real or scoped) —
    /// this type owns no I/O and reads no file itself, matching every other Core policy in this
    /// program.</summary>
    public static IReadOnlyList<RungCapacity> Compute()
    {
        var rows = new List<RungCapacity>();
        foreach (var rarity in Enum.GetValues<CreatureRarity>().OrderBy(r => (int)r))
        {
            if (!CreatureRarityLadder.AtLeast(rarity, CreatureRecipeCatalog.OutputEligibilityFloor)) continue;

            var outputCount = CreatureSpeciesCatalog.All.Count(s =>
                s.BaseRarity == rarity && s.Acquisition != CreatureAcquisition.CaptureOnly);
            if (outputCount == 0) continue;

            var pool = CreatureRecipeCatalog.NearestPopulatedRungBelow(rarity);
            CreatureRarity? nearestBelow = pool.Count > 0 ? pool[0].BaseRarity : null;
            var belowCount = pool.Count;
            var maxPairs = (long)belowCount * (belowCount - 1) / 2;

            rows.Add(new RungCapacity(rarity, outputCount, nearestBelow, belowCount, maxPairs));
        }
        return rows;
    }
}
