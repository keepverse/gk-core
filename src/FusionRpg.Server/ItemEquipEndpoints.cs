using FusionRpg.Contracts;
using FusionRpg.Core.Items;
using FusionRpg.Core.Saves;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Data;
using Microsoft.AspNetCore.SignalR;

namespace FusionRpg.Server;

/// <summary>One durable assignment as a caller renders it — module 4's row, unchanged.</summary>
public sealed record ItemAssignmentDto(string Role, string RefKind, string RefId, string AssignedUtc);

/// <summary>
/// What one equip or unequip did, in the shape a surface can draw without a second read: whether it
/// happened, which named rule refused it when it did not, and the specimen's whole assignment list
/// afterwards so the paperdoll never has to guess what changed.
/// </summary>
/// <param name="Replaced">The occupant this write displaced, or <c>null</c> when the role was empty.
/// Equipping over a worn item is a swap, not a refusal — but it is never silent.</param>
public sealed record ItemEquipOutcomeDto(
    bool Ok,
    string Verb,
    string Reason,
    string SpecimenId,
    string Role,
    string RefKind,
    string RefId,
    ItemAssignmentDto? Replaced,
    IReadOnlyList<ItemAssignmentDto> Assignments);

/// <summary>
/// ⭐ <b>The equip executor</b> — the production caller item module 4 named as its own last blocker.
///
/// <para>Module 4 shipped <c>rpg_item_assignment</c>, <c>SaveAssignment</c>/<c>RemoveAssignment</c>,
/// <see cref="EquipGate"/> and <see cref="EquipProjector"/>, all tested, and <b>nothing outside
/// <c>tests/</c> called the two writes</b>. This class calls them against a real stored item and a
/// real specimen, after the gate has said yes.</para>
///
/// <para><b>Equip costs nothing, and that is the design, not an omission.</b> The workbench's six
/// verbs spend because craft, salvage, enhance and socket consume materials (module 14 prices them).
/// Putting an item you already own into a role you already have consumes nothing, so there is no
/// <c>correlationId</c> here either: with no debit there is no double-spend for one to protect
/// against, and the write is idempotent on its own — <c>SaveAssignment</c> upserts on
/// <c>(specimen_id, role)</c>, so a retried request lands the same row.</para>
///
/// <para><b>⛔ This route owns <c>ref_kind = "rolled"</c> rows and nothing else.</b> The four
/// hand-authored relics live in the same table since the 2026-09-06 row migration (D1 §10 M1), as
/// <c>ref_kind = "stock"</c>, and they are written by
/// <c>PUT /api/unique/actors/{id}/equipment/{slot}</c> — which also rebuilds <c>mods_json</c> and
/// reconciles the <c>unique-equip</c> atom bindings in the same call. Overwriting one of those cells
/// from here would delete the row and leave both of those derived states standing, so a role a relic
/// holds is <b>refused by name</b> and the player is pointed at the flow that owns it. Two flows,
/// one table, no shared writes.</para>
///
/// <para><b>Assign is this class's whole job; the Lawn-runtime sync lives one layer up.</b>
/// `spec-equip-assign.md` is explicit that the runtime binding is rebuilt as a full projection
/// <i>at deploy</i>, never patched at assign time — so this class itself deliberately does not call
/// <c>ApplyEquipProjection</c> (module 5) or <c>ApplyEquippedGrants</c> (module 19). ⭐ <b>Fixed
/// 2026-09-07 (P1.5-L):</b> those two calls are no longer callerless — <c>ItemEquipEndpoints</c>'s
/// own <c>/api/items/equip</c>/<c>/unequip</c> handlers call <c>SyncLawnRuntimeAsync</c> after every
/// successful outcome from this service, which calls <c>RpgStore.MaterializeRolledEquipRuntime</c>
/// (the method that runs both). A Lawn-bound specimen's equipped rolled items now materialize their
/// <c>effect_binding</c> rows and granted actions through the real production write surface, not only
/// through <c>WebMatchService.BuildSquad</c>'s battle-squad path.</para>
/// </summary>
public sealed class ItemEquipService
{
    /// <summary>I13 §4.4's kind for an assignment that pins one rolled copy — the <c>ref_id</c> is an
    /// <c>effect_instance.instance_id</c>. It is also the only kind
    /// <c>RpgStore.ApplyEquipProjection</c> turns into a binding, so writing anything else here would
    /// persist a decision module 5 could never project.</summary>
    public const string RolledRefKind = EquipRefKinds.Rolled;

