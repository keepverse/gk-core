using FusionRpg.Core.Battle;

namespace FusionRpg.Core.Delve.Encounter;

/// <summary>
/// `encounter-generator` D2.3 (spec-encounter-generator.md §4) — 1-D rank on `SideIndex`. There is no
/// geometry: `PositionOf(actorKey) =&gt; null` with no board (`BattleRunState.cs:459-461, :474`); what
/// exists is `SideIndex` — "position within its own side (adjacency)" — which <c>Encounter.Build</c>
/// (D2.2) already fixes to emit order. This module is the runtime READ MODEL over that fixed order:
/// a reach mask and a default-pick rule for whichever intent source targets across it (R9 — never
/// this module's own intent source). When `siege-board` lands, rank collapses into column; nothing
/// authored here is lost.
/// </summary>
public static class RankOrder
{
    /// <summary>Reach → contiguous target mask over the defender's own ranks (0-based, matching
    /// `SideIndex`): `melee` → rank 0 only; `short` → 0-1; `long`/`siege` → every rank. Clamped to
    /// however many ranks the defender's own side actually has — a `short` reach against a lone
    /// defender is just rank 0, not an out-of-range rank 1.</summary>
    public static IReadOnlySet<int> ReachMask(EncounterReach reach, int rankCount)
    {
        if (rankCount <= 0) throw new ArgumentOutOfRangeException(nameof(rankCount), "rankCount must be >= 1.");

        var upTo = reach switch
        {
            EncounterReach.Melee => 1,
            EncounterReach.Short => 2,
            EncounterReach.Long => rankCount,
            EncounterReach.Siege => rankCount,
            _ => throw new ArgumentOutOfRangeException(nameof(reach)),
        };

        var mask = new HashSet<int>();
        for (var r = 0; r < Math.Min(upTo, rankCount); r++) mask.Add(r);
        return mask;
    }

    /// <summary>
    /// The default pick within <paramref name="mask"/>, over <paramref name="ranks"/> (the defender's
    /// side, already in rank order — index == `SideIndex`): `frontline` lowest rank; `backline`
    /// highest; `swarm` smallest `MaxHp`; `elite` largest `MaxHp`; `structure` the first
    /// <see cref="CombatantKind.Structure"/> in the mask, else `frontline`; `indiscriminate` a uniform
    /// draw on <paramref name="stream"/> (required only for this one case — every other preference is
    /// a pure function of <paramref name="ranks"/> and needs no roll at all).
    /// </summary>
    public static int DefaultPick(TargetPreference preference, IReadOnlySet<int> mask, IReadOnlyList<BattleActorSetup> ranks, SeededRng? stream = null)
    {
        if (mask is null) throw new ArgumentNullException(nameof(mask));
        if (ranks is null) throw new ArgumentNullException(nameof(ranks));
        if (mask.Count == 0) throw new ArgumentException("mask must be non-empty.", nameof(mask));

        var eligible = mask.Where(r => r >= 0 && r < ranks.Count).OrderBy(r => r).ToList();
        if (eligible.Count == 0)
            throw new ArgumentException("no masked rank falls within the defender's own rank count.", nameof(mask));

        switch (preference)
        {
            case TargetPreference.Frontline:
                return eligible[0];

            case TargetPreference.Backline:
                return eligible[^1];

            case TargetPreference.Swarm:
            {
                var best = eligible[0];
                foreach (var r in eligible)
                    if (ranks[r].MaxHp < ranks[best].MaxHp) best = r;
                return best;
            }

            case TargetPreference.Elite:
            {
                var best = eligible[0];
                foreach (var r in eligible)
                    if (ranks[r].MaxHp > ranks[best].MaxHp) best = r;
                return best;
            }

            case TargetPreference.Structure:
                foreach (var r in eligible)
                    if (ranks[r].Kind == CombatantKind.Structure) return r;
                return eligible[0]; // else frontline

            case TargetPreference.Indiscriminate:
                if (stream is null) throw new ArgumentNullException(nameof(stream), "indiscriminate needs a stream to draw on.");
                return eligible[stream.NextInt(eligible.Count)];

            default:
                throw new ArgumentOutOfRangeException(nameof(preference));
        }
    }

    /// <summary>Written only for the `boss` role, from `formation.boss.rankSpan` — span `k` at rank
    /// `i` occupies `[i, i+k-1]`, the field/golden/engine-read `delve-battle-profile` (D2.9) already
    /// owns. This is the one write site: `Encounter.Build` calls it for the boss setup only, every
    /// other emitted setup keeps `RankSpan` null (span 1).</summary>
    public static BattleActorSetup WithRankSpan(BattleActorSetup boss, int rankSpan)
    {
        if (boss is null) throw new ArgumentNullException(nameof(boss));
        if (rankSpan < 1) throw new ArgumentOutOfRangeException(nameof(rankSpan), "a rank span must occupy at least 1 rank.");
        return boss with { RankSpan = rankSpan };
    }
}
