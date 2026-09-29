namespace FusionRpg.Core.Items;

using Sockets;

/// <summary>
/// The two <c>ref_kind</c> values <c>rpg_item_assignment</c> actually carries, in one place.
///
/// <para>⛔ <b>This is NOT the same vocabulary as <c>rpg_item_loadout_entry</c>'s.</b> That table —
/// module 2's saved presets — spells its instance-pinned kind <c>"item"</c>
/// (<see cref="LoadoutReport.InstanceRefKind"/>, and <c>GetLoadoutEntriesValidated</c>'s own SQL
/// switches on it). Two tables, two vocabularies, one word apart, and nothing named the difference
/// until a reader in a third module tested an assignment row against the <i>preset</i> table's
/// literal and silently saw nothing (defect R2, 2026-09-06). Both constants live here so that
/// mistake has to be made deliberately.</para>
/// </summary>
public static class EquipRefKinds
{
    /// <summary>One rolled copy: <see cref="EquipAssignment.RefId"/> is an
    /// <c>effect_instance.instance_id</c>. Written by <c>POST /api/items/equip</c>, and the only kind
    /// <c>RpgStore.ApplyEquipProjection</c> turns into a binding.</summary>
    public const string Rolled = "rolled";

    /// <summary>A catalog id: <see cref="EquipAssignment.RefId"/> is a <c>container_id</c>, so it
    /// never pins one specific copy. Written by the relic wire
    /// (<c>PUT /api/unique/actors/{id}/equipment/{slot}</c>) and by D1 §10 M1's row migration.</summary>
    public const string Stock = "stock";
}

/// <summary>One durable equip decision: this player put this item in this role on this specimen.
/// <paramref name="SpecimenId"/> is the `rpg_unique_actor`'s own stable `instance_id` — a kebab-case
/// string, matching `OwnerScope.UniqueActor`'s key exactly (`OwnerScope.cs`: "keyed on the actor's own
/// stable instance_id" — never a numeric id). <c>RefKind</c> is <c>"rolled"</c> (<paramref name="RefId"/>
/// an `effect_instance.instance_id`) or <c>"stock"</c> (<paramref name="RefId"/> a `container_id` into
/// module 2's counter).</summary>
public sealed record EquipAssignment(string SpecimenId, ItemRole Role, string RefKind, string RefId, string AssignedUtc);

/// <summary>What an assignment's item actually asks of the specimen wearing it — supplied by the
/// caller (module 6 does not exist yet), matching how <c>BindGate.Check</c> already takes
/// <c>levelReq</c> as a parameter rather than reading it off a not-yet-existing type.</summary>
public readonly record struct EquipItemFacts(string? Frame, int? LevelReq, string? FactionReq);

public sealed record ProjectionResult(
    IReadOnlyList<EquipAssignment> Bindings,
    IReadOnlyList<(EquipAssignment Assignment, EquipRefusal Reason)> Shortfalls,
    IReadOnlyList<(EquipAssignment Assignment, EquipRefusal Reason)> Skipped);

/// <summary>
/// strain-splice-host SSH4.6 (spec-combo-bind §2): ONE firing combination, as the projector needs it —
/// the evaluator's own <see cref="CombinationResult"/> (never a re-read of it), the circuit it fired in,
/// and the deterministic instance id of its container. The instance id is
/// <c>cmb:{hostInstanceId}#c{circuit}:{containerId}</c>: content-derived, so a reprojection of the same
/// state names the same instance and a fill that stops firing simply produces no target.
/// </summary>
public readonly record struct ComboBindTarget(CombinationResult Result, int Circuit, string ComboInstanceId);

/// <summary>
/// The ONE place the combination instance id's shape lives (strain-splice-host SSH4.6, spec-combo-bind
/// §2): <c>cmb:{hostInstanceId}#c{circuit}:{containerId}</c>. Deterministic and content-derived, so
/// re-projecting the same state names the same instance (nothing new is written) and a fill that stops
/// firing simply produces no target. The caller instantiates it with <c>Instantiator.TryInstantiate</c>
/// on the combo container — zero pool rolls, so no RNG is consumed.
/// </summary>
public static class ComboBindTargets
{
    public static string InstanceId(string hostInstanceId, int circuit, string containerId)
    {
        if (string.IsNullOrWhiteSpace(hostInstanceId))
            throw new ArgumentException("a combination binds to a HOST instance", nameof(hostInstanceId));
        if (string.IsNullOrWhiteSpace(containerId))
            throw new ArgumentException("a combination binds a CONTAINER", nameof(containerId));
        return $"cmb:{hostInstanceId}#c{circuit}:{containerId}";
    }
}