    /// <summary>The relic flow's kind (a catalog id, not a rolled copy). Read here only to refuse —
    /// never written. Since 2026-09-06 the relic flow refuses the reverse case by name too
    /// (<c>slot.claimed_by_item</c>), so the two flows are symmetric rather than one-way.</summary>
    const string StockRefKind = EquipRefKinds.Stock;

    readonly RpgStore _store;
    readonly EquipGate _gate;

    public ItemEquipService(RpgStore store, EquipGate? gate = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _gate = gate ?? new EquipGate();
    }

    // ---- reads ---------------------------------------------------------------------------------

    /// <summary>
    /// What this holder is wearing. The commander is a holder like any unique specimen (owner ruling 2026-09-16), so the
    /// read has to resolve the same two tables the write gate does — <c>rpg_item_assignment</c> keyed by specimen and
    /// <c>rpg_player_item_assignment</c> keyed by player. Reading only the first is what made a commander's own pouch
    /// come back empty right after a successful equip.
    /// </summary>
    /// <param name="playerId">Only consulted for a commander id; a specimen carries its own owner on its row.</param>
    public IReadOnlyList<ItemAssignmentDto> List(string specimenId, long? playerId = null)
    {
        if (!string.IsNullOrWhiteSpace(specimenId)
            && FusionRpg.Core.Commanders.CommanderDirectoryHub.Current.TryResolve(specimenId, out _))
        {
            var owner = playerId ?? _store.GetCurrentPlayerId();
            return _store.ListPlayerItemAssignments(PlayerKey(owner))
                .Select(a => new ItemAssignmentDto(ItemRoles.Id(a.Role), a.RefKind, a.RefId, a.AssignedUtc))
                .ToList();
        }

        return _store.ListAssignments(specimenId).Select(ToDto).ToList();
    }

    // ---- equip ---------------------------------------------------------------------------------

