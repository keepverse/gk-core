using FusionRpg.Contracts;

namespace FusionRpg.Core.Combat;

/// <summary>Delegates to overlay math when enabled; otherwise pass-through.</summary>
public sealed class ConditionalOverlayCombatMath : ICombatMath
{
    readonly OverlayCombatMath _overlay;
    readonly ICombatMath _passThrough;

    public ConditionalOverlayCombatMath(
        OverlayCombatMath overlay,
        ICombatMath? passThrough = null)
    {
        _overlay = overlay ?? throw new ArgumentNullException(nameof(overlay));
        _passThrough = passThrough ?? PassThroughCombatMath.Instance;
    }

    public Func<bool>? IsEnabled { get; init; }

    public long Finalize(long signedAmount, string ptr, DamagePacket packet, BoardEntitySnap? entity)
    {
        if (IsEnabled?.Invoke() != true)
            return _passThrough.Finalize(signedAmount, ptr, packet, entity);
        return _overlay.Finalize(signedAmount, ptr, packet, entity);
    }
}
