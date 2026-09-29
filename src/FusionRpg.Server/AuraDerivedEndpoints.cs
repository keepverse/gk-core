using FusionRpg.Contracts;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Data;
using Microsoft.AspNetCore.SignalR;

namespace FusionRpg.Server;

/// <summary>
/// Actor-scoped derived channels with FULL Hub fan-in + GG-49 contributions.
/// Sheet projection: <c>GET /api/actors/{id}/sheet</c>.
/// Hot live bag: <c>POST /api/internal/actors/{id}/live-state</c> (CG-A4 / S3).
/// Do not bridge to <c>pvz_stat_contributions</c>.
/// </summary>
public static class AuraDerivedEndpoints
{
    public static void MapAuraDerived(this WebApplication app)
    {
        var g = app.MapGroup("/api/actors");

        g.MapGet("/{instanceId}/derived", (string instanceId, RpgStore store) =>
        {
            var actor = store.GetUniqueActor(instanceId);
            if (actor == null) return Results.NotFound();

            var (hub, ctx) = UniqueActorHubCompose.Build(store, actor);
            var (snapshot, contributions) = hub.ResolveDerivedWithContributions(ctx);
            var registry = hub.Composer.Registry;

            var channels = snapshot.Channels
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv =>
                {
                    registry.TryResolveChannel(kv.Key, out var def);
                    return new
                    {
                        channelId = kv.Key,
                        value = kv.Value,
                        composeKind = def?.Compose.ToString() ?? "",
                        contributions = contributions.ContributionsFor(kv.Key)
                            .Select(c => new
                            {
                                sourceId = c.SourceId,
                                label = ContributionSourceIds.FictionLabel(c.SourceId),
                                op = c.Op.ToString(),
                                value = c.Value
                            })
                            .ToList()
                    };
                })
                .ToList();

            return Results.Ok(new { instanceId = actor.InstanceId, channels });
        });

        g.MapGet("/{instanceId}/sheet", (
            string instanceId, RpgStore store, IActorLiveStateStore liveState) =>
        {
            var actor = store.GetUniqueActor(instanceId);
            if (actor == null) return Results.NotFound();
            return Results.Ok(UniqueActorHubCompose.ProjectSheet(store, actor, liveState));
        });

        // CG-A5b: SignalR event name for FE invalidate is "ActorLiveStateChanged" (payload: { instanceId }).
        app.MapPost("/api/internal/actors/{instanceId}/live-state", async (
            string instanceId,
            ActorLiveState body,
            IActorLiveStateStore liveState,
            IHubContext<RpgHub> hub) =>
        {
            if (string.IsNullOrWhiteSpace(instanceId))
                return Results.BadRequest(new { error = "instanceId required" });
            if (body is null)
                return Results.BadRequest(new { error = "body required" });

            var state = new ActorLiveState
            {
                LiveStatuses = body.LiveStatuses ?? Array.Empty<ActorStatusGlyphDto>(),
                ShieldLayers = body.ShieldLayers ?? Array.Empty<ActorShieldLayerDto>()
            };
            liveState.Upsert(instanceId, state);

            await hub.Clients.Group(RpgConstants.WebGroup)
                .SendAsync("ActorLiveStateChanged", new { instanceId = instanceId.Trim() })
                .ConfigureAwait(false);

            return Results.NoContent();
        });
    }
}
