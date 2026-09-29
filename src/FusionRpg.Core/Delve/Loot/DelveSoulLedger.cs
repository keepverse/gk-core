using FusionRpg.Core.Battle;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Power;

namespace FusionRpg.Core.Delve.Loot;

/// <summary>The two amounts a delve owes at extraction — kills already accrued room by room, plus the
/// once-only victory term. `Kills` here is a pre-summed total (already accrued to
/// `rpg_delves.souls_unbanked` room by room, per spec's own literal `AtExtraction`), never re-derived.</summary>
public sealed record ExtractionEarn(long Kills, long Victory, int ThetaRun);

/// <summary>
/// `dungeon-loot` D3.12 (spec-dungeon-loot.md §2, "Soul earn — two reads, no new curve") — souls
/// through the exact same `SoulEarnPolicy` every other kill/victory reads, never a private delve curve.
/// A pure Core-layer computation: the actual `rpg_delves.souls_unbanked` accrual and the once-only
/// `CloseDelve(Extracted)` write are `RpgStore.Delve.cs`'s own job (D3.16), matching the
/// `ExtractionSettlement.Decide` → `RpgStore.Delve.SettleExtractionUnlocked` split D2.21/22 already
/// established for the identical "Core decides, Data writes" shape.
/// </summary>
public static class DelveSoulLedger
{
    /// <summary>
    /// Spec's own Testing strategy, verbatim: "Kills exclude withdrawn: one captured enemy pays
    /// nothing; the sum is `Σ KillEarn(setup.Level)` over `Survived == false &amp;&amp; !Retreated`."
    /// `Retreated` is the SAME field a captured/withdrawn enemy already sets elsewhere in this
    /// codebase — this formula reuses it, not a delve-invented "withdrawn" flag. A downed-but-alive
    /// enemy (`Survived: true`) or a captured one (`Retreated: true`) earns nothing either way.
    /// </summary>
    public static long RoomKillEarn(IReadOnlyList<(BattleActorSetup Setup, BattleActorResult Result)> enemies, PowerTuning tuning)
    {
        if (enemies is null) throw new ArgumentNullException(nameof(enemies));
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));

        long total = 0;
        foreach (var (setup, result) in enemies)
            if (!result.Survived && !result.Retreated)
                total = checked(total + SoulEarnPolicy.KillEarn(setup.Level, tuning));
        return total;
    }

    /// <summary>
    /// Spec's own "Code style" section (`spec-dungeon-loot.md:314-319`), verbatim: once per delve, at
    /// `CloseDelve(Extracted)`. Kills were accrued per room (<see cref="RoomKillEarn"/>, already summed
    /// into <paramref name="soulsUnbanked"/> by the caller); the victory term reads `Θ_run` and pays
    /// ONLY when attrition's own `won` holds — a wipe forfeits it entirely, never partially.
    /// </summary>
    public static ExtractionEarn AtExtraction(long soulsUnbanked, int thetaRun, bool won, PowerTuning tuning)
    {
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));
        return new ExtractionEarn(
            Kills: soulsUnbanked,
            Victory: won ? SoulEarnPolicy.MatchEndEarn(victory: true, thetaRun, tuning) : 0L,
            ThetaRun: thetaRun);
    }
}
