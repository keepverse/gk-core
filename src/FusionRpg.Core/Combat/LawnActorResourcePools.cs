using FusionRpg.Core.Actions.Cost;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Combat;

/// <summary>
/// E28 fix #1 (spec-param-parity.md §3 row 1): the resource-pool registry for LAWN actors — a real
/// Plant/Zombie in a live match, as opposed to <see cref="FusionRpg.Core.Commanders.CommanderResourcePools"/>'s
/// session-scoped <c>CommanderId</c> actors. Wraps the same already-shipped
/// <see cref="ActorResourcePools"/> (spec-action-costs.md's six pools: hp, stamina, hunger, spirit,
/// qi, poise) rather than a second implementation; the only thing this type owns is WHICH pool
/// instance belongs to which lawn actor.
///
/// <para><b>Keyed by combat ptr, not commander id.</b> A lawn actor has no <c>CommanderId</c> — every
/// other Injector-side lookup for a live match (<c>InjectorEntityRegistry.FindZombie</c>/
/// <c>FindPlant</c>, <c>GrantedBulletModifyAtomReader</c>'s owner keys) resolves by combat ptr, and
/// this follows the same precedent rather than inventing a fourth keying scheme. Keys are normalized
/// through <see cref="CombatPtr.Normalize"/>, so <c>"0xABC"</c>, <c>"entity:abc"</c> and <c>"ABC"</c>
/// all resolve to the same pool.</para>
///
/// <para><b>Unity-free by construction</b> — same reasoning as <c>GrantedBulletModifyAtomReader</c>:
/// this only stores <see cref="ActorResourcePools"/> instances against a string key, so it lives in
/// <c>FusionRpg.Core</c> and is exercised directly by <c>FusionRpg.Core.Tests</c> even though the
/// injector (its only production caller) is not built by CI.</para>
/// </summary>
public sealed class LawnActorResourcePools
{
    readonly Dictionary<string, ActorResourcePools> _pools = new(StringComparer.Ordinal);

    /// <summary>Creates the actor's pool on first call for a given ptr (full — every resource starts
    /// at max, the same default every actor gets absent a stored value), returns the SAME instance on
    /// every call after. <paramref name="derived"/> is only consulted on the first call for a given
    /// ptr — later calls do not need it, matching <see cref="ActorResourcePools"/>'s own contract that
    /// max/regen are read fresh from derived on every <c>Resolve</c>/<c>TrySpend</c>/<c>Add</c>, never
    /// cached here or there.</summary>
    public ActorResourcePools GetOrCreate(string targetPtr, ActorDerivedSnapshot derived, long atTick)
    {
        var key = CombatPtr.Normalize(targetPtr);
        if (_pools.TryGetValue(key, out var existing)) return existing;
        var created = ActorResourcePools.CreateFull(derived, atTick);
        _pools[key] = created;
        return created;
    }

    public bool TryGet(string targetPtr, out ActorResourcePools pools) =>
        _pools.TryGetValue(CombatPtr.Normalize(targetPtr), out pools!);

    /// <summary>Drops one actor's pool. Call on death/despawn (mirroring
    /// <c>InjectorEntityRegistry.Remove</c>'s own per-actor lifecycle flush for shields) so a reused
    /// ptr in a later match never inherits a stranger's drained pool.</summary>
    public bool Remove(string targetPtr) => _pools.Remove(CombatPtr.Normalize(targetPtr));

    /// <summary>Drops every pool. Call at match end / board clear, alongside
    /// <c>InjectorEntityRegistry.Clear</c>.</summary>
    public void Clear() => _pools.Clear();

    public int Count => _pools.Count;

    /// <summary>
    /// `lawn-combat-wire` T12b (spec-basic-attack-cost.md, wire 3: "regen as a third kernel kind"): a
    /// snapshot of every currently-tracked ptr, for a periodic caller (`KernelDriveHost`'s own 100ms
    /// grid) that wants to settle/observe every live actor's pools without needing a second registry
    /// of "which ptrs exist" — this one already is that registry. Returns a defensive copy (an array,
    /// not the live dictionary's own enumerator) so a caller may safely remove a ptr mid-iteration
    /// (e.g. a settle that discovers a stale entry) without an `InvalidOperationException`.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, ActorResourcePools>> Snapshot() =>
        _pools.Count == 0
            ? Array.Empty<KeyValuePair<string, ActorResourcePools>>()
            : new List<KeyValuePair<string, ActorResourcePools>>(_pools);
}
