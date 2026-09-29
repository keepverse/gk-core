using FusionRpg.Core.Dungeon.Tuning;
using FusionRpg.Core.World;
using FusionRpg.Core.World.Intel;

namespace FusionRpg.Core.Delve;

/// <summary>Core's own mirror of one `rpg_delve_rooms` row (<c>FusionRpg.Data</c>'s own
/// <c>DelveRoomRow</c>) — the DAL boundary runs one way, so Core cannot reference
/// <c>FusionRpg.Data</c> directly (the identical situation <c>DomainOffers.cs</c>'s own
/// <c>DomainProgressFact</c>/<c>DomainClearFact</c> already fixed, D4.19). The caller
/// (<c>DelveEndpoints.cs</c>, which sees both layers) projects the real row into this shape field for
/// field before calling <see cref="DelveProjection.For"/>.
///
/// <para>Named <c>DelveRoomStateFact</c>, not the more obvious <c>DelveRoomFact</c> — that name is
/// already taken, one namespace over (<c>FusionRpg.Core.Delve.Roll.DelveRoomFact</c>,
/// <c>DelveGraph.cs</c>), for an unrelated roll-time shape (<c>IsSecret</c>/<c>BaseBand</c>/
/// <c>PartyRouteMask</c>, facts about a room's construction, not its persisted current state). Found
/// the hard way: an unqualified reference in this same namespace's own <c>QuestOffer.cs</c> silently
/// resolved to the wrong one and broke the build (an enclosing-namespace match beats a `using`-imported
/// one), so the two are not merely differently-named in this doc comment — the type itself had to
/// change name to stop colliding.</para>
/// </summary>
public sealed record DelveRoomStateFact(
    string SectorId, int RowIndex, int ColIndex, string Kind, string ArchetypeId,
    bool Visited, bool Cleared, string? KeyForLaneId, string? EventId, string? ResolvedKind,
    string? ResolvedArchetypeId, string FloorJson, long Revision);

/// <summary>
/// One room exactly as far as this player's own sight reaches it (spec-delve-stage.md §7's three
/// sight treatments — unlit/glimpsed/seen, <see cref="SectorSight"/>). <see cref="SectorSight.None"/>'s
/// own doc comment is the rule this record enforces: "Position and lanes only — the graph is public,
/// its contents are not." A glimpsed room additionally "names its kind only"
/// (spec-supplies-and-objects.md:362) — every other content field needs <see cref="SectorSight.Full"/>.
/// </summary>
public sealed record DelveProjectionRoom(
    string SectorId, int RowIndex, int ColIndex, bool Visited, bool Cleared, string? KeyForLaneId,
    SectorSight Sight,
    string? Kind, string? ArchetypeId, string? EventId, string? ResolvedKind, string? ResolvedArchetypeId,
    string? FloorJson);

/// <summary>A door/lane, unconditionally visible — <see cref="SectorSight.None"/>'s own "position and
/// LANES only" clause names lanes as part of the always-public graph shape, so no sight gating applies
/// here (unlike <see cref="DelveProjectionRoom"/>'s own content fields).</summary>
public sealed record DelveProjectionDoor(
    string LaneId, string FromSectorId, string ToSectorId, string TypeId, string? GateKeyId, LaneState State);

/// <summary>Where one of this player's own parties currently stands, joined from
/// <c>WorldState.Entities</c> by entity id — null/null when the party has no live position yet (no
/// entity for it exists in the world, e.g. before anything ever wires party entities into a real
/// delve's <c>WorldState</c> — see <see cref="DelveProjection"/>'s own doc comment).</summary>
public sealed record DelveProjectionPartyPosition(long EntityId, string? AtSectorId, string? OnLaneId);

public sealed record DelveProjectionResult(
    IReadOnlyList<DelveProjectionRoom> Rooms, IReadOnlyList<DelveProjectionDoor> Doors,
    IReadOnlyList<DelveProjectionPartyPosition> Parties, long Revision);

