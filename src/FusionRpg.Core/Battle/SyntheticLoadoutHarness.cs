using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Unlock;

namespace FusionRpg.Core.Battle;

/// <summary>
/// A20 (spec-synthetic-loadout-harness.md). Gives balance work a code-level way to compare two
/// loadouts on the same actor template across many seeds. Never touches `gk-data/packs/fusion/data/seed`, never persists
/// a loadout, never runs through a shipped golden fixture — every actor/battle it builds lives exactly
/// as long as the comparison call, the same synthetic-construction discipline the rest of this
/// codebase's balance tooling already uses.
/// </summary>
public static class SyntheticLoadoutBuilder
{
    /// <summary>
    /// One base template, one candidate loadout in, one variant out — differing from
    /// <paramref name="template"/> in `EquippedActionIds` and nothing else. A record `with` expression
    /// makes this true BY CONSTRUCTION (every other field is copied verbatim), rather than by manually
    /// listing fields that could drift out of sync with `BattleActorSetup` the next time it grows one.
    /// </summary>
    public static BattleActorSetup Vary(BattleActorSetup template, IReadOnlyList<string> loadout) =>
        template with { EquippedActionIds = loadout };
}

/// <summary>
/// A20's plain output record (spec §3: "never a rendered report, a chart, or a file write"). Raw
/// counts are carried as `long`/`int` (whole counts); the two derived means are `double` because they
/// are descriptive balance-report numbers (floating point is allowed for any quantity — no exemption
/// needed since the 2026-09-15 owner ruling).
/// </summary>
public readonly record struct LoadoutComparisonResult(
    int Runs,
    int Wins,
    long TotalRounds,
    long TotalDamageDealt)
{
    public double WinRate => Runs == 0 ? 0 : (double)Wins / Runs;
    public double MeanRounds => Runs == 0 ? 0 : (double)TotalRounds / Runs;
    public double MeanDamagePerRound => TotalRounds == 0 ? 0 : (double)TotalDamageDealt / TotalRounds;
}

/// <summary>
/// A20 §2: the seeded multi-run comparator. Runs the SAME battle N times for one loadout variant,
/// each run seeded `(baseSeed, variantIndex, runIndex)` — never an ambient draw, matching every other
/// seeded stream in this codebase. Determinism needs no extra plumbing here: `BattleRunState`'s own
/// constructor already derives EVERY internal RNG stream (`initiative`, `crit`, `essence`, `riders`,
/// ...) fresh from whatever `seed` `BattleEngine.Resolve` receives (`BattleRunState.cs:238-247`) — so
/// a distinct derived seed per `(variantIndex, runIndex)` already gives full run-to-run isolation with
/// zero shared/global RNG state, verified by reading that constructor rather than assumed.
/// </summary>
public static class LoadoutComparator
{
    /// <summary>
    /// Runs <paramref name="subject"/> (already varied by <see cref="SyntheticLoadoutBuilder.Vary"/>)
    /// against <paramref name="opponents"/> <paramref name="runs"/> times, aggregating the subject's
    /// own outcome/rounds/damage. <paramref name="variantIndex"/> is part of the seed derivation so two
    /// variants run under the SAME `baseSeed` never share a single tick of RNG draws with each other.
    ///
    /// <para>T74/A33: <paramref name="unlockStateFor"/>/<paramref name="unlockTuning"/> are optional and
    /// thread through to <see cref="BattleEngine.Resolve"/>. This is a comparison harness, so its default
    /// is the harness's own documented behaviour — every synthetic run prices at the authored rung unless
    /// a caller asks otherwise; a balance pass that wants a rung's real price supplies the pair.</para>
    /// </summary>
    public static LoadoutComparisonResult RunVariant(
        BattleActorSetup subject,
        IReadOnlyList<BattleActorSetup> opponents,
        ulong baseSeed,
        int variantIndex,
        int runs,
        ActionCatalog? actionCatalog = null,
        Func<string, UnlockState>? unlockStateFor = null,
        UnlockTuning? unlockTuning = null)
    {
        if (runs <= 0) throw new ArgumentOutOfRangeException(nameof(runs), runs, "at least one run is required");

        var wins = 0;
        long totalRounds = 0;
        long totalDamage = 0;

        var setup = new BattleSetup
        {
            WaveId = "loadout-comparison",
            Squad = new[] { subject },
            Wave = opponents,
        };

        for (var runIndex = 0; runIndex < runs; runIndex++)
        {
            // Same derivation shape every other seeded stream in this codebase already uses
            // (SeededRng.DeriveStream(runSeed, streamName)) -- here the "stream name" is the run's own
            // coordinate, and one draw off the derived stream gives a well-mixed ulong seed rather than
            // reaching into DeriveStream's private hash directly.
            var runSeed = SeededRng.DeriveStream(baseSeed, $"loadout:{variantIndex}:{runIndex}").NextULong();
            var report = BattleEngine.Resolve(setup, runSeed, actionCatalog: actionCatalog,
                unlockStateFor: unlockStateFor, unlockTuning: unlockTuning);

            if (report.Outcome == BattleOutcome.Victory) wins++;
            totalRounds += report.Rounds;

            foreach (var actor in report.Actors)
                if (actor.Key == subject.Key)
                    totalDamage += actor.DamageDealt;
        }

        return new LoadoutComparisonResult(runs, wins, totalRounds, totalDamage);
    }
}
