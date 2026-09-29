namespace FusionRpg.Core.Effects;

/// <summary>
/// A2: when TakeDamage will also emit <c>combat.hit</c> (bullet), skip mapping <c>*.damage</c> → OnDamageTaken.
/// Pure — no Unity. Melee plant bites do not skip here (AttackPlant + DealtIdentity).
/// </summary>
public static class CombatHitEmitPolicy
{
    /// <param name="willEmitHit">Host gate: debug hit-capture / LogDamage / OnDamageDealt grant.</param>
    public static bool WillSkipTakenFromDamage(
        string kind,
        IReadOnlyDictionary<string, object> payload,
        bool willEmitHit)
    {
        if (!willEmitHit) return false;
        if (!string.Equals(kind, "zombie.damage", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(kind, "plant.damage", StringComparison.OrdinalIgnoreCase))
            return false;

        if (payload.TryGetValue("damageFromIsBullet", out var b) && b is true)
            return true;

        return false;
    }
}
