using System;
using System.Collections.Generic;
using System.Linq;
using FusionRpg.Contracts;
using FusionRpg.Core.World.Turn;

namespace FusionRpg.Core.World.Notify;

/// <summary>The classifier's output for one entry — everything <c>WorldReportNotificationSource</c>
/// (world-notify-source §1, blocked on A2) needs to build a draft, except the resolved recipient
/// save(s), which is <see cref="WorldRecipientRule"/>'s own separate resolution step.</summary>
public readonly record struct WorldNotificationClassification(
    string Category,
    NotifySeverity Severity,
    WorldRecipientRule Rule,
    /// <summary>R-N5's repeat-window key: <c>faction:{id}</c> / <c>legion:{entityId}</c> /
    /// <c>sector:{id}</c> / <c>command:{id}</c>, or <c>null</c> when the entry names none of those
    /// (never suppressed — a subject-less draft is never throttled, `notify-service` §2).</summary>
    string? SubjectKey);

/// <summary>world-notify-source spec §2 — a closed table, pure and Core (no I/O, no persistence).
/// Maps one <see cref="TurnReportEntry"/> to a <see cref="WorldNotificationClassification"/> or to
/// <c>null</c>. An unmapped entry stays in the turn report and the playback panel; it is never a
/// notification. Adding a row here means adding one row here plus that category's catalog row —
/// never the other way around (a catalog row with no classifier row can never actually fire).
///
/// Deliberately excludes the release forecast's own <c>loam.release</c> row (spec §2's table lists
/// it alongside these for readability, but it is never classified from an entry — it comes from
/// <c>LoamForecast.WillRelease</c> directly, a separate producer <c>WorldReportNotificationSource</c>
/// calls once per component) and <c>claim.lost:</c> (waits on cross-program ask A3 — no producer
/// line exists yet, so there is nothing to classify).</summary>
public static class WorldTurnNotificationClassifier
{
    public static WorldNotificationClassification? Classify(TurnReportEntry entry)
    {
        // Kind-based rows match regardless of Detail — checked before the Event/Detail-prefix table.
        if (entry.Kind == TurnReportKinds.CommandDropped)
            return new WorldNotificationClassification("command.dropped", NotifySeverity.Routine,
                WorldRecipientRule.SubjectCommand, CommandKey(entry.Subject));

        if (entry.Kind == TurnReportKinds.Battle)
            return new WorldNotificationClassification("battle.result", NotifySeverity.Routine,
                WorldRecipientRule.FogVisible, entry.SectorId is { } battleSector ? SectorKey(battleSector) : null);

        // calendar / command.accepted stay report-only; cache-retrieval outcomes (Event-kind,
        // post-Step) fall through the prefix table below with no matching row — both paths return
        // null without a special case.
        if (entry.Kind != TurnReportKinds.Event) return null;

        WorldNotifyRow? best = null;
        foreach (var row in Rows)
        {
            if (!entry.Detail.StartsWith(row.Prefix, StringComparison.Ordinal)) continue;
            // Longest prefix wins — defensive against a future row whose prefix is itself a prefix
            // of an existing one (none of today's rows actually overlap: "loam.shortfall:" and
            // "loam.shortfall.unresolved:" differ at their 15th character, ':' vs '.', so neither
            // ever matches the other's Detail).
            if (best is null || row.Prefix.Length > best.Value.Prefix.Length) best = row;
        }
        if (best is not { } matched) return null;

        return new WorldNotificationClassification(matched.Category, matched.Severity, matched.Rule, matched.SubjectKeyOf(entry));
    }

    /// <summary>Every category this table can ever produce, plus the two Kind-based rows
    /// (`command.dropped`, `battle.result`) — the world-notify-source catalog coherence guard's
    /// join set (spec §4/Testing 9). Deliberately excludes `loam.release`: that category is never
    /// classified from an entry (it comes from the release forecast, a separate producer), so the
    /// coherence guard treats it as the one documented exception rather than asserting it here.
    /// Computed per access (not a field initializer) so it is never at the mercy of this class's
    /// own static-field declaration order relative to <see cref="Rows"/>.</summary>
    public static IReadOnlySet<string> KnownCategories =>
        new HashSet<string>(Rows.Select(r => r.Category).Append("command.dropped").Append("battle.result"), StringComparer.Ordinal);

