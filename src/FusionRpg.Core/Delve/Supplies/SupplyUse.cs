using FusionRpg.Core.Actions;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items.Consumables;

namespace FusionRpg.Core.Delve.Supplies;

/// <summary>A resolved supply use — either a refusal by name, or the atoms to fire plus the pack
/// decrement and decision-log payload the caller applies (spec-supplies-and-objects.md §3, §9).</summary>
public sealed record SupplyUseOutcome(
    bool Ok, string Reason, IReadOnlyList<InstanceAtomRow>? Atoms, string? DecrementContainerId, object? Decision)
{
    public static SupplyUseOutcome Refuse(string reason) => new(false, reason, null, null, null);

    public static SupplyUseOutcome Fire(IReadOnlyList<InstanceAtomRow> atoms, string decrementContainerId, object decision) =>
        new(true, "", atoms, decrementContainerId, decision);
}

/// <summary>
/// D3.26 (spec-supplies-and-objects.md §3, code style) — one supply use: the context gate, the pack's
/// own stock (never `rpg_item_stock` — §3: "checks `HoldsStock`... over the PACK... the armoury stays
/// home"), the revive-target gate, then fire. Pure — every fact is a parameter (§9); the host applies
/// the outcome (fires the atoms, decrements the pack, appends the decision) in one transaction.
/// </summary>
public static class SupplyUse
{
    public static SupplyUseOutcome Use(
        string memberId, long partyIndex, bool memberDowned,
        InstanceRow supply, ConsumableClass supplyClass, UseContext ctx,
        Func<string, long, bool> holdsStock)
    {
        if (supply is null) throw new ArgumentNullException(nameof(supply));
        if (holdsStock is null) throw new ArgumentNullException(nameof(holdsStock));

        // §8: "UseContext.Menu anywhere in a delve (supply.menu-in-delve, before GateManifest)".
        if (ctx == UseContext.Menu) return SupplyUseOutcome.Refuse("supply.menu-in-delve");

        // §3: "useContext: battle RIDES THE ACTION LAYER: the supply's GrantsActionId names a corpus
        // action... and whose cost IS the item -- A3's item-cost row, gating for battle use only."
        // Revive is exempted BY DESIGN, not by oversight: §3 calls it "the one legal Downed -> Charging
        // trigger OUTSIDE a corpus action" -- it never rides GrantsActionId, so it never waits on A3.
        if (ctx == UseContext.Battle && supplyClass != ConsumableClass.Revive && !CrossProgramLandedFlags.ItemCostRowLanded)
            return SupplyUseOutcome.Refuse("supply.battle-cost-row-unbuilt");

        // §3: HoldsStock reads the PACK, never rpg_item_stock -- `holdsStock` is the caller's own pack
        // read, exactly the "read model owned elsewhere" shape this whole program uses for store facts.
        if (!holdsStock(supply.ContainerId, 1)) return SupplyUseOutcome.Refuse("supply.not-held");

        if (supplyClass == ConsumableClass.Revive)
        {
            // §3: "only on a member whose machine is Downed"; "Usable at rest and battle, never at curio".
            if (!memberDowned) return SupplyUseOutcome.Refuse("supply.target-not-downed");
            if (ctx == UseContext.Curio) return SupplyUseOutcome.Refuse("supply.revive-not-at-curio");
        }

        return SupplyUseOutcome.Fire(supply.Atoms, supply.ContainerId,
            new { kind = "supply.use", partyIndex, payload = new { memberId, supply.ContainerId, context = UseContexts.Wire(ctx) } });
    }
}
