using FusionRpg.Core.Actions.Loadout;
using FusionRpg.Core.Aura;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Data;
using FusionRpg.Server.Gates;

namespace FusionRpg.Server;

/// <summary>
/// aura-skill T15: `SetLoadout`/`GetLoadout` (`RpgStore.Loadouts.cs`) have had a real, tested backing
/// table since before this program started — `LoadoutStoreTests.cs` already proves mid-run refusal,
/// reject-leaves-existing-rows-untouched, and auto-equip fallback. What was missing was named exactly:
/// *"`SetLoadout` has no production caller; no `/api/loadout*` endpoint exists."* This is that caller —
/// a thin, player-facing surface over already-shipped, already-tested persistence, the same shape
/// `AptitudeEndpoints.cs` already established for the sibling `/api/aptitudes` surface.
///
/// <para><b>Scoped to Dave (the player's own commander) only.</b> `OwnerScope(OwnerKind.Player,
/// playerId)` is exactly correct for him — he IS the player's own commander (the same reasoning
/// `ICommanderDirectory.AllocationScopeKey` already used for his aptitude allocation, T9a). Zomboss has no
/// loadout endpoint here: `OwnerKind` is a closed, 7-value, reviewed vocabulary
/// (`OwnerScope.cs`'s own doc comment, `definitions.md §6`) with no "commander" or arbitrary-string
/// scope Zomboss could legally use, and inventing an 8th value is exactly the kind of change that
/// needs its own review, not one made mid-task here. Zomboss's own aura selection is authored data
/// (`ZombossPattern`, T9b/T17), never a player-equipped loadout — this gap is real, but it is not a
/// gap in coverage; Zomboss simply does not need this endpoint at all.</para>
///
/// <para><b>`isMidRun` is a named, honest gap, not a fake.</b> No production signal for "is this
/// player currently mid-run" exists anywhere at the Server layer today (confirmed by direct search —
/// `WebMatchService` resolves a battle synchronously and holds no session state at all). The
/// MECHANISM this endpoint threads (`LoadoutSet.Validate`'s own `isMidRun` parameter, and its
/// rejection behavior) is already fully proven at the store level
/// (`LoadoutStoreTests.MidRunRejectsAndPersistsNothing`) — what is missing is a real oracle to plug
/// into it, which is genuinely separate, undecided work (most likely tied to the world map's own
/// "campaign in progress" concept, a different system). Wired to `() => false` here rather than left
/// unimplemented, so equip requests are never spuriously refused while that oracle does not exist —
/// recorded here rather than hidden, matching this program's own T21b precedent.</para>
/// </summary>
public static class LoadoutEndpoints
{
    public static void MapLoadout(this WebApplication app)
    {
        var g = app.MapGroup("/api/loadout");

        g.MapGet("/{playerId:long}", (long playerId, RpgStore store) =>
        {
            if (!store.PlayerExists(playerId)) return Results.NotFound();
            var loadout = store.GetLoadout(DaveScope(playerId));
            return Results.Ok(new { playerId, actionIds = loadout ?? Array.Empty<string>() });
        });

        // build-preset BP1.3 (spec-gate-services.md): the held/mid-run predicates now live once in
        // ActionLoadoutService, not copied here. Constructed inline rather than taken as a DI
        // parameter -- its only dependency is `store`, already a parameter, and keeping this
        // route's own DI parameter list unchanged means LoadoutEndpointsTests.cs's isolated host
        // (which registers only RpgStore, same pattern as AptitudeEndpointsTests.cs) needs no edit.
        g.MapPost("/", (SetLoadoutRequest body, RpgStore store) =>
        {
            if (body.PlayerId is not { } playerId || !store.PlayerExists(playerId))
                return Results.NotFound();
            if (body.ActionIds is null)
                return Results.BadRequest(new { reason = "actionIds.missing" });

            var result = new ActionLoadoutService(store).Set(playerId, body.ActionIds);

            if (!result.Ok)
                return Results.Conflict(new { reason = result.Reason!.Value.ToString(), actionId = result.ActionId });

            return Results.Ok(new { playerId, actionIds = body.ActionIds });
        });
    }

    static OwnerScope DaveScope(long playerId) => new(OwnerKind.Player, playerId.ToString());

    public sealed class SetLoadoutRequest
    {
        public long? PlayerId { get; set; }
        public List<string>? ActionIds { get; set; }
    }
}