    public ItemEquipOutcomeDto Equip(long playerId, string specimenId, string instanceId, string roleId)
    {
        if (!ItemRoles.TryParse(roleId, out var role))
            return Refuse("equip", specimenId, roleId, instanceId,
                $"equip.role-unknown: '{roleId}' is not one of the sixteen registry roles");

        // Owner ruling 2026-09-16 (`item-ideal.md` D1, unimplemented until now): "unique demon (include commander) can equip
        // item, this is general feature inside actor hub, not split by feature". The role no longer decides WHO may wear it —
        // the target does, and both targets pass this one gate. `standard` is the squad-wide role, not a commander-only cell.
        if (!TryResolveTarget(playerId, specimenId, out var target, out var targetRefusal))
            return Refuse("equip", specimenId, roleId, instanceId, targetRefusal);
        var actor = target.Actor;

        var item = _store.GetItem(instanceId);
        if (item is null)
            return Refuse("equip", specimenId, roleId, instanceId, $"item.unknown: no owned item '{instanceId}'");

        if (!string.Equals(item.PlayerId, PlayerKey(playerId), StringComparison.Ordinal))
            return Refuse("equip", specimenId, roleId, instanceId,
                $"item.not-owned: '{instanceId}' belongs to player '{item.PlayerId}'");

        // ⚠ `Locked` is deliberately NOT a refusal here. `RpgItemRow.Locked`'s own contract is
        // "refuse salvage/transfer while true" — it protects an item from being consumed, and wearing
        // one consumes nothing. The workbench checks it because every one of its verbs spends.
        if (!string.Equals(item.Disposition, "owned", StringComparison.Ordinal))
            return Refuse("equip", specimenId, roleId, instanceId,
                $"item.not-owned: '{instanceId}' is '{item.Disposition}'");

        var instance = _store.GetInstance(instanceId);
        if (instance is null)
            return Refuse("equip", specimenId, roleId, instanceId,
                $"item.instance-missing: no effect_instance '{instanceId}'");

        var container = _store.GetContainer(instance.ContainerId);
        if (container is null)
            return Refuse("equip", specimenId, roleId, instanceId,
                $"item.container-missing: '{instance.ContainerId}' is not in the catalog");

        var generation = _store.GetItemGeneration(instanceId);

        // The item's OWN role, from the pipeline's stamp first and the container's declared slot
        // second. Both are real stored decisions; neither is guessed. With neither, the answer is a
        // refusal rather than "any role will do" — the same rule the relic flow's own
        // `SlotMatchesItem` applies to the three legacy slots.
        var itemRoleId = generation?.Role is { Length: > 0 } g ? g : container.Slot ?? "";
        if (!ItemRoles.TryParse(itemRoleId, out var itemRole))
            return Refuse("equip", specimenId, roleId, instanceId,
                $"equip.item-role-unknown: '{instanceId}' declares no role — item_generation has no stamp " +
                $"and its container's slot is '{container.Slot}'");

        if (itemRole != role)
            return Refuse("equip", specimenId, roleId, instanceId,
                $"equip.role-mismatch: '{instanceId}' is a '{ItemRoles.Id(itemRole)}' item, not a '{roleId}'");

        // D19's surviving half, in the order the spec fixes: the unlock predicate answers "does this
        // specimen have this slot?" before frame/level/faction answer "may it wear this?".
        // ⚠ The frame arm is inert until X1 ships a species frame (`actor.Frame` is null today), and
        // no content sets a faction clause — both are passed real values anyway rather than being
        // skipped, so the day either lands the gate is already reading it.
        var refusal = _gate.Explain(role, actor, generation?.Frame, container.LevelReq, factionReq: null);
        if (refusal is { } r)
            return Refuse("equip", specimenId, roleId, instanceId, $"{ReasonCode(r.Reason)}: {r.Remedy}");

        var standing = ListAssignmentsFor(target);
        var occupant = standing.FirstOrDefault(a => a.Role == role);

        // Already in exactly this cell — nothing to write, and saying "done" is the truth.
        if (occupant is not null
            && string.Equals(occupant.RefKind, RolledRefKind, StringComparison.Ordinal)
            && string.Equals(occupant.RefId, instanceId, StringComparison.Ordinal))
            return new ItemEquipOutcomeDto(true, "equip", "equip.already-in-this-role", specimenId,
                roleId, RolledRefKind, instanceId, Replaced: null, ListFor(target));

        if (occupant is not null && string.Equals(occupant.RefKind, StockRefKind, StringComparison.Ordinal))
            return Refuse("equip", specimenId, roleId, instanceId,
                $"equip.role-held-by-relic: '{roleId}' holds '{occupant.RefId}', which was equipped through the " +
                "relic flow — take it off there first, so its mods and atom bindings come off with it");

        // One physical copy cannot be worn twice. The primary key already stops two items sharing a
        // role; this stops one item filling two, on this specimen or any other.
        var holders = _store.FindAssignmentHolders(new[] { instanceId }, RolledRefKind);
        if (holders.TryGetValue(instanceId, out var cell)
            && !(string.Equals(cell.SpecimenId, specimenId, StringComparison.Ordinal)
                 && string.Equals(cell.Role, roleId, StringComparison.Ordinal)))
            return Refuse("equip", specimenId, roleId, instanceId,
                $"equip.already-worn: '{instanceId}' is already in '{cell.Role}' on specimen '{cell.SpecimenId}'");

        if (PhaseRefusal(target) is { } deployed)
            return Refuse("equip", specimenId, roleId, instanceId, deployed);

        SaveAssignmentFor(target, role, instanceId);

        return new ItemEquipOutcomeDto(true, "equip", "", specimenId, roleId, RolledRefKind, instanceId,
            Replaced: occupant is null ? null : ToDto(occupant),
            Assignments: ListFor(target));
    }

    // ---- unequip -------------------------------------------------------------------------------

