namespace FusionRpg.Core.Commanders;

/// <summary>
/// The human empire's own commanders, exposed through <c>/api/commanders</c> — today the player's one;
/// Zomboss is world/AI, never listed here (commander-surface ideal §4). Data-backed through
/// <see cref="ICommanderDirectory"/>: which commander leads the human empire and what it is called are
/// registry facts, not a `switch` here.
/// </summary>
public static class PlayerEmpireCommanders
{
    /// <summary>
    /// The human empire's default commander. One entry while one registry row carries the human empire;
    /// `empire-progression`'s `commander-roster` widens the roster per <c>EmpireRef</c>.
    /// </summary>
    public static IReadOnlyList<CommanderRef> ForPlayer(ICommanderDirectory directory, long playerId)
    {
        if (directory is null) throw new ArgumentNullException(nameof(directory));
        return playerId > 0
            ? new[] { directory.DefaultFor(EmpireId.Dave) }
            : Array.Empty<CommanderRef>();
    }

    /// <summary>
    /// Only the human empire's own commander may be the player's default. The comparison is the empire
    /// (spec-commander-identity's hand-off: this site reads <c>EmpireId.Dave</c> until `save-identity`
    /// re-types it to `HumanEmpireOf(save)`).
    /// </summary>
    public static bool IsPlayerDefaultAllowed(ICommanderDirectory directory, CommanderRef commander)
    {
        if (directory is null) throw new ArgumentNullException(nameof(directory));
        return directory.EmpireOf(commander) == EmpireId.Dave;
    }
}
