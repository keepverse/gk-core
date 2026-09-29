using FusionRpg.Core.Battle.Timeline;

namespace FusionRpg.Core.Actions;

/// <summary>
/// `battle-tempo` `action-timing` (spec-action-timing.md §2, D2): derives the timing envelope at
/// CATALOG BUILD, from an action's own realized power and its category — never at seed time (the
/// Python seeder cannot compute power). Pure, no DB access; `RpgStore.BuildActionCatalog` is the one
/// caller, since it already holds the realized power figure `ContentValidation.Budget` computes.
/// </summary>
public static class ActionTimingDerivation
{
    /// <summary>
    /// Derives wind-up, recovery, time cost and cooldown for one compiled action. Returns
    /// <see cref="ActionEnvelope.NoOp"/>'s fields unchanged (i.e. does nothing) for an action with no
    /// <see cref="ActionRow.Category"/> — "skip, do not guess": an uncategorized row is a pre-existing
    /// data gap this module does not paper over.
    /// </summary>
    /// <param name="category">The action's own category, or null if uncategorized (skip).</param>
    /// <param name="realizedPowerMilli">The action's OWN composed power
    /// (<c>ActorPowerCache.Compose(container.Atoms).Total</c>) — what lets a big-payoff action
    /// telegraph longer than a cheap one at the SAME rung (spec §2.2a's "within a rung" claim). Zero
    /// for an action with no container (no atoms, no power).</param>
    /// <param name="roundDurationMs">The active profile's round horizon — the wind-up cap is
    /// RELATIVE to this, never an absolute literal (D1).</param>
    /// <param name="cdMulti">The action's rung's own <c>cdMulti</c> (per-mille) — decision #11:
    /// cooldown rides rung only, reusing the EXISTING curve, never a second one.</param>
    public static ActionEnvelope Derive(
        ActionEnvelope baseline, ActionCategory? category, long realizedPowerMilli, long roundDurationMs,
        long cdMulti, ActionTimingTuning timing)
    {
        if (category is not { } cat) return baseline; // uncategorized -- skip, do not guess

        if (realizedPowerMilli < 0)
            throw new ArgumentOutOfRangeException(nameof(realizedPowerMilli), realizedPowerMilli, "realized power is never negative");

        var categoryTiming = timing.CategoryOf(cat);

        // Widen before multiplying, divide by 1000 last, exactly once (docs/architecture/numeric-types.md numeric overflow).
        // realizedPowerMilli is already five figures at rung 10 (RungRow.QPowerMilli ~12,400), so the
        // intermediate product must stay `long` throughout -- both operands already are.
        var windupUncapped = checked(timing.WindupPerPowerMilli * realizedPowerMilli) / 1000;
        var windupCap = timing.WindupCapTicks(roundDurationMs);
        var windupTicks = Math.Min(windupUncapped, windupCap);

        var recoveryTicks = checked(timing.RecoveryPerPowerMilli * realizedPowerMilli) / 1000;

        // Decision #11: cooldown rides rung only, via the EXISTING cdMulti curve -- never a second one.
        var cooldownTicks = checked(categoryTiming.CooldownBaseTicks * cdMulti) / 1000;

        return baseline with
        {
            WindupTicks = windupTicks,
            RecoveryTicks = recoveryTicks,
            TimeCostTicks = categoryTiming.TimeCostBaseTicks,
            CooldownTicks = cooldownTicks,
            Class = cooldownTicks > 0 ? CooldownClass.Category : CooldownClass.None,
            CooldownKey = cooldownTicks > 0 ? cat.ToString() : baseline.CooldownKey,
        };
    }

    /// <summary>The basic attack's own token wind-up (§2.1, decision 11: "a meaningful fraction of the
    /// round — a felt beat"). ⛔ Exempt from the power formula above — it has no rung and no seeded
    /// power (§2.2b) — so keeping it a separate, dedicated path is what stops the token drifting when
    /// <see cref="ActionTimingTuning.WindupPerPowerMilli"/> is tuned.</summary>
    public static ActionEnvelope DeriveBasicAttack(ActionEnvelope baseline, ActionTimingTuning timing) =>
        baseline with
        {
            WindupTicks = timing.BasicAttack.WindupTicks,
            RecoveryTicks = timing.BasicAttack.RecoveryTicks,
        };
}
