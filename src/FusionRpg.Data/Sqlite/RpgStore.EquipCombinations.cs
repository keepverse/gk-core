using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Sockets;
using FusionRpg.Core.Power;
using Microsoft.Data.Sqlite;

namespace FusionRpg.Data;

/// <summary>
/// strain-splice-host SSH4.6 (spec-combo-bind §2) — the STORE half of the arm-2 bind: the one place
/// that turns a host's filled sockets into <see cref="ComboBindTarget"/>s and makes sure each
/// target's container instance exists, so <see cref="EquipProjector"/> can bind it through the same
/// projection that carries the host and its inserts.
///
/// <para><b>Why the inputs live on the store.</b> Evaluating a combination needs the socket tuning and
/// a gem's element/tier/family, and no shipped table carries a gem's element — the card path takes the
/// same two facts from its own corpus (<see cref="ItemCardCorpus.LookupInsert"/>). The boot sets them
/// once here (<see cref="UseEquipCombinationEvaluation"/>) so the equip projection, whose callers are
/// request-scoped services with no corpus of their own, can evaluate without a DI change per caller.
/// Unset is the pre-SSH4.6 shape: no combination binds, exactly as before.</para>
///
/// <para>⛔ <b>Instances, not mutations.</b> <c>ComboContainerBuild</c> already wrote the container at
/// boot (SSH4.4); this only instantiates it. Pool rolls are zero, so the roll seed is irrelevant and a
/// re-projection of the same state finds the instance already there — nothing new is written
/// (<c>reprojecting_unchanged_state_writes_no_new_instance</c>).</para>
/// </summary>
public sealed partial class RpgStore
{
    /// <summary>
    /// Every specimen currently WEARING <paramref name="instanceId"/> (a `rolled` assignment). The
    /// workbench's socket writes use it to refresh the wearer's combination bindings in the SAME request
    /// (SSH4.8, spec-combo-bind §2.1): a word completed on an equipped host is a key-set edge — the
    /// target APPEARS — and the read side is self-correcting only for removals, so without this a word
    /// socketed onto a worn item would stay unbound until the next equip or deploy.
    /// </summary>
    public IReadOnlyList<string> SpecimensWearing(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
            throw new ArgumentException("an instance id", nameof(instanceId));

        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText =
                "SELECT specimen_id FROM rpg_item_assignment " +
                "WHERE ref_kind = $kind AND ref_id = $id ORDER BY specimen_id;";
            cmd.Parameters.AddWithValue("$kind", FusionRpg.Core.Items.EquipRefKinds.Rolled);
            cmd.Parameters.AddWithValue("$id", instanceId);
            var ids = new List<string>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) ids.Add(r.GetString(0));
            return ids;
        }
    }

    /// <summary>The two facts combination evaluation needs that no table carries.</summary>
    public sealed record EquipCombinationInputs(
        SocketTuning Sockets,
        Func<string, CardInsertLookup?>? LookupInsert);

    EquipCombinationInputs? _equipCombinationInputs;

    /// <summary>Boot-time wiring, idempotent: called once with the shipped tuning and the gem corpus.
    /// Never called means no combination binds — a valid pre-SSH4.6 boot, never a crash.</summary>
    public void UseEquipCombinationEvaluation(EquipCombinationInputs inputs)
    {
        if (inputs is null) throw new ArgumentNullException(nameof(inputs));
        _equipCombinationInputs = inputs;
    }

    /// <summary>
    /// Every combination the host's fill fires, as bind targets — one per circuit it fired in, with
    /// its container instance ensured. Pure read + an idempotent instantiate; a combination that stops
    /// firing simply produces no target, which is how the projection withdraws its binding AND how the
    /// read side (<c>EquippedBoundAtoms</c>, SSH4.7) recognises only a binding whose id is a CURRENT
    /// target — a stale one contributes nothing.
    /// </summary>
    public IReadOnlyList<ComboBindTarget> ComboTargetsFor(
        string hostInstanceId, IReadOnlyList<SocketSlot> slots)
    {
        var inputs = _equipCombinationInputs;
        if (inputs is null) return Array.Empty<ComboBindTarget>();
        if (inputs.LookupInsert is null) return Array.Empty<ComboBindTarget>();
        if (string.IsNullOrWhiteSpace(hostInstanceId)) return Array.Empty<ComboBindTarget>();

        var catalog = GetComboRecipes();
        if (catalog.Count == 0) return Array.Empty<ComboBindTarget>();
        if (SocketHostFor(hostInstanceId, _ => null) is not { } host)
            return Array.Empty<ComboBindTarget>();

        var fill = new List<SocketFill>(slots.Count);
        foreach (var slot in slots)
        {
            if (slot.IsEmpty || slot.InsertContainerId is not { Length: > 0 } insertContainerId) continue;
            var insert = inputs.LookupInsert(insertContainerId);
            // A socket row naming a container the gem catalog does not have is a content gap the card
            // path throws on; here it simply does not contribute, because a binding set is not the
            // place to surface a corpus defect and the host's other sockets must still evaluate.
            if (insert is { } looked) fill.Add(new SocketFill(slot.Index, slot.Affinity, looked.Def));
        }
        if (fill.Count == 0) return Array.Empty<ComboBindTarget>();

        var targets = new List<ComboBindTarget>();
        foreach (var result in CombinationEvaluator.Evaluate(host, fill, catalog, inputs.Sockets))
        {
            var containerId = ComboContainerBuild.ContainerId(result.ComboId, result.GrantedTier);
            var instanceId = ComboBindTargets.InstanceId(hostInstanceId, result.Circuit, containerId);
            if (GetInstance(instanceId) is null)
            {
                var container = GetContainer(containerId);
                if (container is null) continue;   // SSH4.4 built it at boot; absent = content gap
                var rejection = Instantiator.TryInstantiate(
                    container, GetAtom, GetAffix, rollSeed: 0,
                    PowerTuningHub.Tuning.Curve.PinIndex, PowerTuningHub.Tuning,
                    out var instance, InstanceOrigin.Craft);
                if (!rejection.IsOk || instance is null) continue;
                SaveInstance(instance, instanceId);
            }
            targets.Add(new ComboBindTarget(result, result.Circuit, instanceId));
        }
        return targets;
    }
}
