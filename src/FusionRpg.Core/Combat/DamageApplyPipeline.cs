using FusionRpg.Contracts;
using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Combat;

/// <summary>
/// Minimal HP delta target for the apply pipeline. Two implementations exist: the Funnel
/// adapter (overlay + battle) and direct-apply sinks (sim, tests). The owner key arrives
/// already entity-prefixed — the pipeline owns prefixing, hosts never do.
/// </summary>
public interface IHpDeltaSink
{
    bool Apply(string ownerKey, long amount, string? pluginId, string? effectId, string? grantId,
        string channel, List<ElementPayloadComponentDto>? elements);
}

/// <summary>
/// Optional sink capability for hosts that know how much damage their final target can retain
/// before the Funnel drains. The requested signed delta still travels unchanged; this reports only
/// what higher-level observers should see after overkill/heal-then-damage coalescing.
/// </summary>
public interface IRetainedHpDeltaSink
{
    bool ApplyWithRetainedAmount(string ownerKey, long amount, string? pluginId, string? effectId,
        string? grantId, string channel, List<ElementPayloadComponentDto>? elements,
        out long retainedAmount);
}

/// <summary>EffectFunnel-backed sink — the overlay/battle implementation.</summary>
public sealed class FunnelHpDeltaSink : IHpDeltaSink, IRetainedHpDeltaSink
{
    readonly EffectFunnel _funnel;
    readonly Func<string, long, long>? _retainedDamage;

    public FunnelHpDeltaSink(EffectFunnel funnel, Func<string, long, long>? retainedDamage = null)
    {
        _funnel = funnel ?? throw new ArgumentNullException(nameof(funnel));
        _retainedDamage = retainedDamage;
    }

    public bool Apply(string ownerKey, long amount, string? pluginId, string? effectId, string? grantId,
        string channel, List<ElementPayloadComponentDto>? elements) =>
        ApplyCore(ownerKey, amount, pluginId, effectId, grantId, channel, elements, out _);

    bool IRetainedHpDeltaSink.ApplyWithRetainedAmount(string ownerKey, long amount, string? pluginId,
        string? effectId, string? grantId, string channel, List<ElementPayloadComponentDto>? elements,
        out long retainedAmount) =>
        ApplyCore(ownerKey, amount, pluginId, effectId, grantId, channel, elements, out retainedAmount);

    bool ApplyCore(string ownerKey, long amount, string? pluginId, string? effectId, string? grantId,
        string channel, List<ElementPayloadComponentDto>? elements, out long retainedAmount)
    {
        var accepted = _funnel.EnqueueMutation(ownerKey, amount,
            pluginId: pluginId, effectId: effectId, grantId: grantId, channel: channel, elements: elements);
        var projected = accepted && _retainedDamage is not null
            ? _retainedDamage(ownerKey, amount)
            : amount;
        retainedAmount = amount < 0 ? projected : amount;
        return accepted;
    }
}

public enum DamageApplyOutcome
{
    Applied,
    SinkRefused,
    /// <summary>The shield gate consumed the whole delta — nothing reaches the sink.</summary>
    FullyAbsorbed
}

/// <summary>P1 (spec-gate-counters.md §7): the discriminator `DamageApplyPipeline.Apply` could not
/// tell before this — a direct hit from a DoT pulse. `element_mastery`'s own rule (§2.2d) needs it:
/// crediting a pulse would let one applied status earn an elemental credit every tick for its whole
/// duration, double-paying for the one application `status_applied` already credited.</summary>
public enum DamageOrigin
{
    DirectHit,
    StatusPulse,
}

/// <summary>
/// <paramref name="AppliedAmount"/> is the sink-retained signed amount when the host implements
/// <see cref="IRetainedHpDeltaSink"/>; legacy sinks keep their requested post-shield amount. The
/// transport delta itself is never rewritten.
/// </summary>
public readonly record struct DamageApplyResult(
    DamageApplyOutcome Outcome, long AppliedAmount, long AbsorbedAmount);

