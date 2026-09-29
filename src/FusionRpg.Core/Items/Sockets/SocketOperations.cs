using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items.Mutation;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Items.Sockets;

/// <summary>
/// The four socket operations, as pure state transitions over one item's <c>item_socket</c> rows.
///
/// <para><b>All four are RNG-free</b>, which makes this the cheapest possible client of D2's mutation
/// model. Per D2 clause 13 the ops are appended for <b>audit and idempotency only</b> — nothing
/// replays them to rebuild state, because <c>item_socket</c> is the SSOT (D2 §6 refused
/// ssot-sockets.md §5.2's "materialized view" framing by name).</para>
///
/// <para>⛔ <b>No <c>op_kind</c> is defined here.</b> The namespace is module 15's
/// (<see cref="MutationOpKind"/>), which already carries <c>socket-add</c>, <c>socket-insert</c>,
/// <c>socket-remove</c> and D24's <c>socket-imbue</c>. This module performs them; it does not mint
/// them.</para>
///
/// <para><b>Nothing here touches the host's frozen instance.</b> No method returns an atom row, a
/// value write or anything that could reach <c>effect_instance_atom</c> — socketing composes at the
/// BINDING layer, so <c>InstanceRow.ContentFingerprint()</c> survives every operation untouched and
/// SC5's reproduction contract is not strained at all.</para>
/// </summary>
public static class SocketOperations
{
    /// <summary>
    /// <c>socket-add</c> — open one empty, <b>crafted</b> socket (affinity <c>""</c>, imbuable).
    /// Available at every rarity (D23); the material cost scales with the target's rung and is module
    /// 14's, never this module's.
    /// </summary>
    public static AtomRejection TryAdd(
        IReadOnlyList<SocketSlot> sockets, int entrySocketMax, out IReadOnlyList<SocketSlot> next)
    {
        next = sockets;

        if (entrySocketMax <= 0)
            return SocketRules.Violated(SocketRules.NotSocketable,
                "this base type declares no sockets, so there is nothing to widen");

        if (sockets.Count >= entrySocketMax)
            return SocketRules.Violated(SocketRules.NoFreeSocket,
                $"the item is already at its base type's socketMax of {entrySocketMax} — the fix is to accept the cap, " +
                "not to empty a socket");

        next = sockets.Append(new SocketSlot(sockets.Count, Affinity: "", Crafted: true)).ToList();
        return AtomRejection.Ok;
    }

    /// <summary>
    /// <c>socket-insert</c>. <paramref name="socketIndex"/> is <c>null</c> for auto-pick, which takes
    /// the lowest empty index — deterministic, so two runs of the same sequence agree.
    /// </summary>
    public static AtomRejection TryInsert(
        IReadOnlyList<SocketSlot> sockets, int? socketIndex, InsertDef insert, string insertInstanceId,
        out IReadOnlyList<SocketSlot> next)
    {
        next = sockets;

        // "Wrong type" is deliberately absent from this table: under §6 a socket never rejects an
        // insert for ELEMENT. What it does reject is the wrong KIND of container.
        if (!insert.ContainerId.StartsWith("gem.", StringComparison.Ordinal))
            return SocketRules.Violated(SocketRules.NotSocketable,
                $"'{insert.ContainerId}' is not an insert — only a `gem.*` container goes in a socket, and a " +
                $"`{ResonanceGenerator.ComboPrefix}*` combination row in particular is a BONUS, never an ingredient");

        if (sockets.Count == 0)
            return SocketRules.Violated(SocketRules.NotSocketable,
                "the host has no sockets — add one first, or pick a different item");

        if (insert.Unique && sockets.Any(s =>
                string.Equals(s.InsertContainerId, insert.ContainerId, StringComparison.Ordinal)))
            return AtomRejection.Fail(AtomRejectionReason.DuplicateKey,
                $"'{insert.ContainerId}' is unique-tagged and is already in this item");

        int index;
        if (socketIndex is { } explicitIndex)
        {
            if (explicitIndex < 0 || explicitIndex >= sockets.Count)
                return AtomRejection.Fail(AtomRejectionReason.BadParamValue,
                    $"socket_index {explicitIndex} is outside [0, {sockets.Count})");

            if (!sockets[explicitIndex].IsEmpty)
                // Deliberately NOT folded into no-free-socket: that would tell a player to add a
                // socket when what they need to do is empty one.
                return SocketRules.Violated(SocketRules.Occupied,
                    $"socket {explicitIndex} already holds '{sockets[explicitIndex].InsertContainerId}' — remove it first");

            index = explicitIndex;
        }
        else
        {
            var free = sockets.FirstOrDefault(s => s.IsEmpty, new SocketSlot(-1, "", false));
            if (free.Index < 0)
                return SocketRules.Violated(SocketRules.NoFreeSocket,
                    "every socket on this item is full — the fix is to make room, or to add a socket");
            index = free.Index;
        }

        next = Replace(sockets, index, s => s with
        {
            InsertContainerId = insert.ContainerId,
            InsertInstanceId = insertInstanceId,
        });
        return AtomRejection.Ok;
    }

