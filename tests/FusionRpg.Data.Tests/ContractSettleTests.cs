using FusionRpg.Contracts;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Contracts;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// G3: lazy day-quantised settlement. No background sweep, no offline simulation — the store is
/// handed a clock and charges (or decays) one UTC day at a time, idempotently.
/// </summary>
public class ContractSettleTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    // Midday *today*, not a hard-coded date. `Mint` stamps contract state from the real clock, so a
    // fixed anchor only matches on the day it was written: the day after, every "N days elapsed"
    // assertion is off by one, and the day after that by two. Relative keeps the intent — "day zero
    // is the day this state was created" — permanently true.
    static readonly DateTimeOffset Day0 =
        new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero).AddHours(12);

    public ContractSettleTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _store.AwardSouls(1, 10_000, "seed", "settle-bank");
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

    /// <summary>What one day of this roster should cost, computed from the stored personalities.</summary>
    long DailyTribute(IEnumerable<string> ids) => ids
        .Select(id => _store.GetContract(id))
        .Where(c => c is { Bound: true })
        .Sum(c => (long)ContractPolicy.UpkeepPerDay(Species.BaseRarity, c!.Personality));

    [Fact]
    public void Settling_the_same_day_twice_charges_once()
    {
        var ids = new[] { Mint(), Mint(), Mint() };
        _store.EnsureContractsMigrated(1, Day0);
        var due = DailyTribute(ids);
        var before = _store.GetSoulBalance(1).Balance;

        var first = _store.SettleContracts(1, Day0.AddDays(1));
        Assert.Equal(1, first.DaysSettled);
        Assert.Equal(due, first.SoulsPaid);
        Assert.Equal(before - due, _store.GetSoulBalance(1).Balance);

        // Same UTC day again, from a later hour: the dedupe key already holds that day.
        var second = _store.SettleContracts(1, Day0.AddDays(1).AddHours(6));
        Assert.Equal(0, second.DaysSettled);
        Assert.Equal(0, second.SoulsPaid);
        Assert.Equal(before - due, _store.GetSoulBalance(1).Balance);
    }

    [Fact]
    public void Three_elapsed_days_charge_three_days()
    {
        var ids = new[] { Mint(), Mint() };
        _store.EnsureContractsMigrated(1, Day0);
        var due = DailyTribute(ids);
        var before = _store.GetSoulBalance(1).Balance;

        var result = _store.SettleContracts(1, Day0.AddDays(3));
        Assert.Equal(3, result.DaysSettled);
        Assert.Equal(due * 3, result.SoulsPaid);
        Assert.Equal(before - due * 3, _store.GetSoulBalance(1).Balance);
    }

    [Fact]
    public void An_unaffordable_day_decays_instead_of_charging_and_writes_no_ledger_row()
    {
        var ids = new[] { Mint(), Mint() };
        _store.EnsureContractsMigrated(1, Day0);
        _store.TrySpendSouls(1, _store.GetSoulBalance(1).Balance, "seed", "drain"); // broke on purpose
        Assert.Equal(0, _store.GetSoulBalance(1).Balance);

        var result = _store.SettleContracts(1, Day0.AddDays(1));
        Assert.Equal(1, result.DaysSettled);
        Assert.Equal(0, result.SoulsPaid);
        Assert.Equal(2, result.CreaturesDecayed);
        Assert.Equal(0, _store.GetSoulBalance(1).Balance);   // nothing spent, nothing owed forward

        foreach (var id in ids)
        {
            var row = _store.GetContract(id)!;
            var expected = ContractPolicy.BindLoyalty - ContractPolicy.DecayPerDayFor(row.Personality);
            Assert.Equal(Math.Max(ContractPolicy.DeployFloor, expected), row.Loyalty);
        }
    }

    [Fact]
    public void Decay_stops_at_the_deploy_floor_however_long_the_absence()
    {
        var id = Mint();
        _store.EnsureContractsMigrated(1, Day0);
        _store.TrySpendSouls(1, _store.GetSoulBalance(1).Balance, "seed", "drain2");

        _store.SettleContracts(1, Day0.AddDays(30));
        var row = _store.GetContract(id)!;
        Assert.Equal(ContractPolicy.DeployFloor, row.Loyalty);
        // Still deployable: neglect costs a creature everything it earned, never the right to be fielded.
        Assert.True(row.Deployable);
    }

    [Fact]
    public void A_long_absence_bills_at_most_thirty_days_and_forgives_the_rest()
    {
        var ids = new[] { Mint() };
        _store.EnsureContractsMigrated(1, Day0);
        var due = DailyTribute(ids);
        var before = _store.GetSoulBalance(1).Balance;

        var result = _store.SettleContracts(1, Day0.AddDays(200));
        Assert.Equal(ContractPolicy.MaxSettleDays, result.DaysSettled);
        Assert.Equal(due * ContractPolicy.MaxSettleDays, result.SoulsPaid);
        Assert.Equal(before - due * ContractPolicy.MaxSettleDays, _store.GetSoulBalance(1).Balance);

        // The stamp jumped to the day we settled on — the skipped days are gone, not queued.
        var again = _store.SettleContracts(1, Day0.AddDays(200).AddHours(3));
        Assert.Equal(0, again.DaysSettled);
    }

    [Fact]
    public void A_player_with_no_bound_creatures_owes_nothing()
    {
        _store.EnsureContractsMigrated(1, Day0);
        var before = _store.GetSoulBalance(1).Balance;
        var result = _store.SettleContracts(1, Day0.AddDays(5));
        Assert.Equal(0, result.SoulsPaid);
        Assert.Equal(before, _store.GetSoulBalance(1).Balance);
    }

    [Fact]
    public void Settlement_is_the_migration_entry_point_for_an_untouched_player()
    {
        var id = Mint();
        _store.ClearContractsForTest(1);

        _store.SettleContracts(1, Day0);
        Assert.True(_store.GetContract(id)!.Bound);
        // Migration stamps the settle clock at that moment — no retroactive bill for the past.
        Assert.Equal(0, _store.SettleContracts(1, Day0).DaysSettled);
    }
}
