using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Loadout;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Data;

namespace FusionRpg.Server;

/// <summary>
/// A27 (specimen-loadout-endpoints, action-map.md §17): a real specimen's own held/equipped skill
/// set, over HTTP. <see cref="LoadoutEndpoints"/> already gives Dave (the commander,
/// <see cref="OwnerKind.Player"/>) a loadout surface — but a real battle reads a SPECIMEN's loadout
/// (<c>WebMatchService.EquippedActionIdsFor</c>, `WebMatchService.cs:654-675`), and no REST surface
/// existed for that before this module (searched, zero hits in `gk-core/src/FusionRpg.Server/**`).
///
/// <para><b>Two owner scopes, not one — the exact convention <c>EquippedActionIdsFor</c> already
/// established.</b> The loadout SLOT ASSIGNMENT (what this file's `GetLoadout`/`SetLoadout` calls
/// read and write) is <see cref="OwnerKind.Entity"/> — session-scoped by design, since a loadout
/// PREFERENCE is not permanent progress (`WebMatchService.cs:639-642`'s own doc comment: losing one
/// on a session boundary degrades gracefully to auto-equip, never to nothing). The unlock-ladder
/// GRANTS that decide what is actually HELD are read from <b>both</b>
/// <see cref="OwnerKind.UniqueActor"/> (durable, fixed 2026-09-07 specifically because `Entity` is
/// session-scoped and would silently wipe real progress) <b>and</b> <see cref="OwnerKind.Entity"/>
/// (item-granted actions, correctly session-scoped since they die with the item). Reading only one
/// grant scope would silently drop the other grant source — the same defect class
/// `EquippedActionIdsFor`'s own doc comment was written to prevent.</para>
///
/// <para><b><c>isMidRun</c> is the same honest, named gap as Dave's own endpoint</b>
/// (`LoadoutEndpoints.cs:26-35`) — no production "is this specimen mid-run" oracle exists anywhere at
/// the Server layer yet, so it is wired to <c>() =&gt; false</c> rather than left unimplemented.</para>
/// </summary>
public static class SpecimenLoadoutEndpoints
{
    public static void MapSpecimenLoadout(this WebApplication app)
    {
        var g = app.MapGroup("/api/actors");

        g.MapGet("/{instanceId}/loadout", (string instanceId, RpgStore store) =>
        {
            if (store.GetUniqueActor(instanceId) is null) return Results.NotFound();

            var candidates = HeldSkillCandidates(instanceId, store);
            var actionIds = store.GetLoadoutOrAutoEquip(EntityScope(instanceId), candidates);
            // A30 (actions-tab-fe-wiring, T69/T70): the equipped set alone cannot render "2 held, 1
            // equipped" -- the FE grid needs the WHOLE held set too, sorted for a stable render order.
            // Additive field; every existing consumer of `actionIds` (the equipped/loadout set, unchanged
            // shape) is unaffected.
            var heldActionIds = candidates.Select(c => c.ActionId).OrderBy(id => id, StringComparer.Ordinal).ToList();
            return Results.Ok(new { instanceId, actionIds, heldActionIds });
        });

        g.MapPost("/{instanceId}/loadout", (string instanceId, SetSpecimenLoadoutRequest body, RpgStore store) =>
        {
            if (store.GetUniqueActor(instanceId) is null) return Results.NotFound();
            if (body.ActionIds is null)
                return Results.BadRequest(new { reason = "actionIds.missing" });

            var held = HeldSkillCandidates(instanceId, store)
                .Select(c => c.ActionId)
                .ToHashSet(StringComparer.Ordinal);

            var result = store.SetLoadout(
                EntityScope(instanceId),
                body.ActionIds,
                isHeld: held.Contains,
                isMidRun: () => false);

            if (!result.Ok)
                return Results.Conflict(new { reason = result.Reason!.Value.ToString(), actionId = result.ActionId });

            return Results.Ok(new { instanceId, actionIds = body.ActionIds });
        });
    }

    static OwnerScope EntityScope(string instanceId) => new(OwnerKind.Entity, instanceId);

    /// <summary>
    /// The specimen's real held skills — merging both grant scopes exactly as
    /// `WebMatchService.EquippedActionIdsFor` does (`WebMatchService.cs:663-672`), filtered to
    /// <see cref="ActionKind.Skill"/> (a basic/innate entry is never loadout-eligible, matching T21's
    /// existing category-error rule). A withdrawn grant is never returned here because
    /// <see cref="RpgStore.ListGrants"/> itself only lists live grants (T3's withdraw-by-source rule).
    /// </summary>
    static IReadOnlyList<AutoEquipCandidate> HeldSkillCandidates(string instanceId, RpgStore store)
    {
        var unlockLadderGrants = new OwnerScope(OwnerKind.UniqueActor, instanceId);
        var entityGrants = new OwnerScope(OwnerKind.Entity, instanceId);

        return store.ListGrants(unlockLadderGrants)
            .Concat(store.ListGrants(entityGrants))
            .Select(grant => store.GetAction(grant.ActionId))
            .Where(a => a is { Kind: ActionKind.Skill })
            .Select(a => new AutoEquipCandidate(a!.ActionId, a.Rung))
            .Distinct()
            .ToList();
    }

    public sealed class SetSpecimenLoadoutRequest
    {
        public List<string>? ActionIds { get; set; }
    }
}
