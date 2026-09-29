using FusionRpg.Contracts;
using FusionRpg.Core.Delve;
using FusionRpg.Core.Delve.Difficulty;
using FusionRpg.Core.Delve.Domains;
using FusionRpg.Core.Delve.Pack;
using FusionRpg.Core.Dungeon.Tuning;
using FusionRpg.Data;
using Microsoft.AspNetCore.SignalR;

namespace FusionRpg.Server;

/// <summary>
/// party-dungeon D2.23 (spec-delve-attrition.md §7) — the priced escape from `Recovering`.
///
/// <para>D4.22 (spec-domain-catalog.md §6) added `GET /domains/{playerId}` and `POST /start`. Both
/// are wired to REAL reads end to end (`RpgStore.ReadDomains`/`ReadDomainProgressForPlayer`/
/// `GetDelveStateForDomain`, D4.18/D4.19/D4.22's own new read), not stubs — but `dungeon_domain` has
/// no WRITE arm yet (D4.16, no real anchor JSON exists anywhere to validate a parser against), so it
/// is always empty in production today. That makes both endpoints CORRECT, not incomplete, for every
/// call today: `GET` returns an empty offer list (nothing to offer), `POST` refuses every domainId
/// with `domain.not-found` (nothing to find) — group 1 of <see cref="DelveStart.Run"/>'s own six
/// ordered refusal groups, which always fires FIRST. Every delegate past that point in
/// <see cref="BuildDelveStartLive"/> is consequently unreachable in production today, by
/// construction, the same "reachable today" idiom <c>CaptureAction.TryGate</c>'s own doc comment
/// already uses for the identical situation (an upstream content gap, not a code gap) — each one
/// throws naming exactly what real catalog/lookup it is standing in for, rather than a silent guess
/// that would look correct and be wrong the day real content lands.</para>
/// </summary>
public static class DelveEndpoints
{
    public static void MapDelve(this WebApplication app)
    {
        var g = app.MapGroup("/api/delve");

        g.MapPost("/recovery-ritual", async (RecoveryRitualRequest body, RpgStore store, IHubContext<RpgHub> hub) =>
        {
            var pid = body.PlayerId ?? store.GetCurrentPlayerId();
            if (!store.PlayerExists(pid)) return Results.NotFound();
            var corrError = ValidateCorrelation(body.CorrelationId);
            if (corrError != null) return corrError;

            var (ok, reason, actor) = store.TryPerformRecoveryRitual(
                pid, body.InstanceId ?? "", body.CorrelationId!, DungeonTuningHub.Tuning);
            if (!ok) return Refusal(reason);
            await NotifyAsync(hub, pid);
            return Results.Ok(new { actor });
        });

        g.MapGet("/domains/{playerId:long}", (long playerId, RpgStore store) => HandleGetDomains(playerId, store));

        g.MapPost("/start", (DelveStartHttpRequest body, RpgStore store) => HandleStart(body, store));

        // D5.2 (spec-delve-stage.md §5, §18 ask 1): the one projection endpoint delve-stage reads.
        // {delveId:long} sits at the SAME route-group depth as "domains/{playerId:long}" above --
        // ASP.NET's routing prefers the more specific literal segment ("domains") over the parameter
        // route at every matching request, so the two coexist without collision.
        g.MapGet("/{delveId:long}", (long delveId, long? playerId, RpgStore store) => HandleGetDelve(delveId, playerId, store));
    }

    /// <summary>Extracted from the route lambda so a test can call it directly without a live HTTP
    /// stack — the same reasoning that already applies to every other pure-logic function in this
    /// program; only the DI binding stays in <see cref="MapDelve"/>.</summary>
    internal static IResult HandleGetDomains(long playerId, RpgStore store)
    {
        if (!store.PlayerExists(playerId)) return Results.NotFound();

        var catalog = DomainCatalog.Load(
            store.ReadDomains().Select(r => r.Domain).ToList(),
            new Dictionary<string, int>(StringComparer.Ordinal)).Catalog;
        var progress = store.ReadDomainProgressForPlayer(playerId)
            .Select(p => new DomainProgressFact(
                p.DomainId, p.Clears.Select(c => new DomainClearFact(c.RungId, c.Oath, c.DelveId)).ToList()))
            .ToList();

        var offers = DomainOffers.For(progress, catalog, id => store.GetDelveStateForDomain(playerId, id), BuildDomainOfferLive());
        return Results.Ok(offers);
    }

