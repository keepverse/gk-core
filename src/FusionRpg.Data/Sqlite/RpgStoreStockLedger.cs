using FusionRpg.Core.Actions.Cost;

namespace FusionRpg.Data;

/// <summary>
/// The real <see cref="IStockLedger"/>: an action's <c>holdsStock</c> demands, taken out of
/// <c>rpg_item_stock</c> at commit.
///
/// <para>Deliberately thin. All the interesting behaviour — one transaction, the conditional
/// decrement that is also the re-check, first-shortfall-wins, roll everything back — lives in
/// <see cref="RpgStore.TrySpendStock"/>, because that is where the transaction is and a spend
/// mechanism split across a boundary is a spend mechanism with two owners. This type exists only so
/// Core can hold the seam without holding a store (<c>guard-dal.ps1</c>), exactly as
/// <c>IAffordabilityCheck</c> lets it hold a cost gate without holding one.</para>
///
/// <para>⚠ <b>The actor key is the player id.</b> v1 binds a consumable at <c>player:{id}</c>
/// (ssot-consumables.md §4.3), so one inventory serves every actor a player fields, and
/// <c>rpg_item_stock</c> is keyed <c>(player_id, container_id)</c> with no actor column. The day
/// per-specimen stock exists (§10.4, the owner's) this is where the mapping goes — named here rather
/// than assumed away, since a silent identity mapping is the kind of thing that reads as designed.
/// </para>
/// </summary>
public sealed class RpgStoreStockLedger : IStockLedger
{
    readonly RpgStore _store;
    readonly Func<string, string> _playerIdOf;

    /// <param name="playerIdOf">Maps an actor key to the player whose inventory pays. Defaults to
    /// identity, which is correct while stock is player-scoped.</param>
    public RpgStoreStockLedger(RpgStore store, Func<string, string>? playerIdOf = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _playerIdOf = playerIdOf ?? (key => key);
    }

    public StockSpendResult TrySpend(string actorKey, string actionId, IReadOnlyList<StockDemand> demands) =>
        demands is null || demands.Count == 0
            ? StockSpendResult.Spent
            : _store.TrySpendStock(_playerIdOf(actorKey), demands);
}
