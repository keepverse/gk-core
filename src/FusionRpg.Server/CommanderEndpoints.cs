using FusionRpg.Contracts;
using FusionRpg.Core.Aura;
using FusionRpg.Core.Commanders;
using FusionRpg.Data;
using Microsoft.AspNetCore.SignalR;

namespace FusionRpg.Server;

/// <summary>
/// commander-surface P1: persisted default lawn commander + empire list for the web FE. Single route
/// group for default GET/POST and roster GET (spec-default-persistence, spec-commander-list-api).
/// </summary>
public static class CommanderEndpoints
{
    public static void MapCommanders(this WebApplication app)
    {
        var g = app.MapGroup("/api/commanders");

        g.MapGet("/{playerId:long}/default", (long playerId, RpgStore store) =>
        {
            if (!store.PlayerExists(playerId)) return Results.NotFound();
            return Results.Ok(new DefaultLawnCommanderResponse
            {
                DefaultLawnCommanderId = store.GetDefaultLawnCommanderId(playerId),
            });
        });

        g.MapPost("/default", async (SetDefaultLawnCommanderRequest body, RpgStore store, IHubContext<RpgHub> hub) =>
        {
            var pid = body.PlayerId ?? store.GetCurrentPlayerId();
            if (!store.PlayerExists(pid)) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(body.CommanderId))
                return Results.BadRequest(new { reason = "commander.missing" });

            var (ok, reason) = store.SetDefaultLawnCommanderId(pid, body.CommanderId);
            if (!ok) return Results.BadRequest(new { reason });

            await BroadcastBestEffort(hub, pid);
            return Results.Ok(new DefaultLawnCommanderResponse
            {
                DefaultLawnCommanderId = store.GetDefaultLawnCommanderId(pid),
            });
        });

        // EP3.3 (spec-commander-roster.md "Routes"): grant or revoke the commander ROLE on a specimen.
        // The role is a binding — grant inserts, revoke deletes, and the creature is unchanged either
        // way — and a revoke of the seated default resets the default in the SAME transaction (the
        // store's own method), so the default never points at a non-commander. The response carries the
        // default afterwards, and the existing list refresh is broadcast as for the default route.
        g.MapPost("/role", async (SetCommanderRoleRequest body, RpgStore store, IHubContext<RpgHub> hub) =>
        {
            var pid = body.PlayerId ?? store.GetCurrentPlayerId();
            if (!store.PlayerExists(pid)) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(body.InstanceId))
                return Results.BadRequest(new { reason = RpgStore.CommanderRoleUnknown });

            var empire = new FusionRpg.Core.Saves.EmpireRef(
                new FusionRpg.Core.Saves.SaveId(pid), store.HumanEmpireOf(pid));
            var outcome = body.Grant
                ? store.GrantCommanderRole(empire, body.InstanceId!)
                : store.RevokeCommanderRoleAndResetDefault(empire, body.InstanceId!);
            if (!outcome.Ok) return Results.BadRequest(new { reason = outcome.Reason });

            await BroadcastBestEffort(hub, pid);
            return Results.Ok(new CommanderRoleResponse
            {
                InstanceId = body.InstanceId!,
                Granted = body.Grant,
                DefaultLawnCommanderId = store.GetDefaultLawnCommanderId(pid),
            });
        });

        g.MapGet("/{playerId:long}", (long playerId, RpgStore store) =>
        {
            if (!store.PlayerExists(playerId)) return Results.NotFound();
            return Results.Ok(ProjectList(store, playerId));
        });
    }

    internal static CommanderListResponse ProjectList(RpgStore store, long playerId)
    {
        var defaultId = store.GetDefaultLawnCommanderId(playerId);
        var runtime = AuraRuntimeEndpoints.ResolveRuntimeForEndpoints(playerId, store);
        var equipped = (store.GetLoadout(AuraRuntimeEndpoints.DaveOwnerScope(playerId)) ?? Array.Empty<string>())
            .Where(AuraContentCatalog.IsKnown)
            .ToList();
        var active = equipped.Where(id => runtime.ActiveAuraIds.Contains(id)).ToList();
        var firstActive = active.Count > 0 ? active[0] : null;

        var rows = new List<CommanderListRowDto>();
        var commanderEquipment = store.ListPlayerItemAssignments(
                playerId.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Where(a => a.Role == FusionRpg.Core.Items.ItemRole.Standard)
            .Select(a => new CommanderEquipmentDto
            {
                InstanceId = a.RefId,
                ContainerId = store.GetInstance(a.RefId)?.ContainerId ?? "",
                Role = FusionRpg.Core.Items.ItemRoles.Id(a.Role),
            })
            .FirstOrDefault();
        var directory = FusionRpg.Core.Commanders.CommanderDirectoryHub.Current;
        // The player's own name is what that player's commander displays (ruling 2026-09-18); the
        // endpoint already holds the save, so it supplies the name and the directory applies the rule.
        var playerName = store.ListPlayers().FirstOrDefault(p => p.Id == playerId)?.Name;
        // EP3.3: the roster, not a hard-coded array. `ForEmpire(HumanEmpireOf(save))` is the call the
        // per-`EmpireRef` listing shape plugs into when `save-identity` SE4.32 re-keys this route; this
        // endpoint supplies the human empire of the save today and re-implements nothing of SE4.32's.
        var roster = directory as ICommanderRoster
            ?? throw new InvalidOperationException(
                "the configured commander directory does not implement ICommanderRoster");
        var empireRef = new FusionRpg.Core.Saves.EmpireRef(
            new FusionRpg.Core.Saves.SaveId(playerId), store.HumanEmpireOf(playerId));
        foreach (var commander in roster.ForEmpire(empireRef))
        {
            var stableId = commander.StableId;
            string? activeAuraId = null;
            string? activeAuraName = null;
            if (directory.EmpireOf(commander) == FusionRpg.Core.Commanders.EmpireId.Dave && firstActive is not null)
            {
                activeAuraId = firstActive;
                activeAuraName = firstActive;
            }

            rows.Add(new CommanderListRowDto
            {
                Id = stableId,
                DisplayName = directory.DisplayName(commander, playerName),
                IsDefault = string.Equals(stableId, defaultId, StringComparison.Ordinal),
                ActiveAuraId = activeAuraId,
                ActiveAuraName = activeAuraName,
                LocationStub = null,
                LegionStub = null,
                Equipment = directory.EmpireOf(commander) == FusionRpg.Core.Commanders.EmpireId.Dave
                    ? commanderEquipment
                    : null,
            });
        }

        return new CommanderListResponse
        {
            DefaultLawnCommanderId = defaultId,
            Commanders = rows,
        };
    }

    static async Task BroadcastBestEffort(IHubContext<RpgHub> hub, long playerId)
    {
        try { await hub.Clients.Group(RpgConstants.WebGroup).SendAsync("CommandersUpdated", new { playerId }); }
        catch { /* best-effort; GET reflects durable state */ }
        try { await hub.Clients.Group(RpgConstants.InjectorGroup).SendAsync("CommandersUpdated", new { playerId }); }
        catch { /* best-effort; injector re-syncs at session start regardless */ }
    }
}
