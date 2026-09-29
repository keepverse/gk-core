using FusionRpg.Core.Commanders;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Saves;
using FusionRpg.Data;

namespace FusionRpg.Server;

/// <summary>zomboss-deploy-ai T3.4 request body — <see cref="MatchSeed"/> is the SAME
/// `SeededRng.DeriveStream(0, matchKey).NextULong()` value the injector already computed to call
/// `ZombossDeployPolicy.Decide`, threaded through here so the trait roll this endpoint performs
/// (`RpgStore.MintForEmpire`) derives from that same per-match seed rather than a fresh, non-
/// reproducible one — matching this program's own "everything derives from matchKey" discipline.
/// <see cref="MatchKey"/> is REQUIRED (save-identity SE4.22, "Zomboss stops being a player row"): it
/// is the only way this endpoint learns which save's empire Zomboss deploys as, so a match that cannot
/// resolve to a run is refused (`match_unresolved`) before any mint, never guessed onto "the current
/// save". The injector already never sends a blank one (`MatchHost.CheckZombossDeployTrigger` declines
/// with no cached `matchKey`).</summary>
public sealed class ZombossDeployRequest
{
    public string SpeciesId { get; set; } = "";
    public ulong MatchSeed { get; set; }
    public string MatchKey { get; set; } = "";
    public string? CorrelationId { get; set; }
    public int? Col { get; set; }
    public int? Row { get; set; }
}

public static class ZombossDeployEndpoints
{
    public static void MapZombossDeploy(this WebApplication app)
    {
        var g = app.MapGroup("/api/zomboss");

        // zomboss-deploy-ai T3.4 (Correction 3), widened by save-identity SE4.22: composes
        // ALREADY-EXISTING, already-proven primitives (SaveOfMatch, MintForEmpire,
        // UniqueActorService.DeployAsync) — no new write logic, no shortcut. The decision
        // (whether/which) was already made injector-side by ZombossDeployPolicy.Decide, which needs
        // live board state (ILawnBoardView) this server process never sees; this endpoint performs
        // only the privileged DB half that decision cannot reach on its own. The save resolves from
        // the match BEFORE the mint, so a refused deploy leaves no orphan specimen.
        g.MapPost("/deploy", async (ZombossDeployRequest? body, RpgStore store, UniqueActorService ua) =>
        {
            body ??= new ZombossDeployRequest();
            if (string.IsNullOrWhiteSpace(body.SpeciesId))
                return Results.BadRequest(new { error = "speciesId required" });
            if (!CreatureSpeciesCatalog.IsKnown(body.SpeciesId))
                return Results.BadRequest(new { error = "unknown speciesId '" + body.SpeciesId + "'" });
            if (string.IsNullOrWhiteSpace(body.MatchKey))
                return Results.BadRequest(new { error = "matchKey required" });

            var save = store.SaveOfMatch(body.MatchKey);
            if (save is null)
                return Results.Conflict(new { error = "match_unresolved" });

            var owner = new EmpireRef(save.Value, EmpireId.Zomboss);
            var specimen = store.MintForEmpire(owner, body.SpeciesId, body.MatchSeed);

            var correlationId = string.IsNullOrWhiteSpace(body.CorrelationId)
                ? Guid.NewGuid().ToString("N")
                : body.CorrelationId;
            var result = await ua.DeployAsync(
                specimen.Actor.InstanceId, correlationId, body.Col, body.Row, body.MatchKey);

            if (!result.Ok && result.Reason == "not_found")
                return Results.NotFound(result);
            if (!result.Ok)
                return Results.Conflict(result);
            return Results.Ok(new { result.Ok, result.Reason, result.Queued, result.CorrelationId, result.Actor, specimen });
        });
    }
}
