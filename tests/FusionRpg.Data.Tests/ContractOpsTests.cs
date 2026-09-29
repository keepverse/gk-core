using FusionRpg.Contracts;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Contracts;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>G4: bind / release / ritual / buy-slot — one transaction each, refusals write nothing.</summary>
public class ContractOpsTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    // Midday *today*, not a hard-coded date. `Mint` stamps contract state from the real clock, so a
    // fixed anchor only matches on the day it was written: the day after, every "N days elapsed"
    // assertion is off by one, and the day after that by two. Relative keeps the intent — "day zero
    // is the day this state was created" — permanently true.
    static readonly DateTimeOffset Day0 =
        new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero).AddHours(12);

    public ContractOpsTests()
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

    /// <summary>Fills every base slot, then mints one more — the returned creature is unbound.</summary>
    string MintOverflowCreature()
    {
        for (var i = _store.CountBoundContracts(1); i < ContractPolicy.BaseSlots; i++) Mint();
        var id = Mint();
        Assert.Null(_store.GetContract(id));
        return id;
    }

    [Fact]
    public void Binding_charges_one_day_of_upkeep_and_re_binding_the_same_day_is_free()
    {
        var bound = new List<string>();
        for (var i = 0; i < ContractPolicy.BaseSlots; i++) bound.Add(Mint());
        var id = Mint();                                       // capacity full: arrives unbound
        Assert.Null(_store.GetContract(id));
        Assert.True(_store.ReleaseContract(1, bound[0]).Ok);   // free a slot to bind into
        var fee = ContractPolicy.UpkeepPerDay(Species.BaseRarity, ContractPolicy.PersonalityFor(id));
        var before = _store.GetSoulBalance(1).Balance;

        var bind = _store.BindContract(1, id, Day0);
        Assert.True(bind.Ok, bind.Reason);
        Assert.Equal(before - fee, _store.GetSoulBalance(1).Balance);
        Assert.True(_store.GetContract(id)!.Bound);

        // Churn guard: release and re-sign on the same UTC day costs nothing extra — the day is paid.
        Assert.True(_store.ReleaseContract(1, id).Ok);
        var again = _store.BindContract(1, id, Day0.AddHours(3));
        Assert.True(again.Ok, again.Reason);
        Assert.Equal(before - fee, _store.GetSoulBalance(1).Balance);
    }

    [Fact]
    public void Binding_refuses_when_capacity_is_full_and_writes_nothing()
    {
        var id = MintOverflowCreature();
        var before = _store.GetSoulBalance(1).Balance;

        var bind = _store.BindContract(1, id, Day0);
        Assert.False(bind.Ok);
        Assert.Equal("capacity.full", bind.Reason);
        Assert.Null(_store.GetContract(id));
        Assert.Equal(before, _store.GetSoulBalance(1).Balance);
    }

    [Fact]
    public void Release_keeps_the_loyalty_the_creature_earned()
    {
        var id = Mint();
        _store.SetLoyaltyForTest(id, 750);

        Assert.True(_store.ReleaseContract(1, id).Ok);
        var row = _store.GetContract(id)!;
        Assert.False(row.Bound);
        Assert.Equal(750, row.Loyalty);       // frozen, not reset
        Assert.NotNull(row.ReleasedUtc);
        Assert.Equal(0, _store.CountBoundContracts(1));

        // And it is not decaying while benched: a long absence leaves it exactly where it was.
        _store.SettleContracts(1, Day0.AddDays(20));
        Assert.Equal(750, _store.GetContract(id)!.Loyalty);
    }

    [Fact]
    public void Release_refuses_the_patron_and_an_expedition_member()
    {
        var patron = Mint();
        _store.SetPatron(1, patron, "corr-patron");
        var refused = _store.ReleaseContract(1, patron);
        Assert.False(refused.Ok);
        Assert.Equal("contract.is-patron", refused.Reason);
        Assert.True(_store.GetContract(patron)!.Bound);
    }

    [Fact]
    public void Ritual_raises_loyalty_by_the_personality_scaled_amount_and_charges_by_rarity()
    {
        var id = Mint();
        _store.SetLoyaltyForTest(id, 150);                     // insubordinate: defeats put it here
        var personality = _store.GetContract(id)!.Personality;
        var price = ContractPolicy.RitualPrice(Species.BaseRarity, FusionRpg.Core.Creatures.SoulSinkPolicy.VanillaPvzTheta, FusionRpg.Core.Power.PowerTuningHub.Tuning);
        var before = _store.GetSoulBalance(1).Balance;

        var ritual = _store.PerformRitual(1, id, "corr-ritual-1", Day0);
        Assert.True(ritual.Ok, ritual.Reason);
        Assert.Equal(150 + ContractPolicy.RitualGainFor(personality), _store.GetContract(id)!.Loyalty);
        Assert.Equal(before - price, _store.GetSoulBalance(1).Balance);
        Assert.True(_store.GetContract(id)!.Deployable);        // back over the floor

        // Replay: same correlation, charged once, loyalty unchanged.
        var replay = _store.PerformRitual(1, id, "corr-ritual-1", Day0);
        Assert.True(replay.Ok);
        Assert.Equal("replay", replay.Reason);
        Assert.Equal(before - price, _store.GetSoulBalance(1).Balance);
    }

    [Fact]
    public void Ritual_refuses_an_unbound_creature()
    {
        var unbound = MintOverflowCreature();
        var refusal = _store.PerformRitual(1, unbound, "corr-r2", Day0);
        Assert.False(refusal.Ok);
        Assert.Equal("contract.unbound", refusal.Reason);
    }

    [Fact]
    public void Ritual_refuses_a_broke_player_and_writes_nothing()
    {
        var id = Mint();
        _store.TrySpendSouls(1, _store.GetSoulBalance(1).Balance, "seed", "drain");
        var broke = _store.PerformRitual(1, id, "corr-r3", Day0);
        Assert.False(broke.Ok);
        Assert.Equal("souls.insufficient", broke.Reason);
        Assert.Equal(ContractPolicy.BindLoyalty, _store.GetContract(id)!.Loyalty);
    }

    [Fact]
    public void Slots_climb_a_rising_price_ladder_and_stop_at_the_ceiling()
    {
        var before = _store.GetSoulBalance(1).Balance;

        var first = _store.BuyContractSlot(1, "slot-1", Day0);
        Assert.True(first.Ok, first.Reason);
        Assert.Equal(ContractPolicy.BaseSlots + 1, first.State!.Capacity);
        Assert.Equal(before - 300, _store.GetSoulBalance(1).Balance);

        var second = _store.BuyContractSlot(1, "slot-2", Day0);
        Assert.True(second.Ok);
        Assert.Equal(before - 300 - 600, _store.GetSoulBalance(1).Balance);

        // Replay is free and returns the same state.
        var replay = _store.BuyContractSlot(1, "slot-2", Day0);
        Assert.True(replay.Ok);
        Assert.Equal("replay", replay.Reason);
        Assert.Equal(ContractPolicy.BaseSlots + 2, replay.State!.Capacity);
        Assert.Equal(before - 900, _store.GetSoulBalance(1).Balance);
    }

    [Fact]
    public void A_bought_slot_widens_capacity_for_the_next_mint()
    {
        var overflow = MintOverflowCreature();
        _store.BuyContractSlot(1, "slot-widen", Day0);
        var bind = _store.BindContract(1, overflow, Day0);
        Assert.True(bind.Ok, bind.Reason);
        Assert.Equal(ContractPolicy.BaseSlots + 1, _store.CountBoundContracts(1));
    }
}