    public ItemEquipOutcomeDto Unequip(long playerId, string specimenId, string roleId)
    {
        if (!ItemRoles.TryParse(roleId, out var role))
            return Refuse("unequip", specimenId, roleId, "",
                $"equip.role-unknown: '{roleId}' is not one of the sixteen registry roles");

        if (!TryResolveTarget(playerId, specimenId, out var target, out var targetRefusal))
            return Refuse("unequip", specimenId, roleId, "", targetRefusal);

        var occupant = ListAssignmentsFor(target).FirstOrDefault(a => a.Role == role);
        if (occupant is null)
            return Refuse("unequip", specimenId, roleId, "",
                $"equip.role-empty: nothing is in '{roleId}' on specimen '{specimenId}'");

        if (!string.Equals(occupant.RefKind, RolledRefKind, StringComparison.Ordinal))
            return Refuse("unequip", specimenId, roleId, occupant.RefId,
                $"equip.role-held-by-relic: '{roleId}' holds '{occupant.RefId}', which was equipped through the " +
                "relic flow — take it off there, so its mods and atom bindings come off with it");

        if (PhaseRefusal(target) is { } deployed)
            return Refuse("unequip", specimenId, roleId, occupant.RefId, deployed);

        // §6.4's atomicity claim, exercised: unequip is one row deleted and no second writer. The
        // item itself is untouched — module 1's R1 ("unequip does not destroy the item").
        var removed = RemoveAssignmentFor(target, role);
        if (!removed)
            return Refuse("unequip", specimenId, roleId, occupant.RefId,
                $"equip.role-empty: nothing is in '{roleId}' on specimen '{specimenId}'");

        return new ItemEquipOutcomeDto(true, "unequip", "", specimenId, roleId, occupant.RefKind, occupant.RefId,
            Replaced: ToDto(occupant), Assignments: ListFor(target));
    }

    // ---- shared --------------------------------------------------------------------------------

    /// <summary>
    /// Who is wearing it. Owner ruling 2026-09-16 and `item-ideal.md` D1: the commander and every unique specimen wear the
    /// same role set through the same gate, so the only thing that differs between them is which durable table holds the
    /// assignment — `rpg_item_assignment` keyed by specimen, `rpg_player_item_assignment` keyed by player
    /// (`RpgStore.Items.cs`'s own note: the commander is not a fabricated specimen row). The gate, the item checks and the
    /// one-copy-one-cell rule are shared.
    /// </summary>
    internal sealed record EquipTarget(bool IsCommander, string StoreKey, SpecimenActor Actor);

    IReadOnlyList<EquipAssignment> ListAssignmentsFor(EquipTarget target) =>
        target.IsCommander
            ? _store.ListPlayerItemAssignments(target.StoreKey)
                .Select(a => new EquipAssignment(target.Actor.SpecimenId, a.Role, a.RefKind, a.RefId, a.AssignedUtc))
                .ToList()
            : _store.ListAssignments(target.StoreKey);

    IReadOnlyList<ItemAssignmentDto> ListFor(EquipTarget target) =>
        ListAssignmentsFor(target).Select(ToDto).ToList();

    /// <summary>
    /// Answers the phase question the STORE also answers, so a player gets a refusal instead of a 500.
    ///
    /// <para><c>RpgStore.SaveAssignment</c>/<c>RemoveAssignment</c> throw on a non-<c>Roster</c> unique
    /// actor (the deploy-time-snapshot anti-fraud rule, `spec-corpse-cache.md` §Locked anchors). That
    /// throw is the real gate and stays — it covers every caller by construction. But an endpoint must
    /// not answer a legitimate player action with an unhandled exception, so the same condition is named
    /// here in this surface's own refusal vocabulary. The two cannot drift apart silently: if this check
    /// were ever dropped, the store would still refuse, just less politely.</para>
    ///
    /// <para>Commander targets are exempt because the pouch is Dave's own equipment and has no
    /// deployment phase — the same reason `spec-corpse-cache.md` excludes
    /// <c>rpg_player_item_assignment</c> from the whole module.</para>
    /// </summary>
    string? PhaseRefusal(EquipTarget target)
    {
        if (target.IsCommander) return null;
        var actor = _store.GetUniqueActor(target.StoreKey);
        if (actor is null) return null; // not a unique actor -- no phase concept
        if (string.Equals(actor.Phase, UniqueActorPhases.Roster, StringComparison.Ordinal)) return null;

        return $"equip.specimen-deployed: '{target.StoreKey}' is {actor.Phase}, not "
            + $"{UniqueActorPhases.Roster}. Gear is committed before a specimen deploys and cannot be "
            + "changed while it is out. Bring it home first.";
    }

