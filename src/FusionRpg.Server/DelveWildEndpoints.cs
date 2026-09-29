using FusionRpg.Contracts;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Data;

namespace FusionRpg.Server;

/// <summary>
/// D4.8 (spec-wild-room.md §2, §6, §7) — the wild-room HTTP surface: `POST …/rooms/{id}/talk`,
/// `…/pray`, `…/cage`. `{id}` is the persisted room's own `sectorId`; row/column, room kind, cage
/// marker, party location/ownership, candidate species, traits, sink key, and offer price are never
/// accepted from the join body.
///
/// <para><b>Join authority.</b> `talk`/`cage` retain their request DTO's old `Spec`, `Price`, and
/// `SinkKey` fields only for wire compatibility. They are deliberately ignored. The Data-layer
/// transaction resolves those values again from the sealed delve seed, persisted room archetype and
/// coordinates, persisted world parties, species catalog, dungeon/summoning tuning, and power tuning,
/// then spends and mints in one transaction. `pray` remains the already-shipped altar-pull path.</para>
///
/// <para><b>Steering semantics.</b> The request's <c>PartyEntityId</c> is only the selector being
/// requested. The canonical authority is <c>rpg_delves.steering_json</c>, read through
/// <see cref="RpgStore.ValidateDelveSteering"/> by the precheck and repeated inside the Data write
/// transaction. The persisted raid mode supplies the party bound; the persisted world supplies the
/// player-owned warband and its current room. A missing, malformed, stale, closed, or non-selected
/// record refuses by a named reason.</para>
///
/// <para><b>Room depth.</b> The join price reads the persisted `DelveRow.ThetaRun` watermark because
/// this schema has no per-room theta column. The join body has no theta override. `pray` retains its
/// pre-existing caller-supplied `ThetaRoom` seam; changing that unrelated altar surface is outside
/// this task.</para>
/// </summary>
public static class DelveWildEndpoints
{
    public static void MapDelveWild(this WebApplication app)
    {
        var g = app.MapGroup("/api/delve");

        g.MapPost("/rooms/{id}/talk", (string id, DelveWildJoinRequest body, RpgStore store) =>
            HandleJoin(id, body, store));
        g.MapPost("/rooms/{id}/cage", (string id, DelveWildJoinRequest body, RpgStore store) =>
            HandleCage(id, body, store));
        g.MapPost("/rooms/{id}/pray", (string id, DelveWildPrayRequest body, RpgStore store) =>
            HandlePray(id, body, store));
    }

    /// <summary>
    /// Shared player/delve/room resolution both routes need — extracted so the two handlers
    /// below hold only their own action-specific logic, mirroring `DelveEndpoints.cs`'s own
    /// extracted-handler idiom. The steering check is a Data-owned read of the canonical persisted
    /// record, never a caller-supplied predicate; the Data transaction repeats it before writing.</summary>
    static IResult? ValidateRoom(
        string sectorId, long? bodyPlayerId, long delveId, long partyEntityId,
        RpgStore store,
        out DelveRoomRow? room, out long playerId, bool join = false, bool cage = false)
    {
        room = null;
        playerId = bodyPlayerId ?? store.GetCurrentPlayerId();
        if (!store.PlayerExists(playerId)) return Results.NotFound(new { reason = "player.unknown" });

        var delve = store.LoadDelve(delveId);
        if (delve is null || delve.PlayerId != playerId) return Results.NotFound(new { reason = "delve.not-found" });

        var found = store.LoadDelveRooms(delveId).FirstOrDefault(r => string.Equals(r.SectorId, sectorId, StringComparison.Ordinal));
        if (found is null) return Results.NotFound(new { reason = "room.not-found" });
        room = found;

        if (join)
        {
            var effectiveKind = found.ResolvedKind ?? found.Kind;
            if (!string.Equals(effectiveKind, "wild", StringComparison.Ordinal) &&
                !string.Equals(effectiveKind, "cage", StringComparison.Ordinal))
                return Results.BadRequest(new { reason = "wild.room-kind" });
            if (cage && !string.Equals(effectiveKind, "cage", StringComparison.Ordinal))
                return Results.BadRequest(new { reason = "wild.not-cage" });
            if (!cage && string.Equals(effectiveKind, "cage", StringComparison.Ordinal))
                return Results.BadRequest(new { reason = "wild.cage-route" });
        }

        var steering = store.ValidateDelveSteering(delveId, partyEntityId, sectorId, playerId);
        if (!steering.Ok) return Results.Conflict(new { reason = steering.Reason });

        return null;
    }

