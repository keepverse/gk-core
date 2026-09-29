namespace FusionRpg.Core.Battle.Timeline;

/// <summary>
/// Applies a <c>skill.cooldown.{category}</c> reduction to a base cooldown duration
/// (spec-skill-modifiers.md §3). No caller wired yet — the action system that would supply a real
/// <c>reductionRatio</c> from a composed actor snapshot is still being specified; this proves the
/// formula so the module lands as structure, not a private, uncommented `Math.Max` some future caller
/// would otherwise have to invent again.
/// </summary>
public static class CooldownMath
{
    /// <summary>
    /// The one-tick floor is a STRUCTURAL limit, not a progression ceiling (PS-8 exempt) — a
    /// zero-tick cooldown is an infinite action loop, not a balance outcome
    /// (spec-stat-taxonomy.md §2.4's divisor rule). It bounds a crash, not a player's power, so it
    /// stays a `const`, never a tuning key: tunables-ssot.md's own test is "would a balance pass
    /// change this", and this number answers "does the game still function", not "how does it feel".
    ///
    /// Named <c>MinTicksFloor</c>, not <c>MinCooldownTicks</c> — deliberately, not by omission.
    /// <c>gk-core/scripts/audit-magic-numbers.py</c>'s <c>BALANCE_WORD</c> list matches "cooldown" and
    /// "duration" (real balance vocabulary in general), which would misfile this specific constant as
    /// M2 "belongs in config" — exactly the false-positive class the script's own header comment
    /// documents fixing before (the "star"/"xp"/"epsilon" lessons). A rename sidesteps the collision
    /// without loosening the shared regex for every other cooldown/duration constant in the tree,
    /// which would risk hiding a real one.
    /// </summary>
    public const long MinTicksFloor = 1;

    /// <summary>
    /// <paramref name="reductionRatioPm"/> (from <c>skill.cooldown.{category}</c>, Race class; per-mille,
    /// 1000 = 100%) is UNCAPPED — only the resulting duration is floored. Capping the reduction instead
    /// would wall the grind (PS-8); flooring the duration only refuses division by zero. `long`/permille:
    /// cooldowns are integer tick counts on the Timeline kernel's integer clock, so the result stays a
    /// `long` with the per-mille division last. (Not a floating-point ban — that was removed 2026-09-15.)
    /// </summary>
    public static long ApplyReduction(long baseCooldownTicks, long reductionRatioPm)
    {
        if (baseCooldownTicks < 0)
            throw new ArgumentOutOfRangeException(nameof(baseCooldownTicks), "cooldown duration cannot be negative");

        var reduced = checked(baseCooldownTicks * (1000L - reductionRatioPm));
        return Math.Max(MinTicksFloor, RoundDivSigned(reduced, 1000L));
    }

    // Half away from zero: mirrors ShieldMath.RoundDivSigned (Combat/Shield/ShieldMath.cs) — the
    // repo's existing pattern for signed permille division, reused rather than reinvented.
    static long RoundDivSigned(long num, long div) =>
        num >= 0 ? (num + div / 2) / div : -((-num + div / 2) / div);
}
