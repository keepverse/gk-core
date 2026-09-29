using FusionRpg.Core.Dungeon.Tuning;

namespace FusionRpg.Core.Delve.Difficulty;

/// <summary>
/// Unlocking is by clearing (spec-difficulty-ladder.md §4, R4). Rungs
/// <c>1…domain.maxRungWithoutOath</c> are offered freely; rung <c>r+1</c> is offered once a clear
/// at <c>r</c> exists for <c>(playerId, domainId)</c>, for every <c>r ≥ maxRungWithoutOath</c> — the
/// tail follows the same rule. <b>The Oath unlocks nothing</b> (R4): it is a commit-time flag on
/// rungs below the permadeath gate, recorded on the clear, read by nobody's unlock logic.
/// </summary>
public static class OathUnlock
{
    public static bool IsFreelyOffered(DungeonTuning dungeon, string rungId) =>
        RungTable.OrdinalOf(rungId) <= RungTable.OrdinalOf(dungeon.Domain.MaxRungWithoutOath);

    /// <summary>Whether `rungId` is offered given the set of rung ids this (player, domain) has
    /// already cleared.</summary>
    public static bool IsRungOffered(DungeonTuning dungeon, string rungId, IReadOnlySet<string> clearedRungIds)
    {
        if (IsFreelyOffered(dungeon, rungId)) return true;

        var ordinal = RungTable.OrdinalOf(rungId);
        var previous = RungTable.All().FirstOrDefault(r => RungTable.OrdinalOf(r.RungId) == ordinal - 1);
        return previous != default && clearedRungIds.Contains(previous.RungId);
    }

    /// <summary>Tail step `n` needs a clear at `n-1` (or, for `n == 1`, a clear at rung 10 itself —
    /// "abyss +1 needs a clear at rung 10").</summary>
    public static bool IsTailStepOffered(int n, bool rung10Cleared, IReadOnlySet<int> clearedTailSteps)
    {
        if (n < 1) return false;
        return n == 1 ? rung10Cleared : clearedTailSteps.Contains(n - 1);
    }

    /// <summary>Records a clear. `oath` is true only when the player opted into permadeath on a
    /// rung below the gate (PermadeathGate.Applies would otherwise be false) — recorded for the
    /// first-clear key, never read by <see cref="IsRungOffered"/>.</summary>
    public static ClearRecord RecordClear(string? rungId, int? tailN, bool oath) => new(rungId, tailN, oath);
}

/// <summary>One first-clear record for `(playerId, domainId)` — persistence is `domain-catalog`'s
/// (`rpg_domain_progress`), this is the in-memory shape callers pass around.</summary>
public sealed record ClearRecord(string? RungId, int? TailN, bool Oath);
