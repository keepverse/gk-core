using FusionRpg.Core.Stats.Aptitudes;

namespace FusionRpg.Core.Battle.Ai;

/// <summary>
/// species-build-todo.md T4.4 — spec-zomboss-adaptive.md, read in full this session. Everything this
/// selector needs about the past, supplied by the caller — the selector itself is pure and holds no
/// state of its own (no store, no clock, no I/O). <see cref="PlayerDominantPosture"/> is
/// <see cref="DominantPosture.Of"/> over the player's own most-recent resolved allocation — computing
/// that is the CALLER's job (it needs a real allocation, which is I/O-adjacent); a tie or an empty
/// allocation reads as <c>null</c>, in which case the counter-bias trigger simply has nothing to aim
/// at and the rotation falls back to the unbiased weighted pick.
/// </summary>
public sealed record ZombossHistory(
    string CurrentPatternId,
    int LastLevel,
    int EncountersSinceLastRepattern,
    int PlayerWinStreak,
    Posture? PlayerDominantPosture);

/// <summary>
/// The pure selector spec-zomboss-adaptive.md's own project-structure section calls out by name:
/// `(history, level, seed, tuning) → patternId`. Two triggers — a Zomboss level-up, and a player win
/// streak against him (spec: "after the player wins `loseStreakThreshold` consecutive encounters," the
/// Zomboss's own lose streak) — both rate-limited to at most one re-pattern per
/// `repatternCooldownEncounters`, checked FIRST so it binds regardless of how many triggers fire in the
/// same call (spec's own ⛔: "the rate limit is not optional").
/// </summary>
public static class ZombossPatternSelector
{
    /// <summary>Posture X is countered by the posture whose BREAKS aptitude defeats X's own defenses
    /// (spec's own already-documented counter-cycle, re-stated here rather than re-derived: "Onslaught
    /// breaks Bulwark+Retribution, so FORCE counters BASTION; Pierce breaks Fortitude+Vigor, so FINESSE
    /// counters FORCE; Precision+Ferocity break Agility+Composure, so BASTION counters FINESSE"). Each
    /// entry names the two (guard/aggro) <see cref="ZombossPatterns"/> ids that invest in the
    /// countering posture's OWN breaks aptitude — the pattern ids' own naming convention already
    /// encodes this (e.g. `finesse-defence-force-breaks-*` invests in Onslaught, a FORCE aptitude, so
    /// it is what counters BASTION).</summary>
    static readonly IReadOnlyDictionary<Posture, IReadOnlyList<string>> CountersFor =
        new Dictionary<Posture, IReadOnlyList<string>>
        {
            [Posture.Bastion] = new[] { "finesse-defence-force-breaks-guard", "finesse-defence-force-breaks-aggro" },
            [Posture.Force] = new[] { "bastion-defence-finesse-breaks-guard", "bastion-defence-finesse-breaks-aggro" },
            [Posture.Finesse] = new[] { "force-defence-bastion-breaks-guard", "force-defence-bastion-breaks-aggro" },
        };

    /// <summary>Same inputs, always the same output — never a live roll. The RNG stream is derived from
    /// EVERY input the pick can depend on (spec test 1's own "the same (history, level, seed) always
    /// yields the same pattern"), not just `(seed, level)` alone, since the counter-bias roll and the
    /// weighted pick both need their own independent-but-reproducible draws.</summary>
    public static string SelectNext(ZombossHistory history, int level, ulong seed, ZombossAdaptiveTuning tuning)
    {
        if (history is null) throw new ArgumentNullException(nameof(history));
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));
        if (!ZombossPatterns.IsKnown(history.CurrentPatternId))
            throw new ArgumentException($"'{history.CurrentPatternId}' is not a known Zomboss pattern.", nameof(history));

        // The rate limit binds FIRST and unconditionally -- checked before either trigger, so it caps
        // adaptation speed regardless of how many triggers fire in the same call (spec's own ⛔).
        if (history.EncountersSinceLastRepattern < tuning.RepatternCooldownEncounters)
            return history.CurrentPatternId;

        var leveledUp = level > history.LastLevel;
        var loseStreak = history.PlayerWinStreak >= tuning.LoseStreakThreshold;
        if (!leveledUp && !loseStreak)
            return history.CurrentPatternId;

        var rng = SeededRng.DeriveStream(seed,
            $"zomboss-repattern:{level}:{history.EncountersSinceLastRepattern}:{history.PlayerWinStreak}");

        // Counter-bias is a WEIGHT, not a guarantee (spec's own ⛔ against the Mario Kart failure mode):
        // it raises the odds of landing on a countering pattern, it never forces the pick. Only the
        // lose-streak trigger carries a bias -- a level-up alone rotates through the unbiased weighted
        // pool below, counter or not.
        if (loseStreak && history.PlayerDominantPosture is { } posture && CountersFor.TryGetValue(posture, out var counters))
        {
            if (rng.NextPerMille() < tuning.CounterBiasPermille)
                return counters[rng.NextInt(counters.Count)];
        }

        return WeightedPick(rng, ZombossPatterns.All, tuning.RotationWeights);
    }

    static string WeightedPick(SeededRng rng, IReadOnlyList<string> candidates, IReadOnlyDictionary<string, long> weights)
    {
        long total = 0;
        foreach (var id in candidates) total += weights[id];

        var roll = rng.NextInt((int)total);
        long cumulative = 0;
        foreach (var id in candidates)
        {
            cumulative += weights[id];
            if (roll < cumulative) return id;
        }
        return candidates[^1]; // unreachable while every candidate carries a positive weight
    }
}