/// <summary>
/// spec-delve-stage.md D5.2 / spec-delve-scope.md:347-350 — the one Server-assembled projection
/// `delve-stage` reads, composed of exactly <c>LoadWorldState</c> + the two delve tables +
/// <see cref="World.Intel.Visibility.SeenBy"/> + <see cref="DelveSight.ForParty"/> and nothing else.
/// This is the Core-layer half of that assembly: pure, no store, no clock (matches every other
/// assembler in this program — <c>DomainOffers.For</c>, <c>DelveStart.Run</c>). The caller
/// (<c>DelveEndpoints.cs</c>) does the four named reads and passes the results in; this function does
/// not — and cannot, by construction, since it takes no store — read a fifth source.
///
/// <para><b>Faction id, an explicit convention this module establishes.</b> Nothing in this codebase
/// yet builds a delve <see cref="WorldState"/> with entities/factions wired (confirmed: zero
/// `OwnerFactionId` assignments anywhere under <c>Core/Delve/**</c>, and <c>DelveGraphRoll.Roll</c>
/// only ever produces rooms/doors, never party entities) — so there is no existing production
/// precedent to match. <c>playerId.ToString()</c> is used as the faction id, mirroring the identical
/// string-key convention already used for player-scoped reads elsewhere in <c>RpgStore</c>
/// (<c>ListStock(playerId.ToString())</c>) and matching spec-delve-scope.md:199's own text ("A cleared
/// room is written with <c>OwnerFactionId = player</c>").</para>
///
/// <para><b>Sight-band tuning, a named starting-shape gap.</b> <see cref="DelveSight.ForParty"/> needs
/// a per-room archetype "sightBand" (dim/lit/scouting) to vary a room's own reveal radius — no source
/// for that exists anywhere yet: <c>DelveRoomStateFact</c>/<c>DelveRoomRow</c> carry no such column, and
/// <c>RoomPaletteEntry</c>'s own minimal projection doesn't carry one either (confirmed absent).
/// <c>DelveGraphRoll.cs</c>'s own comment at its sight-assembly step already accepts a uniform,
/// tuning-only floor as correct for exactly this reason ("This module still emits the tuning-only base
/// radii so a caller with no archetype detail yet has a correct floor") — this function mirrors that
/// same accepted starting shape rather than inventing a different placeholder. "Scouted" per party is
/// the same story (no scout-stance flag is persisted anywhere for a delve party either) and is passed
/// as <c>false</c> uniformly for the same reason.</para>
/// </summary>
public static class DelveProjection
{
    /// <param name="requestingPlayerId">Who is asking — checked against <paramref name="delveOwnerPlayerId"/>
    /// before anything is computed, so a wrong player gets <c>null</c> (the caller maps that to
    /// <c>NotFound</c>, same shape as "no such delve" — no existence leak).</param>
    /// <param name="delveOwnerPlayerId"><c>DelveRow.PlayerId</c> — every party in one delve belongs to
    /// the same player (spec-delve-scope.md §4.8: "two parties of one player share sight").</param>
    /// <param name="delveRevision"><c>DelveRow.Revision</c> — bumped by every `rpg_delves` write
    /// (parties, quests, decisions, souls, close). Does NOT cover a room-only mutation.</param>
    /// <param name="partyEntityIds">Each party's <c>DelvePartyState.EntityId</c>, in delve-storage
    /// order.</param>
    public static DelveProjectionResult? For(
        long requestingPlayerId, long delveOwnerPlayerId, long delveRevision,
        IReadOnlyList<long> partyEntityIds, IReadOnlyList<DelveRoomStateFact> rooms,
        WorldState world, DungeonTuning tuning)
    {
        if (partyEntityIds is null) throw new ArgumentNullException(nameof(partyEntityIds));
        if (rooms is null) throw new ArgumentNullException(nameof(rooms));
        if (world is null) throw new ArgumentNullException(nameof(world));
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));

        if (delveOwnerPlayerId != requestingPlayerId) return null;

        var factionId = delveOwnerPlayerId.ToString();

        // The floor: always computed, even with zero parties (a brand-new delve's own parties_json
        // starts '[]' — DelveWildEndpoints.cs's own doc comment: "grows lazily, first room, first...").
        // This is exactly why Visibility.SeenBy is its own named composition input rather than only
        // ever reached transitively through DelveSight.ForParty (which recomputes the identical floor
        // internally) -- without it, a partyless delve would have no well-defined sight at all.
        var sight = new Dictionary<string, SectorSight>(Visibility.SeenBy(world, factionId), StringComparer.Ordinal);

        foreach (var entityId in partyEntityIds)
        {
            var overlay = DelveSight.ForParty(
                world, entityId.ToString(), factionId,
                roomSightBand: _ => UnmappedSightBand,
                sightLanes: tuning.SightLanes,
                scoutLanes: tuning.SightScoutLanes,
                extraLanesFor: _ => 0, // see this class's own doc comment: no per-archetype sightBand
                                       // is persisted anywhere yet, matching DelveGraphRoll.cs's own
                                       // accepted uniform floor for the identical reason.
                scouted: false);

            foreach (var (sectorId, level) in overlay)
                if (!sight.TryGetValue(sectorId, out var current) || level > current)
                    sight[sectorId] = level;
        }

        var roomsOut = rooms
            .Select(r => ProjectRoom(r, sight.TryGetValue(r.SectorId, out var s) ? s : SectorSight.None))
            .OrderBy(r => r.SectorId, StringComparer.Ordinal)
            .ToList();

        // Doors are never sight-gated (see DelveProjectionDoor's own doc comment) — the whole graph
        // shape, per SectorSight.None's own "position and lanes only".
        var doors = world.Lanes
            .Select(l => new DelveProjectionDoor(l.LaneId, l.FromSectorId, l.ToSectorId, l.TypeId, l.GateKeyId, l.State))
            .OrderBy(d => d.LaneId, StringComparer.Ordinal)
            .ToList();

        var parties = partyEntityIds
            .Select(id => new DelveProjectionPartyPosition(
                id,
                world.Entities.FirstOrDefault(e => e.EntityId == id.ToString())?.AtSectorId,
                world.Entities.FirstOrDefault(e => e.EntityId == id.ToString())?.OnLaneId))
            .OrderBy(p => p.EntityId)
            .ToList();

        // rpg_delve_rooms.revision bumps independently of rpg_delves.revision (MarkRoom touches only
        // the room row) -- a revision stamp that read delveRevision alone would miss a room-only
        // mutation entirely, so the projection's own stamp is the ceiling of both tables.
        var roomRevision = rooms.Count == 0 ? 0L : rooms.Max(r => r.Revision);
        var revision = Math.Max(delveRevision, roomRevision);

        return new DelveProjectionResult(roomsOut, doors, parties, revision);
    }

    static DelveProjectionRoom ProjectRoom(DelveRoomStateFact r, SectorSight sight)
    {
        var kindVisible = sight != SectorSight.None;
        var contentsVisible = sight == SectorSight.Full;
        return new DelveProjectionRoom(
            r.SectorId, r.RowIndex, r.ColIndex, r.Visited, r.Cleared, r.KeyForLaneId, sight,
            Kind: kindVisible ? r.Kind : null,
            ArchetypeId: contentsVisible ? r.ArchetypeId : null,
            EventId: contentsVisible ? r.EventId : null,
            ResolvedKind: contentsVisible ? r.ResolvedKind : null,
            ResolvedArchetypeId: contentsVisible ? r.ResolvedArchetypeId : null,
            FloorJson: contentsVisible ? r.FloorJson : null);
    }

    /// <summary>Never a real key in <see cref="DungeonTuning.SightBandExtraLanes"/> by construction —
    /// see this class's own doc comment. Named, not blank, so a debugger/log never mistakes it for an
    /// empty-string bug.</summary>
    const string UnmappedSightBand = "unmapped-starting-shape";
}
