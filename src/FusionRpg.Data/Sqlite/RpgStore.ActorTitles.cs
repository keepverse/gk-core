using FusionRpg.Core.Achievements;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Stats.Derived;
using System.Text.Json;

namespace FusionRpg.Data;

// Actor titles (spec-actor-titles.md): equip title instances into the title-{1,2,3}
// slot domain on the existing effect_binding table (owner unique-actor:{instanceId}).
// Reach is the existing equip path — no new subsystem, no private fold. The 15-role
// gear registry is never widened: title slots are a separate domain by design.
public sealed partial class RpgStore
{
    static string TitleSlotName(int slot) => $"title-{slot}";

    /// <summary>
    /// Equip a player-inventory title instance onto a specimen. Validates slot grammar,
    /// instance existence + player ownership, and the six-resource rule: title atoms
    /// touching resource.max|regen|efficiency.* must cover all six resource ids
    /// (DerivedStatChannels.ResourceIds), else refuse naming the container.
    ///
    /// <para>Inventory/equip split (no double-count): the grant binding stays on the
    /// player owner (slot null — never resolved for an actor), and equip writes a second
    /// binding on the actor owner (slot title-N). The Hub projection reads actor bindings
    /// only, so each title composes exactly once.</para>
    /// </summary>
    public AtomRejection EquipActorTitle(
        long playerId, string actorInstanceId, int slot, string instanceId, string? utc = null)
    {
        if (slot < 1 || slot > TitleWornSelector.TitleSlots)
            return AtomRejection.Fail(AtomRejectionReason.BadParamValue,
                $"title slot {slot} is outside 1..{TitleWornSelector.TitleSlots}");
        var instance = GetInstance(instanceId);
        if (instance is null)
            return AtomRejection.Fail(AtomRejectionReason.StaleInstance,
                $"instance '{instanceId}' does not exist");
        if (string.IsNullOrWhiteSpace(actorInstanceId))
            return AtomRejection.Fail(AtomRejectionReason.BadParamValue, "actor instance id is empty");
        var playerOwner = new OwnerScope(OwnerKind.Player, playerId.ToString());
        var owned = false;
        foreach (var b in ListBindings(playerOwner))
            if (b.InstanceId == instanceId) { owned = true; break; }
        if (!owned)
            return AtomRejection.Fail(AtomRejectionReason.BadParamValue,
                $"instance '{instanceId}' is not in player:{playerId} title inventory");

        var channels = new List<string?>();
        foreach (var a in instance.Atoms)
        {
            var atom = GetAtom(a.AtomId);
            if (atom is null) continue;
            channels.Add(TitleChannelOf(atom.ParamsJson));
        }
        var missing = TitleResourceRule.MissingIds(channels);
        if (missing is not null)
            return AtomRejection.Fail(AtomRejectionReason.BadParamValue,
                $"title '{instance.ContainerId}' touches resource channels but misses: {missing}");

        return Bind(new BindingRow
        {
            OwnerKind = OwnerKind.UniqueActor,
            OwnerKey = actorInstanceId,
            InstanceId = instanceId,
            Slot = TitleSlotName(slot),
            Priority = 1,
            Source = $"title:{instanceId}",
        }, bindingId: $"title-{actorInstanceId}-{slot}", boundUtc: utc);
    }

    /// <summary>Withdraw a title slot binding. Absent slot is Ok (idempotent).</summary>
    public AtomRejection UnequipActorTitle(string actorInstanceId, int slot)
    {
        var owner = new OwnerScope(OwnerKind.UniqueActor, actorInstanceId);
        foreach (var b in ListBindings(owner))
            if (b.Slot == TitleSlotName(slot)) Withdraw(b.BindingId);
        return AtomRejection.Ok;
    }

    /// <summary>Worn title container id, derived from equipped bindings (never stored).</summary>
    public string? GetWornTitle(string actorInstanceId)
    {
        var owner = new OwnerScope(OwnerKind.UniqueActor, actorInstanceId);
        var candidates = new List<WornCandidate>();
        foreach (var b in ListBindings(owner))
        {
            if (!TitleWornSelector.IsTitleSlot(b.Slot, out _)) continue;
            var instance = GetInstance(b.InstanceId);
            if (instance is null) continue;
            var tier = 0;
            foreach (var a in instance.Atoms)
            {
                var atom = GetAtom(a.AtomId);
                if (atom is not null && atom.Tier > tier) tier = atom.Tier;
            }
            candidates.Add(new WornCandidate(instance.ContainerId, b.InstanceId, tier));
        }
        return TitleWornSelector.PickWorn(candidates)?.ContainerId;
    }

    /// <summary>
    /// Reads the plain-string "channel" property only — no cost semantics, no evaluation,
    /// so this duplicates nothing in CostFunction (which is internal to Core by design).
    /// </summary>
    static string? TitleChannelOf(string? paramsJson)
    {
        if (string.IsNullOrWhiteSpace(paramsJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(paramsJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("channel", out var ch) &&
                ch.ValueKind == JsonValueKind.String ? ch.GetString() : null;
        }
        catch (JsonException) { return null; }
    }
}
