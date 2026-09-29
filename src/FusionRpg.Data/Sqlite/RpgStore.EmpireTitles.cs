using FusionRpg.Core.Achievements;
using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Data;

// Empire Hall (spec-empire-titles.md): 3-slot equip over the existing effect_binding
// table (owner player:{id}, slot hall-{1,2,3}) — no second binding table. Equip
// validates capacity, ownership, and the P2 upkeep rule via HallLoadout.Resolve;
// the Production/Pressure call site consumes GetHallIntent (provider-owned
// integration applies it — this module never edits economy files).
public sealed partial class RpgStore
{
    static string HallSlotName(int slot) => $"hall-{slot}";

    IReadOnlyList<BindingRow> HallBindingsUnlocked(long playerId)
    {
        var owner = new OwnerScope(OwnerKind.Player, playerId.ToString());
        var all = ListBindings(owner);
        var hall = new List<BindingRow>();
        foreach (var b in all)
            if (HallLoadout.IsHallSlot(b.Slot, out _)) hall.Add(b);
        return hall;
    }

    List<HallTitleInput> HallInputsUnlocked(IEnumerable<BindingRow> bindings)
    {
        var inputs = new List<HallTitleInput>();
        foreach (var b in bindings)
        {
            var instance = GetInstance(b.InstanceId);
            if (instance is null) continue;
            foreach (var a in instance.Atoms)
            {
                var atom = GetAtom(a.AtomId);
                if (atom is null) continue;
                inputs.Add(new HallTitleInput(
                    instance.ContainerId, atom.FamilyId, atom.Variant ?? "", atom.Tier));
            }
        }
        return inputs;
    }

    /// <summary>
    /// Equip a player-owned title instance into a Hall slot. Idempotent re-equip of the
    /// same instance is Ok. Yield-without-upkeep refuses naming the container (P2).
    /// </summary>
    public AtomRejection EquipHallTitle(
        long playerId, int slot, string instanceId,
        AchievementTitlesTuning tuning, string? utc = null)
    {
        if (slot < 1 || slot > HallLoadout.HallSlots)
            return AtomRejection.Fail(AtomRejectionReason.BadParamValue,
                $"hall slot {slot} is outside 1..{HallLoadout.HallSlots}");
        var instance = GetInstance(instanceId);
        if (instance is null)
            return AtomRejection.Fail(AtomRejectionReason.StaleInstance,
                $"instance '{instanceId}' does not exist");
        var owner = new OwnerScope(OwnerKind.Player, playerId.ToString());
        var owned = false;
        foreach (var b in ListBindings(owner))
            if (b.InstanceId == instanceId) { owned = true; break; }
        if (!owned)
            return AtomRejection.Fail(AtomRejectionReason.BadParamValue,
                $"instance '{instanceId}' is not bound to player:{playerId}");

        var keep = new List<BindingRow>();
        foreach (var b in HallBindingsUnlocked(playerId))
            if (b.Slot != HallSlotName(slot)) keep.Add(b);
        var candidate = new List<HallTitleInput>();
        candidate.AddRange(HallInputsUnlocked(keep));
        foreach (var a in instance.Atoms)
        {
            var atom = GetAtom(a.AtomId);
            if (atom is null) continue;
            candidate.Add(new HallTitleInput(
                instance.ContainerId, atom.FamilyId, atom.Variant ?? "", atom.Tier));
        }
        var (intent, cause) = HallLoadout.Resolve(candidate, tuning);
        if (intent is null)
            return AtomRejection.Fail(AtomRejectionReason.BadParamValue, cause!);

        return Bind(new BindingRow
        {
            OwnerKind = OwnerKind.Player,
            OwnerKey = playerId.ToString(),
            InstanceId = instanceId,
            Slot = HallSlotName(slot),
            Priority = 1,
            Source = $"hall:{instanceId}",
        }, bindingId: $"hall-{playerId}-{slot}", boundUtc: utc);
    }

    /// <summary>Withdraw a Hall slot binding. Absent slot is Ok (idempotent).</summary>
    public AtomRejection UnequipHallTitle(long playerId, int slot)
    {
        foreach (var b in HallBindingsUnlocked(playerId))
            if (b.Slot == HallSlotName(slot)) Withdraw(b.BindingId);
        return AtomRejection.Ok;
    }

    /// <summary>
    /// The intent the economy Production/Pressure step consumes. Equipped state passed
    /// validation at equip; a refusal here names corruption (absolute bounds throw).
    /// </summary>
    public HallIntent GetHallIntent(long playerId, AchievementTitlesTuning tuning)
    {
        var (intent, cause) = HallLoadout.Resolve(
            HallInputsUnlocked(HallBindingsUnlocked(playerId)), tuning);
        return intent
            ?? throw new InvalidOperationException($"hall: equipped state refused: {cause}");
    }
}
