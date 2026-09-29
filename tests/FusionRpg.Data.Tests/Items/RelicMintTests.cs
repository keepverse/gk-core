using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items.Drops;
using FusionRpg.Core.Power;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests.Items;

/// <summary>
/// Empire-development Task 1.3a (spec-relic-item-kind.md §Design 3, §Testing strategy) — the
/// MintRelic host: a relic mints to a durable <c>rpg_item</c> row with a real <c>instance_id</c>,
/// and <c>item_generation</c> gains zero rows from that mint, because a relic has no base type,
/// role or frame to stamp.
/// </summary>
public class RelicMintTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public RelicMintTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose() => _testStore.Dispose();

    static readonly PowerTuning MintTuning = PowerTuning.Build(
        1, 1, 80_000, 0, 20, 680, 1000, 25000, 250, 1000, 5000, 5000, 25000);

    // A relic anchor with ZERO atoms — spec §Design 1 keeps fixedAtoms optional because many
    // relics exist purely as Wonder-build cost tokens. This is the strongest shape to mint: if the
    // host required even one atom, this fixture would refuse.
    void SeedRelicContainer(string containerId)
    {
        var result = _store.UpsertContainer(new ContainerRow
        {
            ContainerId = containerId, Kind = ContainerKind.Relic,
        });
        Assert.True(result.IsOk, result.ToString());
    }

    static LootGrant RelicGrant(string refId, int index = 0) => new(
        index, DropEntryKind.Relic, refId, 1, AffixChannels.Drop,
        BaseTypeId: null, Frame: null, Role: null, RarityId: null,
        ItemLevel: 20, RollSeed: 1234UL + (ulong)index);

    [Fact]
    public void mint_relic_writes_rpg_item_and_never_item_generation()
    {
        const string refId = "relic.test-cost-token";
        SeedRelicContainer(refId);

        var result = _store.MintRelic(RelicGrant(refId), "player-1", thetaContent: 20, MintTuning);

        Assert.True(result.Rejection.IsOk, result.Rejection.ToString());
        Assert.False(string.IsNullOrWhiteSpace(result.InstanceId));

        // The durable ownership row exists...
        var item = _store.GetItem(result.InstanceId!);
        Assert.NotNull(item);
        Assert.Equal("player-1", item.PlayerId);
        Assert.Equal("drop", item.OriginKind);
        Assert.Equal(refId, item.OriginRef);
        Assert.Equal("owned", item.Disposition);

        // ...backed by a real effect_instance carrying the relic container...
        var instance = _store.GetInstance(result.InstanceId!);
        Assert.NotNull(instance);
        Assert.Equal(refId, instance.ContainerId);
        Assert.Empty(instance.Atoms);

        // ...and item_generation gained nothing from this mint: no stamp for this instance, and
        // the inflow measurement over the whole player reads zero.
        Assert.Null(_store.GetItemGeneration(result.InstanceId!));
        Assert.Equal(0, _store.CountEquipmentMinted("player-1", "2000-01-01T00:00:00Z"));
    }

    [Fact]
    public void mint_relic_produces_no_base_type_role_or_frame()
    {
        // The persisted rpg_item row itself carries none of the three — not merely that
        // item_generation gains no row, but that there is nowhere on the rpg_item row to put them.
        const string refId = "relic.test-no-provenance";
        SeedRelicContainer(refId);

        var result = _store.MintRelic(RelicGrant(refId), "player-1", thetaContent: 20, MintTuning);
        Assert.True(result.Rejection.IsOk, result.Rejection.ToString());

        var item = _store.GetItem(result.InstanceId!);
        Assert.NotNull(item);
        var propertyNames = typeof(RpgItemRow).GetProperties().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("BaseTypeId", propertyNames);
        Assert.DoesNotContain("base_type_id", propertyNames);
        Assert.DoesNotContain("Role", propertyNames);
        Assert.DoesNotContain("Frame", propertyNames);

        // And the relic container underneath never authored any of them either.
        var container = _store.GetContainer(refId);
        Assert.NotNull(container);
        Assert.Equal(ContainerKind.Relic, container.Kind);
        Assert.Null(container.Frame);
        Assert.Null(container.BaseTypeId);
        Assert.Null(container.Rarity);
    }

    [Fact]
    public void mint_relic_refuses_an_unknown_container_by_name()
    {
        var result = _store.MintRelic(RelicGrant("relic.does-not-exist"), "player-1", thetaContent: 20, MintTuning);

        Assert.False(result.Rejection.IsOk);
        Assert.Contains("drop.unknown-relic", result.Rejection.Detail, StringComparison.Ordinal);
        Assert.Contains("relic.does-not-exist", result.Rejection.Detail, StringComparison.Ordinal);
        Assert.Null(result.InstanceId);
        Assert.Empty(_store.ListItemsByPlayer("player-1"));
    }

    [Fact]
    public void mint_relic_refuses_a_non_relic_container_by_name()
    {
        var containerResult = _store.UpsertContainer(new ContainerRow
        {
            ContainerId = "item.not-a-relic", Kind = ContainerKind.Item,
        });
        Assert.True(containerResult.IsOk, containerResult.ToString());

        var result = _store.MintRelic(RelicGrant("item.not-a-relic"), "player-1", thetaContent: 20, MintTuning);

        Assert.False(result.Rejection.IsOk);
        Assert.Contains("drop.unknown-relic", result.Rejection.Detail, StringComparison.Ordinal);
        Assert.Null(result.InstanceId);
        Assert.Empty(_store.ListItemsByPlayer("player-1"));
    }

    [Fact]
    public void mint_relic_refuses_a_non_relic_grant()
    {
        var grant = new LootGrant(0, DropEntryKind.Unique, "item.some-unique", 1, AffixChannels.Drop);
        var result = _store.MintRelic(grant, "player-1", thetaContent: 20, MintTuning);

        Assert.False(result.Rejection.IsOk);
        Assert.Contains("drop.mint-kind-unsupported", result.Rejection.Detail, StringComparison.Ordinal);
        Assert.Null(result.InstanceId);
    }
}