/// <summary>
/// Assignments → bindings, a full rebuild every time — never a delta. `UpsertUniqueEquipment` already
/// works this way and `UniqueOwnerBinder.ToEntityKey` already discards the instance id at deploy, so
/// this is the shipped shape, not a simplification; it is also what makes unequip atomic (one
/// assignment row deleted, and the next projection simply does not produce that binding).
///
/// <para><b>Two moments, two tests.</b> <see cref="EquipGate.Admits"/> is the ASSIGN gate and is hard.
/// <see cref="EquipGate.Projectable"/> is the DEPLOY test and is deliberately weaker — a standing
/// assignment whose `level_req` lapsed still projects, because filtering it here would be
/// force-unequip wearing a projection's clothes (I11 §2.6). A lapse produces a reported shortfall,
/// never a missing binding.</para>
/// </summary>
public sealed class EquipProjector
{
    readonly EquipGate _gate;
    readonly Func<string, SpecimenActor> _actorOf;
    readonly Func<EquipAssignment, EquipItemFacts> _itemFactsOf;
    readonly Func<EquipAssignment, IReadOnlyList<SocketSlot>> _socketsOf;
    readonly Func<EquipAssignment, IReadOnlyList<SocketSlot>, IReadOnlyList<ComboBindTarget>>? _combosOf;

    public EquipProjector(
        EquipGate gate,
        Func<string, SpecimenActor> actorOf,
        Func<EquipAssignment, EquipItemFacts> itemFactsOf,
        Func<EquipAssignment, IReadOnlyList<SocketSlot>>? socketsOf = null,
        Func<EquipAssignment, IReadOnlyList<SocketSlot>, IReadOnlyList<ComboBindTarget>>? combosOf = null)
    {
        _gate = gate;
        _actorOf = actorOf;
        _itemFactsOf = itemFactsOf;
        // Unwired (null) means no sockets exist to the projector — the pre-T21 shape. Callers that
        // equip socketable items pass the host's rows; the projector never reads storage itself.
        _socketsOf = socketsOf ?? (_ => Array.Empty<SocketSlot>());
        // Unwired (null) means the caller runs no combination evaluator — the pre-SSH4.6 shape. The
        // CALLER supplies the call (it owns the catalog and the tuning), so the projector stays pure.
        _combosOf = combosOf;
    }

    public ProjectionResult Project(string specimenId, IReadOnlyList<EquipAssignment> assignments)
    {
        var actor = _actorOf(specimenId);

        // (assignment, item facts, admit refusal or null) computed once per row -- Admits and
        // Projectable would otherwise each re-derive the same facts independently.
        var judged = assignments.Select(a =>
        {
            var facts = _itemFactsOf(a);
            return (Assignment: a, Facts: facts,
                    AdmitRefusal: _gate.Explain(a.Role, actor, facts.Frame, facts.LevelReq, facts.FactionReq),
                    Projectable: _gate.Projectable(a.Role, actor, facts.Frame, facts.FactionReq));
        }).ToList();

        var bindings = new List<EquipAssignment>();
        foreach (var j in judged.Where(j => j.Projectable))
        {
            bindings.Add(j.Assignment);
            var sockets = _socketsOf(j.Assignment);
            // The insert's binding MUST come out of this projection, not a parallel Bind() call
            // beside it. ApplyEquipProjection withdraws every UniqueActor-scoped binding whose
            // InstanceId is absent from `desired` -- so a socket binding written anywhere else is
            // reaped on the next squad build, and the symptom is a gem that works once and then
            // silently stops contributing. Inserts inherit the host row's admission (no separate
            // gate: no refusal vocabulary exists for them, and the host was already judged above);
            // they bind at the host's role, carrying the insert instance as RefId.
            foreach (var slot in sockets.Where(s => !s.IsEmpty))
            {
                if (slot.InsertInstanceId is not { Length: > 0 } insertInstance)
                    continue;   // pre-gem-tier rows carry no instance; skipping is honest, inventing one is not
                bindings.Add(new EquipAssignment(
                    j.Assignment.SpecimenId, j.Assignment.Role,
                    EquipRefKinds.Rolled, insertInstance, j.Assignment.AssignedUtc));
            }

            // SSH4.6 (spec-combo-bind §2): every firing combination binds through THIS projection too,
            // after the inserts, for the identical reason — ApplyEquipProjection reaps anything the
            // desired set does not carry. ⛔ No per-actor count (R12): every target binds, and the only
            // "at most one" is one Strain/Splice identity per CIRCUIT, enforced inside the evaluator.
            if (_combosOf is null) continue;
            foreach (var target in _combosOf(j.Assignment, sockets))
                bindings.Add(new EquipAssignment(
                    j.Assignment.SpecimenId, j.Assignment.Role,
                    EquipRefKinds.Rolled, target.ComboInstanceId, j.Assignment.AssignedUtc));
        }

        return new ProjectionResult(
            Bindings: bindings,
            Shortfalls: judged.Where(j => j.AdmitRefusal is not null)
                .Select(j => (j.Assignment, j.AdmitRefusal!.Value)).ToList(),
            // Invariant, asserted rather than assumed: today Projectable is strictly weaker than
            // Admits (it drops the level check only), so "not Projectable" always implies Explain
            // found SOME reason. A future Gate change that lets Projectable fail independently of
            // Explain must update this, not silently null-deref here.
            Skipped: judged.Where(j => !j.Projectable)
                .Select(j => (j.Assignment, j.AdmitRefusal ?? throw new InvalidOperationException(
                    $"role {j.Assignment.Role}: Projectable() refused but Explain() found no reason — " +
                    "the two must agree on every row Projectable rejects.")))
                .ToList());
    }
}
