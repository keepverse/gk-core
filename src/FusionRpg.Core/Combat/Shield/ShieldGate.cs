using FusionRpg.Contracts;
using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Diagnostics;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Combat.Shield;

/// <summary>
/// The gate above the Funnel — shield-system-spec.md §2.2. Sits in
/// CombatDamageDispatcher.DispatchInstant after Finalize: negative amounts drain the owner's
/// shields, only the remainder reaches the hp channel. Heals and no-shield actors pass through
/// untouched (fast path: one dictionary lookup, zero allocation, no probe).
/// </summary>
public sealed class ShieldGate
{
    public ShieldRuntime Runtime { get; }
    readonly CombatActorResolve _resolve;

    readonly List<(ShieldInstance Shield, long Spent)> _spentScratch = new();
    readonly List<ShieldInstance> _brokenScratch = new();

    public ShieldGate(ShieldRuntime runtime, CombatActorResolve resolve)
    {
        Runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
    }

    /// <summary>
    /// Apply a shield grant to one normalized board ptr — resolves the owner's derived
    /// snapshot (capacity read) and rewrites the owner key. Used by the GrantShield effect
    /// action and the debug grant endpoint.
    /// </summary>
    public ShieldApplyResult ApplyGrant(string normalizedPtr, ShieldGrant grant)
    {
        var ownerSnap = _resolve(normalizedPtr, attackerLess: false).Derived;
        return Runtime.Apply(new ShieldGrant
        {
            OwnerKey = EffectOwnerKeys.Entity(normalizedPtr),
            SourceId = grant.SourceId,
            Element = grant.Element,
            BaseHp = grant.BaseHp,
            Priority = grant.Priority,
            DurationTicks = grant.DurationTicks,
            RefillOnMerge = grant.RefillOnMerge,
            IsInnate = grant.IsInnate
        }, ownerSnap, Runtime.LastTick);
    }

    /// <summary>Legacy packet path (dispatcher) — parses the payload and delegates.</summary>
    public long AbsorbFinalized(long signedAmount, string ptr, DamagePacket packet, int hitCount)
    {
        if (signedAmount >= 0 || !Runtime.HasAnyInstances())
            return signedAmount;
        // Per-owner miss stays a zero-allocation fast path — parse only for shielded targets.
        if (Runtime.GetShields(EffectOwnerKeys.Entity(ptr)).Count == 0)
            return signedAmount;
        var components = packet.ElementPayload is { Count: > 0 }
            ? OverlayCombatCalculator.ParseComponents(packet.ElementPayload)
            : Array.Empty<ElementPayloadComponent>();
        return AbsorbFinalized(signedAmount, ptr, components, hitCount,
            attackerSnapshot: null, ownerSnapshot: null, attackerPtrForResolve: packet.ActorPtr);
    }

    /// <summary>
    /// Packet-free core (combat-unification, damage-apply-pipeline): when snapshots are
    /// provided they are the ONLY ones used (single source with the resolver — no silent
    /// divergence); null snapshots fall back to the gate's resolve delegate (legacy
    /// dispatcher path). Returns the signed amount that should reach the sink (0 = fully
    /// absorbed).
    /// </summary>
    public long AbsorbFinalized(
        long signedAmount, string ptr, IReadOnlyList<ElementPayloadComponent> components,
        int hitCount, ActorDerivedSnapshot? attackerSnapshot, ActorDerivedSnapshot? ownerSnapshot,
        string? attackerPtrForResolve = null)
    {
        if (signedAmount >= 0 || !Runtime.HasAnyInstances())
            return signedAmount;
        var ownerKey = EffectOwnerKeys.Entity(ptr);
        if (Runtime.GetShields(ownerKey).Count == 0)
            return signedAmount;

        using var _perf = PerfProbe.Measure(PerfSection.ShieldAbsorb);
        ownerSnapshot ??= _resolve(ptr, attackerLess: false).Derived;
        if (attackerSnapshot == null && !string.IsNullOrWhiteSpace(attackerPtrForResolve))
            attackerSnapshot = _resolve(attackerPtrForResolve, attackerLess: false).Derived;

        _spentScratch.Clear();
        _brokenScratch.Clear();
        var remainder = Runtime.Absorb(
            ownerKey, -signedAmount, hitCount < 1 ? 1 : hitCount,
            components, attackerSnapshot, ownerSnapshot, _spentScratch, _brokenScratch);
        return -remainder;
    }
}
