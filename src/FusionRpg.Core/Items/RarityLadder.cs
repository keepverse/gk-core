using FusionRpg.Core.Creatures;

namespace FusionRpg.Core.Items;

/// <summary>
/// The ten-rung ladder's **structural** facts (item-ideal.md, `rarity-bands`, module 7) — the ones
/// that are logic, not a balance number. The rows themselves (id, ordinal, tier window, prefix/suffix
/// floor) are content, seeded from `gk-data/packs/fusion/data/seed/rarity/ladder.v1.json` through the existing
/// `AtomSeedFile.ReadRarity` → `RpgStore` import pipeline, exactly like every other seeded table —
/// this class does not duplicate that data. The balance-surface numbers (drop weight, enhancement
/// cap, power-ceiling share) live in `gk-core/data/tuning/item-rarity.v1.json` and are read by
/// <see cref="ItemRarityTuning"/>, never hardcoded here — a balance pass must be able to change them
/// with a file save, not a rebuild (tunables-ssot.md).
/// </summary>
public static class RarityLadder
{
    /// <summary>Append-only, ordinal order — the order `ssot-rarity.md` §3.3 publishes.
    /// DERIVED from <see cref="CreatureRarityLadder.All"/> through <see cref="CreatureRarityIds.ToId"/>
    /// (combat-math-dedup Task 11, D15): the creature ladder's enum is the one declaration of the ten
    /// rung ids, so a rung cannot be added there and missed here.
    /// `RarityLadderSeedAgreementTests` still pins this sequence value-for-value against
    /// `gk-data/packs/fusion/data/seed/rarity/ladder.v1.json`, so the derivation cannot drift from the seed import's own
    /// source either. The negative guard against a re-introduced literal is
    /// <c>ItemRarityLadderTests.The_rung_ids_are_declared_once</c>.</summary>
    public static IReadOnlyList<string> RungIds { get; } =
        CreatureRarityLadder.All.Select(r => r.ToId()).ToArray();

    /// <summary>D7's own registered rule: no rung is drop-only. All ten promote from a lower rung.</summary>
    public static int PromoteFrom(string rarityId) => 1;

    /// <summary>
    /// The ladder's own width, DERIVED from the ids (species-gear-chain T25), so a rung that ships
    /// without this moving is impossible — the same shape `CreatureRarityLadder` uses, kept as its own
    /// member because the item ladder's ordinals are its own (10/20-ordinal rows, not the creature
    /// enum's).
    /// </summary>
    public static int RungCount => RungIds.Count;

    /// <summary>Position on the ladder, or -1 for an id it does not carry. Ordinal comparison —
    /// rung ids are a closed vocabulary the code owns.</summary>
    public static int RungIndexOf(string rarityId)
    {
        for (var i = 0; i < RungIds.Count; i++)
            if (string.Equals(RungIds[i], rarityId, StringComparison.Ordinal)) return i;
        return -1;
    }

    /// <summary>`almanac` today. Promotion REFUSES cleanly at the top rather than reaching
    /// <see cref="OneRungAbove"/>'s throw — a caller's own rule, stated at the caller.</summary>
    public static bool IsTopRung(string rarityId) =>
        RungIndexOf(rarityId) == RungCount - 1;

    /// <summary>
    /// The next rung up. Throws rather than clamping: the ladder's top is an absolute bound derived
    /// from the ids, and a promotion past it has no successor to name (AGENTS.md — an absolute bound
    /// throws, never silently deviates). A caller that can reach the top must refuse earlier.
    /// </summary>
    public static string OneRungAbove(string rarityId)
    {
        var index = RungIndexOf(rarityId);
        if (index < 0)
            throw new ArgumentOutOfRangeException(nameof(rarityId), rarityId, "not a rung id");

        if (index == RungCount - 1)
            throw new InvalidOperationException($"'{rarityId}' is the top rung — there is no rung above it");

        return RungIds[index + 1];
    }

    /// <summary>§3.8: the two guarded rungs, mirroring the summon precedent's two counters. `almanac`
    /// is deliberately unguarded — D7 lifted rule 7, so it is reachable by promotion, and its
    /// deterministic source is module 11's to register. The two ids are read from the creature enum's
    /// own <see cref="CreatureRarityIds.ToId"/> rather than re-listed (Task 11, D15).</summary>
    public static bool IsPityGuarded(string rarityId) =>
        string.Equals(rarityId, CreatureRarity.Heirloom.ToId(), StringComparison.Ordinal)
        || string.Equals(rarityId, CreatureRarity.Sunwoven.ToId(), StringComparison.Ordinal);
}
