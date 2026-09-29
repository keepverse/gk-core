namespace FusionRpg.Core.Achievements;

// Title lifecycle policy (spec-title-lifecycle.md). Pure: no I/O.
// Windows anchor at equip (equippables) or grant (honors/curses) on the scope's
// clock. Save/load converges from hashed state: remaining = anchor + valid − now.
public static class TitleLifecyclePolicy
{
    public const string WorldTurns = "world-turns";
    public const string BattleTicks = "battle-ticks";
    public const string WallClock = "wall-clock";

    /// <summary>
    /// Scope→clock table, enforced at window-record time. Empire runs on world
    /// virtual turns (determinism P13); unique-actor windows bind a NAMED
    /// battle-tick counter; wall-clock is expedition-only and refused for both
    /// current scopes (replay cannot see wall time).
    /// </summary>
    public static string? ClockRefusal(string scope, string clock, string? counterName)
    {
        if (scope == AchievementScopes.Empire)
            return clock == WorldTurns ? null
                : $"title lifecycle: empire scope runs on {WorldTurns}, not '{clock}'";
        if (scope == AchievementScopes.UniqueActor)
        {
            if (clock != BattleTicks)
                return $"title lifecycle: unique-actor scope runs on {BattleTicks}, not '{clock}'";
            return string.IsNullOrWhiteSpace(counterName)
                ? "title lifecycle: battle-ticks windows need a named counter"
                : null;
        }
        return $"title lifecycle: unknown scope '{scope}'";
    }

    /// <summary>Turns remaining on a window. Expired when remaining &lt;= 0.</summary>
    public static long Remaining(long anchorTurn, long validTurns, long currentTurn) =>
        anchorTurn + validTurns - currentTurn;
}

/// <summary>v1 curse taxonomy (closed): transgression fact kinds with a producer.
//  Only kill.innocent ships — other kinds refuse until a Cold fact source exists.</summary>
public static class CurseTransgressions
{
    public const string KillInnocent = "kill.innocent";
    public static bool IsKnown(string? v) => v is KillInnocent;
}
