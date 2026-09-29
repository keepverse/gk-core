namespace FusionRpg.Core.Stats;

/// <summary>
/// The decision "does this resolve reach the injector's single stat writer?", as a pure function.
///
/// <para><b>The bug this shape exists to prevent (owner-observed live, 2026-08-30).</b> The gate used
/// to <b>enumerate contributors</b> — a chain of `hasScaleMods || hasPvz || hasEffectMods || hasAbsolute`
/// flags. Any producer missing from that list composed correctly and then wrote nothing, with no error
/// to notice: commander aptitude bonuses resolved to a real value while the plant's HP never moved.
/// The fix is to ask a <b>value</b> question instead of a <b>source</b> question —
/// <see cref="EntityFinal.DiffersFrom"/> — so a producer added tomorrow (auras, atoms, items) needs no
/// edit here to be seen. Never reintroduce a contributor flag in this path.</para>
///
/// <para><b>Why it lives in Core (aura-skill-todo.md Phase 5 / TC3).</b> It was inline in
/// <c>FusionRpg.Injector.Stats.EntityApply</c>, duplicated across <c>RunPlant</c> and <c>RunZombie</c>,
/// where no test CI runs could reach it: that assembly needs a real PVZ Fusion install to build and is
/// absent from `ci.yml`'s ten test projects. The regression test for the highest-value bug this repo
/// found in 2026-08 therefore did not exist — the defect was caught by the owner playing the game.
/// Both call sites now delegate here, so the rule is stated once and tested once.</para>
/// </summary>
public static class EntityWriteGate
{
    /// <summary>Sources that must write even when nothing differs.
    ///
    /// <para><c>pushScales</c> and <c>reapply</c> both run <b>after</b> a bag clear, when the resolved
    /// value has legitimately returned to the baseline. That is exactly the case
    /// <see cref="EntityFinal.DiffersFrom"/> reports as "no change" — correct as a value answer, wrong
    /// as an action: without this override the entity would keep whatever the writer last poked into
    /// it, and clearing a cheat would visibly do nothing.</para></summary>
    public static bool IsForcedSource(string? source) =>
        source is not null
        && (source.Contains("pushScales", StringComparison.Ordinal)
            || source.Contains("reapply", StringComparison.Ordinal));

    /// <summary>The whole decision. <paramref name="source"/> is the caller tag
    /// (<c>"spawn"</c>, <c>"pushScales"</c>, <c>"reapply"</c>, …).</summary>
    public static bool ShouldWrite(EntityFinal final, EntityBaseline baseline, string? source)
    {
        if (final is null) throw new ArgumentNullException(nameof(final));
        if (baseline is null) throw new ArgumentNullException(nameof(baseline));

        return IsForcedSource(source) || final.DiffersFrom(baseline);
    }
}
