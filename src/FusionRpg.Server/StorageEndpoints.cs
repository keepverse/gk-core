using FusionRpg.Contracts;
using FusionRpg.Data;
using FusionRpg.Data.Abstractions;

namespace FusionRpg.Server;

public static class StorageEndpoints
{
    public const string ActiveBoundRefuseReason = "unique.active_bound";

    public static void MapStorageEndpoints(this WebApplication app)
    {
        app.MapGet("/api/storage/summary", (RpgStore store) => store.GetStorageSummary());

        app.MapGet("/api/storage/archives", (IColdArchiveCatalog catalog) =>
        {
            var items = catalog.List().Select(e => new StorageArchiveItemDto
            {
                Uri = e.Uri,
                Kind = e.Kind,
                RunId = e.RunId,
                CreatedUtc = e.CreatedUtc
            }).ToList();
            return new { items };
        });

        app.MapPost("/api/storage/archives/delete", (StorageUrisRequest? body, RpgStore store) =>
        {
            if (store.HasAnyActiveBoundUniqueActors())
                return Results.Conflict(new { error = ActiveBoundRefuseReason });
            var uris = body?.Uris ?? new List<string>();
            return Results.Ok(store.DeleteArchives(uris));
        });

        app.MapPost("/api/storage/runs/purge-capture", (StorageRunIdsRequest? body, RpgStore store) =>
        {
            if (store.HasAnyActiveBoundUniqueActors())
                return Results.Conflict(new { error = ActiveBoundRefuseReason });
            var ids = body?.RunIds ?? new List<long>();
            return Results.Ok(store.PurgeClosedRunCapture(ids));
        });

        app.MapPost("/api/storage/runs/delete", (StorageRunIdsRequest? body, RpgStore store) =>
        {
            if (store.HasAnyActiveBoundUniqueActors())
                return Results.Conflict(new { error = ActiveBoundRefuseReason });
            var ids = body?.RunIds ?? new List<long>();
            return Results.Ok(store.DeleteClosedRuns(ids));
        });

        app.MapPost("/api/storage/trim-tails", (RpgStore store) =>
        {
            if (store.HasAnyActiveBoundUniqueActors())
                return Results.Conflict(new { error = ActiveBoundRefuseReason });
            store.TrimHotTailsNow();
            return Results.Ok(new { ok = true });
        });
    }
}
