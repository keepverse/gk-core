using FusionRpg.Core.Actions.Cost;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Delve.Attrition;

/// <summary>
/// `delve-attrition` D2.17 (spec-delve-attrition.md §2) — carries a member's six pools into and out
/// of one room's battle. `ActorResourcePools.FromStored` in, `SettleAll` out, looping over
/// <see cref="DerivedStatChannels.ResourceIds"/> — never a hard-coded five, so a seventh resource
/// (were one ever added) would be caught by the six-coverage loop rather than silently dropped.
///
/// <para><b>`hp` is the one seat, never duplicated.</b> `CurrentHp` (`BattleActorSetup`) and
/// `HpRemaining` (`BattleActorResult`) are `delve-battle-profile`'s own additive fields; this module
/// never touches them directly — <see cref="SplitForCarryIn"/> and <see cref="CarryOut"/> are the pure
/// translation between "one dictionary of six" (`DelveMemberState.Pools`) and "hp separate, five
/// alongside" (the setup/result shape), with <see cref="CarryOut"/>'s own assertion as the seam that
/// catches the one seat ever drifting into two.</para>
/// </summary>
public static class PartyPoolsCarry
{
    /// <summary>§2 Carry-in: "`CurrentHp` is the one hp seat; `CarryInPools` carries the five non-hp
    /// ids." Throws if any of the six ids is missing — the same loud, six-coverage discipline
    /// <see cref="ActorResourcePools.FromStored"/> itself already holds to.</summary>
    public static (long CurrentHp, IReadOnlyDictionary<string, long> CarryInPools) SplitForCarryIn(IReadOnlyDictionary<string, long> pools)
    {
        if (pools is null) throw new ArgumentNullException(nameof(pools));

        long? hp = null;
        var rest = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var id in DerivedStatChannels.ResourceIds)
        {
            if (!pools.TryGetValue(id, out var value))
                throw new ArgumentException($"pools is missing resource id '{id}' — all six are required.", nameof(pools));
            if (id == "hp") hp = value;
            else rest[id] = value;
        }

        return (hp!.Value, rest);
    }

    /// <summary>Rebuilds the seeded <see cref="ActorResourcePools"/> a room's battle carries in —
    /// `CurrentHp` plus the five `CarryInPools` ids, merged back into the six-id contract
    /// <see cref="ActorResourcePools.FromStored"/> requires.</summary>
    public static ActorResourcePools BuildForBattle(long currentHp, IReadOnlyDictionary<string, long> carryInPools, long atTick)
    {
        if (carryInPools is null) throw new ArgumentNullException(nameof(carryInPools));

        var stored = new Dictionary<string, long>(carryInPools, StringComparer.Ordinal) { ["hp"] = currentHp };
        return ActorResourcePools.FromStored(stored, atTick);
    }

    /// <summary>
    /// §2 Carry-out: `SettleAll` → `members[].pools`. <b>Asserts `hpRemaining == pools["hp"]`</b> — the
    /// one hp seat must never diverge between the battle's own HP tracking and the pool that also
    /// claims to hold it; a mismatch here means something wrote `hp` through a second path (a bug this
    /// module exists to catch, not to silently reconcile).
    /// </summary>
    public static IReadOnlyDictionary<string, long> CarryOut(ActorResourcePools pools, long hpRemaining, long atTick, ActorDerivedSnapshot derived)
    {
        if (pools is null) throw new ArgumentNullException(nameof(pools));
        if (derived is null) throw new ArgumentNullException(nameof(derived));

        var settled = pools.SettleAll(atTick, derived);
        if (!settled.TryGetValue("hp", out var poolsHp))
            throw new InvalidOperationException("SettleAll did not return an 'hp' entry — the six-id contract is broken.");
        if (poolsHp != hpRemaining)
            throw new InvalidOperationException(
                $"hp drift: the battle's own HpRemaining ({hpRemaining}) != pools[\"hp\"] ({poolsHp}) — the one hp seat must never diverge.");

        return settled;
    }
}
