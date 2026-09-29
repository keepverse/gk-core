namespace FusionRpg.Core.PassiveTree.GateCounters;

/// <summary>
/// Who a gate-counter credit belongs to (spec-gate-counters.md §4.1's <c>owner_kind</c>/<c>owner_key</c>
/// pair, not a bare player id — "a single typed column fits one owner shape and this table has to
/// survive a second one"). <c>Kind</c> is <c>"player"</c> today; <c>Key</c> mirrors
/// <c>AptitudeEndpoints.ScopeKey(playerId)</c>'s own <c>"player:{id}"</c> shape so a later per-specimen
/// owner kind can arrive without a migration.
///
/// <para>This is the resolver's OUTPUT, never something a counter derives itself (§2.1's "ownership is
/// decided at spawn" rule) — see <see cref="StatusAppliedCounter"/>'s own doc comment for why the
/// resolver, not this record, is where that rule actually has to be honoured.</para>
/// </summary>
public readonly record struct GateOwnerKey(string Kind, string Key);

/// <summary>
/// One sparse row's identity in <c>rpg_gate_counter</c> (spec-gate-counters.md §4.1) — the accumulator's
/// dictionary key and the store's upsert key are the SAME shape on purpose, so nothing translates
/// between "what accumulated" and "what persists" (§4.3's whole point: a flush is a batched write of
/// exactly what this type already carries, never a second mapping step that could drift from it).
/// </summary>
public readonly record struct GateCounterKey(string OwnerKind, string OwnerKey, string Quantity, string SubjectId)
{
    public GateCounterKey(GateOwnerKey owner, string quantity, string subjectId)
        : this(owner.Kind, owner.Key, quantity, subjectId) { }
}
