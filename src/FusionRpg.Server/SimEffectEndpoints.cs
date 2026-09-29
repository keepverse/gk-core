using System.Text.Json;
using FusionRpg.Contracts;
using FusionRpg.Core.Effects;

namespace FusionRpg.Server;

/// <summary>
/// Offline Effect sim HTTP — no Unity / SimEngine board. Always available for FE/Server CI.
/// </summary>
public static class SimEffectEndpoints
{
    public static void MapSimEffect(this WebApplication app)
    {
        var g = app.MapGroup("/api/sim/effect");

        g.MapPost("/clear", (SimEffectHost host) =>
        {
            host.ClearAll();
            return Results.Ok(new { ok = true, revision = host.Snapshot().Revision });
        });

        g.MapPost("/grant", (EffectGrantDto? body, SimEffectHost host) =>
        {
            if (body == null || string.IsNullOrWhiteSpace(body.EffectId))
                return Results.BadRequest(new { ok = false, error = "effectId required" });
            if (body.Overlay != null)
                body.Overlay = JsonOverlay.FromObject(body.Overlay);
            try
            {
                var grant = host.Grant(body);
                return Results.Ok(new { ok = true, grant = grant.ToDto(), revision = host.Snapshot().Revision });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { ok = false, error = ex.Message });
            }
        });

        g.MapPost("/withdraw", (SimEffectWithdrawRequest? body, SimEffectHost host) =>
        {
            if (body == null || string.IsNullOrWhiteSpace(body.GrantId))
                return Results.BadRequest(new { ok = false, error = "grantId required" });
            var ok = host.Withdraw(body.GrantId);
            return Results.Ok(new { ok, revision = host.Snapshot().Revision });
        });

        g.MapPost("/fire", (SimEffectFireRequest? body, SimEffectHost host) =>
        {
            if (body == null)
                return Results.BadRequest(new { ok = false, error = "body required" });

            if (body.Event != null)
                return Results.Ok(host.OnEvent(body.Event));

            if (!string.IsNullOrWhiteSpace(body.Kind))
            {
                var payload = body.Payload ?? new Dictionary<string, object>();
                var plan = host.FireFromCapture(body.Kind, payload);
                if (plan == null)
                    return Results.BadRequest(new { ok = false, error = "unmapped kind: " + body.Kind });
                return Results.Ok(plan);
            }

            if (string.Equals(body.Helper, "hit", StringComparison.OrdinalIgnoreCase))
                return Results.Ok(host.HitDealt(
                    body.ActorPtr ?? "0xA",
                    body.TargetPtr ?? "0xB",
                    body.AttackerSide ?? "plant",
                    body.TypeId ?? 0,
                    body.TargetTypeId ?? 0,
                    body.Damage ?? 20));

            if (string.Equals(body.Helper, "die", StringComparison.OrdinalIgnoreCase))
                return Results.Ok(host.Die(body.Side ?? "zombie", body.Ptr ?? "0xZ", body.TypeId ?? 0, body.KillerPtr));

            if (string.Equals(body.Helper, "spawn", StringComparison.OrdinalIgnoreCase))
                return Results.Ok(host.Spawn(body.Side ?? "plant", body.Ptr ?? "0xP", body.TypeId ?? 0));

            return Results.BadRequest(new { ok = false, error = "provide event, kind, or helper=hit|die|spawn" });
        });

        g.MapPost("/scenario", async (HttpRequest req, SimEffectHost host) =>
        {
            EffectScenarioDto? dto = null;
            string? goldenRoot = null;
            try
            {
                using var doc = await JsonDocument.ParseAsync(req.Body);
                if (doc.RootElement.TryGetProperty("path", out var pathEl) &&
                    pathEl.ValueKind == JsonValueKind.String)
                {
                    var path = pathEl.GetString()!;
                    if (!File.Exists(path))
                        return Results.NotFound(new { ok = false, error = "scenario file not found" });
                    var result = EffectScenarioRunner.RunFile(path);
                    return Results.Ok(result);
                }

                dto = JsonSerializer.Deserialize<EffectScenarioDto>(doc.RootElement.GetRawText(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { ok = false, error = ex.Message });
            }

            if (dto == null)
                return Results.BadRequest(new { ok = false, error = "scenario body required" });

            // HTTP scenario uses process host seed but runner creates its own host — return runner result
            var run = EffectScenarioRunner.Run(dto, goldenRoot);
            return Results.Ok(run);
        });

        g.MapGet("/snapshot", (SimEffectHost host) => Results.Ok(host.Snapshot()));
    }
}

public sealed class SimEffectWithdrawRequest
{
    public string GrantId { get; set; } = "";
}

public sealed class SimEffectFireRequest
{
    public EffectEventDto? Event { get; set; }
    public string? Kind { get; set; }
    public Dictionary<string, object>? Payload { get; set; }
    public string? Helper { get; set; }
    public string? ActorPtr { get; set; }
    public string? TargetPtr { get; set; }
    public string? AttackerSide { get; set; }
    public string? Side { get; set; }
    public string? Ptr { get; set; }
    public string? KillerPtr { get; set; }
    public int? TypeId { get; set; }
    public int? TargetTypeId { get; set; }
    public long? Damage { get; set; }
}
