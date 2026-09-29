using System.Collections.Concurrent;
using FusionRpg.Core.Actions.Aura;
using FusionRpg.Core.Aura;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Data;

namespace FusionRpg.Server;

/// <summary>
/// aura-skill T18c: the session-scoped aura enable/disable surface `spec-aura-surface.md` §2.1 needs
/// — `AuraRuntime`/`AuraActiveSet` (T13) have been real, tested Core classes since before this task
/// with zero HTTP surface. This is that surface, **process-local by design**
/// (`AuraRuntime`/`AuraActiveSet` hold no persistence themselves, T13's own doc comment — matching
/// this program's own T15 finding, "active state does not persist (RAM only)"), the same pattern
/// <see cref="PatronRuntimeState"/> already established for a different in-memory session cache.
///
/// <para><b>Equipped means "in the player's real loadout," read fresh on every call</b> — never cached
/// at the moment a player's `AuraRuntime` is first created, so equipping a NEW aura later (through
/// `POST /api/loadout`) takes effect on the very next enable attempt without needing this endpoint to
/// know loadouts changed.</para>
///
/// <para><b>Found and fixed a real prerequisite gap, not an invented one:</b> `AuraContentCatalog`
/// ids (T16) are never `ActionRow`s — a deliberately separate authoring catalog — so
/// `LoadoutEndpoints.cs`'s original `isHeld` check (`store.GetAction(id) is not null`) would refuse
/// every real aura id, making it impossible to ever legally equip one. Fixed at the source
/// (`LoadoutEndpoints.cs`'s `isHeld` now also accepts `AuraContentCatalog.IsKnown`) rather than worked
/// around here.</para>
/// </summary>
public static class AuraRuntimeEndpoints
{
    static readonly ConcurrentDictionary<long, AuraRuntime> Runtimes = new();

    /// <summary>Test-only: the runtime cache is keyed by bare `playerId`, and every test spins up a
    /// fresh SQLite file whose autoincrement id sequence restarts at the same values — without this,
    /// a later test's `player 1` would inherit an earlier test's still-active aura from this static
    /// dictionary. Never called from production Program.cs.</summary>
    public static void ResetForTests() => Runtimes.Clear();

    static AuraRuntime ResolveRuntime(long playerId, RpgStore store) =>
        Runtimes.GetOrAdd(playerId, pid => new AuraRuntime(
            AuraTuningHub.Tuning.MaxActiveAuras,
            auraId => (store.GetLoadout(DaveOwnerScope(pid)) ?? Array.Empty<string>()).Contains(auraId)));

    /// <summary>Shared session cache — list API and aura-runtime endpoints must use this, not a second dictionary.
    /// W7 (battle-derived-wire T4): the web-match setup producer reads the SAME cache through
    /// <see cref="ActiveRuntime"/>, so a player's active set is one value per process, never two.</summary>
    internal static AuraRuntime ResolveRuntimeForEndpoints(long playerId, RpgStore store) =>
        ActiveRuntime(playerId, store);

    /// <summary>W7 (battle-derived-wire T4): the shared process-local active-aura set, named for its
    /// battle-setup reader — same cache as the endpoints (<see cref="ResolveRuntimeForEndpoints"/>).</summary>
    internal static AuraRuntime ActiveRuntime(long playerId, RpgStore store) =>
        ResolveRuntime(playerId, store);

    internal static OwnerScope DaveOwnerScope(long playerId) => new(OwnerKind.Player, playerId.ToString());

    static OwnerScope DaveScope(long playerId) => DaveOwnerScope(playerId);

    public static void MapAuraRuntime(this WebApplication app)
    {
        var g = app.MapGroup("/api/aura-runtime");

        g.MapGet("/{playerId:long}", (long playerId, RpgStore store) =>
        {
            if (!store.PlayerExists(playerId)) return Results.NotFound();

            var runtime = ResolveRuntime(playerId, store);
            var equipped = (store.GetLoadout(DaveScope(playerId)) ?? Array.Empty<string>())
                .Where(AuraContentCatalog.IsKnown)
                .ToList();

            return Results.Ok(new
            {
                playerId,
                activeAuraIds = runtime.ActiveAuraIds,
                equippedAuraIds = equipped,
                maxActiveAuras = AuraTuningHub.Tuning.MaxActiveAuras
            });
        });

        g.MapPost("/{playerId:long}/enable", async (long playerId, EnableAuraRequest body, RpgStore store, UniqueActorService uniqueActors) =>
        {
            if (!store.PlayerExists(playerId)) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(body.AuraId) || !AuraContentCatalog.IsKnown(body.AuraId))
                return Results.BadRequest(new { reason = "auraId.unknown" });

            var runtime = ResolveRuntime(playerId, store);
            var result = runtime.Enable(body.AuraId);
            if (!result.Enabled)
                return Results.Conflict(new { reason = result.Refusal!.Value.ToString(), auraId = body.AuraId });

            // BP2/BP3 (spec-aura-binding-producer.md S3.2): the real save path -- the only production
            // caller of RpgStore.Bind. Reconciled AFTER the runtime already decided who is active, so
            // an eviction and a new bind land in the same reconcile rather than two half-states.
            //
            // live-qa 2026-09-20 (BP4 probe): SyncAsync's own refusals were silently discarded here --
            // a real bind failure (stale container, tuning gap) looked identical to success from the
            // client's point of view, and it cost this exact probe a long blind diagnosis before the
            // real cause (a stale dist/ build predating this program's own merge) was found. Logged,
            // never surfaced to the client: refusing the WHOLE enable over one aura's content gap
            // would be a worse regression than a silent one (BP2's own "refuse honestly rather than
            // fabricate a row, without failing every other aura's reconcile" rule).
            foreach (var refusal in await new AuraBindingProducer(store, uniqueActors).SyncAsync(playerId, runtime.ActiveAuraIds))
                Console.Error.WriteLine($"[aura-bind] refused for player {playerId}: {refusal.Reason} — {refusal.Detail}");

            return Results.Ok(new
            {
                playerId,
                enabledAuraId = body.AuraId,
                evictedAuraId = result.EvictedAuraId,
                activeAuraIds = runtime.ActiveAuraIds
            });
        });

        g.MapPost("/{playerId:long}/disable", async (long playerId, EnableAuraRequest body, RpgStore store, UniqueActorService uniqueActors) =>
        {
            if (!store.PlayerExists(playerId)) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(body.AuraId))
                return Results.BadRequest(new { reason = "auraId.missing" });

            var runtime = ResolveRuntime(playerId, store);
            var wasActive = runtime.Disable(body.AuraId);

            if (wasActive)
                foreach (var refusal in await new AuraBindingProducer(store, uniqueActors).SyncAsync(playerId, runtime.ActiveAuraIds))
                    Console.Error.WriteLine($"[aura-bind] refused for player {playerId}: {refusal.Reason} — {refusal.Detail}");

            return Results.Ok(new
            {
                playerId,
                disabledAuraId = body.AuraId,
                wasActive,
                activeAuraIds = runtime.ActiveAuraIds
            });
        });
    }

    public sealed class EnableAuraRequest
    {
        public string? AuraId { get; set; }
    }
}