    /// <summary>See <see cref="HandleGetDomains"/>'s own doc comment.</summary>
    internal static IResult HandleStart(DelveStartHttpRequest body, RpgStore store)
    {
        var pid = body.PlayerId ?? store.GetCurrentPlayerId();
        if (!store.PlayerExists(pid)) return Results.NotFound();

        var catalog = DomainCatalog.Load(
            store.ReadDomains().Select(r => r.Domain).ToList(),
            new Dictionary<string, int>(StringComparer.Ordinal)).Catalog;

        var request = new DelveStartRequest(
            body.CorrelationId ?? "", pid, body.DomainId ?? "", body.ParentWorldId,
            body.RungIdOrTailLabel ?? "", body.Oath, body.RaidMode ?? "",
            body.MemberInstanceIds ?? Array.Empty<string>(),
            (body.CarryIn ?? Array.Empty<DelveStartHttpRequest.CarryInItem>())
                .Select(i => new PackItem(i.Kind, i.RefId, i.InstanceId, i.Qty, i.W, i.H, i.GrantIndex, PackItemOrigin.CarryIn))
                .ToList());

        var (refusal, replay, _) = DelveStart.Run(request, catalog, BuildDelveStartLive(store, pid));
        if (replay is not null) return Results.Ok(new { delveId = replay.DelveId, worldId = replay.WorldId });
        if (refusal is not null) return Refusal(refusal.Rule);

        // Every DelveStartLive delegate past group 1 is provably unreachable while `catalog` is
        // empty (see this file's own doc comment) -- a non-null Plan here would mean real domain
        // content has landed, at which point group 7's own write (extending CreateDelve, D4.21's
        // own evidence) is the next task, not yet built.
        return Results.Problem("delve.start-plan-write-not-yet-wired", statusCode: 501);
    }

    /// <summary>
    /// D5.2 (spec-delve-stage.md §5, §18 ask 1) — <c>GET /api/delve/{delveId}</c>. Assembles the one
    /// revision-stamped projection <c>delve-stage</c> reads, from exactly the four sources
    /// spec-delve-scope.md:347-350 names: this method does the actual <c>LoadWorldState</c> +
    /// <c>LoadDelve</c> + <c>LoadDelveRooms</c> reads (the "two delve tables"), then hands the results
    /// to <see cref="DelveProjection.For"/> (Core-layer, store-free — it composes
    /// <c>Visibility.SeenBy</c>/<c>DelveSight.ForParty</c> and nothing else). <paramref name="playerId"/>
    /// is an optional query parameter (a GET route has no body to carry one), defaulting to
    /// <c>GetCurrentPlayerId()</c> — the same fallback <see cref="HandleStart"/>'s own body-bound
    /// <c>PlayerId</c> already uses, applied to query-string binding instead.
    /// </summary>
    internal static IResult HandleGetDelve(long delveId, long? playerId, RpgStore store)
    {
        var pid = playerId ?? store.GetCurrentPlayerId();
        if (!store.PlayerExists(pid)) return Results.NotFound();

        var delve = store.LoadDelve(delveId);
        if (delve is null) return Results.NotFound();

        var rooms = store.LoadDelveRooms(delveId);
        var world = store.LoadWorldState(delve.WorldId);
        if (world is null) return Results.NotFound(); // a delve's own world row should always exist; defensive, not expected

        var roomFacts = rooms.Select(r => new DelveRoomStateFact(
            r.SectorId, r.RowIndex, r.ColIndex, r.Kind, r.ArchetypeId, r.Visited, r.Cleared,
            r.KeyForLaneId, r.EventId, r.ResolvedKind, r.ResolvedArchetypeId, r.FloorJson, r.Revision)).ToList();
        var partyEntityIds = delve.Parties.Select(p => p.EntityId).ToList();

        var projection = DelveProjection.For(
            pid, delve.PlayerId, delve.Revision, partyEntityIds, roomFacts, world, DungeonTuningHub.Tuning);
        // Ownership mismatch reads identically to "no such delve" -- never confirms existence to the
        // wrong requester (the same posture PlayerExists/LoadDelve's own null checks above already keep).
        if (projection is null) return Results.NotFound();

        return Results.Ok(new
        {
            delveId = delve.DelveId,
            worldId = delve.WorldId,
            state = delve.State,
            domainId = delve.DomainId,
            raidMode = delve.RaidMode,
            rungId = delve.RungId,
            soulsUnbanked = delve.SoulsUnbanked,
            thetaRun = delve.ThetaRun,
            questsJson = delve.QuestsJson,
            decisionsJson = delve.DecisionsJson,
            contentTermsJson = delve.ContentTermsJson,
            parties = delve.Parties,
            rooms = projection.Rooms,
            doors = projection.Doors,
            partyPositions = projection.Parties,
            revision = projection.Revision,
        });
    }

