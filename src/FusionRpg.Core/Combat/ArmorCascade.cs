namespace FusionRpg.Core.Combat;

/// <summary>
/// How one damage amount is spent across a zombie's armour layers before it reaches health.
///
/// <para><b>The defect this exists to fix (2026-09-17).</b> <c>EntityStatWriter.AddZombieHp</c> applied a
/// negative FA10 delta straight to <c>theHealth</c> via <see cref="Effects.ResourceDeltaMath.Apply"/>,
/// which knows nothing about armour. Every overlay damage source therefore **bypassed both armour
/// layers entirely** — a Buckethead took overlay damage exactly like a plain zombie, and its armour sat
/// at full while its health drained. Armour was read (dumps, stat writes, base-stat capture) and
/// written, but never *spent*.</para>
///
/// <para><b>The order, ruled by the owner:</b> <c>armor2 → armor1 → hp</c>. Damage fills the outermost
/// layer first and only what survives a layer reaches the next one. Nothing bypasses a layer that still
/// has points in it.</para>
///
/// <para><b>Healing is deliberately not symmetric.</b> A positive delta goes to health alone and never
/// refills armour. Restoring armour is a different feature with its own design (what refills it, at what
/// rate, whether a destroyed bucket comes back at all) and quietly inventing it here would be a silent
/// behaviour change dressed up as a bug fix. <see cref="Apply"/> only routes damage; a caller handling a
/// heal keeps its existing health-only path.</para>
///
/// <para>Pure and Core-only: no Unity, no Il2Cpp, no store. The injector reads three fields, calls this,
/// and writes back what it returns — so the arithmetic is testable without a running game, which is the
/// reason it does not live in the writer.</para>
/// </summary>
public static class ArmorCascade
{
    /// <summary>The result of spending one damage amount across the layers.</summary>
    /// <param name="Armor2">Second armour layer after absorption.</param>
    /// <param name="Armor1">First armour layer after absorption.</param>
    /// <param name="Hp">Health after whatever reached it.</param>
    /// <param name="HpLost">How much actually reached health — 0 when armour ate all of it.</param>
    public readonly record struct Result(long Armor2, long Armor1, long Hp, long HpLost);

    /// <summary>
    /// Spends <paramref name="damage"/> across <c>armor2 → armor1 → hp</c>.
    ///
    /// <para><paramref name="damage"/> is a positive magnitude, not a signed delta — the caller converts
    /// its negative FA10 delta before calling, so the sign convention cannot be lost in the middle of the
    /// cascade. A non-positive amount is a no-op that returns the inputs unchanged rather than throwing,
    /// because a zero-damage tick is a normal thing for an effect to produce.</para>
    ///
    /// <para>A negative layer value is treated as 0: the game's own fields are <c>int</c> and nothing
    /// should ever write one negative, but a layer that somehow is must not *add* capacity by subtracting
    /// a negative from the remaining damage.</para>
    /// </summary>
    public static Result Apply(long damage, long armor2, long armor1, long hp)
    {
        if (armor2 < 0) armor2 = 0;
        if (armor1 < 0) armor1 = 0;

        if (damage <= 0) return new Result(armor2, armor1, hp, 0);

        var remaining = damage;

        var absorbed2 = Math.Min(armor2, remaining);
        armor2 -= absorbed2;
        remaining -= absorbed2;

        if (remaining > 0)
        {
            var absorbed1 = Math.Min(armor1, remaining);
            armor1 -= absorbed1;
            remaining -= absorbed1;
        }

        if (remaining <= 0) return new Result(armor2, armor1, hp, 0);

        // Only what survived both layers reaches health. `hp` may go to or below zero here; deciding
        // that a zombie is dead is the caller's job (the injector force-kills rather than writing a
        // non-positive health), because death has side effects this pure function must not own.
        var nextHp = hp - remaining;
        return new Result(armor2, armor1, nextHp, remaining);
    }
}
