using FusionRpg.Core.BuildPresets;
using FusionRpg.Core.Saves;
using FusionRpg.Core.Time;
using FusionRpg.Data;

namespace FusionRpg.Server;

/// <summary>BP1.12 — CRUD over the human empire's reference-only build-preset library.</summary>
public static class BuildPresetEndpoints
{
    public static void MapBuildPresets(this WebApplication app)
    {
        var group = app.MapGroup("/api/build-presets");

        group.MapGet("/{playerId:long}", (long playerId, RpgStore store) =>
        {
            if (!store.PlayerExists(playerId)) return Results.NotFound();
            var owner = EmpireScopeRequests.HumanOwnerOf(store, playerId);
            var presets = store.ListBuildPresets(owner)
                .Select(row => store.GetBuildPresetValidated(row.PresetId, owner))
                .Where(read => read is not null)
                .Select(read => Project(read!))
                .ToList();
            return Results.Ok(new { playerId, presets });
        });

        group.MapPost("/", (SaveBuildPresetRequest body, RpgStore store) =>
        {
            var playerId = body.PlayerId ?? store.GetCurrentPlayerId();
            if (!store.PlayerExists(playerId)) return Results.NotFound();
            if (!TryParsePieces(body.Pieces, out var pieces, out var parseReason))
                return Results.BadRequest(new { reason = parseReason });

            var owner = EmpireScopeRequests.HumanOwnerOf(store, playerId);
            var presetId = string.IsNullOrWhiteSpace(body.PresetId)
                ? Guid.NewGuid().ToString("N")
                : body.PresetId.Trim();
            var row = new RpgBuildPresetRow(
                presetId, owner.Save.Value, owner.Empire.Value,
                body.Name ?? "", ServerClock.UtcNow.ToString("o"), Revision: 0);
            var reason = store.SaveBuildPreset(owner, row, pieces, isCreate: true);
            if (reason.Length != 0) return Refusal(reason);

            return Results.Ok(Project(store.GetBuildPresetValidated(presetId, owner)!));
        });

        group.MapPut("/{presetId}", (string presetId, SaveBuildPresetRequest body, RpgStore store) =>
        {
            var playerId = body.PlayerId ?? store.GetCurrentPlayerId();
            if (!store.PlayerExists(playerId)) return Results.NotFound();
            if (!TryParsePieces(body.Pieces, out var pieces, out var parseReason))
                return Results.BadRequest(new { reason = parseReason });

            var owner = EmpireScopeRequests.HumanOwnerOf(store, playerId);
            var row = new RpgBuildPresetRow(
                presetId, owner.Save.Value, owner.Empire.Value,
                body.Name ?? "", ServerClock.UtcNow.ToString("o"), Revision: 0);
            var reason = store.SaveBuildPreset(owner, row, pieces, isCreate: false);
            if (reason.Length != 0) return Refusal(reason);

            return Results.Ok(Project(store.GetBuildPresetValidated(presetId, owner)!));
        });

        group.MapDelete("/{presetId}", (string presetId, long playerId, RpgStore store) =>
        {
            if (!store.PlayerExists(playerId)) return Results.NotFound();
            var owner = EmpireScopeRequests.HumanOwnerOf(store, playerId);
            return store.DeleteBuildPreset(owner, presetId)
                ? Results.NoContent()
                : Results.NotFound(new { reason = "build-preset.notFound" });
        });
    }

    static bool TryParsePieces(
        IReadOnlyList<BuildPresetPieceRequest>? source,
        out List<BuildPresetPieceRow> pieces,
        out string reason)
    {
        pieces = new List<BuildPresetPieceRow>();
        if (source is null)
        {
            reason = "build-preset.pieces.missing";
            return false;
        }

        foreach (var piece in source)
        {
            if (!BuildPresetPieceKinds.TryParse(piece.Kind, out var kind))
            {
                reason = "build-preset.kind.unknown";
                return false;
            }

            pieces.Add(new BuildPresetPieceRow(
                kind,
                piece.TargetRef ?? "",
                piece.Ordinal,
                piece.RefId ?? ""));
        }

        reason = "";
        return true;
    }

    static IResult Refusal(string reason) => reason switch
    {
        "build-preset.notFound" => Results.NotFound(new { reason }),
        "build-preset.id.exists" or "build-preset.softMax" or "build-preset.owner.mismatch" =>
            Results.Conflict(new { reason }),
        _ => Results.BadRequest(new { reason }),
    };

    static object Project(RpgBuildPresetRead read) => new
    {
        playerId = read.Preset.SaveId,
        presetId = read.Preset.PresetId,
        read.Preset.Name,
        read.Preset.CreatedUtc,
        read.Preset.Revision,
        pieces = read.Pieces.Select(piece => new
        {
            kind = piece.KindId,
            piece.TargetRef,
            piece.Ordinal,
            piece.RefId,
            state = piece.State == BuildPresetPieceState.Present ? "present" : "missing",
            piece.Reason,
        }).ToList(),
    };

    public sealed class SaveBuildPresetRequest
    {
        public long? PlayerId { get; set; }
        public string? PresetId { get; set; }
        public string? Name { get; set; }
        public List<BuildPresetPieceRequest>? Pieces { get; set; }
    }

    public sealed class BuildPresetPieceRequest
    {
        public string? Kind { get; set; }
        public string? TargetRef { get; set; }
        public long Ordinal { get; set; }
        public string? RefId { get; set; }
    }
}
