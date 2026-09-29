using FusionRpg.Core.Actions.Cost;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Delve.Events;

/// <summary>
/// `event-deck` D3.6 (spec-event-deck.md §5) — PARTIALLY BUILT: the out-of-fight `resource.delta`
/// executor that applies an already-resolved delta to a member's own six pools. `ResourceDelta`
/// (channel + signed amount) is a plain input, deliberately NOT parsed here from a real
/// `InstanceRow.Atoms[].ValuesJson` — no consumer anywhere in the codebase turns an instance's frozen
/// atom JSON into a live executed effect yet, for ANY instance (item, loot or event alike; checked,
/// confirmed absent). Building that bridge is a foundational, cross-program change outside this task's
/// own file list; extracting `channel`/`amount` from a real instance stays the caller's job (`EventDeck`,
/// D3.3, itself unbuilt) — this class only owns applying an already-extracted delta to a pool.
/// </summary>
public static class DelveResourceDelta
{
    /// <summary>One already-resolved `resource.delta` atom's own target channel and signed amount —
    /// positive heals/restores, negative drains, matching <see cref="ActorResourcePools.Add"/>'s own
    /// signed-delta contract.</summary>
    public readonly record struct ResourceDelta(string Channel, long Amount);

    /// <summary>
    /// Spec §5, verbatim: "the loop over <c>DerivedStatChannels.ResourceIds</c>, never a hand list" —
    /// a delta naming any of the six registered ids is applied through
    /// <see cref="ActorResourcePools.Add"/> (settle-then-clamp-to-[0,max], the same rail
    /// <c>RestResolver.Heal</c> already relies on rather than a second clamp here); a delta naming
    /// anything else refuses loudly rather than silently doing nothing. Runs only between rooms — in
    /// -fight `hp` stays FA10's own path (spec, verbatim), never this one's.
    /// </summary>
    public static IReadOnlyDictionary<string, long> Apply(
        IReadOnlyDictionary<string, long> pools,
        IReadOnlyList<ResourceDelta> deltas,
        ActorDerivedSnapshot derived,
        long atTick)
    {
        if (pools is null) throw new ArgumentNullException(nameof(pools));
        if (deltas is null) throw new ArgumentNullException(nameof(deltas));
        if (derived is null) throw new ArgumentNullException(nameof(derived));

        var actorPools = ActorResourcePools.FromStored(pools, atTick);
        foreach (var delta in deltas)
        {
            if (!DerivedStatChannels.ResourceIds.Contains(delta.Channel))
                throw new ArgumentException(
                    $"'{delta.Channel}' is not one of the six registered resource ids", nameof(deltas));
            actorPools.Add(delta.Channel, delta.Amount, atTick, derived);
        }

        return actorPools.SettleAll(atTick, derived);
    }
}