    static string CommandKey(string commandId) => $"command:{commandId}";
    static string SectorKey(string sectorId) => $"sector:{sectorId}";
    static string FactionKey(string factionId) => $"faction:{factionId}";
    static string LegionKey(string entityId) => $"legion:{entityId}";

    /// <summary>Falls back to the faction key when a row's usual sector is absent — matches the real
    /// producer shapes, where `SectorId` is populated for every classified row that carries one, but
    /// the type itself is nullable.</summary>
    static string SubjectOrSectorKey(TurnReportEntry entry) => entry.SectorId is { } s ? SectorKey(s) : FactionKey(entry.Subject);

    readonly record struct WorldNotifyRow(string Prefix, string Category, NotifySeverity Severity, WorldRecipientRule Rule, Func<TurnReportEntry, string?> SubjectKeyOf);

    // One row per mapped producer line; the table IS the spec (world-notify-source §2). SubjectKey
    // picks the id that identifies the real-world THING this notification is about (R-N5) — the
    // faction/legion/sector/command whose repeat within the window should suppress the next one —
    // which is not always the same field the recipient RULE reads (e.g. `legion.topup:`'s recipient
    // is the Audience faction, but its subject key is still that faction, since Subject IS the
    // faction id there; `supply.restored`'s Subject is instead the legion entity id).
    static readonly WorldNotifyRow[] Rows =
    {
        // LoamPhases.cs
        new("loam.shortfall.unresolved:", "loam.shortfall", NotifySeverity.Important, WorldRecipientRule.SubjectFaction, e => FactionKey(e.Subject)),
        new("loam.shortfall:",             "loam.shortfall", NotifySeverity.Important, WorldRecipientRule.SubjectFaction, e => FactionKey(e.Subject)),
        new("loam.lost:",                  "territory.lost", NotifySeverity.Important, WorldRecipientRule.SubjectFaction, SubjectOrSectorKey),
        // MovementPhase.cs
        new("legion.runway:",              "legion.runway",  NotifySeverity.Important, WorldRecipientRule.Audience,       e => LegionKey(e.Subject)),
        // LegionSupply.cs
        new("legion.topup:",               "supply.change",  NotifySeverity.Routine,   WorldRecipientRule.Audience,       e => FactionKey(e.Subject)),
        new("supply.restored",             "supply.change",  NotifySeverity.Routine,   WorldRecipientRule.Audience,       e => LegionKey(e.Subject)),
        // SupplyGraph.cs — both carry the cut-off sector; keying by sector (not faction) so two
        // sectors cut off the same turn get two independent repeat windows.
        new("supply.besieged:",            "supply.change",  NotifySeverity.Routine,   WorldRecipientRule.SubjectFaction, SubjectOrSectorKey),
        new("supply.cut:",                 "supply.change",  NotifySeverity.Routine,   WorldRecipientRule.SubjectFaction, SubjectOrSectorKey),
        // GrowthPhases.cs
        new("growth.pulse:",               "growth",         NotifySeverity.Routine,   WorldRecipientRule.SectorOwner,    e => e.SectorId is { } s ? SectorKey(s) : null),
        new("develop.completed:",          "growth",         NotifySeverity.Routine,   WorldRecipientRule.SectorOwner,    e => e.SectorId is { } s ? SectorKey(s) : null),
        new("development.raised:",         "growth",         NotifySeverity.Routine,   WorldRecipientRule.SectorOwner,    e => e.SectorId is { } s ? SectorKey(s) : null),
        // BuildResolver.cs — Subject is the command id (`command.CommandId`), never an entity.
        new("build.started:",              "growth",         NotifySeverity.Routine,   WorldRecipientRule.SubjectCommand, e => CommandKey(e.Subject)),
        // TurnEngine.cs
        new("intel.new:",                  "intel.new",      NotifySeverity.Routine,   WorldRecipientRule.SubjectFaction, e => FactionKey(e.Subject))
    };
}
