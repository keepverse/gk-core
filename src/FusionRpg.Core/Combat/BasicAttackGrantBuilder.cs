using FusionRpg.Contracts;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Combat;

/// <summary>
/// lawn-combat-wire T10 (spec-basic-attack-grant.md): the one construction site for the per-actor
/// basic-attack effect grant that makes <c>EffectRuntime.HasOnDamageDealtGrant()</c> true on the lawn.
///
/// <para>The def is already shipped, purpose-built (`gk-data/packs/fusion/data/seed/atoms/fx-core.json:33`, compiled into
/// <c>EffectAtomCatalog.CreateAll()</c> as <see cref="EffectId"/>): <c>kind: resource.delta</c>,
/// <c>trigger: OnDamageDealt</c>. Nothing binds it to any actor today — this is that bind, built the
/// SAME shape <c>AtomCompiler.EmitDefAndGrant</c> already bakes for a compiled owner (Phase 7 F3.1):
/// an <c>elementPayload</c> built by <see cref="HybridPayload.BuildOverlay"/> straight from the owner's
/// OWN species element, never authored on the action row (`action-ideal.md:158-160`) — that is why one
/// shared <c>act.attack</c> row still yields per-species elemental damage.</para>
///
/// <para>Pure and Unity-free on purpose: the injector (`LawnBasicAttackGrantBinder`) resolves the
/// owner's element and calls this once per spawn; this class only builds the DTO.</para>
/// </summary>
public static class BasicAttackGrantBuilder
{
    /// <summary>The compiled def this grant points at — see `EffectAtomCatalog.Generated.cs`.</summary>
    public const string EffectId = "fx.overlay_damage";

    const string PluginId = "lawn-basic-attack";
    const string GrantPrefix = "lawn-basic-attack";

    /// <summary>Structural, not tunable (lawn-combat-wire L-N36): the proc policy gives every <c>OnDamageDealt</c> grant
    /// without an <c>icd_ms</c> overlay a 250 ms internal cooldown (<c>EffectProcPolicy.ResolveIcdMs</c>). On a basic attack
    /// that cooldown let only the first victim of a multi-victim swing ride and skipped a second pea fired within 250 ms,
    /// after the swing's cost was already paid. The rate control this spec names is the per-swing cost
    /// (<c>spec-lawn-hit-entry.md</c> "One swing, one trigger"), so the rider has no cooldown of its own.</summary>
    public const int RiderIcdMs = 0;

    /// <summary>
    /// One grant for <paramref name="ptr"/>, scoped to `entity:{ptr}` — never `plant:{typeId}` /
    /// `zombie:{typeId}` (the spec's own boundary: a type-scoped grant cannot express one specimen's
    /// own element, which is the whole point). <paramref name="ptr"/> travels verbatim into the owner
    /// key and the grant id; the caller is responsible for handing it the same spelling it will later
    /// use to withdraw (already true today — `GameHooks.ForgetEntity`'s withdraw path compares owner
    /// keys via `StatApplyScope.Normalize`, so an exact-casing mismatch cannot leak a stale grant).
    ///
    /// <para>Deterministic <c>GrantId</c> — a repeat bind for the same ptr (e.g. a re-resolve after a
    /// side change, or two coalesced spawn records racing at drain time) is an idempotent upsert, never
    /// a duplicate grant.</para>
    /// </summary>
    /// <param name="amount">action-enrich AE2.2 (`spec-lawn-action-base.md` §Design): the action's own
    /// base, already resolved by the caller as <c>-ActionBaseMath.BasePerHit(base, P(Θ_owner))</c>
    /// (negative = damage) and written into the grant overlay as a PLAIN amount, so the rider no longer
    /// reads the host's event amount. <c>0</c> — the default, and not a value the lawn ever passes — omits
    /// the key, leaving the def's own <c>eventField</c> resolution in charge exactly as before; that is
    /// what keeps every pre-AE2.2 caller byte-identical instead of silently dealing zero.</param>
    public static EffectGrantDto Build(
        string ptr, ElementTypeId? primary, ElementTypeId? secondary = null,
        int secondaryWeightMilli = 0, long amount = 0)
    {
        if (string.IsNullOrWhiteSpace(ptr))
            throw new ArgumentException("ptr is required.", nameof(ptr));

        var normalized = ptr.Trim();
        var overlayElementPayload = HybridPayload.BuildOverlay(primary, secondary, secondaryWeightMilli);

        var overlay = new Dictionary<string, object?>(StringComparer.Ordinal);
        // No elementPayload for a Neutral owner (no primary element) — OverlayCombatMath.Finalize passes
        // the amount through unchanged with no payload, the documented degenerate case; never an
        // empty-list payload riding the overlay.
        if (overlayElementPayload != null) overlay["elementPayload"] = overlayElementPayload;
        overlay["icd_ms"] = RiderIcdMs;
        if (amount != 0) overlay["amount"] = amount;
        // AE2.1/AE2.2: the lawnmower / board-wipe refusal, in the grammar that owns it — a plain amount
        // never reaches `DamagePacketBuilder`'s event-field branch, so the guard has to live here.
        overlay["filters"] = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["excludeInstakill"] = true,
        };

        return new EffectGrantDto
        {
            GrantId = GrantIdFor(normalized),
            EffectId = EffectId,
            OwnerKind = "entity",
            OwnerKey = EffectOwnerKeys.Entity(normalized),
            PluginId = PluginId,
            Priority = 0,
            Overlay = overlay,
        };
    }

    /// <summary>Keyed on the canonical ptr (<see cref="CombatPtr.Normalize"/>): "1A2B", "1a2b" and
    /// "0x1a2b" are one entity and must upsert one grant, never hold two that both fire.</summary>
    public static string GrantIdFor(string ptr) => GrantPrefix + "@" + CombatPtr.Normalize(ptr);

    /// <summary>True for a grant this builder made — what the kill switch withdraws when it turns off mid-match
    /// (lawn-combat-wire L-N8).</summary>
    public static bool IsBasicAttackGrantId(string? grantId) =>
        grantId is not null && grantId.StartsWith(GrantPrefix + "@", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// lawn-combat-wire L-N8: detects the kill switch turning off. With the switch off, <c>ShouldApplyRider</c> passes every
/// <c>OnDamageDealt</c> record through, so grants bound while it was on kept applying riders with no cost. The binder
/// withdraws them on the off edge; failing closed in <c>ShouldApplyRider</c> instead would also silence every other
/// <c>OnDamageDealt</c> effect. The first observation is never an edge: a process that starts with the switch off has
/// bound nothing.
/// </summary>
public sealed class FeatureSwitchEdge
{
    bool? _last;

    /// <returns><c>true</c> exactly once per on→off transition.</returns>
    public bool TurnedOff(bool enabled)
    {
        var turnedOff = _last == true && !enabled;
        _last = enabled;
        return turnedOff;
    }
}
