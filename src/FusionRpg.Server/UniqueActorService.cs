using FusionRpg.Contracts;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Match;
using FusionRpg.Core.Saves;
using FusionRpg.Data;
using Microsoft.AspNetCore.SignalR;

namespace FusionRpg.Server;

/// <summary>Cold UniqueActor FSM orchestration (W4–W5). Pending GC via Injector command.</summary>
public sealed class UniqueActorService
{
    public static readonly TimeSpan DefaultDeployTimeout = TimeSpan.FromSeconds(30);

    readonly RpgStore _store;
    readonly InjectorCommandInbox _inbox;
    readonly IHubContext<RpgHub> _hub;

    public UniqueActorService(RpgStore store, InjectorCommandInbox inbox, IHubContext<RpgHub> hub)
    {
        _store = store;
        _inbox = inbox;
        _hub = hub;
    }

    public UniqueActorDto Create(long playerId, string side, int typeId) =>
        _store.CreateUniqueActor(playerId, side, typeId);

    public UniqueActorDto? Get(string instanceId) => _store.GetUniqueActor(instanceId);

    public UniqueActorListDto List(long playerId) => _store.ListUniqueActors(playerId);

    public (bool Ok, string Reason, UniqueActorDto? Actor) Retire(string instanceId) =>
        _store.TryRetireUniqueActor(instanceId);

    public UniqueEquipmentListDto? GetEquipment(string instanceId) =>
        _store.GetUniqueEquipment(instanceId);

    /// <summary>Roster-only equip. The sole Cold materialize path is rolled / atom bindings:
    /// this writes the assignment row, then one rebuild — <c>mods_json</c> refresh plus
    /// <c>ReconcileUniqueEquipmentAtomBindings</c> — so Hub and battle read the same bound atoms
    /// through <c>EquippedBoundAtoms</c>. The stub catalog is an item-id allowlist only (DEBT —
    /// cold-equip-one); combat magnitudes come from the bound atoms' own containers, never from
    /// stub grant templates.
    ///
    /// <para>⛔ <b><c>slot.claimed_by_item</c> is the symmetric half of the item route's
    /// <c>equip.role-held-by-relic</c></b> (defect R1, fixed 2026-09-06). Two flows write
    /// <c>rpg_item_assignment</c>; the item route refused a relic's role by name from the day it
    /// shipped and this one refused nothing, so a <c>PUT</c> here answered 200 and quietly took a
    /// player's equipped item off. The check itself lives in the store, inside the same lock as the
    /// write — see <c>RefuseIfRoleHeldByAnItemUnlocked</c>. It is <b>not</b> in
    /// <see cref="UniqueActorEndpoints"/>'s validation-reason list, so it answers <b>409</b>, matching
    /// the 409 the mirror refusal already answers.</para></summary>
    public (bool Ok, string Reason, UniqueEquipmentListDto? Equipment) PutEquipment(
        string instanceId, string slot, string? itemId)
    {
        var actor = _store.GetUniqueActor(instanceId);
        if (actor is null) return (false, "not_found", null);
        if (!string.Equals(actor.Phase, UniqueActorPhases.Roster, StringComparison.Ordinal))
            return (false, "phase.not_roster", null);
        try
        {
            var eq = _store.UpsertUniqueEquipment(instanceId, slot, itemId);
            return (true, "", eq);
        }
        catch (UniqueEquipmentSlotClaimed)
        {
            return (false, "slot.claimed_by_item", _store.GetUniqueEquipment(instanceId));
        }
        catch (ArgumentException ex)
        {
            if (string.Equals(ex.ParamName, "itemId", StringComparison.Ordinal))
                return (false, ex.Message.StartsWith("slot_mismatch", StringComparison.Ordinal) ? "slot_mismatch" : "unknown_item", null);
            return (false, "bad_slot", null);
        }
        catch (InvalidOperationException)
        {
            return (false, "not_found", null);
        }
    }

    public (bool Ok, string Reason, UniqueEquipmentListDto? Equipment) ClearEquipment(
        string instanceId, string slot) =>
        PutEquipment(instanceId, slot, "");

    public (bool Ok, string Reason, UniqueActorDto? Actor, int LevelsGained) AwardXp(
        string instanceId, long delta, string? reason) =>
        _store.AwardUniqueActorXp(instanceId, delta, reason);


    public (bool Ok, string Reason, UniqueActorDto? Actor) FailDeploy(string instanceId)
    {
        var before = _store.GetUniqueActor(instanceId);
        var result = _store.TryFailUniqueDeploy(instanceId);
        if (result.Ok)
            _ = NotifyBindingClearAsync(before?.InstanceId ?? instanceId, before?.DeployCorrelationId);
        return result;
    }

