namespace FusionRpg.Core.Effects.Atoms;

/// <summary>
/// backlog-clear BP2 (`spec-aura-binding-producer.md` §3.2, §5): one aura's own currently-durable
/// binding, as the caller (Server-side <c>AuraBindingProducer</c>) already resolved it — an aura id
/// paired with the `effect_binding` row's id. Kept minimal and Core-local rather than reusing
/// <c>BindingRow</c> itself: that type lives in <c>FusionRpg.Data</c>, and Core never depends on Data
/// (verified against both `.csproj` files — <c>InstanceProducer</c>'s own doc comment records the same
/// rule for the same reason).
/// </summary>
public readonly record struct AuraBoundRow(string AuraId, string BindingId);

/// <summary>What <see cref="AuraBindingPlan.Compute"/> decided: which aura ids need a fresh bind, and
/// which existing binding ids are no longer wanted and should be withdrawn. Both lists are ordered
/// (`StringComparer.Ordinal`) so two runs over the same inputs produce byte-identical output — this
/// feeds a durable write, and an unordered plan would make "what changed" un-diffable.</summary>
public sealed record AuraBindingPlanResult(
    IReadOnlyList<string> ToBind,
    IReadOnlyList<string> ToWithdraw);

/// <summary>
/// `aura-binding-producer` BP2: the reconcile itself, as a pure function — no store, no I/O, so every
/// edge case (already bound, no longer active, duplicate bindings for one aura, an aura equipped but
/// never enabled) is table-testable without a database (spec §5's own stated reason for the split).
///
/// <para><b>Declarative, not incremental</b> (spec §3.2): given the aura ids currently active and the
/// bindings that already exist, decide what to add and withdraw in one pass. An incremental
/// add/remove API would need every caller to already be correct; a reconcile only needs this function
/// to be correct once.</para>
/// </summary>
public static class AuraBindingPlan
{
    /// <summary>
    /// <paramref name="activeAuraIds"/> is the runtime's own post-eviction set (e.g.
    /// <c>AuraRuntime.ActiveAuraIds</c> after <c>Enable</c>/<c>Disable</c> already ran) — this function
    /// never re-derives activation, it only reconciles durable rows to match it.
    /// </summary>
    public static AuraBindingPlanResult Compute(
        IReadOnlyList<string> activeAuraIds,
        IReadOnlyList<AuraBoundRow> existingBindings)
    {
        var active = new HashSet<string>(activeAuraIds ?? Array.Empty<string>(), StringComparer.Ordinal);

        var byAura = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var row in existingBindings ?? Array.Empty<AuraBoundRow>())
        {
            if (!byAura.TryGetValue(row.AuraId, out var list))
                byAura[row.AuraId] = list = new List<string>();
            list.Add(row.BindingId);
        }

        var toWithdraw = new List<string>();
        foreach (var (auraId, bindingIds) in byAura)
        {
            bindingIds.Sort(StringComparer.Ordinal);
            if (active.Contains(auraId))
            {
                // Idempotent: already bound, nothing to add. Any SECOND row for the same still-active
                // aura is a duplicate a caller-side bug produced — keep the lowest id, withdraw the
                // rest, so this reconcile self-heals rather than double-applying the aura forever.
                for (var i = 1; i < bindingIds.Count; i++) toWithdraw.Add(bindingIds[i]);
            }
            else
            {
                // No longer active (disabled, evicted, or never re-confirmed) — withdraw every row.
                toWithdraw.AddRange(bindingIds);
            }
        }

        var toBind = active.Where(id => !byAura.ContainsKey(id))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        toWithdraw.Sort(StringComparer.Ordinal);
        return new AuraBindingPlanResult(toBind, toWithdraw);
    }
}
