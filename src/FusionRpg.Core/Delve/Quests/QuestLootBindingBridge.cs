using FusionRpg.Core.Items.Drops;

namespace FusionRpg.Core.Delve.Quests;

/// <summary>
/// D4.13's own real remaining bridge, closed 2026-09-07 (citation corrected the same day):
/// `QuestOffer.Satisfiable`'s own `lootBindingOffersRole(role)` delegate, spec §2 step 2's own wording
/// verbatim: "the role in some `lootBinding` TABLE's base-type set
/// (<see cref="LootContentView.BaseTypesFor"/>, `LootPipeline.cs:76`)."
///
/// <para><b>Resolved directly against the real shipped shape, not a new indexer.</b> A domain's own
/// `lootBinding` (<see cref="Domains.DomainSeedFile.LoadLootBindings"/>) is `roomKind -&gt; dropTableId`;
/// a real <see cref="DropTableRow"/> already carries its own `Equipment`-kind entries with an OPTIONAL
/// `(Frame, Role)` pair (`Items/Drops/DropTableModel.cs`, `DropTableEntryRow.Frame`/`.Role`) — no walk
/// through `item_base_type` is needed to find candidate entries, only to confirm the pair is actually
/// BACKED by real content: an authored `(frame, role)` combination with zero real base types must not
/// count as satisfiable (the same "row kept, never drawn" distinction `DropTableDraw.EffectiveWeight`
/// already draws between "authored" and "actually offers something").</para>
/// </summary>
public static class QuestLootBindingBridge
{
    /// <summary>Does <paramref name="table"/> carry ≥ 1 enabled `Equipment`-kind entry naming
    /// <paramref name="role"/>, whose own `(Frame, Role)` pair resolves to ≥ 1 real base type via
    /// <paramref name="baseTypesFor"/>? Entries of every other <see cref="DropEntryKind"/> never carry
    /// a role in the base-type sense (frame/role is an Equipment-only concept — every other kind names
    /// a concrete `RefId` directly), so this only ever looks at `Equipment` rows.</summary>
    public static bool TableOffersRole(DropTableRow table, string role, Func<string, string, IReadOnlyList<string>> baseTypesFor)
    {
        if (table is null) throw new ArgumentNullException(nameof(table));
        if (role is null) throw new ArgumentNullException(nameof(role));
        if (baseTypesFor is null) throw new ArgumentNullException(nameof(baseTypesFor));

        return table.Groups.SelectMany(g => g.Entries).Any(e =>
            e.Enabled && e.Kind == DropEntryKind.Equipment && e.Frame is not null
            && string.Equals(e.Role, role, StringComparison.Ordinal)
            && baseTypesFor(e.Frame, e.Role!).Count > 0);
    }

    /// <summary>Builds the per-domain `lootBindingOffersRole(role)` delegate
    /// <see cref="QuestOffer.Satisfiable"/> needs: ANY of the domain's own bound tables (across every
    /// room-kind binding — spec §2 step 2's own "in SOME lootBinding table," never a single fixed one)
    /// offering the role is enough. An unknown `tableId` (a binding naming a table absent from
    /// <paramref name="tables"/>) reads as "does not offer" here — referential integrity for the
    /// binding itself is `domain-catalog` row 9's own job
    /// (<c>DomainPreflightInputs.LootBindingFor</c>/`KnownDropTableIds`), not this bridge's.</summary>
    public static Func<string, bool> Build(
        IReadOnlyDictionary<string, string> lootBindingForDomain,
        IReadOnlyDictionary<string, DropTableRow> tables,
        Func<string, string, IReadOnlyList<string>> baseTypesFor)
    {
        if (lootBindingForDomain is null) throw new ArgumentNullException(nameof(lootBindingForDomain));
        if (tables is null) throw new ArgumentNullException(nameof(tables));
        if (baseTypesFor is null) throw new ArgumentNullException(nameof(baseTypesFor));

        var boundTableIds = lootBindingForDomain.Values.Distinct(StringComparer.Ordinal).ToList();
        return role => boundTableIds.Any(tableId => tables.TryGetValue(tableId, out var t) && TableOffersRole(t, role, baseTypesFor));
    }
}
