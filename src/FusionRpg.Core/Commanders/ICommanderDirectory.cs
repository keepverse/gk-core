namespace FusionRpg.Core.Commanders;

/// <summary>
/// The one place a commander's empire, display name, allocation scope key and default are decided
/// (the D in SOLID). Every one of them comes from data, through <see cref="DataCommanderDirectory"/> —
/// there is no <c>switch</c> over <see cref="EmpireId"/> or <see cref="CommanderRef"/> anywhere in
/// <c>src/</c>. A creature that becomes a commander is a new directory row (or, later,
/// <c>empire-progression</c>'s row source backed by <c>rpg_unique_actor</c>): it extends the directory
/// and never edits it.
/// </summary>
public interface ICommanderDirectory
{
    /// <summary>Replaces <c>CommanderIds.TryParseStableId</c>. False for an unknown or blank stable id.</summary>
    bool TryResolve(string? stableId, out CommanderRef commander);

    /// <summary>Which faction this commander leads.</summary>
    EmpireId EmpireOf(CommanderRef commander);

    /// <summary>
    /// The display name for one save. A row authored <c>displayFromPlayer</c> answers with
    /// <paramref name="playerName"/> — the player's empire's default commander <b>is</b> the player
    /// (ruling 2026-09-18) — and falls back to the row's authored neutral label when the caller's name
    /// is blank, never to another person's name. The caller supplies the name it already holds: the
    /// directory decides the rule, it does not reach into a store.
    /// </summary>
    string DisplayName(CommanderRef commander, string? playerName);

    /// <summary>
    /// The <c>AllocationScope.Commander</c> pool key for this commander within one save. The one
    /// encoder of <c>"player:{id}"</c> / <c>"zomboss:{id}"</c>; <c>save-identity</c> X9 routes the three
    /// other encoders of the same string through it.
    /// </summary>
    string AllocationScopeKey(CommanderRef commander, long playerId);

    /// <summary>The default commander for an empire, or a throw when none is registered.</summary>
    CommanderRef DefaultFor(EmpireId empire);
}