    void SaveAssignmentFor(EquipTarget target, ItemRole role, string instanceId)
    {
        if (target.IsCommander) _store.SavePlayerItemAssignment(target.StoreKey, role, RolledRefKind, instanceId);
        else _store.SaveAssignment(target.StoreKey, role, RolledRefKind, instanceId);
    }

    bool RemoveAssignmentFor(EquipTarget target, ItemRole role) =>
        target.IsCommander
            ? _store.RemovePlayerItemAssignment(target.StoreKey, role)
            : _store.RemoveAssignment(target.StoreKey, role);

    bool TryResolveTarget(long playerId, string specimenId, out EquipTarget target, out string reason)
    {
        target = null!;

        if (!string.IsNullOrWhiteSpace(specimenId)
            && FusionRpg.Core.Commanders.CommanderDirectoryHub.Current.TryResolve(specimenId, out var commander))
        {
            if (FusionRpg.Core.Commanders.CommanderDirectoryHub.Current.EmpireOf(commander)
                != FusionRpg.Core.Commanders.EmpireId.Dave)
            {
                reason = $"equip.commander-not-owned: '{specimenId}' is not this player's commander";
                return false;
            }

            // The commander's own level is the player's progression level — the same number every other
            // commander-scoped read uses, never a specimen level and never a guess.
            var level = checked((int)(_store.GetRpgActor(playerId, FusionRpg.Core.Progression.RpgActorKinds.Player, 0)?.Level ?? 1));
            target = new EquipTarget(IsCommander: true, StoreKey: PlayerKey(playerId),
                Actor: new SpecimenActor(specimenId.Trim(), Frame: null, level, Faction: null));
            reason = "";
            return true;
        }

        if (!TryResolveSpecimen(playerId, specimenId, out var actor, out reason)) return false;
        target = new EquipTarget(IsCommander: false, StoreKey: actor.SpecimenId, Actor: actor);
        return true;
    }

    bool TryResolveSpecimen(long playerId, string specimenId, out SpecimenActor actor, out string reason)
    {
        actor = default;

        if (string.IsNullOrWhiteSpace(specimenId))
        {
            reason = "equip.specimen-required: name the specimen to equip to";
            return false;
        }

        var row = _store.GetUniqueActor(specimenId);
        if (row is null)
        {
            reason = $"equip.specimen-unknown: no bound creature '{specimenId}'";
            return false;
        }

        // save-identity SE4.25: the one ownership predicate — a Zomboss specimen of this save must
        // never be equippable by the human just because it shares the save's player_id.
        if (!_store.OwnsSpecimen(new EmpireRef(new SaveId(playerId), _store.HumanEmpireOf(playerId)), row.InstanceId))
        {
            reason = $"equip.specimen-not-owned: '{specimenId}' belongs to player '{row.PlayerId}'";
            return false;
        }

        // ⚠ `UniqueActorDto.Level` is a `long` and `SpecimenActor.Level` is an `int` (module 4's own
        // shape, matching `ActorContext` and `BindGate`). A level is a ladder INDEX, not a magnitude,
        // so `int` is the repo's shape for it — but the narrowing still has to be checked rather than
        // wrapped or clamped: a clamp would silently admit an item the level gate should refuse.
        // `checked` makes an impossible level throw instead of lying.
        actor = new SpecimenActor(row.InstanceId, Frame: null, checked((int)row.Level), Faction: null);
        reason = "";
        return true;
    }

    static string PlayerKey(long playerId) => playerId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Module 4's internal refusal enum as a stable wire code. ⚠ <c>RoleLocked</c> is still
    /// unratified as I13's fifteenth official reason code — this is the module's own result
    /// vocabulary, which the spec distinguishes from that closed list.</summary>
    static string ReasonCode(EquipRefusalReason reason) => reason switch
    {
        EquipRefusalReason.RoleLocked => "equip.role-locked",
        EquipRefusalReason.RoleNotOnFrame => "equip.role-not-on-frame",
        EquipRefusalReason.LevelTooLow => "equip.level-too-low",
        EquipRefusalReason.FactionMismatch => "equip.faction-mismatch",
        _ => "equip.refused",
    };

