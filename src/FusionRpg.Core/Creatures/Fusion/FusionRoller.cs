using FusionRpg.Core.Battle;

namespace FusionRpg.Core.Creatures.Fusion;

public sealed record FusionRollResult(string Variant, IReadOnlyList<string> TraitIds);

/// <summary>
/// Pure fusion rolls (spec-creature-fusion.md, locks 4+5): the output species is the recipe's —
/// only traits and variant roll, from dedicated `fusion:*` streams so replays reproduce the
/// outcome bit-for-bit. Pick-one is player agency; the rest comes from the combined INPUT pool
/// (an exhausted pool yields fewer traits, never padding from elsewhere).
/// </summary>
public static class FusionRoller
{
    // The summon altar's constant, referenced — the two shiny odds can never drift apart.
    static int ShinyDie => SummonRoller.ShinyOneIn;

    public static FusionRollResult Roll(
        CreatureSpeciesDef resultSpecies, CreatureRarity resultRarity,
        string pickedTraitId, IReadOnlyList<string> combinedInputTraits, ulong seed)
    {
        if (!CreatureTraitCatalog.IsKnown(pickedTraitId))
            throw new ArgumentException($"Unknown trait id '{pickedTraitId}'.");
        if (!combinedInputTraits.Contains(pickedTraitId, StringComparer.Ordinal))
            throw new ArgumentException($"Picked trait '{pickedTraitId}' is not on any input creature.");

        var traitRng = SeededRng.DeriveStream(seed, "fusion:traits");
        var want = SlotsFor(resultRarity);
        var traits = new List<string>(want) { pickedTraitId };
        var pool = combinedInputTraits
            .Distinct(StringComparer.Ordinal)
            .Where(t => t != pickedTraitId)
            .OrderBy(t => t, StringComparer.Ordinal) // stable pool order — rolls are seed-only
            .ToList();
        while (traits.Count < want && pool.Count > 0)
        {
            var i = traitRng.NextInt(pool.Count);
            traits.Add(pool[i]);
            pool.RemoveAt(i);
        }

        var variantRng = SeededRng.DeriveStream(seed, "fusion:variant");
        // Gate on the species' own variant list (SummonRoller does the same) — the generator
        // happens to give everyone `shiny`, but that is a generator habit, not a catalog contract.
        var variant = variantRng.NextInt(ShinyDie) == 0
                      && resultSpecies.Variants.Contains("shiny", StringComparer.Ordinal)
            ? "shiny"
            : "normal";
        return new FusionRollResult(variant, traits);
    }

    /// <summary>Promotion trait growth (lock 7): existing traits KEPT in order; only the new
    /// slots roll, from the SPECIES pool (the creature grows into its kind, not its fuel).</summary>
    public static IReadOnlyList<string> RollPromotionTraits(
        CreatureSpeciesDef species, IReadOnlyList<string> existing, int newSlotCount, ulong seed)
    {
        if (existing.Count >= newSlotCount)
            return existing;

        var rng = SeededRng.DeriveStream(seed, "fusion:promotion");
        var traits = existing.ToList();
        var pool = species.TraitPool
            .Where(t => !traits.Contains(t, StringComparer.Ordinal))
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();
        while (traits.Count < newSlotCount && pool.Count > 0)
        {
            var i = rng.NextInt(pool.Count);
            traits.Add(pool[i]);
            pool.RemoveAt(i);
        }

        return traits;
    }

    /// <summary>Trait slot count by rarity — tuning-driven (`fusion.v1.json`'s `slotsByRarity`),
    /// not a hardcoded switch (this repo's own magic-number rule: a balance pass would want to
    /// change this per rung, especially now that ten rungs exist instead of four). Moved off the
    /// old bare `Common=>1, Rare/Epic=>2, _=>3` switch during `seed-to-concrete` T4.1 — that
    /// wildcard arm would have silently caught every one of the six new intermediate rungs.</summary>
    public static int SlotsFor(CreatureRarity rarity) => StarPolicy.Tuning.SlotsByRarity[rarity];
}
