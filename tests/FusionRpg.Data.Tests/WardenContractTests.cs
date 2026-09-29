using FusionRpg.Contracts;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Contracts;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// L42 (spec-loam-texture.md): `BindAsWarden` extends `creature-contracts`' shipped binding machinery
/// with one new rule — the resulting bind is permanent. Same capacity check, same upkeep fee as an
/// ordinary `BindContract`; `ReleaseContract` refuses it unconditionally, for the life of the world.
/// </summary>
public class WardenContractTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    static readonly DateTimeOffset Day0 =
        new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero).AddHours(12);

    public WardenContractTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _store.AwardSouls(1, 50_000, "seed", "ops-bank");
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    static readonly CreatureSpeciesDef Species = CreatureSpeciesCatalog.All
        .First(s => s.Acquisition != CreatureAcquisition.CaptureOnly && s.TraitPool.Count > 0);

    string Mint()
    {
        var (specimen, _) = _store.MintCreature(1, new CreatureMintSpec
        {
            SpeciesId = Species.SpeciesId,
            Side = Species.Side,
            GameTypeId = Species.GameTypeId,
            Rarity = Species.BaseRarity.ToId(),
            Variant = "normal",
            ElementPrimary = Species.ElementPrimary.ToElementId(),
            ElementSecondary = Species.ElementSecondary?.ToElementId(),
            TraitIds = new List<string> { Species.TraitPool[0] },
            Origin = "summon"
        });
        return specimen.Actor.InstanceId;
    }

    string MintOverflowCreature()
    {
        for (var i = _store.CountBoundContracts(1); i < ContractPolicy.BaseSlots; i++) Mint();
        var id = Mint();
        Assert.Null(_store.GetContract(id));
        return id;
    }

    /// <summary>An unbound creature with a free capacity slot waiting — <see cref="Mint"/> auto-binds
    /// up to base capacity, so an ordinary bindable creature needs a slot freed first, the same setup
    /// `ContractOpsTests`'s own binding test uses.</summary>
    string MintUnboundWithFreeSlot()
    {
        var bound = new List<string>();
        for (var i = 0; i < ContractPolicy.BaseSlots; i++) bound.Add(Mint());
        var id = Mint();
        Assert.Null(_store.GetContract(id));
        Assert.True(_store.ReleaseContract(1, bound[0]).Ok);
        return id;
    }

    [Fact]
    public void Binding_a_warden_costs_the_same_capacity_slot_and_fee_as_an_ordinary_bind()
    {
        var id = MintUnboundWithFreeSlot();
        var fee = ContractPolicy.UpkeepPerDay(Species.BaseRarity, ContractPolicy.PersonalityFor(id));
        var before = _store.GetSoulBalance(1).Balance;

        var bind = _store.BindAsWarden(1, id, Day0);

        Assert.True(bind.Ok, bind.Reason);
        Assert.True(bind.Contract!.Warden);
        Assert.True(bind.Contract.Bound);
        Assert.Equal(before - fee, _store.GetSoulBalance(1).Balance);
    }

    [Fact]
    public void Binding_a_warden_refuses_when_capacity_is_full_and_writes_nothing()
    {
        var id = MintOverflowCreature();
        var before = _store.GetSoulBalance(1).Balance;

        var bind = _store.BindAsWarden(1, id, Day0);

        Assert.False(bind.Ok);
        Assert.Equal("capacity.full", bind.Reason);
        Assert.Null(_store.GetContract(id));
        Assert.Equal(before, _store.GetSoulBalance(1).Balance);
    }

    [Fact]
    public void Release_refuses_a_warden_bind_unconditionally_even_though_ordinary_release_would_succeed()
    {
        var id = MintUnboundWithFreeSlot();
        var warden = _store.BindAsWarden(1, id, Day0);
        Assert.True(warden.Ok, warden.Reason);

        var release = _store.ReleaseContract(1, id);

        Assert.False(release.Ok);
        Assert.Equal("contract.warden-permanent", release.Reason);
        Assert.True(_store.GetContract(id)!.Bound);
        Assert.True(_store.GetContract(id)!.Warden);
    }

    [Fact]
    public void Binding_a_warden_refuses_an_instance_already_bound_ordinarily()
    {
        var id = Mint(); // auto-bound ordinary, within base capacity

        var warden = _store.BindAsWarden(1, id, Day0);

        Assert.False(warden.Ok);
        Assert.Equal("contract.already-bound", warden.Reason);
        Assert.False(_store.GetContract(id)!.Warden);
    }

    [Fact]
    public void An_ordinary_bind_is_not_flagged_as_a_warden()
    {
        var id = Mint();
        Assert.False(_store.GetContract(id)!.Warden);
        Assert.True(_store.ReleaseContract(1, id).Ok); // an ordinary bind releases exactly as before
    }

    [Fact]
    public void Re_signing_a_warden_the_same_day_replays_without_a_second_charge()
    {
        var id = MintUnboundWithFreeSlot();
        var first = _store.BindAsWarden(1, id, Day0);
        Assert.True(first.Ok, first.Reason);
        var afterFirst = _store.GetSoulBalance(1).Balance;

        var replay = _store.BindAsWarden(1, id, Day0.AddHours(2));

        Assert.True(replay.Ok);
        Assert.Equal("replay", replay.Reason);
        Assert.Equal(afterFirst, _store.GetSoulBalance(1).Balance);
        Assert.True(replay.Contract!.Warden);
    }
}
