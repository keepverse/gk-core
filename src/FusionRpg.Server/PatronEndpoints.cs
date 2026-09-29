using FusionRpg.Contracts;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Patron;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Data;
using FusionRpg.Server.Gates;
using Microsoft.AspNetCore.SignalR;

namespace FusionRpg.Server;

/// <summary>
/// Patron designation (spec-patron-creature.md): server-authoritative aura computed from the
/// specimen's rarity/star/level; changes broadcast to the web AND pushed to the injector as a
/// `patron.aura` command (applied from the NEXT match — the plugin freezes the running one).
/// </summary>
public static class PatronEndpoints
{
    public static void MapPatron(this WebApplication app)
    {
        var g = app.MapGroup("/api/patron");

        g.MapGet("/{playerId:long}", (long playerId, RpgStore store) =>
        {
            if (!store.PlayerExists(playerId)) return Results.NotFound();
            return Results.Ok(ProjectState(store, playerId));
        });

        g.MapPost("/set", async (SetPatronRequest body, RpgStore store, PatronService patron) =>
        {
            var pid = body.PlayerId ?? store.GetCurrentPlayerId();
            if (!store.PlayerExists(pid)) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(body.CorrelationId))
                return Results.BadRequest(new { reason = "correlation.missing" });
            if (body.CorrelationId.Trim().Length > 64)
                return Results.BadRequest(new { reason = "correlation.toolong" });

            var outcome = await patron.SetAsync(pid, body.InstanceId ?? "", body.CorrelationId!);
            if (!outcome.Ok)
            {
                return outcome.Reason is "souls.insufficient"
                    ? Results.Conflict(new { reason = outcome.Reason })
                    : Results.BadRequest(new { reason = outcome.Reason });
            }

            return Results.Ok(ProjectState(store, pid));
        });
    }

    /// <summary>The injector-facing aura command for the CURRENT player's patron — also pushed
    /// on injector Hello so a fresh inject/reconnect always has the latest designation.</summary>
    public static CommandDto? TryBuildPatronCommand(RpgStore store)
    {
        var playerId = store.GetCurrentPlayerId();
        var computed = Compute(store, playerId);
        if (computed == null) return null;
        var aura = computed.Value.Aura;
        return new CommandDto
        {
            Name = "patron.aura",
            Payload = new Dictionary<string, object?>
            {
                ["playerId"] = playerId,
                ["elementPrimary"] = aura.ElementPrimary,
                ["elementSecondary"] = aura.ElementSecondary,
                ["powerMilli"] = aura.PowerMilli,
                ["defenseMilli"] = aura.DefenseMilli,
                ["secondaryPowerMilli"] = aura.SecondaryPowerMilli,
                ["secondaryDefenseMilli"] = aura.SecondaryDefenseMilli
            }
        };
    }

    /// <summary>
    /// The session-grant form of the aura marker — upserted into the effect-grant session at each
    /// pvzrh board.start so reconnect rehydrate carries it (same grant id the injector plugin uses;
    /// upserts converge).
    ///
    /// <para><b>Scope is PLANT-SIDE, and this is the SECOND producer of <c>patron:aura</c> that has to
    /// say so</b> (creature-standalone PT7b, 2026-09-20). The injector's own match-start plugin
    /// (<c>PatronSecondaryPlugin</c>, run by <c>SecondaryPluginRegistry</c>) grants the identical
    /// <c>GrantId</c>/<c>EffectId</c>/<c>OwnerKey</c>/<c>OwnerKind</c>/<c>PluginId</c>; this one is
    /// pushed over <c>effects.grants.apply</c> on Hello/reconnect and re-pushed by
    /// <c>UniqueActorService</c> on bind/unbind, and the injector's bag is keyed by grant id, so either
    /// write can land last.</para>
    ///
    /// <para><b>Which producer wins, and why that is no longer observable.</b> A reconnect does not
    /// replay the injector's own <c>OnMatchStart</c> (the plugin only fires at a real match start), so
    /// the SERVER's copy is the one that survives a Hello/re-inject — that was always true. What is new
    /// is that the two copies now agree on the whole scope: before 2026-09-20 this method stamped
    /// <c>match</c>, so the server's re-push silently re-widened the key the plugin had just narrowed,
    /// and the aura reached the zombies (the live PT7 finding). Both producers now stamp
    /// <see cref="EffectOwnerKey.PlantSide"/> plus the matching <c>OwnerKind</c>, so the upsert is
    /// idempotent however the two interleave — they converge rather than fight. Change one of them and
    /// this becomes a live scope flip again.</para>
    ///
    /// <para>The key names the side only; the <c>OwnerKind</c> must name the SAME side, because
    /// <c>GrantedDerivedAtomReader</c> looks a plant up as <c>("plant", "plant:{typeId}")</c> and
    /// <c>IEffectGrantStore.ForOwner</c> filters on both fields — a <c>plant:*</c> key under
    /// <c>ownerKind</c> <c>match</c> is found by neither query and the aura would be silently inert.</para>
    /// </summary>
    public static EffectGrantDto? TryBuildPatronSessionGrant(RpgStore store)
    {
        var playerId = store.GetCurrentPlayerId();
        var computed = Compute(store, playerId);
        if (computed == null) return null;
        return new EffectGrantDto
        {
            GrantId = "patron:aura",
            EffectId = "fx.patron_aura",
            OwnerKind = OwnerScope.Name(OwnerKind.Plant),
            OwnerKey = EffectOwnerKey.PlantSide,
            PluginId = "sec.patron.aura"
        };
    }

    /// <summary>Keeps the in-process runtime state (SIM plugins share it) aligned with the store.</summary>
    public static void RefreshRuntimeState(RpgStore store)
    {
        var playerId = store.GetCurrentPlayerId();
        var computed = Compute(store, playerId);
        PatronRuntimeState.Set(playerId, computed?.Aura);
    }

    // patron-absorption (spec-patron-absorption.md, 2026-09-06): widened from `private` to `internal`
    // so AtomPushService.Build's own externalRefs callback can reuse this EXACT logic (patron row →
    // profile/actor → the player's own Θ → PatronPolicy.Aura) rather than a second copy that could
    // silently disagree with what this endpoint itself reports.
    internal static (PatronRow Row, PatronAura Aura)? Compute(RpgStore store, long playerId)
    {
        var row = store.GetPatron(playerId);
        if (row == null) return null;
        var profile = store.GetCreatureProfile(row.InstanceId);
        var actor = profile == null ? null : store.ListCreatureRoster(playerId).Items
            .FirstOrDefault(s => s.Profile.InstanceId == row.InstanceId)?.Actor;
        if (profile == null) return null;
        if (!CreatureRarityIds.TryParse(profile.Rarity, out var rarity)) return null;

        // aura-skill T22 (owner sign-off 2026-08-30): the player's own Θ, read the SAME way
        // AptitudeEndpoints.cs's own ProjectState does — no DI thread needed through this class's 4
        // external callers (Program.cs, RpgHub.cs, EventIngest.cs, SimEndpoints.cs), since
        // ServerPowerIndexProvider wraps only `store` + the already-globally-configured
        // PowerTuningHub.Tuning, both already in scope here.
        var powerIndex = new FusionRpg.Server.Power.ServerPowerIndexProvider(
            store, FusionRpg.Core.Power.PowerTuningHub.Tuning);
        var theta = powerIndex.ActorIndex(new FusionRpg.Core.Stats.StatContext { PlayerId = playerId });

        var aura = PatronPolicy.Aura(
            rarity, profile.Star, actor?.Level ?? 1, theta, FusionRpg.Core.Power.PowerTuningHub.Tuning,
            profile.ElementPrimary, profile.ElementSecondary);
        return (row, aura);
    }

    static object ProjectState(RpgStore store, long playerId)
    {
        var computed = Compute(store, playerId);
        return new
        {
            patron = computed == null
                ? null
                : new
                {
                    instanceId = computed.Value.Row.InstanceId,
                    setUtc = computed.Value.Row.SetUtc,
                    revision = computed.Value.Row.Revision,
                    aura = computed.Value.Aura,
                    switchCostSouls = PatronPolicy.SwitchCostSouls
                },
            switchCostSouls = PatronPolicy.SwitchCostSouls
        };
    }

    public sealed class SetPatronRequest
    {
        public long? PlayerId { get; set; }
        public string? InstanceId { get; set; }
        public string? CorrelationId { get; set; }
    }
}