    static ItemAssignmentDto ToDto(EquipAssignment a) =>
        new(ItemRoles.Id(a.Role), a.RefKind, a.RefId, a.AssignedUtc);

    ItemEquipOutcomeDto Refuse(string verb, string specimenId, string roleId, string refId, string reason) =>
        new(false, verb, reason, specimenId, roleId, RolledRefKind, refId, Replaced: null,
            Assignments: string.IsNullOrWhiteSpace(specimenId)
                ? Array.Empty<ItemAssignmentDto>()
                : List(specimenId));
}

/// <summary>
/// item module 4 (<c>equip-assign</c>) — the <b>write</b> surface for putting an item in a role.
///
/// <para>Its own file for the same reason <c>WorkbenchEndpoints.cs</c> is: module 20
/// (<c>ItemSurfaceEndpoints.cs</c>) is read-only by construction, and a write path through the
/// presentation layer is the "second surface" that module exists to prevent. These verbs belong to
/// module 4, and each route is a thin shell over <see cref="ItemEquipService"/> — no gate logic and
/// no persistence decisions live in this file.</para>
///
/// <para><b>No <c>correlationId</c>, and the asymmetry with the workbench is deliberate.</b> Every
/// workbench verb is a spend, and a spend without an idempotency key is a double-spend waiting for a
/// network retry. Equipping debits nothing, and <c>SaveAssignment</c> upserts on
/// <c>(specimen_id, role)</c>, so a retried equip lands the same row and a retried unequip finds the
/// role already empty.</para>
/// </summary>
public static class ItemEquipEndpoints
{
    public sealed record EquipRequest(long? PlayerId, string? SpecimenId, string? InstanceId, string? Role);

    public sealed record UnequipRequest(long? PlayerId, string? SpecimenId, string? Role);

    public static void MapItemEquip(this WebApplication app, ItemEquipService equip)
    {
        if (equip is null) throw new ArgumentNullException(nameof(equip));

        // What a specimen is actually wearing, across all fifteen roles. Module 4's own table, so it
        // sits with the writes rather than in module 20's read-only file — and unlike
        // `GET /api/unique/actors/{id}/equipment` it does not project through the three legacy slot
        // words, because an item can occupy any of the fifteen.
        app.MapGet("/api/items/assignments/{specimenId}", (string specimenId, long? playerId) =>
            Results.Ok(equip.List(specimenId, playerId)));

        app.MapPost("/api/items/equip", async (EquipRequest body, RpgStore store, UniqueActorService uniqueActorService, IHubContext<RpgHub> hub) =>
        {
            if (body.SpecimenId is not { Length: > 0 } specimenId)
                return Results.BadRequest(new { error = "specimenId required" });
            if (body.InstanceId is not { Length: > 0 } instanceId)
                return Results.BadRequest(new { error = "instanceId required" });
            if (body.Role is not { Length: > 0 } role)
                return Results.BadRequest(new { error = "role required" });

            var playerId = body.PlayerId ?? store.GetCurrentPlayerId();
            var outcome = equip.Equip(playerId, specimenId, instanceId, role);
            if (outcome.Ok)
            {
                await SyncLawnRuntimeAsync(store, uniqueActorService, specimenId, playerId).ConfigureAwait(false);
                await BroadcastLivenessAsync(hub, playerId, specimenId).ConfigureAwait(false);
            }
            return Render(outcome);
        });

        app.MapPost("/api/items/unequip", async (UnequipRequest body, RpgStore store, UniqueActorService uniqueActorService, IHubContext<RpgHub> hub) =>
        {
            if (body.SpecimenId is not { Length: > 0 } specimenId)
                return Results.BadRequest(new { error = "specimenId required" });
            if (body.Role is not { Length: > 0 } role)
                return Results.BadRequest(new { error = "role required" });

            var playerId = body.PlayerId ?? store.GetCurrentPlayerId();
            var outcome = equip.Unequip(playerId, specimenId, role);
            if (outcome.Ok)
            {
                await SyncLawnRuntimeAsync(store, uniqueActorService, specimenId, playerId).ConfigureAwait(false);
                await BroadcastLivenessAsync(hub, playerId, specimenId).ConfigureAwait(false);
            }
            return Render(outcome);
        });
    }

