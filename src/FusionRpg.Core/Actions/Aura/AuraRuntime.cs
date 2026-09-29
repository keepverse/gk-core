namespace FusionRpg.Core.Actions.Aura;

/// <summary>
/// aura-skill T13 (`spec-aura-action-shape.md` §5.1): one commander's aura toggle mechanics — enable,
/// disable, the active set, eviction. Enable is one of three outcomes, never a silent no-op:
/// enabled clean, enabled-with-eviction (named), or refused (typed).
///
/// <para><b>Not `IStanceCheck`.</b> `StanceRuntime` (the shipped precedent this module's shape mirrors)
/// blocks every other action while held (`UsabilityReason.StanceHeld`) — deliberately wrong for an
/// aura: *"a commander who can do nothing else while their aura runs is not a commander"* (spec §2).
/// `AuraRuntime` implements no gate-0 interface at all; it holds loadout capacity, never the kernel's
/// concurrency width. Proven by omission — <see cref="AuraRuntimeTests"/>' own
/// `Does_not_implement_IStanceCheck_the_anti_StanceHeld_regression` asserts this directly rather than
/// leaving it implicit.</para>
/// </summary>
public sealed class AuraRuntime
{
    readonly AuraActiveSet _active;
    readonly Func<string, bool> _isEquipped;

    /// <summary><paramref name="isEquipped"/> answers "does this commander carry this aura in their
    /// 5-slot loadout" — injected rather than a hard `LoadoutSet` dependency, so equipped/active stay
    /// the independent scarcities the spec names (a commander may carry five auras and run one)
    /// without this type needing to know how loadouts are stored.</summary>
    public AuraRuntime(int maxActiveAuras, Func<string, bool> isEquipped)
    {
        _active = new AuraActiveSet(maxActiveAuras);
        _isEquipped = isEquipped ?? throw new ArgumentNullException(nameof(isEquipped));
    }

    public IReadOnlyList<string> ActiveAuraIds => _active.Active;

    public bool IsActive(string auraId) => _active.IsActive(auraId);

    public AuraEnableResult Enable(string auraId)
    {
        if (!_isEquipped(auraId))
            return AuraEnableResult.Refuse(UsabilityReason.NotEquipped, auraId);

        if (_active.IsActive(auraId))
            return AuraEnableResult.Refuse(UsabilityReason.AlreadyActive, auraId);

        var evicted = _active.Activate(auraId);
        return evicted is null ? AuraEnableResult.EnabledClean : AuraEnableResult.EnabledWithEviction(evicted);
    }

    /// <summary>Safe no-op if <paramref name="auraId"/> was not active — mirrors
    /// `EffectBag.WithdrawForOwner`'s own contract.</summary>
    public bool Disable(string auraId) => _active.Deactivate(auraId);
}
