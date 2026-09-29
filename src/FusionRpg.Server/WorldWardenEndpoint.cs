using FusionRpg.Contracts;
using FusionRpg.Core.World.Turn;
using FusionRpg.Data;

namespace FusionRpg.Server;

/// <summary>
/// world-stage W29: the first production caller of <see cref="RpgStore.BindAsWarden"/>
/// (`RpgStore.Contracts.cs:283`). `FusionRpg.Core.csproj` declares exactly one `ProjectReference` —
/// `FusionRpg.Contracts` — so Core cannot call it; the orchestration lives here, in the Server layer,
/// which references both `FusionRpg.Core` and `FusionRpg.Data`.
///
/// Two steps, and there is no rollback between them:
///   1. `store.BindAsWarden(playerId, instanceId)` — the creature-contract side: capacity, the soul fee,
///      and the non-releasable flag are all shipped and read as-is (`RpgStore.Contracts.cs:310-323`).
///   2. `store.SubmitWorldCommands(worldId, [bind-warden])` — the ordinary world command path.
///
/// **Accepted risk, stated rather than engineered around: if step 2 fails, step 1 is not rolled
/// back.** The player has paid the soul fee and holds a non-releasable binding with no sector
/// attached. There is no cross-store transaction to reach for — the contract lives in the player
/// database and the order lives in the world's command log — and inventing a distributed rollback
/// for a single-player local server is a worse trade than the failure it prevents.
///
/// **What makes it tolerable is that step 1 is idempotent.** `BindAsWarden` returns `("replay",
/// existing)` for an instance already bound as a warden (`RpgStore.Contracts.cs:301-305`), and this
/// endpoint's own `CommandId` is derived from the instance id, so a retry of the *whole call* re-hits
/// both idempotent paths — it never double-charges the soul fee and never double-files the order.
/// The correct client response to any failure here is exactly that: retry the whole call.
///
/// <para><b>Retired (warden-freeze-fix, RulesetVersion 13).</b> Admission refuses every
/// `bind-warden` with `warden.retired`. The endpoint therefore asks <see cref="WorldCommandAdmission"/>
/// first, before step 1 runs, so a refused order never charges the soul fee or marks a contract
/// non-releasable. Every call now ends at that preflight. Steps 1 and 2 stay as the shape a future
/// warden-shaped mechanic would reuse (warden-mortality-ideal.md).</para>
/// </summary>
public static class WorldWardenEndpoint
{
    public static void MapWorldWarden(this WebApplication app)
    {
        app.MapPost("/api/world/{worldId}/bind-warden", (string worldId, BindWardenRequest body, RpgStore store) =>
        {
            var world = store.LoadWorldState(worldId);
            if (world is null) return Results.NotFound();

            var playerId = body.PlayerId ?? store.GetCurrentPlayerId();
            if (!store.PlayerExists(playerId))
                return Results.BadRequest(new BindWardenResultDto { Ok = false, Reason = "player.unknown" });

            var sectorId = (body.SectorId ?? "").Trim();
            if (sectorId.Length == 0)
                return Results.BadRequest(new BindWardenResultDto { Ok = false, Reason = "sector.missing" });

            var commanderId = string.IsNullOrWhiteSpace(body.CommanderId)
                ? world.Factions.FirstOrDefault(f => f.Kind == Core.World.WorldFactionKind.Player)?.FactionId
                : body.CommanderId!.Trim();
            if (commanderId is null)
                return Results.BadRequest(new BindWardenResultDto { Ok = false, Reason = "commander.unknown" });

            // A fixed, instance-derived CommandId so a retry after step 2 fails re-hits
            // SubmitWorldCommands' own replay path (RpgStore.WorldTurns.cs) instead of filing twice.
            var instanceId = body.InstanceId ?? "";
            var command = new WorldCommand
            {
                CommanderId = commanderId,
                CommandId = "bind-warden:" + instanceId,
                Kind = WorldCommandKinds.BindWarden,
                SectorId = sectorId,
                WardenId = instanceId
            };

            // Preflight: the same gate step 2 passes, asked before step 1 can charge anything. Since
            // RulesetVersion 13 it always answers `warden.retired`.
            var (admitted, admitReason) = WorldCommandAdmission.Admit(world, command);
            if (!admitted)
                return Results.BadRequest(new BindWardenResultDto { Ok = false, Reason = admitReason });

            // Step 1.
            var (bindOk, bindReason, contract) = store.BindAsWarden(playerId, instanceId);
            if (!bindOk) return Results.BadRequest(new BindWardenResultDto { Ok = false, Reason = bindReason });

            // Step 2.
            var outcome = store.SubmitWorldCommands(worldId, new[] { command })[0];
            if (!outcome.Ok)
                return Results.BadRequest(new BindWardenResultDto { Ok = false, Reason = outcome.Reason });

            return Results.Ok(new BindWardenResultDto
            {
                Ok = true,
                InstanceId = contract!.InstanceId,
                SectorId = sectorId,
                CommandReplayed = outcome.Replayed
            });
        });
    }
}
