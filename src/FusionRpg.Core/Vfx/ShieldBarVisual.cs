namespace FusionRpg.Core.Vfx;

/// <summary>
/// Presentation-only shield bar length rules. Absorb math stays continuous;
/// the world VFX fill snaps to 10% capacity buckets (floor).
/// </summary>
public static class ShieldBarVisual
{
    /// <summary>
    /// Display fill ratio in [0,1]. Floor to tenths (89% → 0.8).
    /// While hp &gt; 0, never returns 0 (minimum 0.1) so a nearly-broken shield still shows a sliver.
    /// </summary>
    public static float DisplayRatio(long hp, long maxHp)
    {
        if (hp <= 0 || maxHp <= 0)
            return 0f;

        var raw = (float)hp / maxHp;
        if (raw > 1f) raw = 1f;
        if (raw < 0f) raw = 0f;

        var step = MathF.Floor(raw * 10f) / 10f;
        if (step <= 0f)
            return 0.1f;
        return step;
    }

    /// <summary>
    /// The shield's <b>true</b> fill ratio — the raw <c>hp / max</c>, not the tenth-bucketed display
    /// fill. Clamped to <c>[0,1]</c>: a merge can over-heal a pool past its own max, and a negative is
    /// not a ratio. Zero on either rail (<c>hp &lt;= 0</c> or <c>maxHp &lt;= 0</c>), matching the inline
    /// forms it replaces. Shared because two HUD folds computed it themselves, byte-identically in the
    /// debug payload (combat-math-dedup T16, D21).
    /// </summary>
    public static float TrueRatio(long hp, long maxHp)
    {
        if (hp <= 0 || maxHp <= 0)
            return 0f;

        var raw = (float)hp / maxHp;
        if (raw > 1f) raw = 1f;
        if (raw < 0f) raw = 0f;
        return raw;
    }
}
