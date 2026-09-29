using FusionRpg.Contracts;
using FusionRpg.Core.Notify;
using FusionRpg.Core.Saves;
using FusionRpg.Core.World;
using FusionRpg.Core.World.Intel;
using FusionRpg.Core.World.Loam;
using FusionRpg.Core.World.Notify;
using FusionRpg.Core.World.Turn;
using FusionRpg.Data;

namespace FusionRpg.Server.Notifications;

/// <summary>
/// world-notify-source spec §1-§2 — the world's first consumer of the notification pipeline. Pure
/// over the persisted turn report plus one read-only forecast; never writes, never pushes (the
/// pump/publisher own that).
///
/// <para><b>Two inputs, both already durable.</b> The report for the resolved turn
/// (<c>ctx.Report</c>, read from the stored <c>report_json</c> by the pump — never a replay, so the
/// entry indexes its dedup keys are built from are stable) and <c>LoamForecast.WillRelease</c>, the
/// same call the state projection makes, so the warning and the event cannot disagree.</para>
///
/// <para><b>Who is told is not who may see.</b> The classifier names a recipient <i>rule</i>
/// (<see cref="WorldRecipientRule"/>) and this class resolves it to a save through
/// <see cref="IWorldFactionSaves"/>. An enemy watching my shortfall sector sees the line in the turn
/// report and gets no notification for it; a battle line reaches exactly the factions
/// <see cref="WorldReportVisibility"/> already shows it to, because that is the same function.</para>
/// </summary>
public sealed class WorldReportNotificationSource : IWorldTurnNotificationSource
{
    public string SourceId => "world-notify-source";

    /// <summary>The producer-side names the catalogue must declare: a draft's message key is refused at
    /// publish time unless its category's row declares it (notify-vocabulary §1). Named as public consts so
    /// the catalogue join (`NotificationProducerJoinTests`) reads the code instead of a literal in a test.
    /// </summary>
    public const string TurnEntryMessageKey = "world.turn-entry";
    public const string ReleaseMessageKey = "world.release-forecast";
    public const string ReleaseCategory = "loam.release";
    const string EntryArgName = "entry";
    const string SectorArgName = "sector";

    readonly RpgStore _store;
    readonly IWorldFactionSaves _saves;