    /// <summary>
    /// lawn LW1.5 (spec-actor-liveness-refresh.md): ONE liveness invalidation per equip/unequip,
    /// naming the holder it moved. It rides the shared <c>AptitudesUpdated</c> transport (the same
    /// message SP6.6's player-switch notice uses) rather than opening a channel of its own — the
    /// specimen-scoped <c>Equip</c> kind is what tells the injector WHICH cache to drop, and the
    /// existing atom-push sync above is a different concern that stays exactly as it is. Only called
    /// on a successful outcome: a refused equip changes nothing, so it announces nothing.
    /// </summary>
    static Task BroadcastLivenessAsync(IHubContext<RpgHub> hub, long playerId, string specimenId) =>
        AptitudeEndpoints.BroadcastBestEffort(hub, new AptitudeEndpoints.AptitudesUpdatedDto(
            playerId, "unique", specimenId, null, Kind: LivenessInvalidationWire.Equip));

    /// <summary>
    /// P1.5-L (2026-09-07) — the Lawn half of module 5's equip wiring, found missing by reading
    /// <c>RpgStore.MaterializeRolledEquipRuntime</c>'s own doc comment: its only production caller was
    /// <c>WebMatchService.BuildSquad</c> (Battle/expedition), so a specimen bound to the live Lawn never
    /// had its rolled-item `effect_binding` rows materialized at all — equip/unequip persisted the
    /// assignment and changed nothing else. This closes the gap the same way Battle already does: run
    /// the full projection (bindings + granted actions), then re-push the compiled atom union so the
    /// injector's own <c>effects.grants.apply</c> path picks it up — the same call
    /// <see cref="UniqueActorService.PushAtomUnionAsync"/> makes for a bind/unbind transition.
    ///
    /// <para>Best-effort like every other post-Hello atom push: a failure here must never turn a
    /// successful equip write into a 500 for the caller, so it is caught and logged, not surfaced.</para>
    ///
    /// <para>build-preset BP1.7 (spec-item-loadout-apply.md): widened from <c>private</c> to
    /// <c>internal</c> so <c>ItemLoadoutApplyService</c> can call this EXACT logic once after the
    /// last write of a loadout apply, rather than re-deriving "sync the Lawn runtime after an
    /// equip write" a second time — the same widening precedent as
    /// <see cref="PatronEndpoints.Compute"/>.</para>
    /// </summary>
    internal static async Task SyncLawnRuntimeAsync(RpgStore store, UniqueActorService uniqueActorService, string specimenId, long playerId)
    {
        try
        {
            // The commander's assignments live in their own table and project onto the player's own scope — the scope
            // AtomPushService.OwnersForSave already pushes, so this reaches a live lawn the same way a specimen does
            // (owner ruling 2026-09-16: one equip feature, not one per actor kind).
            if (FusionRpg.Core.Commanders.CommanderDirectoryHub.Current.TryResolve(specimenId, out _))
            {
                var level = checked((int)(store.GetRpgActor(playerId, FusionRpg.Core.Progression.RpgActorKinds.Player, 0)?.Level ?? 1));
                store.MaterializeRolledCommanderEquipRuntime(
                    playerId.ToString(System.Globalization.CultureInfo.InvariantCulture), level);
                await uniqueActorService.PushAtomUnionAsync(playerId).ConfigureAwait(false);
                return;
            }

            var actor = store.GetUniqueActor(specimenId);
            if (actor is null) return;
            store.MaterializeRolledEquipRuntime(specimenId, checked((int)actor.Level));
            await uniqueActorService.PushAtomUnionAsync(playerId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[item-equip] Lawn runtime sync failed: " + ex.Message);
        }
    }

    /// <summary>
    /// A refused operation is <b>409 with the named rule</b>, never a 200 carrying a sad face and never
    /// a bare 400 — the request was well formed and the answer is "the rules say no". Identical to
    /// <c>WorkbenchEndpoints.Render</c> on purpose: one refusal shape across the item program's whole
    /// write surface, so `httpErrorMessage` lifts `reason` out of either without a special case.
    /// </summary>
    static IResult Render(ItemEquipOutcomeDto outcome) =>
        outcome.Ok ? Results.Ok(outcome) : Results.Json(outcome, statusCode: StatusCodes.Status409Conflict);
}
