using FusionRpg.Contracts;

namespace FusionRpg.Core.Combat;

/// <summary>Adjust signed amount after targeting, before Funnel. Pass-through until CombatMath ships.</summary>
public interface ICombatMath
{
    long Finalize(long signedAmount, string ptr, DamagePacket packet, BoardEntitySnap? entity);
}

public sealed class PassThroughCombatMath : ICombatMath
{
    public static PassThroughCombatMath Instance { get; } = new();

    public long Finalize(long signedAmount, string ptr, DamagePacket packet, BoardEntitySnap? entity) =>
        signedAmount;
}
