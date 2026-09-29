using FusionRpg.Core.Battle;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Sockets;
using FusionRpg.Core.Stats.Derived.Subsystems;
using FusionRpg.Data;

namespace FusionRpg.Server;

/// <summary>
/// Shared UniqueActor equip → <see cref="EquippedAtomInput"/> / <see cref="BoundDerivedAtom"/> path
/// so battle <see cref="BattleStatComposer"/> and sheet Hub fan-in mint the same
/// <c>equip:{role}:{itemRef}</c> SourceIds (GG-49).
/// </summary>
public static class EquippedBoundAtoms
{
    /// <summary>Role-tagged inputs for <see cref="EquipAtomSource.FromEquippedResolver"/>.</summary>
    public static IReadOnlyList<EquippedAtomInput> InputsFromStore(RpgStore store, string specimenId)
    {
        var roleToItem = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // item module 24: the host assignment's own ref_kind travels with the input so the activation
        // filter can rebuild the durable assignment identity from the input alone. One read of the
        // same assignment list, never a second query.
        var roleToRefKind = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in store.ListAssignments(specimenId))
        {
            var roleId = ItemRoles.Id(a.Role);
            roleToItem[roleId] = a.RefId;
            roleToRefKind[roleId] = a.RefKind;
        }

        var resolution = store.ResolveBindings(
            new OwnerScope(OwnerKind.UniqueActor, specimenId),
            new BindContext(RuntimeId.Battle));

        if (resolution.AtomsByBinding is null || resolution.AtomsByBinding.Count == 0)
            return Array.Empty<EquippedAtomInput>();

        var inputs = new List<EquippedAtomInput>();
        // Socket rows by host instance, resolved lazily per host and cached for the call: an insert
        // binding's InstanceId is the gem's own instance (never the host's), so the socket index for
        // its SourceId comes from re-reading the host's rows here, at resolve time — never persisted
        // (§8.1: contributions are ephemeral per resolve, never a SQLite ledger).
        var socketsByHost = new Dictionary<string, IReadOnlyList<SocketSlot>>(StringComparer.Ordinal);
        // SSH4.7 (spec-combo-bind §3): the host's CURRENT combination targets, resolved lazily per host
        // with the ONE evaluator the projection uses. A binding is recognised only while its id is one of
        // these — a word that stopped firing contributes nothing, and no private fold of combat numbers
        // is introduced (guard-actor-hub).
        var combosByHost = new Dictionary<string, IReadOnlyList<ComboBindTarget>>(StringComparer.Ordinal);
        foreach (var binding in resolution.Bindings)
        {
            if (!resolution.AtomsByBinding.TryGetValue(binding.BindingId, out var atoms)) continue;
            var role = string.IsNullOrWhiteSpace(binding.Slot) ? "unknown" : binding.Slot!;
            if (!roleToItem.TryGetValue(role, out var itemRef))
                itemRef = binding.InstanceId;
            roleToRefKind.TryGetValue(role, out var refKind);

            // A combination binding: the id is deterministic (`cmb:{host}#c{circuit}:{containerId}`),
            // carries the host's role, and is recognised ONLY against the host's current targets.
            if (!string.Equals(binding.InstanceId, itemRef, StringComparison.Ordinal)
                && binding.InstanceId.StartsWith("cmb:", StringComparison.Ordinal))
            {
                if (!combosByHost.TryGetValue(itemRef, out var combos))
                {
                    if (!socketsByHost.TryGetValue(itemRef, out var hostSockets))
                        socketsByHost[itemRef] = hostSockets = store.GetSockets(itemRef);
                    combosByHost[itemRef] = combos = store.ComboTargetsFor(itemRef, hostSockets);
                }
                var target = combos.FirstOrDefault(t =>
                    string.Equals(t.ComboInstanceId, binding.InstanceId, StringComparison.Ordinal));
                if (target.ComboInstanceId is null) continue;   // no current target: contributes nothing
                foreach (var atom in atoms)
                    inputs.Add(new EquippedAtomInput(
                        role, itemRef, atom, SocketIndex: null,
                        ComboId: target.Result.ComboId, Circuit: target.Circuit, RefKind: refKind));
                continue;
            }
            int? socketIndex = null;
            if (!string.Equals(binding.InstanceId, itemRef, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(binding.Slot))
            {
                // Either an insert (the binding id carries the gem's instance while the slot carries
                // the host's role) or a stale host binding (re-equipped since the last materialize,
                // reaped on the next). The two are distinguished by the host's own socket rows, never
                // guessed: a matching socket mints the insert id with its index; anything else keeps
                // the exact pre-T21 behavior below (Equip mint against the mapped ref), so no existing
                // flow changes shape. Skipping a socketless match would drop a real number; inventing
                // an Equip mint for a real insert would misattribute one — the lookup avoids both.
                if (!socketsByHost.TryGetValue(itemRef, out var sockets))
                    socketsByHost[itemRef] = sockets = store.GetSockets(itemRef);
                var match = sockets.FirstOrDefault(s =>
                    string.Equals(s.InsertInstanceId, binding.InstanceId, StringComparison.Ordinal));
                if (match.InsertInstanceId is { Length: > 0 })
                {
                    socketIndex = match.Index;
                }
                else if (IsGemInstance(store, binding.InstanceId))
                {
                    // A stale insert binding: socketed after the last materialize emptied (or moved)
                    // the socket, reaped on the next. Skipped, not Equip-minted — attributing the
                    // gem's atoms to the host would be the wrong-but-plausible defect the Insert arm
                    // exists to prevent, and skipping self-heals at the next reconcile.
                    continue;
                }
                // Otherwise a stale host binding (re-equipped since the last materialize): keep the
                // exact pre-T21 Equip mint below, misattribution and all — changing it is a separate
                // program's reconciliation decision, not this module's.
            }
            foreach (var atom in atoms)
                inputs.Add(new EquippedAtomInput(role, itemRef, atom, socketIndex, RefKind: refKind));
        }

        return inputs;
    }

    /// <summary>Whether an instance id names a gem (insert) rather than equipment: resolved through
    /// the instance's container kind, never guessed from id shape. Slow path only — called solely
    /// for bindings that matched neither their host ref nor any socket row.</summary>
    static bool IsGemInstance(RpgStore store, string instanceId)
    {
        var instance = store.GetInstance(instanceId);
        var container = instance is null ? null : store.GetContainer(instance.ContainerId);
        return container is not null && container.Kind == FusionRpg.Core.Effects.Atoms.ContainerKind.Gem;
    }

    public static EquipAtomSource SourceFromStore(RpgStore store) =>
        EquipAtomSource.FromEquippedResolver(specimenId => InputsFromStore(store, specimenId));

    public static IReadOnlyList<BoundDerivedAtom> DerivedFromStore(RpgStore store, string specimenId)
    {
        var inputs = InputsFromStore(store, specimenId);
        if (inputs.Count == 0) return Array.Empty<BoundDerivedAtom>();
        return EquipAtomSource.FromEquippedResolver(_ => inputs).DerivedAtomsFor(specimenId);
    }
}
