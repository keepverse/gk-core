using FusionRpg.Contracts;
using FusionRpg.Core.Notify;
using FusionRpg.Core.Saves;
using FusionRpg.Data;
using FusionRpg.Server.Notifications;

namespace FusionRpg.Server;

/// <summary>notify-service spec §4 - the catch-up page, the per-category history page and state
/// changes. Save-scoped like `/api/pvz-stats/{playerId:long}`; the path value is the SaveId.</summary>
public static class NotificationEndpoints
{
    // Structural (tunables-ssot T2): a buffer bound for one HTTP response, not a balance number.
    const int MaxPageSize = 200;

    public static void MapNotifications(this WebApplication app)
    {
        var g = app.MapGroup("/api/notifications");

        g.MapGet("/{playerId:long}", (long playerId, long? since, int? limit, RpgStore store) =>
        {
            if (!store.PlayerExists(playerId)) return Results.NotFound();
            var page = store.ListNotificationChanges(new SaveId(playerId), since ?? 0, Bound(limit));
            return Results.Ok(ToPageDto(page, page.Items.Count > 0 ? page.Items[^1].Rev : since ?? 0));
        });

        g.MapGet("/{playerId:long}/history", (long playerId, string? category, long? before, int? limit, RpgStore store) =>
        {
            if (!store.PlayerExists(playerId)) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(category)) return Results.BadRequest(new { reason = "category.missing" });
            if (!NotificationCatalogHub.Catalog.TryGet(category, out _))
                return Results.BadRequest(new { reason = "category.unknown" });

            var page = store.ListNotificationsByCategory(new SaveId(playerId), category, before, Bound(limit));
            var next = page.Items.Count > 0 ? page.Items[^1].Seq : before ?? 0;
            return Results.Ok(ToPageDto(page, next));
        });

        g.MapPost("/{playerId:long}/state", (long playerId, SetNotificationStateRequest body, RpgStore store, IPlayerPush push) =>
        {
            if (!store.PlayerExists(playerId)) return Results.NotFound();

            var changes = store.SetNotificationState(new SaveId(playerId), body.Seqs, body.State);
            var changedDtos = changes.Select(c => new NotificationStateChangeDto { Seq = c.Seq, Rev = c.Rev }).ToList();
            if (changedDtos.Count > 0)
            {
                push.Push(new SaveId(playerId), NotificationEvents.StateChanged,
                    new NotificationStateChangedDto { PlayerId = playerId, State = body.State, Changes = changedDtos });
            }
            return Results.Ok(new SetNotificationStateResponseDto { Changed = changedDtos });
        });
    }

    static int Bound(int? limit) => Math.Clamp(limit ?? MaxPageSize, 1, MaxPageSize);

    static NotificationPageDto ToPageDto(FusionRpg.Data.Notifications.NotificationPage page, long nextSince) => new()
    {
        Items = page.Items.Select(NotificationRowMapping.ToDto).ToList(),
        NextSince = nextSince,
        HasMore = page.HasMore
    };
}