    /// <summary>
    /// <c>socket-remove</c>. The item always survives; what varies is whether the insert does, which
    /// <see cref="SocketTuning.RemovalFor"/> decides from the insert's tier.
    /// </summary>
    public static AtomRejection TryRemove(
        IReadOnlyList<SocketSlot> sockets, int socketIndex, out IReadOnlyList<SocketSlot> next)
    {
        next = sockets;

        if (socketIndex < 0 || socketIndex >= sockets.Count)
            return AtomRejection.Fail(AtomRejectionReason.BadParamValue,
                $"socket_index {socketIndex} is outside [0, {sockets.Count})");

        if (sockets[socketIndex].IsEmpty)
            return SocketRules.Violated(SocketRules.NoFreeSocket,
                $"socket {socketIndex} is already empty — there is nothing there to take");

        next = Replace(sockets, socketIndex, s => s with { InsertContainerId = null, InsertInstanceId = null });
        return AtomRejection.Ok;
    }

    /// <summary>
    /// ⭐ <c>socket-imbue</c> (D24) — set a socket's affinity to one concrete element. Legal only on a
    /// socket that is <b>empty</b> and <b>crafted</b>: a drop-declared affinity is the base type's
    /// statement about the item and is not the crafter's to overwrite, and imbuing a filled socket
    /// would retroactively attune an insert already committed.
    ///
    /// <para>⚠ The operation is <b>not</b> named <c>attune</c>: ssot-sockets.md §4.2/§7.1/§7.2 already
    /// use <i>attuned</i> for "an insert whose element matches its socket's affinity", and one word
    /// with two meanings in one lane is how a spec stops being readable.</para>
    /// </summary>
    public static AtomRejection TryImbue(
        IReadOnlyList<SocketSlot> sockets, int socketIndex, string element, out IReadOnlyList<SocketSlot> next)
    {
        next = sockets;

        if (socketIndex < 0 || socketIndex >= sockets.Count)
            return AtomRejection.Fail(AtomRejectionReason.BadParamValue,
                $"socket_index {socketIndex} is outside [0, {sockets.Count})");

        if (!ElementRoster.TryParse(element, out _))
            return AtomRejection.Fail(AtomRejectionReason.BadParamValue,
                $"'{element}' is not a concrete element — 'omni' in particular is never an affinity " +
                "(element-hub-ssot.md §4)");

        var slot = sockets[socketIndex];

        if (!slot.IsEmpty)
            return SocketRules.Violated(SocketRules.NotImbuable,
                $"socket {socketIndex} holds '{slot.InsertContainerId}' — imbuing it would retroactively attune an " +
                "insert the player already committed. Remove it first");

        if (!slot.Crafted)
            return SocketRules.Violated(SocketRules.NotImbuable,
                $"socket {socketIndex} was declared at drop, and its affinity is the base type's statement about the " +
                "item. D24 gives the crafter the affinity of a socket THEY opened, not of one they were given");

        next = Replace(sockets, socketIndex, s => s with { Affinity = element.Trim().ToLowerInvariant() });
        return AtomRejection.Ok;
    }

    /// <summary>
    /// Join <c>item_socket</c> rows to the gem catalog for the evaluator. Empty sockets drop out; an
    /// insert id the catalog cannot resolve is <c>UnknownContainer</c> rather than a silent skip.
    /// </summary>
    public static AtomRejection TryBuildFill(
        IReadOnlyList<SocketSlot> sockets, IReadOnlyDictionary<string, InsertDef> catalog,
        out IReadOnlyList<SocketFill> fill)
    {
        var rows = new List<SocketFill>();
        foreach (var slot in sockets.Where(s => !s.IsEmpty).OrderBy(s => s.Index))
        {
            if (!catalog.TryGetValue(slot.InsertContainerId!, out var insert))
            {
                fill = Array.Empty<SocketFill>();
                return AtomRejection.Fail(AtomRejectionReason.UnknownContainer,
                    $"socket {slot.Index} holds '{slot.InsertContainerId}', which is not in the insert catalog");
            }

            rows.Add(new SocketFill(slot.Index, slot.Affinity, insert));
        }

        fill = rows;
        return AtomRejection.Ok;
    }

    /// <summary>
    /// ⚠ <b>The <c>bind_ordinal</c> a socket-layer binding would carry</b>, per spec-sockets.md §2:
    /// <c>socket_index + 1</c>, so two identical inserts in two sockets of one item no longer tie
    /// under <c>(priority DESC, container_id ASC, seq ASC)</c>. It is <b>content-derived</b>, which is
    /// why <c>binding_id</c> was rejected for the job — that is generated.
    ///
    /// <para>⏸ The column itself is <b>effect-atom E6's</b> and does not exist on
    /// <c>effect_binding</c> today, so this value has nowhere to be written yet. It is computed here
    /// so the socket half of the contract is testable now and the DAL half is a wiring change, not a
    /// design one. Everything that is not a socket binding stays <c>0</c> and sorts exactly as it
    /// does today.</para>
    /// </summary>
    public static int BindOrdinalFor(int socketIndex) => socketIndex + 1;

    static IReadOnlyList<SocketSlot> Replace(
        IReadOnlyList<SocketSlot> sockets, int index, Func<SocketSlot, SocketSlot> f)
    {
        var copy = sockets.ToArray();
        copy[index] = f(copy[index]);
        return copy;
    }
}
