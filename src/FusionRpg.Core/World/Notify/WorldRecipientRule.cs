namespace FusionRpg.Core.World.Notify;

/// <summary>Closed (5 members) — world-notify-source spec §2. A notification is about YOU, which is
/// stricter than the fog rule (which decides what you may SEE): an enemy faction watching my
/// shortfall sector may see the line in the turn report, but it is not their <c>loam.shortfall</c>.
/// Resolving a rule to an actual save is <c>WorldReportNotificationSource</c>'s job (world-notify-source
/// §2 "Faction to save") — this classifier only names WHICH rule applies.</summary>
public enum WorldRecipientRule
{
    /// <summary>The faction named in <c>entry.Audience</c>.</summary>
    Audience,

    /// <summary>The faction the producer put in <c>entry.Subject</c>.</summary>
    SubjectFaction,

    /// <summary>The commander of the turn's command whose id is <c>entry.Subject</c> — resolved via
    /// <c>RpgStore.ListWorldCommands</c> (the turn GET's own "your own orders always" rule).</summary>
    SubjectCommand,

    /// <summary>The current owner of <c>entry.SectorId</c>.</summary>
    SectorOwner,

    /// <summary>Every human faction for which the (moved) fog rule holds — the turn-report GET's
    /// own visibility rule, reused so the two can never drift.</summary>
    FogVisible
}
