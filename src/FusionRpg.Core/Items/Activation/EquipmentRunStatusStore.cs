using FusionRpg.Core.Actions.Cost;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Items.Activation;

/// <summary>
/// The live actor inputs one activation evaluation needs — level and allocation for the trial,
/// the resource pools and the fresh derived snapshot for upkeep. Supplied by the deployment's own
/// clock/pool owner (spec-equipment-activation.md: "Equipment owns neither a second clock nor a
/// pool"), never cached here: a buff or an exhaustion debuff can move <c>max</c> between reads.
/// </summary>
public readonly record struct EquipmentActorState(
    int Level,
    AptitudeAllocation Allocation,
    ActorResourcePools Pools,
    ActorDerivedSnapshot Derived);

/// <summary>
/// item/spec-equipment-activation.md §"Upkeep schedule and ordering": the narrow host adapter over
/// whichever owner already holds a deployment's lifecycle — <c>MatchRuntime</c> for a lawn board,
/// <c>BattleEngine</c> for a standalone battle, the Delve session across rooms, the siege resolver
/// for an engagement. Module 24 replaces none of those FSMs; it reads one logical clock and one pool
/// owner from each.
///
/// <para><b>Logical ticks, never wall time.</b> A paused lawn freezes <see cref="NowTick"/>, which is
/// what makes "never charges offline, paused, rest, or cross-deployment elapsed time" true by
/// construction rather than by a guard.</para>
///
/// <para>A host that cannot yet supply an actor state (no pool, no snapshot) returns false from
/// <see cref="TryResolveActor"/> and the evaluation is skipped for that assignment — a missing
/// adapter is inert, never a fabricated full pool.</para>
/// </summary>
public interface IEquipmentDeploymentClock
{
    /// <summary>The host's opaque deployment key — the same value every
    /// <see cref="EquipmentAssignmentIdentity.DeploymentKey"/> in this run carries.</summary>
    string DeploymentKey { get; }

    /// <summary>The deployment's monotonic logical tick. Frozen while the run is paused.</summary>
    long NowTick { get; }

    /// <summary>This specimen's live level, allocation, pools and derived snapshot.</summary>
    bool TryResolveActor(string specimenId, out EquipmentActorState state);
}

/// <summary>
/// item/spec-equipment-activation.md §"Deployment-run status" — the deployment-scoped status owner.
///
/// <para><b>Every row is keyed by the deployment key inside its own identity</b>, so a status cannot
/// be read, written, or cleared against a run it does not belong to. That is the whole mechanism
/// behind "a new deployment always evaluates the frozen profile afresh; it inherits no due tick,
/// suspension, HP latch, or resource debt from a previous deployment": a new key has no rows, and
/// <see cref="BeginDeployment"/> resets a key that is being reused for a new run.</para>
///
/// <para><see cref="Set"/> owns the revision counter. A write that changes nothing observable leaves
/// the revision alone, so the host's snapshot refresh fires on a real transition and not on every
/// evaluation — the evaluations happen on projection, deployment, aptitude change and every due tick,
/// which is far more often than transitions.</para>
/// </summary>
public sealed class EquipmentRunStatusStore
{
    readonly Dictionary<string, Dictionary<EquipmentAssignmentIdentity, EquipmentRunStatus>> _byDeployment =
        new(StringComparer.Ordinal);

    /// <summary>Open a deployment. An unknown key starts empty; a key being reused for a NEW run is
    /// reset, because a reused key must not leak the previous run's schedule or latch. Returns how
    /// many rows the reset discarded (0 for a first open).</summary>
    public int BeginDeployment(string deploymentKey)
    {
        Require(deploymentKey, nameof(deploymentKey));
        var discarded = _byDeployment.TryGetValue(deploymentKey, out var existing) ? existing.Count : 0;
        _byDeployment[deploymentKey] = new Dictionary<EquipmentAssignmentIdentity, EquipmentRunStatus>();
        return discarded;
    }

    /// <summary>Close a deployment — board end, battle end, Delve run end, engagement end. Removes
    /// every row for that key and returns how many were removed. The durable assignment is never
    /// touched: this store holds no item fact.</summary>
    public int EndDeployment(string deploymentKey)
    {
        Require(deploymentKey, nameof(deploymentKey));
        if (!_byDeployment.TryGetValue(deploymentKey, out var rows)) return 0;
        var count = rows.Count;
        _byDeployment.Remove(deploymentKey);
        return count;
    }

