using FusionRpg.Contracts;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Cost;
using FusionRpg.Core.Battle.Timeline;

namespace FusionRpg.Core.Match.Ai;

/// <summary>What a cast plan decided. <see cref="None"/> is "no action was chosen" — the plan never
/// throws for it, because a decision that declared nothing is an ordinary outcome.</summary>
public enum LawnCastOutcome
{
    None = 0,
    Cast,
    InsufficientFunds,
}

/// <summary>
/// combat-ai `lawn-cast-activation` (module 18, CAI4.6) — the pure plan's result.
///
/// <para><see cref="Event"/> is non-null exactly when <see cref="Fired"/>: the plan refuses totally, so
/// a caller never has to check two fields to know whether anything happened. Every event this type
/// carries is a <b>cast</b> event, which is what the injector's fire site stamps onto the DTO's
/// <c>CastOrigin</c> discriminator — see <see cref="LawnCastPlan"/>'s own note on that owed field.</para>
/// </summary>
public readonly record struct LawnCastPlanResult(
    LawnCastOutcome Outcome,
    EffectEventDto? Event,
    string? ShortfallResourceId)
{
    public bool Fired => Outcome == LawnCastOutcome.Cast;

    public static readonly LawnCastPlanResult None = new(LawnCastOutcome.None, null, null);
}

/// <summary>
/// combat-ai `lawn-cast-activation` (module 18, CAI4.6, spec-lawn-cast-activation.md §1) — the ordered,
/// pure plan that turns a chosen <see cref="ActionIntent"/> into a paid, cooled-down activation event.
///
/// <para><b>The order is not negotiable: pay, then start the cooldown, then build the event.</b>
/// Committing is what costs, not landing — a cast that misses still paid, and a cast that was never
/// afforded never happened. So an <see cref="CostPayOutcome.InsufficientFunds"/> is a TOTAL refusal: no
/// cooldown starts, no event is built, nothing is flushed. Swapping steps 1 and 2 makes a refused cast
/// leave a cooldown behind, which is the planted violation the plan's own test kills.</para>
///
/// <para><b>What this half does NOT do, named rather than implied.</b> It does not fire anything — the
/// runner → bag → flush tail is the injector's (<c>LawnCastActivation</c>), and its order (runner BEFORE
/// bag) is copied from battle's own documented trap, not re-decided here. And it does not stamp the
/// record-kind discriminator: <c>EffectEventDto.CastOrigin</c> is an additive default-<c>false</c> field
/// on <c>gk-core/src/FusionRpg.Contracts/EffectDtos.cs</c>, which this lane's fence does not reach. Every event
/// this plan returns IS a cast, so the fire site sets the flag; the two consumers that read it (the lawn
/// basic-attack charge and module 19's swing counter) are both on the injector side for the same reason.
/// </para>
///
/// <para>No number here is a tunable: the cost is authored on the action's own cost rows, the cooldown
/// rides the action's envelope, and <see cref="EffectEventDto.HitCount"/> is 1 — the same value battle's
/// own activation raise uses.</para>
/// </summary>
public static class LawnCastPlan
{
    /// <summary>
    /// Pays for <paramref name="intent"/>, starts its cooldown and builds the <c>OnActivate</c> event —
    /// in that order, and never partially.
    /// </summary>
    /// <param name="actorKey">The casting ptr.</param>
    /// <param name="intent">The policy's decision. <see cref="ActionIntent.IsNone"/> yields
    /// <see cref="LawnCastPlanResult.None"/> rather than throwing: declaring nothing is ordinary.</param>
    /// <param name="nowTick">The lawn tick, from the host — the plan reads no clock.</param>
    /// <param name="ledger">The mode's cost ledger, already holding this action's rows.</param>
    /// <param name="cooldowns">The shared cooldown ledger, the mode-agnostic one battle uses.</param>
    /// <param name="rng">The caller's stream for a spread cost; null for a fixed one.</param>
    public static LawnCastPlanResult Build(
        string actorKey,
        in ActionIntent intent,
        long nowTick,
        CostLedger ledger,
        CooldownLedger cooldowns,
        AtomRng? rng = null)
    {
        if (string.IsNullOrEmpty(actorKey))
            throw new ArgumentException("actorKey must not be empty", nameof(actorKey));
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(cooldowns);

        if (intent.IsNone) return LawnCastPlanResult.None;

        // The intent's envelope must name the action being paid for. The plan charges
        // `intent.ActionId` and arms `intent.Envelope`, so a caller that pairs one action's id with
        // ANOTHER action's envelope would charge one action and cool a different one — a silent
        // mis-charge rather than a visible refusal, which is why this throws instead of tolerating it.
        // Every producer shipped today pairs them by construction (`StubIntentSource`, `CoreIntentPolicy`
        // and the siege source all build the intent from the action itself); the one place a wrong row can
        // arrive is a lookup keyed by id, which is exactly what `commander-direct-orders`' owed order path
        // will do. A programming error, not state — so it throws rather than degrading.
        if (!string.Equals(intent.Envelope.ActionId, intent.ActionId, StringComparison.Ordinal))
            throw new ArgumentException(
                $"intent names action '{intent.ActionId}' but its envelope names " +
                $"'{intent.Envelope.ActionId}' — the plan would pay one action and cool another",
                nameof(intent));

        // 1. Pay on commit. A shortfall is a refusal: nothing below this line happens.
        var paid = ledger.TryPay(actorKey, intent.ActionId, ActionCostTiming.OnCommit, rng);
        if (paid.Outcome != CostPayOutcome.Paid)
            return new LawnCastPlanResult(LawnCastOutcome.InsufficientFunds, null, paid.ShortfallResourceId);

        // 2. Start the cooldown, on the SAME ledger battle uses. The caller decides WHEN commit is; the
        // envelope declares what the cooldown is keyed on.
        cooldowns.Start(actorKey, intent.Envelope, nowTick);

        // 3. Build the event, field for field like battle's own activation raise. The fire site does
        // runner -> bag -> flush with it.
        var activation = new EffectEventDto
        {
            Trigger = EffectTriggers.OnActivate,
            ActorPtr = actorKey,
            TargetPtr = intent.TargetKey,
            Tick = nowTick,
            HitCount = 1,
        };

        return new LawnCastPlanResult(LawnCastOutcome.Cast, activation, null);
    }
}
