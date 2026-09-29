using FusionRpg.Core.Actions.Cost;
using FusionRpg.Core.Items.Requirements;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Items.Activation;

/// <summary>
/// The maintenance-payment seam: the two halves of
/// <see cref="CostLedger.TryPayProfileMaintenance"/> that activation needs — a peek (reactivation
/// requires affordability without charging) and the payment itself. A host binds it to its own
/// per-deployment <see cref="CostLedger"/>, so equipment maintenance rides the same pool owner, the
/// same logical clock and the same validate-then-spend rule every action cost already uses.
/// </summary>
public interface IEquipmentMaintenancePayment
{
    /// <summary>Whether the charge can be paid while preserving its reserve — spends nothing.</summary>
    bool CanPreserveReserve(string specimenId, FrozenResourceCharge charge);

    /// <summary>Pay the charge, or report the typed shortfall. Never partially.</summary>
    CostPayResult TryPay(string specimenId, FrozenResourceCharge charge);
}

/// <summary>The only production <see cref="IEquipmentMaintenancePayment"/>: a thin forward to the
/// deployment's own <see cref="CostLedger"/>. It adds no arithmetic of its own — a second
/// reserve/floor implementation is exactly the divergence the shared entry point exists to prevent.</summary>
public sealed class CostLedgerEquipmentPayment : IEquipmentMaintenancePayment
{
    readonly CostLedger _ledger;

    public CostLedgerEquipmentPayment(CostLedger ledger) =>
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));

    public bool CanPreserveReserve(string specimenId, FrozenResourceCharge charge) =>
        _ledger.CanPayProfileMaintenance(specimenId, charge);

    public CostPayResult TryPay(string specimenId, FrozenResourceCharge charge) =>
        _ledger.TryPayProfileMaintenance(specimenId, charge);
}

/// <summary>
/// The closed resource vocabulary a maintenance charge may name. The deployment's pool owner is an
/// <see cref="ActorResourcePools"/>, which knows exactly the six registered actor resources
/// (<see cref="DerivedStatChannels.ResourceIds"/>) — so a profile whose upkeep names anything else is
/// unpayable here, and the service turns that into a visible shortfall instead of an exception on
/// live content.
///
/// <para>⚠ <b>That case is live today, not hypothetical.</b>
/// <c>gk-core/data/tuning/equipment-requirements.v1.json</c> draws <c>upkeepResources</c> from
/// <c>[souls, essence]</c>, which are empire resources, not actor pools. Every <c>sustained</c> or
/// <c>jackpot</c> profile the shipped resolver mints therefore carries an upkeep the deployment
/// cannot charge. That is a tuning/content defect owned by the file's publisher (the path is outside
/// this lane's fence); it is filed as a row in <c>tasks/item-todo.md</c>. Until it is republished, a
/// sustained item reads <c>Suspended(upkeep_shortfall(souls))</c> — visible, nameable, and never a
/// crash and never a silently free item.</para>
/// </summary>
public static class EquipmentMaintenanceRules
{
    public static bool IsActorResource(string? resourceId)
    {
        if (string.IsNullOrWhiteSpace(resourceId)) return false;
        for (var i = 0; i < DerivedStatChannels.ResourceIds.Count; i++)
            if (DerivedStatChannels.ResourceIds[i] == resourceId) return true;
        return false;
    }
}

/// <summary>One evaluation step: the resulting status, and whether this step actually paid an
/// upkeep interval (which is what <see cref="EquipmentUpkeepScheduler"/> counts).</summary>
public readonly record struct EquipmentEvaluation(EquipmentRunStatus Status, bool ChargedInterval);

/// <summary>
/// item/spec-equipment-activation.md §"Evaluation and effect visibility" and §"Reactivation and HP":
/// the pure transition owner. It evaluates the FROZEN profile through module 23 and writes only
/// deployment-run status — it never touches a durable assignment, never calls
/// <c>EquipGate.Admits</c> (a generated requirement is not an equip refusal), and never composes an
/// actor magnitude.
///
/// <para><see cref="EvaluateStep"/> advances at most ONE upkeep interval. Advancing a delayed frame's
/// several intervals in canonical order is <see cref="EquipmentUpkeepScheduler"/>'s job — it calls
/// <see cref="EvaluateAndAdvance"/> in the spec's order, so the per-transition logic here stays a
/// single named step that a test can read.</para>
/// </summary>
public sealed class EquipmentActivationService
{
    readonly EquipmentRunStatusStore _store;
    readonly IEquipmentMaintenancePayment _payment;

