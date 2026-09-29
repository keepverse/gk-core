using FusionRpg.Core.Actions.Loadout;
using FusionRpg.Core.Actions.Unlock;

namespace FusionRpg.Core.Actions.Grants;

/// <summary>
/// T24 (spec-grant-seam.md §5, item 8): the cap question, answered by NAMING which existing cap
/// governs rather than inventing a new one on the assembled/granted set.
///
/// <para><b>"The number is not 8, and it is not one number"</b> — the item lane's original ask
/// conflated three different scarcities, each already owned and already built:</para>
///
/// <list type="bullet">
/// <item><see cref="HeldCap"/> — levelling unlocks HELD (`A11`/T19, tunable, the free faucet,
/// capped because it is free).</item>
/// <item><see cref="EquippedSkillCap"/> — equipped AT ONCE (`A16`/T21, the real bottleneck; the
/// innate and three basics are intrinsic and never count against it).</item>
/// <item><b>Granted by paid sources: uncapped, on purpose</b> (spec §5.1 — "an uncapped pool grows
/// the choice, never the power"). There is no third field here for that: this class HAS no
/// "grantedCap" member, which is the answer, not an omission.</item>
/// </list>
///
/// <para><b>"Exceeding an actual cap rejects at equip time"</b> (spec §5) — that is
/// <see cref="LoadoutSet.Validate"/>'s existing <c>LoadoutFull</c> rejection (T21), already built and
/// already tested. This class does not re-implement it; it names it.</para>
/// </summary>
public static class CapPolicy
{
    /// <summary>Levelling unlocks held — `UnlockTuning.HeldCap` (A11/T19; split from a single `Cap`
    /// field 2026-09-03, A-U1 §3.3 — `RungCap` is the ladder's own ceiling, a different scarcity).</summary>
    public static int HeldCap(UnlockTuning unlockTuning)
    {
        if (unlockTuning is null) throw new ArgumentNullException(nameof(unlockTuning));
        return unlockTuning.HeldCap;
    }

    /// <summary>Equipped skills at once — `LoadoutSet.EffectiveMaxSize` (A16/T21; extended D4.25 for
    /// the extend-action-slot grant). The innate and three basics are intrinsic and never counted
    /// against it. No longer a compile-time constant: <paramref name="loadoutSlotsChannel"/> is the
    /// actor's composed <see cref="FusionRpg.Core.Stats.Derived.DerivedStatChannels.LoadoutSlots"/>
    /// value (whole `long`, not `double` — see <see cref="LoadoutSet.EffectiveMaxSize"/>), 0 by
    /// default (nothing worn grants it) — the plain base 5 for any caller that has not been updated
    /// to pass a channel value.</para>
    ///
    /// <para><b>W14 (battle-derived-wire T18): no production site passes a channel value yet</b> — the
    /// composed `loadout.slots` is reserved at 0 until the equip/atom layer that grants it ships.</para>
    /// </summary>
    public static int EquippedSkillCap(long loadoutSlotsChannel = 0) =>
        LoadoutSet.EffectiveMaxSize(loadoutSlotsChannel);
}
