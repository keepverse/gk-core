using FusionRpg.Contracts;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Stats;

namespace FusionRpg.Core.Match;

/// <summary>
/// Sole rewrite seam for durable <c>instance:{guid}</c> → live <c>entity:{ptr}</c> (W5-B).
/// Hot Resolve / EffectBag still reject raw <c>instance:</c>.
/// </summary>
public static class UniqueOwnerBinder
{
    /// <summary>
    /// The PRODUCER half of the same grammar <see cref="BindOwnerKey"/> consumes: the owner key a
    /// durable, server-side grant carries before any live pointer exists.
    ///
    /// <para><b>Only <see cref="OwnerKind.UniqueActor"/> gets a scoped key.</b> That scope names one
    /// persistent actor, so a passive modifier sourced from it must not reach the whole match — and
    /// <c>instance:{id}</c> is the one key the hot path refuses outright, which is what makes an
    /// unbound one fail loudly instead of applying to the wrong scope.</para>
    ///
    /// <para>Every other scope answers <see cref="EffectOwnerKeys.Match"/>, unchanged. A
    /// <see cref="OwnerKind.Player"/> has no single live entity to scope a passive buff to, so
    /// match-wide is correct there rather than merely tolerated; <see cref="OwnerKind.Entity"/> and the
    /// type scopes are already resolved elsewhere and are deliberately not widened here.</para>
    /// </summary>
    public static string OwnerKeyForDurableGrant(OwnerScope scope) =>
        scope.Kind == OwnerKind.UniqueActor && !string.IsNullOrWhiteSpace(scope.Key)
            ? StatApplyScope.Normalize(EffectOwnerKeys.Instance(scope.Key))
            : EffectOwnerKeys.Match;

    /// <summary>The <c>ownerKind</c> that belongs beside a key from <see cref="OwnerKeyForDurableGrant"/>.</summary>
    public static string OwnerKindForDurableGrant(string? ownerKey) =>
        StatApplyScope.IsInstanceOwnerKey(ownerKey) ? EffectOwnerKeys.InstanceKind : "match";

    public static string ToEntityKey(string? instanceId, string ptr)
    {
        if (string.IsNullOrWhiteSpace(ptr))
            throw new ArgumentException("ptr required", nameof(ptr));
        _ = instanceId; // retained for call-site clarity / future audit
        return EffectOwnerKeys.Entity(NormalizePtrHex(ptr));
    }

    /// <summary>
    /// If <paramref name="ownerKey"/> is <c>instance:</c>, rewrite to <c>entity:{ptr}</c>;
    /// otherwise return normalized known key (or original trimmed).
    /// </summary>
    public static string BindOwnerKey(string? ownerKey, string ptr)
    {
        if (string.IsNullOrWhiteSpace(ptr))
            throw new ArgumentException("ptr required", nameof(ptr));

        if (StatApplyScope.IsInstanceOwnerKey(ownerKey))
            return ToEntityKey(ExtractInstanceId(ownerKey), ptr);

        if (string.IsNullOrWhiteSpace(ownerKey))
            return EffectOwnerKeys.Match;

        return StatApplyScope.Normalize(ownerKey);
    }

    public static EffectGrantDto BindGrant(EffectGrantDto dto, string ptr)
    {
        if (dto == null) throw new ArgumentNullException(nameof(dto));
        return new EffectGrantDto
        {
            GrantId = dto.GrantId,
            EffectId = dto.EffectId,
            OwnerKind = dto.OwnerKind,
            OwnerKey = BindOwnerKey(dto.OwnerKey, ptr),
            PluginId = dto.PluginId,
            Priority = dto.Priority,
            Overlay = dto.Overlay
        };
    }

    /// <summary>True when Resolve / Grant must never see this key on the hot path.</summary>
    public static bool WouldRejectOnHot(string? ownerKey) =>
        StatApplyScope.IsInstanceOwnerKey(ownerKey);

    public static string? ExtractInstanceId(string? ownerKey)
    {
        var key = StatApplyScope.Normalize(ownerKey);
        if (!key.StartsWith("instance:", StringComparison.Ordinal)) return null;
        var id = key["instance:".Length..];
        return string.IsNullOrWhiteSpace(id) ? null : id;
    }

    static string NormalizePtrHex(string ptr) =>
        MatchUniqueBindingsFacet.NormalizePtr(ptr);
}