    public EquipmentActivationService(EquipmentRunStatusStore store, IEquipmentMaintenancePayment payment)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _payment = payment ?? throw new ArgumentNullException(nameof(payment));
    }

    /// <summary>
    /// Evaluate one assignment in one deployment at <paramref name="nowTick"/> and write its status.
    /// Called on assignment projection, loadout deployment, aptitude change, and every due upkeep
    /// tick — the four moments the spec names. Idempotent when nothing changes (the revision holds),
    /// so a host may call it on every projection without churning its snapshot.
    /// </summary>
    public EquipmentRunStatus Evaluate(
        in EquipmentAssignmentIdentity id,
        RequirementProfile profile,
        EquipmentActorState actor,
        long nowTick) => EvaluateStep(id, profile, actor, nowTick).Status;

    /// <summary>
    /// Evaluate, then pay every interval the deployment clock has already passed for this assignment.
    /// The intervals are paid one at a time because a later interval's affordability depends on the
    /// earlier one having been paid; an interval that cannot be paid suspends the item and ends its
    /// pass. Returns the final status and how many intervals were actually paid.
    /// </summary>
    public (EquipmentRunStatus Status, int IntervalsCharged) EvaluateAndAdvance(
        in EquipmentAssignmentIdentity id,
        RequirementProfile profile,
        EquipmentActorState actor,
        long nowTick)
    {
        var step = EvaluateStep(id, profile, actor, nowTick);
        var charged = step.ChargedInterval ? 1 : 0;
        while (step.Status.State == EquipmentActivationState.Active
               && step.Status.NextDueTick is { } due
               && due <= nowTick)
        {
            step = EvaluateStep(id, profile, actor, nowTick);
            if (!step.ChargedInterval) break;
            charged++;
        }

        return (step.Status, charged);
    }

    /// <summary>One evaluation step, reporting whether it paid an interval. See
    /// <see cref="Evaluate"/> for the four moments a host calls this.</summary>
    public EquipmentEvaluation EvaluateStep(
        in EquipmentAssignmentIdentity id,
        RequirementProfile profile,
        EquipmentActorState actor,
        long nowTick)
    {
        if (profile is null) throw new ArgumentNullException(nameof(profile));
        if (actor.Allocation is null) throw new ArgumentNullException(nameof(actor));
        if (nowTick < 0) throw new ArgumentOutOfRangeException(nameof(nowTick), nowTick, "a logical tick is never negative");

        var prior = _store.TryGet(id, out var existing) ? existing : EquipmentRunStatus.Initial;
        var trial = RequirementTrialEvaluator.Evaluate(profile, actor.Level, actor.Allocation);

        // The HP latch is a latch: it survives trial/suspension and clears only at an evaluation whose
        // fresh derived snapshot reads full HP. It is read BEFORE the trial arm so a lapsed trial
        // cannot quietly erase a recovery lock.
        var hpFull = IsFullHp(actor, nowTick);
        var locked = prior.HpRecoveryLocked && !hpFull;

        if (!trial.Ready)
        {
            var reason = ReasonFor(trial.Unmet);
            // A trial that was never met is guidance (`trial`); a trial that lapsed while the item was
            // contributing is a suspension (spec §"Deployment-run status" lists "an active build trial
            // lapsed" under suspended). Either way there is no schedule: a later reactivation must not
            // back-charge the intervals it was not contributing for.
            var state = prior.State == EquipmentActivationState.Active
                ? EquipmentActivationState.Suspended
                : EquipmentActivationState.Trial;
            return new EquipmentEvaluation(
                _store.Set(id, new EquipmentRunStatus(state, reason, null, locked, 0)), false);
        }

        if (profile.Upkeep is not { } upkeep)
        {
            // No maintenance: once the trial passes the item is simply active, with no schedule.
            return new EquipmentEvaluation(_store.Set(id, new EquipmentRunStatus(
                EquipmentActivationState.Active, EquipmentActivationReason.None, null, locked, 0)), false);
        }

        RequirePositivePeriod(upkeep);

        if (locked)
        {
            return new EquipmentEvaluation(_store.Set(id, new EquipmentRunStatus(
                EquipmentActivationState.Suspended, EquipmentActivationReason.HpRecoveryLocked, null, true, 0)), false);
        }

        if (prior.State == EquipmentActivationState.Active)
        {
            // Already contributing. Nothing is due until the schedule says so; when it is due, pay
            // exactly one interval and advance exactly one period.
            if (prior.NextDueTick is { } due && nowTick < due) return new EquipmentEvaluation(prior, false);
            return ChargeOneInterval(id, upkeep, prior, nowTick, hpFull);
        }

        // Entering Active — a first activation or a reactivation. Equipping costs nothing and a
        // reactivation never back-charges: the payment becomes due one period out. What reactivation
        // DOES require is that the next payment can preserve the reserve, so an item cannot flicker
        // active while unaffordable ("becomes active only after that payment can preserve reserve").
        if (!CanPreserveReserve(id, upkeep)) return Suspend(id, upkeep, hpFull);

        return new EquipmentEvaluation(_store.Set(id, new EquipmentRunStatus(
            EquipmentActivationState.Active, EquipmentActivationReason.None, NextDue(nowTick, upkeep), locked, 0)), false);
    }

    /// <summary>Charge one due interval. On success the schedule advances by exactly one period; on a
    /// shortfall only this item suspends, naming the resource. An <c>hp</c> shortfall also sets the
    /// recovery latch — HP maintenance is non-lethal, so a pool that cannot keep its floor locks the
    /// item out until it is full again.</summary>
    EquipmentEvaluation ChargeOneInterval(
        in EquipmentAssignmentIdentity id, UpkeepClause upkeep, EquipmentRunStatus prior, long nowTick, bool hpFull)
    {
        if (!CanPreserveReserve(id, upkeep)) return Suspend(id, upkeep, hpFull);

        var charge = new FrozenResourceCharge(upkeep.ResourceId, upkeep.Cost, upkeep.Reserve);
        var result = _payment.TryPay(id.SpecimenId, charge);
        if (result.Outcome != CostPayOutcome.Paid)
        {
            var resourceId = result.ShortfallResourceId ?? upkeep.ResourceId;
            var reason = IsHp(upkeep)
                ? EquipmentActivationReason.HpRecoveryLocked
                : EquipmentActivationReason.UpkeepShortfall(resourceId);
            return new EquipmentEvaluation(_store.Set(id, new EquipmentRunStatus(
                EquipmentActivationState.Suspended, reason, null, IsHp(upkeep), 0)), false);
        }

        var paidThrough = prior.NextDueTick ?? nowTick;
        return new EquipmentEvaluation(_store.Set(id, prior with
        {
            State = EquipmentActivationState.Active,
            Reason = EquipmentActivationReason.None,
            NextDueTick = NextDue(paidThrough, upkeep),
            HpRecoveryLocked = false,
        }), true);
    }

    /// <summary>
    /// Suspend an item whose next payment cannot preserve its reserve. An <c>hp</c> shortfall latches
    /// only when the pool is genuinely not full — a full pool that still cannot carry the reserve is a
    /// tuning impossibility, and reporting it as a recovery lock would re-latch on every evaluation
    /// and churn the revision forever.
    /// </summary>
    EquipmentEvaluation Suspend(in EquipmentAssignmentIdentity id, UpkeepClause upkeep, bool hpFull)
    {
        var latched = IsHp(upkeep) && !hpFull;
        var reason = latched
            ? EquipmentActivationReason.HpRecoveryLocked
            : EquipmentActivationReason.UpkeepShortfall(upkeep.ResourceId);
        return new EquipmentEvaluation(_store.Set(id, new EquipmentRunStatus(
            EquipmentActivationState.Suspended, reason, null, latched, 0)), false);
    }

    /// <summary>Whether this item's next payment can be made while preserving the reserve. A resource
    /// the deployment's pool owner does not carry is unpayable, and reads as unaffordable rather than
    /// throwing on live content (see <see cref="EquipmentMaintenanceRules"/>).</summary>
    bool CanPreserveReserve(in EquipmentAssignmentIdentity id, UpkeepClause upkeep)
    {
        if (!EquipmentMaintenanceRules.IsActorResource(upkeep.ResourceId)) return false;
        return _payment.CanPreserveReserve(
            id.SpecimenId, new FrozenResourceCharge(upkeep.ResourceId, upkeep.Cost, upkeep.Reserve));
    }

    /// <summary>"The lock clears only at an evaluation where <c>currentHp == maxHp</c> from the same
    /// fresh derived snapshot", and "a changed maximum HP changes the definition of full at the next
    /// evaluation; no stale max value is persisted" — so both sides are read here, fresh.</summary>
    static bool IsFullHp(EquipmentActorState actor, long nowTick)
    {
        if (actor.Pools is null || actor.Derived is null) return false;
        var max = ResourceChannelReader.Max(actor.Derived, "hp");
        return actor.Pools.Resolve("hp", nowTick, actor.Derived) >= max;
    }

    static bool IsHp(UpkeepClause upkeep) => string.Equals(upkeep.ResourceId, "hp", StringComparison.Ordinal);

    static long NextDue(long fromTick, UpkeepClause upkeep) => checked(fromTick + upkeep.PeriodTicks);

    /// <summary>A period of zero (or less) has no schedule and would make the scheduler's advance loop
    /// non-terminating. It is a tuning gap, and it throws rather than being coerced to some default
    /// interval.</summary>
    static void RequirePositivePeriod(UpkeepClause upkeep)
    {
        if (upkeep.PeriodTicks <= 0)
            throw new ArgumentOutOfRangeException(nameof(upkeep), upkeep.PeriodTicks,
                "an upkeep period must be a positive number of logical ticks");
    }

    static EquipmentActivationReason ReasonFor(TrialUnmetClause unmet) => unmet switch
    {
        TrialUnmetClause.Level => EquipmentActivationReason.LevelUnmet,
        TrialUnmetClause.FixedAptitude => EquipmentActivationReason.BuildUnmet,
        TrialUnmetClause.RatioAptitude => EquipmentActivationReason.BuildUnmet,
        TrialUnmetClause.None => EquipmentActivationReason.None,
        _ => throw new ArgumentOutOfRangeException(nameof(unmet), unmet, "unhandled trial clause"),
    };
}
