using FusionRpg.Core.Actions.Unlock;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Data;

namespace FusionRpg.Server;

/// <summary>
/// A28 (unlock-discard-endpoint, action-map.md §17): <c>UnlockDiscardService</c> (T20, built, tested)
/// has zero Server callers today — this gives it one.
///
/// <para><b>Owner scope: <see cref="OwnerKind.UniqueActor"/>, not <see cref="OwnerKind.Entity"/> —
/// the OPPOSITE of A27's loadout-slot scope, deliberately.</b> Per A27's own audit finding
/// (`SpecimenLoadoutEndpoints.cs`'s doc comment), the unlock-ladder grants and earn history are the
/// DURABLE half — fixed 2026-09-07 specifically because `Entity` is session-scoped and would let a
/// session boundary silently erase real progress. `GetUnlockState`/`SaveUnlockState`
/// (`RpgStore.ActionUnlocks.cs:48,89`) are always called against `UniqueActor`.</para>
///
/// <para><b>Θ source, resolved rather than deferred (spec's own boundary):</b> the shipped convention
/// for a specimen's own Θ is its own level, read directly — `BattleActorSetup.Index =&gt; Level`
/// (`BattleModels.cs:24`) and `WebMatchService.BuildSquad` (`WebMatchService.cs:585`,
/// `(int)Math.Max(1, s.Actor.Level)`) both already treat a specimen's level as Θ with no separate
/// transform. This module reuses that exact reading rather than inventing a private `f(actor)`
/// (PS-4/PS-14).</para>
///
/// <para><b>A real soul spend, not a stub.</b> Unlike A27's <c>isMidRun</c> (no production oracle
/// exists anywhere yet), <c>RpgStore.TrySpendSouls</c> is a real, already-shipped ledger — this
/// endpoint is simply its first non-test production caller.</para>
/// </summary>
public static class UnlockDiscardEndpoints
{
    public static void MapUnlockDiscard(this WebApplication app)
    {
        app.MapPost("/api/actors/{instanceId}/unlock/discard", (string instanceId, UnlockDiscardRequest body, RpgStore store) =>
        {
            var actor = store.GetUniqueActor(instanceId);
            if (actor is null) return Results.NotFound();
            if (body.UnlockId is not { Length: > 0 } unlockId)
                return Results.BadRequest(new { reason = "unlockId.missing" });

            // Design point 4: should not happen post-A26 (this endpoint has no reason to run before
            // it), but a null tuning here is a real server misconfiguration, never a silent no-op.
            var tuning = UnlockTuningPolicy.Tuning;
            if (tuning is null) return Results.StatusCode(StatusCodes.Status500InternalServerError);

            var scope = new OwnerScope(OwnerKind.UniqueActor, instanceId);
            var state = store.GetUnlockState(scope); // UnlockState.Empty()-shaped for a fresh specimen -- no special case needed
            var theta = Math.Max(1L, actor.Level);

            FusionRpg.Contracts.SoulBalanceDto? balance = null;
            var service = new UnlockDiscardService(
                isMidRun: () => false, // same honest, named gap as A27 -- no production "mid-run" oracle exists yet
                trySpendSoul: amount =>
                {
                    var (ok, _, bal) = store.TrySpendSouls(
                        actor.PlayerId, amount, SoulEarnPolicy.Reasons.UnlockDiscard,
                        $"unlock-discard:{instanceId}:{unlockId}:{Guid.NewGuid():N}");
                    balance = bal;
                    return ok;
                });

            var outcome = service.TryDiscard(state, unlockId, theta, tuning);
            if (!outcome.Discarded)
                return Results.Conflict(new { reason = outcome.Reason!.Value.ToString() });

            // TryDiscard mutated `state` in place (UnlockState.TryDiscard) -- persist the freed slot.
            // The spent souls are already persisted by TrySpendSouls itself.
            store.SaveUnlockState(scope, state);

            return Results.Ok(new { instanceId, unlockId, balance });
        });
    }

    public sealed class UnlockDiscardRequest
    {
        public string? UnlockId { get; set; }
    }
}
