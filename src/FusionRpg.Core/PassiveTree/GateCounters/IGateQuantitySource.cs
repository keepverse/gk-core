namespace FusionRpg.Core.PassiveTree.GateCounters;

/// <summary>
/// spec-gate-counters.md §5.2 -- what <c>tree-resolve</c> asks the actor for a gate quantity in, and
/// the only unit it is allowed to see: aptitude-point-EQUIVALENTS, always, for every family. Nothing
/// in the contract mentions a raw count, an index, or the aptitude-allocation scope machinery; the conversion is
/// this module's job, not the caller's, so <c>req(t)</c> means the same threshold on every tree
/// category regardless of which family answers it.
///
/// <para><b>Who owns the actual index/equivalents math for <c>element_mastery</c> and
/// <c>status_applied</c> is task G4's job</b> (<c>MasteryIndex</c>, spec §9), not this interface's --
/// this file only fixes the shape every producer answers through, so G3's registration-exclusivity
/// guarantee (§12, below) and G4's real producers share one contract rather than each inventing
/// their own.</para>
/// </summary>
public interface IGateQuantitySource
{
    /// <summary>The one family this source answers for -- <c>"status_applied"</c>,
    /// <c>"element_mastery"</c>, or a future family this module has not named yet. Never empty.</summary>
    string Family { get; }

    /// <summary>Aptitude-point-equivalents for <paramref name="id"/>, given <paramref name="actor"/>.
    /// A family with no registered source answers <c>0</c> at the registry level (§5.2: "a known
    /// content gap", never inferred as "not started") -- this method itself is only ever called once a
    /// producer exists for <paramref name="id"/>.Family.</summary>
    long AptitudePointEquivalents(GateQuantityId id, GateActorContext actor);
}

/// <summary>
/// The actor identity an <see cref="IGateQuantitySource"/> resolves a quantity for -- today just the
/// player-scoped <see cref="GateOwnerKey"/> every counter already credits against (§2's "whose
/// progress? the player's, not the individual creature's"). A record so task G4 (or a later
/// <c>UniqueCreature</c>-scoped source, G7) can widen it with additional fields without breaking this
/// contract's callers.
/// </summary>
public readonly record struct GateActorContext(GateOwnerKey Owner);