    /// <summary>W5-D: expire stuck Deploying rows → Roster + Injector Pending GC.</summary>
    public int FailExpiredDeploys(TimeSpan? timeout = null, DateTimeOffset? utcNow = null)
    {
        var failed = _store.FailExpiredUniqueDeploys(timeout ?? DefaultDeployTimeout, utcNow);
        foreach (var (id, corr) in failed)
            _ = NotifyBindingClearAsync(id, corr);
        return failed.Count;
    }

    /// <summary>
    /// Roster → Deploying + enqueue pvz.spawn.extra with instanceId + correlationId + loadout.
    /// Idempotent on same correlationId.
    /// </summary>
    public async Task<UniqueActorDeployResultDto> DeployAsync(
        string instanceId,
        string? correlationId,
        int? col,
        int? row,
        string? matchKey,
        string? loadoutJson = null,
        CancellationToken ct = default)
    {
        var actor = _store.GetUniqueActor(instanceId);
        if (actor is null)
            return Fail("not_found", correlationId ?? "");

        var corr = string.IsNullOrWhiteSpace(correlationId)
            ? Guid.NewGuid().ToString("N")
            : correlationId.Trim();

        var begin = _store.TryBeginUniqueDeploy(instanceId, corr, matchKey);
        if (!begin.Ok)
            return new UniqueActorDeployResultDto
            {
                Ok = false,
                Reason = begin.Reason,
                Queued = false,
                CorrelationId = corr,
                Actor = begin.Actor
            };

        if (!begin.Queued)
        {
            return new UniqueActorDeployResultDto
            {
                Ok = true,
                Reason = "",
                Queued = false,
                CorrelationId = corr,
                Actor = begin.Actor
            };
        }

        var side = begin.Actor!.Side;
        var typeId = begin.Actor.TypeId;
        // save-identity SE4.23: the activity rollup is the human save's own play-activity record — an AI
        // empire's own deploy (Zomboss's mint) never lands on it (today it landed on the Zomboss row,
        // which no reader ever saw; recording it on the save would change the human's own rollup).
        if (_store.IsHumanEmpireSpecimen(instanceId))
        {
            try
            {
                _store.RecordExtraSpawnIntent(begin.Actor.PlayerId, corr, typeId, "unique-deploy", side);
            }
            catch
            {
                /* activity rollup optional for unique path */
            }
        }

        var effectiveLoadout = UniqueLoadoutMerge.Merge(loadoutJson, _store.GetUniqueStatModsJson(instanceId));

        // save-identity SE4.28 ("Injector ownership"): read off the specimen's own row, in this same
        // call that decides the deploy, so the Injector reads ownership rather than inferring it by
        // elimination. Null (no empire_id yet, or an empire rpg_save_empires does not carry) means the
        // payload omits both fields, never a guessed empire.
        var ownerEmpire = _store.SpecimenOwnerEmpire(instanceId);
        var controllerToken = ownerEmpire?.Controller switch
        {
            EmpireController.Human => "human",
            EmpireController.Ai => "ai",
            _ => null,
        };

        await SendInjectorCommand(_hub, _inbox, new CommandDto
        {
            Id = corr,
            Name = "pvz.spawn.extra",
            Payload = new
            {
                typeId,
                col,
                row,
                reason = "unique-deploy",
                correlationId = corr,
                side,
                playerId = begin.Actor.PlayerId,
                instanceId,
                source = "extra",
                loadoutJson = effectiveLoadout,
                empireId = ownerEmpire?.Empire.Value,
                controller = controllerToken
            }
        }).ConfigureAwait(false);

        return new UniqueActorDeployResultDto
        {
            Ok = true,
            Reason = "",
            Queued = true,
            CorrelationId = corr,
            Actor = _store.GetUniqueActor(instanceId)
        };
    }

    public void ObserveEvents(IReadOnlyList<EventEnvelope> batch)
    {
        if (batch.Count == 0) return;
        var mapped = new List<(string Kind, string? MatchKey, string PayloadJson, string? EventTime)>(batch.Count);
        foreach (var e in batch)
        {
            if (string.IsNullOrWhiteSpace(e.Kind)) continue;
            mapped.Add((e.Kind, e.MatchKey, RpgStore.PayloadToJson(e.Payload), e.T));
        }
        var result = _store.ObserveUniqueActorEvents(mapped);
        foreach (var playerId in result.AffectedPlayerIds)
            _ = PushAtomUnionAsync(playerId);

        // EP1.15 (spec-default-build.md, cache trigger T3): a lawn-kill or lawn-duration award that
        // leveled a specimen changed nothing about its allocation, yet its resolved default did --
        // broadcast the same AptitudesUpdated(unique, instanceId) event T1/T2 already send.
        foreach (var instanceId in result.LeveledSpecimenIds)
        {
            var owner = _store.GetUniqueActor(instanceId);
            if (owner is null) continue;
            _ = AptitudeEndpoints.BroadcastBestEffort(_hub,
                new AptitudeEndpoints.AptitudesUpdatedDto(owner.PlayerId, "unique", instanceId, null));
        }
    }