    public bool IsDeployed(string deploymentKey)
    {
        Require(deploymentKey, nameof(deploymentKey));
        return _byDeployment.ContainsKey(deploymentKey);
    }

    /// <summary>How many assignments currently hold a status in this deployment.</summary>
    public int CountIn(string deploymentKey)
    {
        Require(deploymentKey, nameof(deploymentKey));
        return _byDeployment.TryGetValue(deploymentKey, out var rows) ? rows.Count : 0;
    }

    public bool TryGet(in EquipmentAssignmentIdentity id, out EquipmentRunStatus status)
    {
        if (_byDeployment.TryGetValue(id.DeploymentKey, out var rows) && rows.TryGetValue(id, out status))
            return true;
        status = EquipmentRunStatus.Initial;
        return false;
    }

    /// <summary>Whether this item currently contributes its effects. The one predicate the equipment
    /// read filters on; an assignment with no status in this deployment is NOT active — it has not
    /// been evaluated, which is never the same as "evaluated active".</summary>
    public bool IsActive(in EquipmentAssignmentIdentity id) =>
        TryGet(id, out var status) && status.ContributesEffects;

    /// <summary>
    /// Write a status, advancing the row's revision when (and only when) something observable changed.
    /// The incoming status's own <see cref="EquipmentRunStatus.Revision"/> is ignored — the store is
    /// the only writer of the counter, so a caller cannot rewind it.
    /// </summary>
    public EquipmentRunStatus Set(in EquipmentAssignmentIdentity id, EquipmentRunStatus status)
    {
        if (!_byDeployment.TryGetValue(id.DeploymentKey, out var rows))
        {
            // Writing into an unopened deployment is a host bug: the key was never begun, so the run
            // has no lifecycle and "clears at deployment end" would have nothing to clear.
            throw new InvalidOperationException(
                $"deployment '{id.DeploymentKey}' has not been begun; call BeginDeployment first");
        }

        var next = status with
        {
            Revision = rows.TryGetValue(id, out var prior) && Unchanged(prior, status)
                ? prior.Revision
                : (rows.TryGetValue(id, out var existing) ? existing.Revision + 1 : 1),
        };
        rows[id] = next;
        return next;
    }

    /// <summary>Drop one assignment's status — the assignment was removed, or its binding cleared.
    /// The row is gone, so a later re-assignment in the same deployment evaluates afresh.</summary>
    public bool ClearAssignment(in EquipmentAssignmentIdentity id) =>
        _byDeployment.TryGetValue(id.DeploymentKey, out var rows) && rows.Remove(id);

    /// <summary>Every row of one deployment in canonical contention order — the order the scheduler
    /// processes due payments in, exposed here so a host (or the item surface) reads the same order
    /// the payment path uses.</summary>
    public IReadOnlyList<(EquipmentAssignmentIdentity Identity, EquipmentRunStatus Status)> RowsIn(string deploymentKey)
    {
        Require(deploymentKey, nameof(deploymentKey));
        if (!_byDeployment.TryGetValue(deploymentKey, out var rows)) return Array.Empty<(EquipmentAssignmentIdentity, EquipmentRunStatus)>();
        var ordered = new List<(EquipmentAssignmentIdentity, EquipmentRunStatus)>(rows.Count);
        foreach (var pair in rows) ordered.Add((pair.Key, pair.Value));
        ordered.Sort((a, b) => EquipmentAssignmentIdentity.CanonicalOrder.Compare(a.Item1, b.Item1));
        return ordered;
    }

    /// <summary>Every row across every open deployment, for a host that owns more than one at once.
    /// Ordered by deployment key then canonical order, so the read is deterministic.</summary>
    public IReadOnlyList<(EquipmentAssignmentIdentity Identity, EquipmentRunStatus Status)> AllRows()
    {
        var keys = new List<string>(_byDeployment.Keys);
        keys.Sort(StringComparer.Ordinal);
        var result = new List<(EquipmentAssignmentIdentity, EquipmentRunStatus)>();
        foreach (var key in keys) result.AddRange(RowsIn(key));
        return result;
    }

    static bool Unchanged(EquipmentRunStatus prior, EquipmentRunStatus next) =>
        prior.State == next.State
        && prior.Reason == next.Reason
        && prior.NextDueTick == next.NextDueTick
        && prior.HpRecoveryLocked == next.HpRecoveryLocked;

    static string Require(string value, string what) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"a deployment key is required ('{what}')", what)
            : value;
}
