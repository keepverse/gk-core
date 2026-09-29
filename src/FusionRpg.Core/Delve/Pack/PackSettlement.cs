namespace FusionRpg.Core.Delve.Pack;

/// <summary>What one placed item resolves to at `CloseDelve` (spec-loot-pack.md §7). The Data-layer
/// caller (`RpgStore.Delve.cs`) is the only writer these actions describe — this type only decides.</summary>
public enum PackSettlementAction
{
    /// <summary>A haul GEAR instance's lock row is deleted; the instance, already owned since
    /// placement (§5), becomes visible at home with no further write.</summary>
    UnlockHaulInstance,

    /// <summary>A fungible stack (haul or unconsumed carry-in remainder) is upserted into
    /// `rpg_item_stock` by its own qty.</summary>
    BankStack,

    /// <summary>A carry-in GEAR instance's lock row is deleted — it was never removed from home
    /// ownership, only reserved for the delve.</summary>
    UnlockCarryInInstance,

    /// <summary>A haul instance (gear or the residual of a stack) is destroyed on a wipe — `disposition
    /// = 'destroyed'` plus `DeleteInstance` (§7: "the haul banks nothing").</summary>
    DestroyHaulInstance,
}

public sealed record PackSettlementWrite(PackSettlementAction Action, string RefId, string? InstanceId, long Qty);

/// <summary>
/// D3.22 (spec-loot-pack.md §7) — `PackSettlement.Decide(items, extracted)`, pure, returns a write
/// list; `RpgStore.Delve.CloseDelve` is the only applier, on the SAME transaction as attrition
/// settlement and loot earn (spec's own stated hook order: pack settlement first).
///
/// <para><paramref name="items"/> is every <see cref="PackItem"/> CURRENTLY in a party's pack at
/// settlement time — grid cells and room-floor items alike (§7 treats both the same way per origin;
/// floor items are not a separate persisted list on the party itself, they live on
/// `rpg_delve_rooms.floor_json`, already `delve-scope`'s own column — the caller supplies them here
/// having already read that column, this function does not know where an item came from, only what
/// it is).</para>
///
/// <para><b>Honest gap, named:</b> a carry-in item already DROPPED mid-delve via
/// <see cref="PackMoves.ApplyDrop"/> leaves neither the grid nor the floor (its own doc comment: "does
/// not join the floor... destroyed at settlement"). Read literally against a store with no real
/// caller yet for `pack.drop`, the only coherent reading is that ITS OWN destruction fires immediately
/// when the drop is applied, not deferred here — there is nothing left for `Decide` to see by
/// settlement time. That immediate-destroy wiring belongs to whichever future endpoint calls
/// `PackMoves.ApplyDrop` in production; it does not exist today, so it cannot be built against a real
/// caller yet, matching this whole task list's own "don't guess wiring with no caller" discipline.</para>
/// </summary>
public static class PackSettlement
{
    public static IReadOnlyList<PackSettlementWrite> Decide(IReadOnlyList<PackItem> items, bool extracted)
    {
        if (items is null) throw new ArgumentNullException(nameof(items));

        var writes = new List<PackSettlementWrite>();
        foreach (var item in items)
        {
            var isStack = item.InstanceId is null;
            if (extracted)
            {
                if (isStack)
                    writes.Add(new PackSettlementWrite(PackSettlementAction.BankStack, item.RefId, null, item.Qty));
                else if (item.Origin == PackItemOrigin.Haul)
                    writes.Add(new PackSettlementWrite(PackSettlementAction.UnlockHaulInstance, item.RefId, item.InstanceId, item.Qty));
                else
                    writes.Add(new PackSettlementWrite(PackSettlementAction.UnlockCarryInInstance, item.RefId, item.InstanceId, item.Qty));
            }
            else // Wiped
            {
                if (item.Origin == PackItemOrigin.Haul)
                    writes.Add(new PackSettlementWrite(PackSettlementAction.DestroyHaulInstance, item.RefId, item.InstanceId, item.Qty));
                else if (isStack)
                    writes.Add(new PackSettlementWrite(PackSettlementAction.BankStack, item.RefId, null, item.Qty)); // unconsumed carry-in stock returns home
                else
                    writes.Add(new PackSettlementWrite(PackSettlementAction.UnlockCarryInInstance, item.RefId, item.InstanceId, item.Qty)); // carry-in gear returns home
            }
        }
        return writes;
    }

    /// <summary>Spec §5, verbatim: "grant i → party i mod parties" — the boss room's own grant-index
    /// deal, deterministic, no roll (`loot.bossGrantDistribution = round-robin`).</summary>
    public static int RoundRobinParty(int grantIndex, int partyCount)
    {
        if (partyCount <= 0) throw new ArgumentOutOfRangeException(nameof(partyCount));
        if (grantIndex < 0) throw new ArgumentOutOfRangeException(nameof(grantIndex));
        return grantIndex % partyCount;
    }
}