    /// <summary>
    /// D5.2 (spec-delve-stage.md §5, §18 ask 2 — spec-delve-battle-profile.md's own structure block
    /// names the exact shape <c>DelveUpdated{delveId, revision}</c>). Mirrors the existing
    /// <see cref="NotifyAsync"/> helper's own best-effort shape.
    ///
    /// <para><b>No real production caller wires this yet, named honestly rather than guessed:</b> the
    /// natural trigger, <c>RpgStore.MarkRoom</c>, still has zero production callers (re-checked this
    /// session — only test fixtures call it). Every OTHER write that bumps `rpg_delves.revision`
    /// (`WritePartyMembers`, `AppendDecision`, `CloseDelve`, ...) lives behind routes this task's own
    /// Files line does not touch (`DelveWildEndpoints.cs`, `DelveBattleEndpoints.cs` — none of them
    /// registered from `DelveEndpoints.cs` itself), and this file's own two other routes never reach a
    /// real delve mutation either: `/recovery-ritual` never touches `rpg_delves`/`rpg_delve_rooms`, and
    /// `/start` always returns before <c>CreateDelve</c> today (D4.22's own already-recorded finding —
    /// `dungeon_domain` is empty, so group 1 refuses first). This method is real and directly callable
    /// —proven by <c>DelveProjectionEndpointTests.cs</c>'s own direct call against a real
    /// <c>IHubContext&lt;RpgHub&gt;</c> — with no live production trigger wired to it, the same
    /// "provably correct, zero production callers" posture this program already uses elsewhere
    /// (D4.22's own six delegates, D4.25's Data-layer wiring).</para>
    ///
    /// <para><b>A pre-existing name collision, found while wiring this, not fixed here:</b>
    /// <see cref="NotifyAsync"/> already sends an event literally named <c>"DelveUpdated"</c> (shape
    /// <c>{playerId}</c>) from the real, live `/recovery-ritual` route. spec-delve-battle-profile.md's
    /// own structure block names this exact event name for a DIFFERENT shape, <c>{delveId, revision}</c>
    /// — so the same SignalR event name now carries two different payload shapes depending on which
    /// call site fired it. No real consumer breaks today (a case-insensitive scan of
    /// `gk-web/web/fusion-rpg-web/src` for "delve" is still zero hits, spec-delve-stage.md §19 point 6), so
    /// nothing is silently wrong in production — but a future frontend integration must branch on
    /// payload shape, or whichever task wires a real trigger for THIS broadcast should also reconcile
    /// the two under distinct names. Renaming the recovery-ritual one is out of this task's own scope
    /// (a different, already-shipped, tested D2.23 route) and not done here.</para>
    /// </summary>
    internal static async Task NotifyDelveUpdatedAsync(IHubContext<RpgHub> hub, long delveId, long revision)
    {
        try
        {
            await hub.Clients.Group(RpgConstants.WebGroup).SendAsync("DelveUpdated", new { delveId, revision });
        }
        catch
        {
            // best-effort: the write is durable, the next read reconciles
        }
    }

    /// <summary>Every delegate <see cref="DomainOffers.For"/> needs beyond the real reads above.
    /// Unreachable in production today (see this file's own doc comment) — `dungeon_domain` never
    /// has a row for the loop that would call any of these to iterate over.</summary>
    static DomainOfferLive BuildDomainOfferLive() => new(
        KnownRungIds: Array.Empty<string>(),
        StalenessFor: _ => throw new NotImplementedException("DomainStaleness needs validated_json parsed from a real dungeon_domain row -- none exists yet (D4.16)."),
        ComposeRungs: (_, _) => throw new NotImplementedException("RungOffer.For needs PowerTuning/DungeonTuning/DomainThetaInputs/ParentWorldTerms -- ParentWorldTerms has never been built from live state anywhere (D4.21's own finding)."),
        RungLabelFor: _ => throw new NotImplementedException("No rung display-name registry exists anywhere in this codebase (D4.19's own finding)."),
        BossDisplayNameFor: _ => throw new NotImplementedException("No almanac keyed by a creature species id exists -- only PVZ's own (side, type_id) shape (D4.19's own finding)."),
        // Real, 2026-09-07: LayoutTemplateCatalog now exists (gk-data/packs/fusion/data/seed/dungeon/layouts/*.json,
        // six entries) -- an unknown layoutId returns empty (RaidModesFor's own documented
        // behavior), never throws, matching every caller's own "not offered" handling.
        RaidModesForLayout: layoutId => FusionRpg.Core.Delve.Roll.LayoutTemplateHub.Catalog.RaidModesFor(layoutId),
        ProvisionableFor: _ => throw new NotImplementedException("provisionable[] pricing is delve-stage's own not-yet-specified concern (spec-delve-stage.md §18 ask 6, Phase 5 unbuilt)."));