    public WorldReportNotificationSource(RpgStore store, IWorldFactionSaves saves)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _saves = saves ?? throw new ArgumentNullException(nameof(saves));
    }

    public IEnumerable<AddressedDraft> Collect(WorldTurnNotificationContext ctx)
    {
        var drafts = new List<AddressedDraft>();

        // One believed view per faction, reused across this turn's entries: the fog rule asks about
        // the whole turn's ground, and building the view per entry would re-derive every belief.
        var believed = new Dictionary<string, BelievedWorldView>(StringComparer.Ordinal);
        IReadOnlyList<WorldCommand>? commands = null;

        // A trimmed turn carries Report = null (the pump never replays: notify-service §3 step 4),
        // so there is nothing to classify and no store read to make.
        if (ctx.Report is not null)
            AddReportDrafts(ctx, believed, ref commands, drafts);

        // A forecast is about the NEXT turn, so it is only true for the turn that just resolved.
        // A boot run catching up over R-2..R therefore ends with only R carrying `loam.release`.
        if (ctx.IsLatestResolved)
            AddForecastDrafts(ctx, drafts);

        return drafts;
    }

    void AddReportDrafts(
        WorldTurnNotificationContext ctx,
        Dictionary<string, BelievedWorldView> believed,
        ref IReadOnlyList<WorldCommand>? commands,
        List<AddressedDraft> into)
    {
        var entries = ctx.Report!.Entries;

        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (WorldTurnNotificationClassifier.Classify(entry) is not { } classified) continue;

            // R-N5: the entry's own position in the PERSISTED report. Stable across re-runs, and
            // never recomputed from a replayed list (which drops post-Step lines and renumbers).
            var draft = new NotificationDraft(
                DedupKey: $"world:{ctx.WorldId}:t{ctx.ResolvedTurn}:e{index}",
                CategoryId: classified.Category,
                Severity: classified.Severity,
                SourceId: SourceId,
                MessageKey: TurnEntryMessageKey,
                Args: EntryArgs(entry),
                SubjectKey: classified.SubjectKey,
                WorldId: ctx.WorldId,
                WorldTurn: ctx.ResolvedTurn);

            foreach (var factionId in Recipients(entry, classified.Rule, ctx, believed, ref commands))
            {
                var faction = Faction(ctx.CurrentWorld, factionId);
                if (faction is null) continue; // a rule that named a faction this world does not have
                if (_saves.SaveOf(faction, ctx.Header) is not { } save) continue; // AI: told nothing
                into.Add(new AddressedDraft(save, draft));
            }
        }
    }

    void AddForecastDrafts(WorldTurnNotificationContext ctx, List<AddressedDraft> into)
    {
        foreach (var faction in ctx.CurrentWorld.Factions)
        {
            if (faction.Kind != WorldFactionKind.Player) continue;
            if (_saves.SaveOf(faction, ctx.Header) is not { } save) continue;

            foreach (var component in TerritoryComponents.For(ctx.CurrentWorld, faction.FactionId))
            {
                // No pending `cede` order is knowable here: orders are filed against the OPEN turn,
                // and this run is about a turn that already resolved. The player's own /state panel
                // reads a live one; the forecast that was already sent was true when it was sent.
                var sectorId = LoamForecast.WillRelease(ctx.CurrentWorld, component, ceded: null);
                if (sectorId is null) continue;

                into.Add(new AddressedDraft(save, new NotificationDraft(
                    DedupKey: $"world:{ctx.WorldId}:t{ctx.ResolvedTurn}:release:{sectorId}",
                    CategoryId: ReleaseCategory,
                    Severity: NotifySeverity.Important,
                    SourceId: SourceId,
                    MessageKey: ReleaseMessageKey,
                    Args: Args.Ref(SectorArgName, NotifyRefKind.Sector, sectorId).Build(),
                    // R-N5: the notification is about that sector, so a still-releasing sector inside
                    // the repeat window is one story, not one per turn.
                    SubjectKey: $"sector:{sectorId}",
                    WorldId: ctx.WorldId,
                    WorldTurn: ctx.ResolvedTurn)));
            }
        }
    }

    /// <summary>Resolves the classifier's rule to the factions it names. Duplicates are the caller's
    /// problem only in the sense that they would be the same save and the same dedup key; the store's
    /// ledger is what makes a repeated draft a no-op (notify-store §2).</summary>
    IReadOnlyList<string> Recipients(
        TurnReportEntry entry,
        WorldRecipientRule rule,
        WorldTurnNotificationContext ctx,
        Dictionary<string, BelievedWorldView> believed,
        ref IReadOnlyList<WorldCommand>? commands)
    {
        switch (rule)
        {
            case WorldRecipientRule.Audience:
                // The line says whose it is; nobody named it if Audience is absent, and the row's own
                // producers always set it.
                return entry.Audience is { Length: > 0 } audience ? new[] { audience } : Array.Empty<string>();

            case WorldRecipientRule.SubjectFaction:
                // The producer put the faction id in Subject (loam.shortfall, supply.cut/besieged,
                // intel.new, loam.lost). This is the rule that keeps an enemy's glimpse of MY
                // shortfall from becoming their notification.
                return entry.Subject.Length > 0 ? new[] { entry.Subject } : Array.Empty<string>();

            case WorldRecipientRule.SubjectCommand:
                // The turn GET's own "your own orders always" rule: the commander who filed the order.
                commands ??= _store.ListWorldCommands(ctx.WorldId, ctx.ResolvedTurn);
                foreach (var command in commands)
                    if (string.Equals(command.CommandId, entry.Subject, StringComparison.Ordinal))
                        return new[] { command.CommanderId };
                return Array.Empty<string>();

            case WorldRecipientRule.SectorOwner:
                if (entry.SectorId is not { } sectorId) return Array.Empty<string>();
                var sector = Sector(ctx.CurrentWorld, sectorId);
                return sector?.OwnerFactionId is { Length: > 0 } owner ? new[] { owner } : Array.Empty<string>();

            case WorldRecipientRule.FogVisible:
                var visible = new List<string>();
                foreach (var faction in ctx.CurrentWorld.Factions)
                {
                    // "every human faction" (spec §2): an AI faction that happens to watch the same
                    // ground is not a recipient, and its own line arrives through a rule about IT.
                    if (faction.Kind != WorldFactionKind.Player) continue;

                    if (!believed.TryGetValue(faction.FactionId, out var view))
                    {
                        view = new BelievedWorldView(ctx.CurrentWorld, faction.FactionId);
                        believed[faction.FactionId] = view;
                    }
                    if (WorldReportVisibility.VisibleTo(entry, faction.FactionId, view))
                        visible.Add(faction.FactionId);
                }
                return visible;

            default:
                return Array.Empty<string>();
        }
    }

    /// <summary>
    /// The draft's arguments (§3). The `entry` argument is a `domainToken` whose value is the entry
    /// itself as a plain JSON object — `{kind, subject, detail, sectorId}` — which is the wire
    /// convention the web world translator owns (`worldTranslator.ts`): it is the ONE reader of that
    /// token, and it words the line through the same `playbackTable.ts` the playback panel uses. A
    /// `sector` ref rides along whenever the entry names ground, and the world mount uses it as the
    /// item's target.
    /// </summary>
    static IReadOnlyList<NotifyArg> EntryArgs(TurnReportEntry entry)
    {
        // Lowercase anonymous field names on purpose: the publisher serializes with no camelCase
        // policy, so a named record's PascalCase properties would never match the web's read.
        var token = new
        {
            kind = entry.Kind,
            subject = entry.Subject,
            detail = entry.Detail,
            sectorId = entry.SectorId
        };

        var args = Args.DomainToken(EntryArgName, token);
        return entry.SectorId is { } sectorId
            ? args.Ref(SectorArgName, NotifyRefKind.Sector, sectorId).Build()
            : args.Build();
    }

    static WorldFaction? Faction(WorldState world, string factionId)
    {
        if (factionId.Length == 0) return null;
        foreach (var faction in world.Factions)
            if (string.Equals(faction.FactionId, factionId, StringComparison.Ordinal)) return faction;
        return null;
    }

    static WorldSector? Sector(WorldState world, string sectorId)
    {
        foreach (var sector in world.Sectors)
            if (string.Equals(sector.SectorId, sectorId, StringComparison.Ordinal)) return sector;
        return null;
    }
}