    /// <summary>
    /// T6.1 (2026-09-06, `mods-absorption`) — the real remaining gap the audit found: the Hello-time
    /// owner union (<see cref="AtomPushService.OwnersForSave"/>) never re-fired mid-session, so a
    /// unique actor that deployed (or recovered) after Hello never actually reached the runner. Fires
    /// on exactly the phase transitions <see cref="RpgStore.ObserveUniqueActorEvents"/> reports
    /// (bind ↔ ActiveBound), reusing the SAME union Hello already builds — never a second, divergent
    /// list — and sends it as an atom rehydrate <c>effects.grants.apply</c>. The payload carries the
    /// compiled grants for the affected owners but no player-session grant, so a mid-match equip or
    /// unequip never touches the player's own session Effect-bag state.
    ///
    /// <para>P1.5-L (2026-09-07): also the real remaining half of a rolled item's LAWN wiring. Made
    /// public so <see cref="ItemEquipEndpoints"/> can call it — bind/unbind was never the only
    /// transition that changes what a bound specimen's `effect_binding` rows should say; equipping or
    /// unequipping a rolled item on an ALREADY-bound specimen does too, and nothing fired this before.
    /// See <see cref="RpgStore.MaterializeRolledEquipRuntime"/>, whose only production caller before
    /// this was `WebMatchService.BuildSquad` (the Battle/expedition path) — the Lawn specimen never
    /// had its equip bindings materialized at all.</para>
    /// </summary>
    public async Task PushAtomUnionAsync(long playerId)
    {
        AtomPushDto atoms;
        try
        {
            var owners = AtomPushService.OwnersForSave(_store, playerId);
            atoms = new AtomPushService(_store).Build(owners, new BindContext(RuntimeId.Lawn), matchSeed: 0);
        }
        catch (Exception ex)
        {
            // Matches BuildApplyCommand's own rule: a failed atom push must never throw into an
            // unrelated caller (here, event ingestion) — log and drop, the next real trigger retries.
            Console.Error.WriteLine("[atom-push] mid-session re-push failed: " + ex.Message);
            return;
        }

        // T6.2 (2026-09-06): assembled by AtomPushService.BuildApplyPayload, the one place this shape
        // is built. It was hand-rolled here and again in RpgHub.BuildApplyCommand, and BOTH copies
        // dropped `atoms.Grants` — the compiled (passive) half of the push — which is exactly the
        // drift a second hand-rolled copy invites. The `grants` array is still always present (the
        // injector's RunEffectsGrantsApply, CheatCommandRunner.cs:777-814, refuses the WHOLE command
        // — InstallAtomPush never reached — when it is absent or not an array), and it still carries
        // no SESSION grant: a mid-match equip/unequip never touches the player's own Effect-bag
        // snapshot. What it now carries is this push's own compiled grants, which is the point.
        var payload = AtomPushService.BuildApplyPayload(atoms, sessionGrants: null);

        await SendInjectorCommand(_hub, _inbox, new CommandDto
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = EffectGrantRehydrate.ApplyCommandName,
            Payload = payload,
        }).ConfigureAwait(false);
    }

    Task NotifyBindingClearAsync(string? instanceId, string? correlationId)
    {
        if (string.IsNullOrWhiteSpace(instanceId) && string.IsNullOrWhiteSpace(correlationId))
            return Task.CompletedTask;
        return SendInjectorCommand(_hub, _inbox, new CommandDto
        {
            Id = "ubc-" + (correlationId ?? instanceId ?? Guid.NewGuid().ToString("N")),
            Name = "unique.binding.clear",
            Payload = new { instanceId, correlationId }
        });
    }

    static UniqueActorDeployResultDto Fail(string reason, string corr) => new()
    {
        Ok = false,
        Reason = reason,
        Queued = false,
        CorrelationId = corr
    };

    static async Task SendInjectorCommand(IHubContext<RpgHub> hub, InjectorCommandInbox inbox, CommandDto cmd)
    {
        inbox.Enqueue(cmd);
        try
        {
            await hub.Clients.Group(RpgConstants.InjectorGroup).SendAsync("Command", cmd).ConfigureAwait(false);
        }
        catch
        {
            /* inbox poll is the reliable path */
        }
    }
}