    /// <summary>Every delegate <see cref="DelveStart.Run"/> needs beyond the real reads above.
    /// Unreachable in production today for the identical reason (see this file's own doc comment).</summary>
    static DelveStartLive BuildDelveStartLive(RpgStore store, long playerId) => new(
        ReplayFor: corr =>
        {
            var existing = store.LoadDelveByCorrelation(playerId, corr);
            return existing is null ? null : new DelveStartReplay(existing.DelveId, existing.WorldId, existing.DomainId, existing.RaidMode, existing.RungId, Oath: false);
        },
        StalenessFor: _ => throw new NotImplementedException("see BuildDomainOfferLive's own StalenessFor."),
        DelveStateFor: id => store.GetDelveStateForDomain(playerId, id),
        ClearsFor: id => store.ReadDomainProgress(playerId, id) is { } p
            ? new PlayerClears(
                p.Clears.Select(c => c.RungId).ToHashSet(StringComparer.Ordinal),
                new HashSet<int>())
            : PlayerClears.None,
        ComposeRungs: (_, _) => throw new NotImplementedException("see BuildDomainOfferLive's own ComposeRungs."),
        RaidModesForLayout: layoutId => FusionRpg.Core.Delve.Roll.LayoutTemplateHub.Catalog.RaidModesFor(layoutId),
        PartyShapeForRaidMode: mode => DungeonTuningHub.Tuning.RaidModes.TryGetValue(mode, out var t) ? (t.Parties, t.SquadSlots) : null,
        MemberIsOwnedRosterBound: id => store.GetUniqueActor(id) is { Phase: "Roster" },
        MemberIsRecovering: id => store.GetUniqueActor(id) is { Phase: "Recovering" },
        MemberIsOnExpedition: store.HasActiveExpeditionMembership,
        MemberIsInAnotherActiveDelve: id => store.IsActorInAnyActiveDelve(playerId, id),
        ProvisionCells: 0,
        StockOf: id => store.ListStock(playerId.ToString()).FirstOrDefault(s => s.ContainerId == id)?.Qty ?? 0,
        ProvisioningPriceFor: _ => throw new NotImplementedException("DelvePrices.Provisioning needs a composed row-0 Θ -- see BuildDomainOfferLive's own ComposeRungs."),
        SoulBalanceFor: pid => store.GetSoulBalance(pid).Balance,
        ContentTermsJsonFor: (_, _) => throw new NotImplementedException("ParentWorldTerms has never been built from live state anywhere (D4.21's own finding)."),
        SealSeed: () => unchecked((ulong)Random.Shared.NextInt64()),
        RollAndPreflight: (_, _, _) => throw new NotImplementedException("DelveGraphRoll.Roll needs a DomainAnchor, a delve-graph-roll-owned type distinct from DomainRow (D4.21's own finding)."));

    public sealed class DelveStartHttpRequest
    {
        public long? PlayerId { get; set; }
        public string? CorrelationId { get; set; }
        public string? DomainId { get; set; }
        public string? ParentWorldId { get; set; }
        public string? RungIdOrTailLabel { get; set; }
        public bool Oath { get; set; }
        public string? RaidMode { get; set; }
        public IReadOnlyList<string>? MemberInstanceIds { get; set; }
        public IReadOnlyList<CarryInItem>? CarryIn { get; set; }

        public sealed class CarryInItem
        {
            public string Kind { get; set; } = "";
            public string RefId { get; set; } = "";
            public string? InstanceId { get; set; }
            public long Qty { get; set; }
            public int W { get; set; }
            public int H { get; set; }
            public int GrantIndex { get; set; }
        }
    }

    static IResult? ValidateCorrelation(string? correlationId)
    {
        if (string.IsNullOrWhiteSpace(correlationId))
            return Results.BadRequest(new { reason = "correlation.missing" });
        return correlationId.Trim().Length > 64
            ? Results.BadRequest(new { reason = "correlation.toolong" })
            : null;
    }

    /// <summary>A price the player cannot meet is a conflict, not malformed input — same mapping
    /// `ContractEndpoints.Refusal` already uses for the identical "insufficient souls" shape.</summary>
    static IResult Refusal(string reason) => reason switch
    {
        "souls.insufficient" => Results.Conflict(new { reason }),
        "not_found" or "recovery.missing" or "rung.unknown" => Results.NotFound(new { reason }),
        _ => Results.BadRequest(new { reason }),
    };

    static async Task NotifyAsync(IHubContext<RpgHub> hub, long playerId)
    {
        try
        {
            await hub.Clients.Group(RpgConstants.WebGroup).SendAsync("DelveUpdated", new { playerId });
            await hub.Clients.Group(RpgConstants.WebGroup).SendAsync("SoulsUpdated", new { playerId });
        }
        catch
        {
            // best-effort: the write is durable, the next read reconciles
        }
    }

    public sealed class RecoveryRitualRequest
    {
        public long? PlayerId { get; set; }
        public string? InstanceId { get; set; }
        public string? CorrelationId { get; set; }
    }
}
