using FusionRpg.Core.Items;
using FusionRpg.Data;

namespace FusionRpg.Server.Gates;

/// <summary>One role's outcome from an apply, in stored order. A <see cref="LoadoutEntryState.Missing"/>
/// entry is reported here too, never dropped (spec-item-loadout-apply.md's own testing strategy).</summary>
public sealed record LoadoutRoleOutcome(string Role, bool Ok, string Reason);

/// <summary>A stock entry's own named plan-time refusal — found by preview, before anything is
/// written, never a skipped row (spec-item-loadout-apply.md §4).</summary>
public sealed record LoadoutStockRefusal(string Role, string RefId, string Reason);

/// <summary>
/// The loadout library's <see cref="LoadoutPlan"/> plus the plan-time refusals that plan alone
/// cannot express: a stock entry whose role has no legacy slot, or whose target is the commander.
/// Either kind of refusal means <b>nothing is written</b>, same as a conflict.
/// </summary>
public sealed record ItemLoadoutPlan(LoadoutPlan Loadout, IReadOnlyList<LoadoutStockRefusal> StockRefusals)
{
    public bool Refused => Loadout.Refused || StockRefusals.Count > 0;
}

/// <summary>The whole outcome of an apply: the plan that was checked, and — only when the plan was
/// not refused — one <see cref="LoadoutRoleOutcome"/> per stored entry.</summary>
public sealed record ItemLoadoutApplyOutcome(
    bool Ok, ItemLoadoutPlan Plan, IReadOnlyList<LoadoutRoleOutcome> Results);

/// <summary>
/// build-preset BP1.7 (spec-item-loadout-apply.md): the item program's loadout library (module 2)
/// has never had a caller for its apply half. This dispatches each entry to the flow that owns its
/// ref kind — never writes <c>rpg_item_assignment</c> directly, and never lets one flow write the
/// other's kind (X4, <see cref="LoadoutReport.AssignmentRefKindFor"/>).
///
/// <para><b>Rolled entries reuse the equip route's own target resolution.</b> <see cref="ItemEquipService.Equip"/>/
/// <see cref="ItemEquipService.Unequip"/> already resolve a specimen id or the commander's stable id
/// through their own <c>TryResolveTarget</c> — this service never re-derives that grammar, it just
/// passes <c>targetId</c> straight through.</para>
///
/// <para><b>Stock entries take the relic wire's legacy slot word, not a role</b>
/// (<see cref="LegacyEquipSlots.TryToLegacy"/>), and only for a specimen in <c>Roster</c> phase
/// (<see cref="UniqueActorService.PutEquipment"/>'s own gate) — never the commander, who has no
/// legacy-slot pouch. Both constraints are checked at plan time (<see cref="ItemLoadoutPlan.StockRefusals"/>),
/// so a preview names them before an apply would ever attempt the write.</para>
/// </summary>
public sealed class ItemLoadoutApplyService
{
    readonly RpgStore _store;
    readonly ItemEquipService _equip;
    readonly UniqueActorService _unique;

    public ItemLoadoutApplyService(RpgStore store, ItemEquipService equip, UniqueActorService unique)
    {
        _store = store;
        _equip = equip;
        _unique = unique;
    }

    /// <summary>The plan only, no write — the same computation <see cref="ApplyAsync"/> checks before
    /// writing anything.</summary>
    public ItemLoadoutPlan Preview(long playerId, string loadoutId, string targetId, bool force = false) =>
        BuildPlan(playerId, loadoutId, targetId, force);