/// <summary>
/// The one apply path (combat-unification, spec-damage-apply-pipeline): finalized signed
/// delta → shield gate → sink. Stateless — hosts mount their own gate and sink. Positive
/// deltas (heals) bypass the gate; zero un-absorbed deltas still reach the sink (miss
/// telemetry parity with the pre-extraction dispatcher); a fully absorbed delta reaches
/// nothing. Single-target by design — the ptr is the raw host actor id, never pre-prefixed.
/// </summary>
public static class DamageApplyPipeline
{
    public static DamageApplyResult Apply(
        string ptr,
        long finalizedSignedAmount,
        int hitCount,
        IReadOnlyList<ElementPayloadComponent> components,
        ActorDerivedSnapshot? attackerSnapshot,
        ActorDerivedSnapshot? ownerSnapshot,
        Shield.ShieldGate? shieldGate,
        IHpDeltaSink sink,
        string? pluginId = null,
        string? effectId = null,
        string? grantId = null,
        List<ElementPayloadComponentDto>? elementsDto = null,
        string? attackerPtr = null,
        Action<long>? onHpDamageApplied = null,
        DamageOrigin origin = DamageOrigin.DirectHit)
    {
        if (string.IsNullOrWhiteSpace(ptr))
            throw new ArgumentException("ptr is required", nameof(ptr));
        if (ptr.StartsWith("entity:", StringComparison.Ordinal))
            throw new ArgumentException(
                "ptr must be the raw host actor id — the pipeline owns key prefixing", nameof(ptr));
        if (sink == null) throw new ArgumentNullException(nameof(sink));

        var amount = finalizedSignedAmount;
        long absorbed = 0;
        if (amount < 0 && shieldGate != null)
        {
            var afterShield = shieldGate.AbsorbFinalized(
                amount, ptr, components, hitCount, attackerSnapshot, ownerSnapshot, attackerPtr);
            absorbed = afterShield - amount;
            amount = afterShield;
            if (amount == 0)
                return new DamageApplyResult(DamageApplyOutcome.FullyAbsorbed, 0, absorbed);
        }

        var ownerKey = EffectOwnerKeys.Entity(ptr);
        long retainedAmount;
        bool ok;
        if (sink is IRetainedHpDeltaSink retainedSink)
            ok = retainedSink.ApplyWithRetainedAmount(
                ownerKey, amount, pluginId, effectId, grantId, "hp", elementsDto, out retainedAmount);
        else
        {
            ok = sink.Apply(ownerKey, amount, pluginId, effectId, grantId, "hp", elementsDto);
            retainedAmount = amount;
        }
        if (!ok)
            return new DamageApplyResult(DamageApplyOutcome.SinkRefused, 0, absorbed);
        if (retainedAmount < 0)
            onHpDamageApplied?.Invoke(retainedAmount);
        return new DamageApplyResult(DamageApplyOutcome.Applied, retainedAmount, absorbed);
    }

    /// <summary>
    /// Funnel-specialized entry for the dispatcher hot path — zero allocation (no sink
    /// adapter, no closures), byte-identical to the pre-extraction tail: legacy packet gate
    /// (lazy payload parse), packet channel preserved (non-hp still reaches the funnel guard
    /// for its refusal telemetry), NoteOverlayDamage on applied damage. Same stage order as
    /// <see cref="Apply"/> — semantics locked by the cross-entry parity tests.
    /// </summary>
    public static DamageApplyResult ApplyPacketToFunnel(
        DamagePacket packet,
        string ptr,
        long finalizedSignedAmount,
        int hitCount,
        Shield.ShieldGate? shieldGate,
        EffectFunnel funnel,
        EffectEventDto? ev,
        DamageOrigin origin = DamageOrigin.DirectHit)
    {
        if (funnel == null) throw new ArgumentNullException(nameof(funnel));
        var amount = finalizedSignedAmount;
        long absorbed = 0;
        if (amount < 0 && shieldGate != null)
        {
            var afterShield = shieldGate.AbsorbFinalized(amount, ptr, packet, hitCount);
            absorbed = afterShield - amount;
            amount = afterShield;
            if (amount == 0)
                return new DamageApplyResult(DamageApplyOutcome.FullyAbsorbed, 0, absorbed);
        }

        var ok = funnel.EnqueueMutation(
            EffectOwnerKeys.Entity(ptr),
            amount,
            pluginId: packet.PluginId ?? "combat",
            effectId: packet.EffectId,
            grantId: string.IsNullOrWhiteSpace(packet.SourceGrantId) ? null : packet.SourceGrantId,
            channel: packet.Channel ?? "hp",
            elements: packet.ElementPayload);
        if (!ok)
            return new DamageApplyResult(DamageApplyOutcome.SinkRefused, 0, absorbed);
        if (amount < 0)
            funnel.NoteOverlayDamage(
                packet.ActorPtr ?? ev?.ActorPtr, ptr, amount, packet.ChainDepth, packet.SourceGrantId, ev);
        return new DamageApplyResult(DamageApplyOutcome.Applied, amount, absorbed);
    }
}
