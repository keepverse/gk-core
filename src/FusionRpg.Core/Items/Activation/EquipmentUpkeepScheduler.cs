using FusionRpg.Core.Items.Requirements;

namespace FusionRpg.Core.Items.Activation;

/// <summary>
/// One assignment as the scheduler needs it: the identity, and the FROZEN profile the item carries.
/// The live actor inputs come from the deployment's own <see cref="IEquipmentDeploymentClock"/> —
/// never from here — so the scheduler holds no pool and no snapshot.
/// </summary>
public readonly record struct EquipmentAssignmentInput(
    EquipmentAssignmentIdentity Identity,
    RequirementProfile Profile);

/// <summary>
/// One assignment's outcome for a scheduler pass. <see cref="Status"/> is <c>null</c> when the
/// deployment could not resolve the wearing actor's live state: nothing was evaluated and nothing was
/// charged, and the host can see that rather than reading a fabricated status.
/// <see cref="IntervalsCharged"/> counts upkeep intervals actually paid in this pass — 0 for an item
/// that was in trial, suspended, not yet due, or whose actor could not be resolved.
/// </summary>
public readonly record struct EquipmentUpkeepOutcome(
    EquipmentAssignmentIdentity Identity,
    EquipmentRunStatus? Status,
    int IntervalsCharged);

/// <summary>
/// item/spec-equipment-activation.md §"Upkeep schedule and ordering": the canonical order and the
/// delayed-frame rule, and nothing else.
///
/// <para>Assignments are processed in the spec's exact order — <c>specimenId, role, refKind, refId</c>,
/// all ordinal (<see cref="EquipmentAssignmentIdentity.CanonicalOrder"/>) — so shared-resource
/// contention has ONE outcome regardless of the order the host happened to enumerate its assignments
/// in. "A delayed frame" (the clock jumped past several periods) charges every due interval, one at a
/// time and in that same order, because a later interval's affordability depends on the earlier
/// interval having been paid. An interval that cannot be paid suspends its item and ends that item's
/// pass; it never blocks another item and never leaves a partial debit.</para>
///
/// <para>The loop is bounded by the deployment's own elapsed logical ticks divided by the profile's
/// period, because the clock is a logical, real-play clock: a paused or offline run does not advance
/// it, which is why no offline charge and no wall-clock charge can occur here.</para>
/// </summary>
public sealed class EquipmentUpkeepScheduler
{
    readonly EquipmentActivationService _service;

    public EquipmentUpkeepScheduler(EquipmentActivationService service) =>
        _service = service ?? throw new ArgumentNullException(nameof(service));

    /// <summary>
    /// Advance every assignment in one deployment up to the clock's current tick. The clock owns the
    /// tick and the actor state; this method owns the order and the multi-interval walk.
    /// </summary>
    public IReadOnlyList<EquipmentUpkeepOutcome> AdvanceDue(
        IEquipmentDeploymentClock clock,
        IReadOnlyList<EquipmentAssignmentInput> assignments)
    {
        if (clock is null) throw new ArgumentNullException(nameof(clock));
        if (assignments is null) throw new ArgumentNullException(nameof(assignments));

        var nowTick = clock.NowTick;
        var ordered = new List<EquipmentAssignmentInput>(assignments);
        ordered.Sort((a, b) => EquipmentAssignmentIdentity.CanonicalOrder.Compare(a.Identity, b.Identity));

        var outcomes = new List<EquipmentUpkeepOutcome>(ordered.Count);
        foreach (var assignment in ordered)
        {
            var id = assignment.Identity;
            // A crossed assignment would charge one deployment's pools against another run's
            // schedule. That is a host bug with no correct fallback, so it throws rather than being
            // skipped — a silent skip here is exactly how a cross-deployment charge would ship.
            if (!string.Equals(id.DeploymentKey, clock.DeploymentKey, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"assignment '{id}' belongs to deployment '{id.DeploymentKey}' but the clock owns '{clock.DeploymentKey}'");

            if (!clock.TryResolveActor(id.SpecimenId, out var actor))
            {
                outcomes.Add(new EquipmentUpkeepOutcome(id, null, 0));
                continue;
            }

            var (status, charged) = _service.EvaluateAndAdvance(id, assignment.Profile, actor, nowTick);

            outcomes.Add(new EquipmentUpkeepOutcome(id, status, charged));
        }

        return outcomes;
    }
}
