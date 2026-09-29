using FusionRpg.Contracts;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Contracts;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// G2: contract schema, the one-shot migration auto-bind, and mint-time binding into a free slot.
/// </summary>
public class ContractStoreTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public ContractStoreTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _store.AwardSouls(1, 10_000, "seed", "contract-bank");
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    static CreatureSpeciesDef SpeciesOf(CreatureRarity rarity) => CreatureSpeciesCatalog.All
        .First(s => s.BaseRarity == rarity && s.Acquisition != CreatureAcquisition.CaptureOnly
                    && s.TraitPool.Count > 0);

    string Mint(CreatureRarity rarity = CreatureRarity.Chaff)
    {
        var species = SpeciesOf(rarity);
        var (specimen, _) = _store.MintCreature(1, new CreatureMintSpec
        {
            SpeciesId = species.SpeciesId,
            Side = species.Side,
            GameTypeId = species.GameTypeId,
            Rarity = species.BaseRarity.ToId(),
            Variant = "normal",
            ElementPrimary = species.ElementPrimary.ToElementId(),
            ElementSecondary = species.ElementSecondary?.ToElementId(),
            TraitIds = new List<string> { species.TraitPool[0] },
            Origin = "summon"
        });
        return specimen.Actor.InstanceId;
    }

    [Fact]
    public void Mint_binds_into_a_free_slot_and_stops_at_capacity()
    {
        var ids = new List<string>();
        for (var i = 0; i < ContractPolicy.BaseSlots; i++) ids.Add(Mint());

        Assert.All(ids, id => Assert.True(_store.GetContract(id)!.Bound));
        Assert.Equal(ContractPolicy.BaseSlots, _store.CountBoundContracts(1));

        // Capacity is full: the next creature is owned but unbound — no row at all is the unbound state.
        var overflow = Mint();
        Assert.Null(_store.GetContract(overflow));
        Assert.Equal(ContractPolicy.BaseSlots, _store.CountBoundContracts(1));
    }

    [Fact]
    public void A_fresh_contract_sits_in_the_zero_bonus_band()
    {
        var id = Mint();
        var row = _store.GetContract(id)!;
        Assert.Equal(ContractPolicy.BindLoyalty, row.Loyalty);
        Assert.Equal(LoyaltyRank.Bound, ContractPolicy.RankFor(row.Loyalty));
        Assert.Equal(0, ContractPolicy.RankBonusMilli(ContractPolicy.RankFor(row.Loyalty)));
    }

    [Fact]
    public void Personality_is_stored_and_matches_the_derivation()
    {
        var id = Mint();
        var row = _store.GetContract(id)!;
        Assert.Equal(ContractPolicy.PersonalityFor(id), row.Personality);
    }

    [Fact]
    public void Migration_binds_best_first_up_to_capacity_and_runs_once()
    {
        var commons = new List<string>();
        for (var i = 0; i < 14; i++) commons.Add(Mint(CreatureRarity.Chaff));
        var epic = Mint(CreatureRarity.Heirloom);
        var legendary = Mint(CreatureRarity.Sunwoven);

        // Rewind to what a pre-contracts database looks like: specimens, no contracts.
        _store.ClearContractsForTest(1);
        Assert.Null(_store.GetContract(epic));

        Assert.True(_store.EnsureContractsMigrated(1));

        Assert.Equal(ContractPolicy.BaseSlots, _store.CountBoundContracts(1));
        // Best-first by rarity: the two valuable specimens take slots ahead of any common.
        Assert.True(_store.GetContract(legendary)!.Bound);
        Assert.True(_store.GetContract(epic)!.Bound);
        Assert.Equal(4, commons.Count(id => _store.GetContract(id) is null));

        // One-shot: a second call changes nothing and reports that it did nothing.
        Assert.False(_store.EnsureContractsMigrated(1));
        Assert.Equal(ContractPolicy.BaseSlots, _store.CountBoundContracts(1));
    }

    [Fact]
    public void Migration_is_free_and_leaves_the_balance_untouched()
    {
        for (var i = 0; i < 3; i++) Mint();
        _store.ClearContractsForTest(1);
        var before = _store.GetSoulBalance(1).Balance;
        _store.EnsureContractsMigrated(1);
        Assert.Equal(before, _store.GetSoulBalance(1).Balance);
    }

    [Fact]
    public void Reset_clears_contract_tables()
    {
        Mint();
        Assert.Equal(1, _store.CountBoundContracts(1));
        _store.Reset();
        Assert.Equal(0, _store.CountBoundContracts(1));
    }
}
