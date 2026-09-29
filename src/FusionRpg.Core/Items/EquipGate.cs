namespace FusionRpg.Core.Items;

/// <summary>What the gate knows about the specimen asking, composed from the **unassisted** sources
/// only (<see cref="UnassistedAttributes"/>) — never from anything an equippable container itself
/// grants (I11 §2.7's cycle rule). <see cref="SpecimenId"/> is the `rpg_unique_actor`'s own stable
/// `instance_id` string, matching `OwnerScope.UniqueActor`'s key.</summary>
public readonly record struct SpecimenActor(string SpecimenId, string? Frame, int Level, string? Faction);

/// <summary>The four axes the gate refuses on today. **Not** I13 §6's official closed list — that is
/// a spec-level vocabulary and minting a fifteenth entry in it is an Ask-first this module does not
/// take unilaterally. This is this module's own internal result type.</summary>
public enum EquipRefusalReason
{
    /// <summary>Module 3's `SlotUnlock` closed this role. Proposed as I13's fifteenth code
    /// (`RoleLocked`) but not yet ratified — tracked, not decided here.</summary>
    RoleLocked,

    /// <summary>D19's surviving arm. Remedy: "wrong specimen".</summary>
    RoleNotOnFrame,

    /// <summary>A4 — `level_req` actually enforced, against the **specimen's** level (recommended,
    /// unopposed: "you should always be able to wear what the content you just beat dropped", and
    /// that content was beaten by a specimen, not an account).</summary>
    LevelTooLow,

    /// <summary>Content-restricted to hand-authored uniques/set pieces (I11 §2.3) — structurally
    /// present, inert until modules 13/17 ship content that sets it.</summary>
    FactionMismatch,
}

public readonly record struct EquipRefusal(EquipRefusalReason Reason, string Remedy);

/// <summary>
/// D19's surviving half. Bind-time refusal with a reason, distinct from module 3's unlock predicate
/// ("does this actor have this slot?") — this asks "may this actor wear this item?", and it asks the
/// predicate FIRST: a role the predicate closes is not a slot this specimen has, so frame/level/
/// faction never get to answer the wrong question in the wrong order.
///
/// <para><b>Two moments, two tests (item-ideal.md, `equip-assign`).</b> <see cref="Admits"/> is the
/// ASSIGN gate and is hard — every axis refuses. <see cref="Projectable"/> is the DEPLOY test and is
/// deliberately weaker: it excludes the level check, because a standing assignment whose
/// `level_req` lapsed still projects — filtering it out here would be force-unequip arriving through
/// the back door on the next deploy (I11 §2.6, the answer chosen over cascading unequip). Frame and
/// faction need no re-check at deploy: neither can change after assignment (a body does not change;
/// a faction clause is content-restricted to hand-authored content, itself inert today), so a mismatch
/// on either can only ever be caught at assign time.</para>
/// </summary>
public sealed class EquipGate
{
    readonly SlotUnlock _unlock;

    public EquipGate(SlotUnlock? unlock = null) => _unlock = unlock ?? new SlotUnlock();

    public bool Admits(ItemRole role, SpecimenActor actor, string? itemFrame, int? levelReq, string? factionReq) =>
        Explain(role, actor, itemFrame, levelReq, factionReq) is null;

    /// <summary>Deliberately does not re-check <paramref name="levelReq"/> — see the class doc.
    /// Structurally always true against today's inputs (nothing yet produces a non-executable
    /// binding at deploy time); written as its own method so that check has a home later without
    /// touching <see cref="Admits"/>.</summary>
    public bool Projectable(ItemRole role, SpecimenActor actor, string? itemFrame, string? factionReq) =>
        ExplainAssignTimeOnly(role, actor, itemFrame, factionReq) is null;

    public EquipRefusal? Explain(ItemRole role, SpecimenActor actor, string? itemFrame, int? levelReq, string? factionReq)
    {
        var assignTime = ExplainAssignTimeOnly(role, actor, itemFrame, factionReq);
        if (assignTime is not null) return assignTime;

        if (levelReq is { } req && actor.Level < req)
            return new EquipRefusal(EquipRefusalReason.LevelTooLow, $"needs level {req}, specimen is level {actor.Level}");

        return null;
    }

    EquipRefusal? ExplainAssignTimeOnly(ItemRole role, SpecimenActor actor, string? itemFrame, string? factionReq)
    {
        if (!_unlock.IsUnlocked(role, new ActorContext(actor.SpecimenId, actor.Level)))
            return new EquipRefusal(EquipRefusalReason.RoleLocked, "unlock this slot");

        // D30/X1: nothing equippable carries a frame check that can actually fire yet -- no species
        // carries `frame`, so actor.Frame is always null at this build stage. Structurally enforced
        // anyway: the FIRST attribute clause to land must not silently inherit a no-op arm.
        if (itemFrame is not null && actor.Frame is not null &&
            !string.Equals(itemFrame, actor.Frame, StringComparison.Ordinal))
            return new EquipRefusal(EquipRefusalReason.RoleNotOnFrame, "wrong specimen");

        if (factionReq is not null && actor.Faction is not null &&
            !string.Equals(factionReq, actor.Faction, StringComparison.Ordinal))
            return new EquipRefusal(EquipRefusalReason.FactionMismatch, "wrong faction");

        return null;
    }
}