    // ---- talk / cage: server-resolved join transaction (see this file's own doc comment) --

    internal static IResult HandleJoin(
        string sectorId, DelveWildJoinRequest body, RpgStore store, bool cage = false)
    {
        if (body is null) return Results.BadRequest(new { reason = "body.missing" });
        var precheck = ValidateRoom(
            sectorId, body.PlayerId, body.DelveId, body.PartyEntityId, store,
            out _, out var playerId, join: true, cage: cage);
        if (precheck is not null) return precheck;

        // Spec, price, and sinkKey remain wire fields for compatibility, but none is an input to
        // this transaction. RpgStore re-resolves them from the persisted room, delve seed, world
        // ownership, and tuning after this HTTP precheck.
        var (ok, reason, specimen, soulsUnbanked) = store.TalkJoin(
            body.DelveId, playerId, body.PartyEntityId, sectorId, cage);
        if (!ok) return Refusal(reason);
        return Results.Ok(new { specimen, soulsUnbanked });
    }

    internal static IResult HandleCage(string sectorId, DelveWildJoinRequest body, RpgStore store) =>
        HandleJoin(sectorId, body, store, cage: true);

    // ---- pray: the full §6 altar-pull transaction, real end to end ----------------------------------

    internal static IResult HandlePray(string sectorId, DelveWildPrayRequest body, RpgStore store)
    {
        if (body is null) return Results.BadRequest(new { reason = "body.missing" });
        var precheck = ValidateRoom(sectorId, body.PlayerId, body.DelveId, body.PartyEntityId, store, out var room, out var playerId);
        if (precheck is not null) return precheck;

        if (string.IsNullOrWhiteSpace(body.AltarBannerId)) return Results.BadRequest(new { reason = "altarBannerId.missing" });
        ElementTypeId? focus = null;
        if (!string.IsNullOrWhiteSpace(body.FocusElementId))
        {
            if (!ElementRoster.TryParse(body.FocusElementId, out var parsed)) return Results.BadRequest(new { reason = "focusElement.unknown" });
            focus = parsed;
        }

        var (ok, reason, result, soulsUnbanked) = store.PullAtAltar(
            body.DelveId, body.PartyEntityId, room!.RowIndex, room.ColIndex, body.ThetaRoom, body.AltarBannerId!, focus,
            room.SectorId);
        if (!ok) return Refusal(reason);
        return Results.Ok(new { result, soulsUnbanked });
    }

    /// <summary>Same mapping shape `DelveEndpoints.Refusal`/`ContractEndpoints.Refusal` already use:
    /// a price the player cannot meet is a conflict, not malformed input; a missing row is a 404;
    /// everything else (a named content refusal like `altar.banner-unknown`) is a 400.</summary>
    static IResult Refusal(string reason) => reason switch
    {
        "delve.souls-insufficient" => Results.Conflict(new { reason }),
        "delve.not-found" or "player.unknown" or "player.not-found" => Results.NotFound(new { reason }),
        _ => Results.BadRequest(new { reason }),
    };

    public sealed class DelveWildJoinRequest
    {
        public long? PlayerId { get; set; }
        public long DelveId { get; set; }

        /// <summary>
        /// Zero-based party selector requested by the caller. It is compared with the canonical
        /// persisted steering record; the Data transaction maps it to the persisted player-owned
        /// warband and checks its current room, so this field is never an ownership or steering fact.
        /// </summary>
        public long PartyEntityId { get; set; }

        /// <summary>Compatibility field. The server ignores it and charges its own OfferFloor.</summary>
        public long Price { get; set; }

        /// <summary>Compatibility field. The server derives the sink from the persisted coordinates.</summary>
        public string? SinkKey { get; set; }

        /// <summary>Compatibility field. The server ignores it and resolves/mints its own candidate.</summary>
        public CreatureMintSpec? Spec { get; set; }
    }

    public sealed class DelveWildPrayRequest
    {
        public long? PlayerId { get; set; }
        public long DelveId { get; set; }
        public long PartyEntityId { get; set; }
        public int ThetaRoom { get; set; }
        public string? AltarBannerId { get; set; }
        public string? FocusElementId { get; set; }
    }
}
