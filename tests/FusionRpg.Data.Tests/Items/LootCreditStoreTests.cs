using FusionRpg.Core.Items.Drops;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests.Items;

/// <summary>
/// species-gear-chain T29 — the shelf credit at the DAL. A drawn <c>Material</c> entry (and a
/// <c>Currency</c> <c>souls</c> entry) is credited in the SAME transaction as the drop-log row, the
/// <c>(player_id, correlation_id)</c> replay early-return credits nothing twice, and a credit that
/// refuses rolls the log row back with it.
/// </summary>
public class LootCreditStoreTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    readonly long _playerId;
    readonly string _playerKey;

    const string Substrate = "substrate.humanoid.crude";

    public LootCreditStoreTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _playerId = _store.GetCurrentPlayerId();
        _playerKey = _playerId.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public void Dispose() => _testStore.Dispose();

    static LootManifest Manifest(string correlationId, params LootGrant[] grants) =>
        new(correlationId, "table.loot-credit-test", 7UL, 24, grants, Array.Empty<string>(), "{}",
            LootPityState.Empty, LootPityState.Empty, null, false, null);

    static LootGrant MaterialGrant(string refId, long count) =>
        new(0, DropEntryKind.Material, refId, count, "");

    static LootGrant CurrencyGrant(string refId, long count) =>
        new(1, DropEntryKind.Currency, refId, count, "");

    void Persist(string playerKey, LootManifest manifest) =>
        _store.PersistLoot(playerKey, manifest, "test", "loot-credit",
            _store.GetCatalogRevision(), 1, Array.Empty<ItemGenerationRow>());

    [Fact]
    public void Loot_credits_a_drawn_material_entry_to_the_shelf()
    {
        Persist(_playerKey, Manifest("lc-1", MaterialGrant(Substrate, 7)));

        Assert.Equal(7, _store.GetMaterialQty(_playerId, Substrate));
        Assert.Single(_store.ListDropLog(_playerKey));
    }

    [Fact]
    public void Loot_credits_a_souls_currency_entry()
    {
        Persist(_playerKey, Manifest("lc-2", CurrencyGrant("souls", 25)));

        Assert.Equal(25, _store.GetSoulBalance(_playerId).Balance);
    }

    [Fact]
    public void A_replayed_correlation_id_credits_nothing_twice()
    {
        Persist(_playerKey, Manifest("lc-3", MaterialGrant(Substrate, 7)));
        Persist(_playerKey, Manifest("lc-3", MaterialGrant(Substrate, 7)));

        Assert.Equal(7, _store.GetMaterialQty(_playerId, Substrate));
        Assert.Single(_store.ListDropLog(_playerKey));
    }

    [Fact]
    public void A_credit_that_refuses_rolls_the_drop_log_row_back_with_it()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            Persist(_playerKey, Manifest("lc-4", MaterialGrant("material.not-a-real-id", 3))));

        Assert.Contains("Unknown material id", ex.Message);
        Assert.Equal(0, _store.GetMaterialQty(_playerId, "material.not-a-real-id"));
        Assert.Empty(_store.ListDropLog(_playerKey));
    }

    [Fact]
    public void A_non_numeric_player_id_with_a_credit_refuses_the_persist_by_name()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            Persist("player-not-numeric", Manifest("lc-5", MaterialGrant(Substrate, 3))));

        Assert.Contains("loot.credit-player-id-not-numeric", ex.Message);
        Assert.Empty(_store.ListDropLog("player-not-numeric"));
        Assert.Equal(0, _store.GetMaterialQty(_playerId, Substrate));
    }

    /// <summary>The parse is paid only when something is credited — a manifest with nothing to credit
    /// keeps the pre-T29 behaviour, numeric player id or not.</summary>
    [Fact]
    public void Loot_with_no_creditable_grant_does_not_need_a_numeric_player_id()
    {
        Persist("player-not-numeric", Manifest("lc-6"));

        Assert.Single(_store.ListDropLog("player-not-numeric"));
    }
}
