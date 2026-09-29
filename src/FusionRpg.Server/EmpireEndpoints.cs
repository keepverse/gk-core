using FusionRpg.Core.Commanders;
using FusionRpg.Core.Progression;
using FusionRpg.Core.Saves;
using FusionRpg.Data;
using FusionRpg.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace FusionRpg.Server;

/// <summary>
/// `empire-level` EP4.7 (module 13, `empire-progression` Wave D; spec:
/// `docs/architecture/empire-progression/spec-empire-level.md` §"Contracts"). The one read an empire's
/// level has: its row, the curve's next step, and the earned free-respec stock that row has paid for so
/// far. The empire level is not an actor magnitude and never reaches Unity, so this is a REST read and
/// nothing else — the injector has no business with it.
///
/// <para>The raw row stays readable through the generic progression route
/// (<c>/api/rpg/progression/{playerId}/empire/0</c>), which needs no change: `RpgActorKinds.IsKnown`
/// accepted `empire` from EP4.1, and that route passes the kind straight through. This route is the
/// one a player-facing surface wants, because it joins the two numbers (level and stock) that live in
/// different tables.</para>
///
/// <para>`save-identity` SE4.31 adds the sibling read the same route family needed: the save's own
/// **empires as data** (<c>GET /api/players/{playerId}/empires</c>), which is what lets a consumer name
/// an empire instead of assuming the human one.</para>
/// </summary>
public static class EmpireEndpoints
{
    public static void MapEmpireRoutes(this WebApplication app)
    {
        // save-identity SE4.31 (spec-save-identity.md §Contracts): a save owns its empires as data, and
        // this is the one read of that fact. The controller token is the closed two-member vocabulary the
        // authored registry and `rpg_save_empires.controller` carry (declared once in Core), never a
        // literal list here.
        app.MapGet("/api/players/{playerId:long}/empires", (long playerId, RpgStore store) =>
        {
            if (!store.PlayerExists(playerId)) return Results.NotFound();
            var items = store.EmpiresOf(playerId)
                .Select(e => new SaveEmpireDto
                {
                    EmpireId = e.Empire.Value,
                    Controller = EmpireControllerTokens.Of(e.Controller),
                })
                .ToList();
            return Results.Ok(items);
        });

        app.MapGet("/api/players/{playerId:long}/empires/{empireId}/level", (long playerId, string empireId, RpgStore store) =>
        {
            if (!store.PlayerExists(playerId)) return Results.NotFound();

            var empire = new EmpireRef(new SaveId(playerId), new EmpireId(empireId));
            // A null row is a legal state, not an error: an empire's row is created by its first
            // credited species level, so an empire that has never earned one reads level 1 with no XP.
            // Reading it as a 404 would make "no progress yet" indistinguishable from "no such empire".
            var row = store.GetRpgActor(playerId, RpgActorKinds.Empire, 0);
            var level = row?.Level ?? 1;

            return Results.Ok(new
            {
                empireId,
                level,
                xp = row?.Xp ?? 0,
                xpToNext = RpgXpCurve.XpToNext(RpgActorKinds.Empire, level),
                highestLevel = row?.HighestLevel ?? 1,
                freeRespecStock = store.FreeRespecStock(empire),
                freeRespecsPerLevel = EmpireLevelTuningHub.Tuning.FreeRespecsPerEmpireLevel,
            });
        });
    }
}

/// <summary>
/// `empire-level` EP4.7 — the `EmpireLevelUp` emission. One message per empire level crossed, from the
/// dirties the APPEND returned, so nothing is ever sent from inside the transaction: a rolled-back
/// append returns no dirty and therefore broadcasts nothing (EP4.3's own test).
///
/// <para><b>Why this is a named seam rather than three lines inside the ingest lambda.</b> The caller is
/// the PVZ fact route's loop in `Program.cs`, which sends `RpgProgressionUpdated` for the same dirties.
/// A test that had to reach the payload through the whole host would be testing the host; this way the
/// payload crosses the REAL hub to a REAL client while the production caller stays the route loop —
/// the same shape this repo's other extracted seams use. <see cref="Payload"/> is public so the wire
/// shape is asserted rather than described.</para>
/// </summary>
public static class EmpireLevelBroadcast
{
    public static async Task SendEmpireLevelUpsAsync(
        IHubContext<RpgHub> hub, RpgStore store, IEnumerable<RpgProgressionDirty> progression)
    {
        foreach (var dirty in progression)
            foreach (var levelUp in dirty.LevelUps ?? Array.Empty<RpgStore.EmpireLevelUpEvent>())
                await hub.Clients.Group(RpgConstants.WebGroup).SendAsync("EmpireLevelUp", Payload(store, levelUp));
    }

    /// <summary>The spec's own shape (`spec-empire-level.md` §Contracts): which empire, the levels it moved
    /// between, the grants it earned (a closed-vocabulary list), and the stock those grants just grew from
    /// the ledger rather than predicted.</summary>
    public static object Payload(RpgStore store, RpgStore.EmpireLevelUpEvent levelUp) => new
    {
        playerId = levelUp.PlayerId,
        empireId = levelUp.EmpireId,
        levelBefore = levelUp.LevelBefore,
        levelAfter = levelUp.LevelAfter,
        grants = levelUp.Grants.Select(g => new { kind = g.Kind.ToString(), amount = g.Amount }),
        freeRespecStock = store.FreeRespecStock(new EmpireRef(
            new SaveId(levelUp.PlayerId), new EmpireId(levelUp.EmpireId))),
    };
}
