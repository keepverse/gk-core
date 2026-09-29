using FusionRpg.Core.Creatures;
using FusionRpg.Data;

namespace FusionRpg.Server;

/// <summary>Souls reads (spec-soul-economy.md). Spends are feature-owned (summoning etc.), never generic.</summary>
public static class SoulEndpoints
{
    public static void MapSouls(this WebApplication app)
    {
        var g = app.MapGroup("/api/souls");

        // save-identity SE4.31 — souls are a Tier B store (`rpg_soul_ledger` / `rpg_soul_balances` keep
        // the save's key: only the human empire owns such rows today), so `?empire=` names the empire the
        // caller means and a non-human one is refused with 409 `empire_scope_not_widened` BEFORE any read,
        // rather than answering with the human empire's balance under another empire's name. SE4.38 widens
        // the store's key; until then this route is the contract the widening will move.
        g.MapGet("/{playerId:long}", (long playerId, RpgStore store, string? empire) =>
        {
            if (!store.PlayerExists(playerId)) return Results.NotFound();
            if (EmpireScopeRequests.RefuseNonHumanEmpire(store, playerId, empire) is { } refusal) return refusal;
            return Results.Ok(store.GetSoulBalance(playerId));
        });

        g.MapGet("/{playerId:long}/ledger", (long playerId, RpgStore store, int? limit, long? afterId) =>
        {
            if (!store.PlayerExists(playerId)) return Results.NotFound();
            return Results.Ok(store.ListSoulLedger(playerId, limit ?? 100, afterId ?? 0));
        });
    }

    /// <summary>SIM-only seed — mapped inside the /api/test group.</summary>
    public static void MapSoulTestSeed(this RouteGroupBuilder test)
    {
        test.MapPost("/seed-souls-demo", (RpgStore store, long? playerId, long? amount) =>
        {
            var pid = playerId ?? store.GetCurrentPlayerId();
            if (!store.PlayerExists(pid)) return Results.NotFound();
            // Clamp: huge seeds overflow SQLite integer addition into REAL and corrupt the balance.
            var delta = Math.Clamp(amount ?? 1000, 1, 1_000_000);
            var (_, balance) = store.AwardSouls(
                pid, delta, SoulEarnPolicy.Reasons.Seed, "seed-" + Guid.NewGuid().ToString("N"));
            return Results.Ok(balance);
        });
    }
}
