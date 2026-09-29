using FusionRpg.Core.Dungeon.Registry;
using FusionRpg.Core.Dungeon.Tuning;

namespace FusionRpg.Core.Delve.Difficulty;

/// <summary>
/// Reads the ten difficulty rungs — id, ordinal (from <see cref="DifficultyRungCatalog"/>) and
/// every column (from <see cref="DungeonTuningHub"/>'s already-validated
/// <see cref="DifficultyRungTuning"/>). <b>Not a second owner:</b> R8's neighbour-difference rule
/// and the identity-row check already ran once, at tuning-load time
/// (<c>DungeonTuningLoader.ValidateRungNeighbours</c>, D1.3) — this module reads the validated
/// result rather than re-validating it, so there is exactly one place that check can fail.
/// </summary>
public static class RungTable
{
    /// <summary>All ten rungs, ordinal order (`very-easy` first, `impossible` last).</summary>
    public static IReadOnlyList<(string RungId, DifficultyRungTuning Def)> All()
    {
        var ordered = DifficultyRungCatalog.All.OrderBy(r => r.Ordinal).Select(r => r.RungId).ToList();
        return ordered.Select(id => (id, Get(id))).ToList();
    }

    public static DifficultyRungTuning Get(string rungId) =>
        DungeonTuningHub.Tuning.Rungs.TryGetValue(rungId, out var def)
            ? def
            : throw new ArgumentException($"Unknown difficulty rung id '{rungId}'.");

    public static int OrdinalOf(string rungId) => DifficultyRungCatalog.Get(rungId).Ordinal;

    /// <summary>The rung one ordinal above <paramref name="rungId"/>, or null past `impossible`
    /// (the tail is <see cref="TailLadder"/>'s, not a rung).</summary>
    public static string? NextRungId(string rungId)
    {
        var ordinal = OrdinalOf(rungId);
        return DifficultyRungCatalog.All.FirstOrDefault(r => r.Ordinal == ordinal + 1)?.RungId;
    }
}

/// <summary>§2's structural rules, checked again HERE as a guard-test surface distinct from the
/// tuning loader's own copy — same rule, same failure mode, asserted from this module's own
/// tests too (spec-difficulty-ladder.md Testing strategy: "Validator red/green").</summary>
public static class RungValidator
{
    public static void ValidateContiguousOrdinals()
    {
        var ordinals = DifficultyRungCatalog.All.Select(r => r.Ordinal).OrderBy(o => o).ToList();
        for (var i = 0; i < ordinals.Count; i++)
            if (ordinals[i] != i + 1)
                throw new InvalidOperationException($"Difficulty rungs must be ordinals 1..{ordinals.Count} contiguous.");
    }

    /// <summary>`hard` (rung 4) is the identity row — every *MultMilli 1000, every delta 0
    /// (spec-difficulty-ladder.md §2).</summary>
    public static void ValidateHardIsIdentity()
    {
        var hard = RungTable.Get("hard");
        if (hard.BandDelta != 0) throw new InvalidOperationException("'hard' must have bandDelta 0 (the identity row).");
        if (hard.EliteWeightMultMilli != 1000 || hard.RestWeightMultMilli != 1000 || hard.HungerMultMilli != 1000
            || hard.SpiritDrainMultMilli != 1000 || hard.MerchantMarkupMultMilli != 1000)
            throw new InvalidOperationException("'hard' must have every *MultMilli at 1000 (the identity row).");
        if (hard.EnemyCountDeltaFight != 0 || hard.EnemyCountDeltaElite != 0)
            throw new InvalidOperationException("'hard' must have enemyCountDelta 0 (the identity row).");
    }
}
