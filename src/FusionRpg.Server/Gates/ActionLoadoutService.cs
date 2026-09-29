using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Loadout;
using FusionRpg.Core.Aura;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Data;

namespace FusionRpg.Server.Gates;

/// <summary>
/// build-preset (BP1.3, spec-gate-services.md): the held and mid-run predicates
/// `LoadoutEndpoints.cs`'s `POST /api/loadout` lambda builds inline, named once so a build-preset
/// skills applier (`SkillsApplier`, BP2.3) previews through the exact same rules `Set` then
/// enforces -- a second caller of `RpgStore.SetLoadout` copying these predicates by hand could
/// silently disagree on which ids count as "held" (`LoadoutEndpoints.cs:60-64`'s aura-id OR is easy
/// to drop). Behaviour is byte-identical: `Set` is exactly the route's existing `store.SetLoadout`
/// call; `Preview` is the same predicates run through the pure validator with no write.
/// </summary>
public sealed class ActionLoadoutService
{
    readonly RpgStore _store;

    public ActionLoadoutService(RpgStore store)
    {
        _store = store;
    }

    public LoadoutValidation Set(long playerId, IReadOnlyList<string> actionIds) =>
        _store.SetLoadout(DaveScope(playerId), actionIds, IsHeld, IsMidRun);

    public LoadoutValidation Preview(long playerId, IReadOnlyList<string> actionIds) =>
        LoadoutSet.Validate(actionIds, IsHeld, KindOf, IsMidRun);

    ActionKind KindOf(string actionId) => _store.GetAction(actionId)?.Kind ?? ActionKind.Skill;

    // aura-skill T18c: an aura id is never an ActionRow (AuraContentCatalog is a deliberately
    // separate authoring catalog, T16) but it DOES occupy the same 5-slot loadout
    // (spec-aura-action-shape.md:21) -- without this OR, no real aura could ever legally be
    // equipped, which would make AuraRuntime's `_isEquipped` unfalsifiable.
    bool IsHeld(string id) => _store.GetAction(id) is not null || AuraContentCatalog.IsKnown(id);

    // aura-skill T21b: no production "is this player currently mid-run" signal exists at the
    // Server layer yet (LoadoutEndpoints.cs's own doc comment) -- wired to `() => false` here for
    // the same reason the route was, not a new decision made by this lift.
    static bool IsMidRun() => false;

    // Scoped to Dave (the player's own commander) only -- see LoadoutEndpoints.cs's class doc for
    // why Zomboss has no loadout endpoint here.
    static OwnerScope DaveScope(long playerId) => new(OwnerKind.Player, playerId.ToString());
}