    public async Task<ItemLoadoutApplyOutcome> ApplyAsync(long playerId, string loadoutId, string targetId, bool force)
    {
        var plan = BuildPlan(playerId, loadoutId, targetId, force);
        if (plan.Refused)
            return new ItemLoadoutApplyOutcome(false, plan, Array.Empty<LoadoutRoleOutcome>());

        // force: strip every contested cell through ITS owning flow before this apply's own writes.
        // Every cell here comes from FindAssignmentHolders queried at the assignment table's own
        // Rolled kind (LoadoutReport.Plan's heldBy contract), so the owning flow is always the equip
        // executor -- never the relic wire, which cannot hold an instance-pinned conflict at all
        // (a stock entry never pins one copy, spec-item-loadout-apply.md's own "not in conflict").
        foreach (var cell in plan.Loadout.Stripped)
            _equip.Unequip(playerId, cell.SpecimenId, cell.Role);

        var results = new List<LoadoutRoleOutcome>();
        var wroteAnything = plan.Loadout.Stripped.Count > 0;

        foreach (var e in plan.Loadout.Entries)
        {
            if (e.State == LoadoutEntryState.Missing)
            {
                results.Add(new LoadoutRoleOutcome(e.Role, false, "loadout.entry-missing"));
                continue;
            }

            var (ok, reason) = LoadoutReport.AssignmentRefKindFor(e.RefKind) switch
            {
                EquipRefKinds.Rolled => ApplyRolled(playerId, targetId, e),
                EquipRefKinds.Stock => ApplyStock(targetId, e),
                var kind => throw new InvalidOperationException($"unreachable assignment ref kind: {kind}"),
            };
            results.Add(new LoadoutRoleOutcome(e.Role, ok, reason));
            wroteAnything = true;
        }

        // One lawn runtime sync after the LAST write, not one per role (spec-item-loadout-apply.md
        // §Apply step 6) -- the exact call ItemEquipEndpoints.cs's own /equip and /unequip routes
        // make, reused rather than re-derived.
        if (wroteAnything)
            await ItemEquipEndpoints.SyncLawnRuntimeAsync(_store, _unique, targetId, playerId).ConfigureAwait(false);

        return new ItemLoadoutApplyOutcome(true, plan, results);
    }

    ItemLoadoutPlan BuildPlan(long playerId, string loadoutId, string targetId, bool force)
    {
        var playerKey = PlayerKey(playerId);
        var entries = _store.GetLoadoutEntriesValidated(loadoutId, playerKey);

        var instancePinnedRefIds = entries
            .Where(e => e.State == LoadoutEntryState.Present
                && string.Equals(e.RefKind, LoadoutReport.InstanceRefKind, StringComparison.Ordinal))
            .Select(e => e.RefId)
            .ToList();
        var heldBy = _store.FindAssignmentHolders(instancePinnedRefIds);

        var loadoutPlan = LoadoutReport.Plan(entries, targetId, heldBy, force);

        var isCommanderTarget = FusionRpg.Core.Commanders.CommanderDirectoryHub.Current.TryResolve(targetId, out _);
        var stockRefusals = new List<LoadoutStockRefusal>();
        foreach (var e in entries)
        {
            if (e.State != LoadoutEntryState.Present) continue;
            if (!string.Equals(e.RefKind, EquipRefKinds.Stock, StringComparison.Ordinal)) continue;

            if (isCommanderTarget)
            {
                stockRefusals.Add(new LoadoutStockRefusal(e.Role, e.RefId, "loadout.stock-target-commander"));
                continue;
            }
            if (!ItemRoles.TryParse(e.Role, out var role) || !LegacyEquipSlots.TryToLegacy(role, out _))
                stockRefusals.Add(new LoadoutStockRefusal(e.Role, e.RefId, "loadout.stock-role-unmapped"));
        }

        return new ItemLoadoutPlan(loadoutPlan, stockRefusals);
    }

    (bool Ok, string Reason) ApplyRolled(long playerId, string targetId, LoadoutEntryStatus e)
    {
        var outcome = _equip.Equip(playerId, targetId, e.RefId, e.Role);
        return (outcome.Ok, outcome.Reason);
    }

    (bool Ok, string Reason) ApplyStock(string targetId, LoadoutEntryStatus e)
    {
        // stock-role-unmapped / stock-target-commander already refused the whole plan at BuildPlan
        // time, so a Stock entry reaching here always has a legal (role, non-commander target) pair.
        ItemRoles.TryParse(e.Role, out var role);
        LegacyEquipSlots.TryToLegacy(role, out var slot);
        var (ok, reason, _) = _unique.PutEquipment(targetId, slot, e.RefId);
        return (ok, reason);
    }

    static string PlayerKey(long playerId) => playerId.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
