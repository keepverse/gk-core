using FusionRpg.Core.Actions.Cost;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Commanders;

/// <summary>
/// aura-skill T9c: each commander's own resource pool — distinct from any battle actor's, since a
/// commander is not itself a lawn combatant. Wraps the already-shipped, general-purpose
/// <see cref="ActorResourcePools"/> (spec-action-costs.md's six pools: hp, stamina, hunger, spirit,
/// qi, poise) rather than a second implementation; the only thing this type owns is WHICH pool
/// instance belongs to which <see cref="CommanderRef"/>, and keeping that instance alive for the
/// session instead of re-creating it per match.
///
/// <para><b>Session-scoped, not match-scoped.</b> <see cref="GetOrCreate"/> creates a commander's pool
/// exactly once — every later call for the same id returns the SAME instance, so upkeep spent in one
/// battle is still spent when the next one starts. Persisting a commander's pool ACROSS sessions
/// (surviving a restart) is explicitly T18's job (`ActorResourcePools.CreateFull`'s own doc comment:
/// "T18 owns loading a persisted value in its place") — this type only keeps it alive in memory for as
/// long as the process does.</para>
/// </summary>
public sealed class CommanderResourcePools
{
    readonly Dictionary<CommanderRef, ActorResourcePools> _pools = new();

    /// <summary>Creates the commander's pool on first call (full — every resource starts at max, the
    /// same default every actor gets absent a stored value), returns the SAME instance on every call
    /// after. <paramref name="derived"/> is only consulted on the first call for a given id — later
    /// calls do not need it, matching the pattern's own doc comment that max/regen are read fresh from
    /// derived on every `Resolve`/`TrySpend`, not cached here.</summary>
    public ActorResourcePools GetOrCreate(CommanderRef id, ActorDerivedSnapshot derived, long atTick)
    {
        if (_pools.TryGetValue(id, out var existing)) return existing;
        var created = ActorResourcePools.CreateFull(derived, atTick);
        _pools[id] = created;
        return created;
    }

    public bool TryGet(CommanderRef id, out ActorResourcePools pools) => _pools.TryGetValue(id, out pools!);
}
