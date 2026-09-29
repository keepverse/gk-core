namespace FusionRpg.Core.Stats.Derived;

/// <summary>
/// Reads <c>resource.max.{id}</c> / <c>resource.regen.{id}</c> (spec-action-costs.md §1) as
/// <c>long</c>. The composer stores every derived channel as a <c>double</c> internally, but a
/// resource pool is a magnitude a balance pass can push toward the overflow ceiling (docs/architecture/numeric-types.md
/// "Numeric overflow"), so the round-to-long happens here, once, at the boundary — the same point
/// <c>BattleRuleset.BaseHp</c>/<c>BattleChannelMod</c> already round at.
///
/// Lives outside <c>Core/Actions/</c>: the round-to-long is a stats-layer boundary concern, so the
/// action layer consumes a <c>long</c> pool size. (The placement once cited a floating-point ban in
/// <c>KernelPurityScan</c>; that ban was removed by the 2026-09-15 owner ruling — floating point is
/// allowed for any quantity. The integer RANGE reason above, <c>long</c> for a magnitude, still holds.)
/// </summary>
public static class ResourceChannelReader
{
    public static long Max(ActorDerivedSnapshot snap, string resourceId) =>
        (long)Math.Round(snap.Get(DerivedStatChannels.ResourceMax(resourceId)), MidpointRounding.AwayFromZero);

    /// <summary>
    /// Regen in <b>per-mille of a unit per tick</b> — the sub-tick unit named as follow-up S10.1 by
    /// <c>battle-resources.v1.json</c>'s own <c>_meta.regenIsAbsentOnPurpose</c>.
    ///
    /// <para>The channel itself is unchanged: <c>resource.regen.{id}</c> still means <b>units per
    /// tick</b>, so a composer writing <c>5</c> still means five per tick. What changes is the
    /// resolution this reader can carry out of it: rounding to a whole <c>long</c> made
    /// <c>1/tick</c> the smallest expressible non-zero rate, and a battle round runs several hundred
    /// ticks (`action-timing.v1.json`: a basic attack alone is 150 wind-up + 50 recovery), so the
    /// smallest rate that existed at all accrued ~300 poise per round against a spend of 100. There
    /// was no value between "nothing" and "three counters a round". ×1000 puts 999 authorable rates
    /// in that gap.</para>
    ///
    /// <para>Per-mille matches the repo's existing convention (`poolShareMilli`, `categoryMilli`);
    /// a different denominator would fragment it. The single <c>/1000</c> that turns this back into
    /// whole units happens once, at the far end, in <see cref="Actions.Cost.ResourcePoolState"/> —
    /// which carries the remainder forward rather than rounding per tick.</para>
    ///
    /// <para>⚠️ Overflow: <c>checked</c> so an absurd authored rate throws rather than wrapping or
    /// saturating (docs/architecture/numeric-types.md "Numeric overflow" — overflow throws, never wraps). Exempt from the
    /// no-ceilings rule as an arithmetic guard, not a progression ceiling.</para>
    /// </summary>
    public static long RegenPerMilleTick(ActorDerivedSnapshot snap, string resourceId) =>
        checked((long)Math.Round(
            snap.Get(DerivedStatChannels.ResourceRegen(resourceId)) * 1000.0, MidpointRounding.AwayFromZero));
}
