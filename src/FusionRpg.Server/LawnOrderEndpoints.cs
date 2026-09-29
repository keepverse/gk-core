using FusionRpg.Contracts;

namespace FusionRpg.Server;

/// <summary>
/// combat-ai `commander-direct-orders` (module 20, CAI4.9, spec-commander-direct-orders.md §3/§6): the
/// web FE's order route. It relays ONE command to the injector over the SAME server&#8594;injector seam
/// every other host command uses (<see cref="InjectorCommandSender"/>), so the order reaches the lawn's
/// <c>LawnOrderQueue</c> in process and the launcher host relays one pipe verb. One transport; no second.
///
/// <para><b>// Game Injector Debug scope.</b> This route relays to the Injector and therefore proves only
/// what the injector does with the order — never that a cast resolved, which is the lawn activation
/// path's own proof (`live-probe-standard.md`). It is deliberately NOT a <c>/api/debug/*</c> route: it is
/// a player action, not a debug surface, and it carries the scope banner rather than the debug prefix.</para>
///
/// <para><b>The route relays and returns; it does not wait.</b> Admission — <c>StaleRun</c>,
/// <c>SubjectMoved</c>, <c>SubjectGone</c>, <c>NotHeld</c>, <c>QueueFull</c> — belongs to the injector,
/// which holds the live ptr bindings, the run identity and module 16's held sets; none of those exist
/// here. So the honest answer from this side is "accepted for relay", and the acceptance's own line says
/// exactly that: one <see cref="CommandDto"/>, nothing else awaited.</para>
/// </summary>
public static class LawnOrderEndpoints
{
    /// <summary>The command name the injector's <c>CheatCommandRunner</c> dispatches on. A closed
    /// vocabulary of one, named here rather than spelled in two places.</summary>
    public const string CommandName = "lawn.order";

    /// <summary>The request body. Declared Server-side rather than in <c>FusionRpg.Contracts</c> because
    /// the two fields the FE does not own — the durable <c>SubjectId</c>/<c>ScopeId</c> — are the
    /// injector's and the run's to resolve; a Contracts DTO would invite the FE to supply them, which is
    /// the debug-scope rule ("an id must resolve to a row real gameplay could have created, never one the
    /// call invents") turned into a type.</summary>
    public sealed record LawnOrderRequest(
        string? MatchKey, string? ActorKey, string? ActionId, string? TargetKey);

    public static void MapLawnOrders(this WebApplication app)
    {
        app.MapPost("/api/lawn/order", async (LawnOrderRequest req, InjectorCommandSender sender) =>
        {
            if (string.IsNullOrWhiteSpace(req?.MatchKey)
                || string.IsNullOrWhiteSpace(req.ActorKey)
                || string.IsNullOrWhiteSpace(req.ActionId))
                return Results.BadRequest(new { reason = "order.incomplete" });

            await sender.SendAsync(new CommandDto
            {
                Name = CommandName,
                Payload = new Dictionary<string, object?>
                {
                    ["matchKey"] = req.MatchKey,
                    ["actorKey"] = req.ActorKey,
                    ["actionId"] = req.ActionId,
                    ["targetKey"] = req.TargetKey
                }
            });

            // One relay, nothing else awaited: whether the order ADMITS is the injector's answer, not
            // this route's to claim.
            return Results.Ok(new { ok = true });
        });
    }
}
