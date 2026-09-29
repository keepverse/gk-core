using FusionRpg.Contracts;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Contracts;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// Post-Wave-G locks: behaviours that currently hold only by construction. Each of these is
/// something a plausible refactor could silently break — mixed solvency across days, the churn
/// guard's day boundary, cross-player isolation, and (T3.6, 2026-08-24) the slot ladder's price,
/// now that the old hard ceiling behind it is gone.
/// </summary>
public class ContractRegressionTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    // Midday *today*, not a hard-coded date. `Mint` stamps contract state from the real clock, so a
    // fixed anchor only matches on the day it was written: the day after, every "N days elapsed"
    // assertion is off by one, and the day after that by two. Relative keeps the intent — "day zero
    // is the day this state was created" — permanently true.
    static readonly DateTimeOffset Day0 =
        new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero).AddHours(12);

    public ContractRegressionTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _store.AwardSouls(1, 300_000, "seed", "reg-bank");
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    static readonly CreatureSpeciesDef Species = CreatureSpeciesCatalog.All
        .First(s => s.Acquisition != CreatureAcquisition.CaptureOnly && s.TraitPool.Count > 0);

    string Mint(long playerId = 1)
    {
        var (specimen, _) = _store.MintCreature(playerId, new CreatureMintSpec
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

    long DailyTribute(long playerId = 1) => _store.ListContracts(playerId)
        .Where(c => c.Bound)
        .Sum(c => (long)ContractPolicy.UpkeepPerDay(Species.BaseRarity, c.Personality));

    void DrainTo(long target)
    {
        var balance = _store.GetSoulBalance(1).Balance;
        if (balance > target) _store.TrySpendSouls(1, balance - target, "seed", "drain-" + Guid.NewGuid());
    }

    [Fact]
    public void Solvency_is_decided_per_day_not_once_for_the_whole_span()
    {
        var ids = new[] { Mint(), Mint() };
        _store.EnsureContractsMigrated(1, Day0);
        var due = DailyTribute();
        DrainTo(due);                      // exactly one day of tribute in the bank

        var result = _store.SettleContracts(1, Day0.AddDays(3));
        Assert.Equal(3, result.DaysSettled);
        Assert.Equal(due, result.SoulsPaid);          // day 1 paid...
        Assert.Equal(2, result.CreaturesDecayed);        // ...days 2 and 3 could not, so faith eroded
        Assert.Equal(0, _store.GetSoulBalance(1).Balance);

        foreach (var id in ids)
        {
            var row = _store.GetContract(id)!;
            var expected = Math.Max(ContractPolicy.DeployFloor,
                ContractPolicy.BindLoyalty - 2 * ContractPolicy.DecayPerDayFor(row.Personality));
            Assert.Equal(expected, row.Loyalty);
        }
    }

    [Fact]
    public void A_day_one_soul_short_is_not_partially_paid()
    {
        Mint();
        _store.EnsureContractsMigrated(1, Day0);
        var due = DailyTribute();
        DrainTo(due - 1);

        var result = _store.SettleContracts(1, Day0.AddDays(1));
        Assert.Equal(0, result.SoulsPaid);                          // all-or-nothing
        Assert.Equal(due - 1, _store.GetSoulBalance(1).Balance);    // the remainder is untouched
        Assert.Equal(1, result.CreaturesDecayed);
    }

    /// <summary>
    /// Review fix: an already-paid day must not be treated as an unpaid one. The ledger's dedupe key
    /// is the record of payment — if the charge is refused because that day is already on the books,
    /// the creatures have been paid for and must not decay.
    /// </summary>
    [Fact]
    public void A_day_already_on_the_ledger_is_paid_not_unpaid()
    {
        var id = Mint();
        _store.EnsureContractsMigrated(1, Day0);
        var due = DailyTribute();

        // Pre-write the exact ledger row settlement would write for day 1, then settle that day.
        var day = Day0.AddDays(1).UtcDateTime.ToString("yyyy-MM-dd");
        Assert.True(_store.AppendSoulLedgerForTest(1, -due, SoulEarnPolicy.Reasons.Upkeep,
            $"upkeep:1:{day}", Day0.AddDays(1)));
        var afterPrepay = _store.GetSoulBalance(1).Balance;

        var result = _store.SettleContracts(1, Day0.AddDays(1));

        Assert.Equal(0, result.CreaturesDecayed);                       // paid, so no erosion
        Assert.Equal(ContractPolicy.BindLoyalty, _store.GetContract(id)!.Loyalty);
        Assert.Equal(afterPrepay, _store.GetSoulBalance(1).Balance); // and charged exactly once
    }

    [Fact]
    public void Consecutive_settles_never_bill_a_day_twice()
    {
        Mint();
        _store.EnsureContractsMigrated(1, Day0);
        var due = DailyTribute();
        var before = _store.GetSoulBalance(1).Balance;

        _store.SettleContracts(1, Day0.AddDays(1));
        var second = _store.SettleContracts(1, Day0.AddDays(3));

        Assert.Equal(2, second.DaysSettled);                        // days 2 and 3 only
        Assert.Equal(before - due * 3, _store.GetSoulBalance(1).Balance);
    }

    [Fact]
    public void The_pact_fee_is_forgiven_only_within_the_same_utc_day()
    {
        var bound = new List<string>();
        for (var i = 0; i < ContractPolicy.BaseSlots; i++) bound.Add(Mint());
        var id = Mint();                                            // overflow: unbound
        Assert.True(_store.ReleaseContract(1, bound[0]).Ok);

        var fee = ContractPolicy.UpkeepPerDay(Species.BaseRarity, ContractPolicy.PersonalityFor(id));
        var before = _store.GetSoulBalance(1).Balance;

        Assert.True(_store.BindContract(1, id, Day0).Ok);
        Assert.Equal(before - fee, _store.GetSoulBalance(1).Balance);

        // Release and re-sign the NEXT day: that day has not been paid for, so the fee lands again.
        Assert.True(_store.ReleaseContract(1, id, Day0.AddDays(1)).Ok);
        Assert.True(_store.BindContract(1, id, Day0.AddDays(1)).Ok);
        Assert.True(_store.GetSoulBalance(1).Balance < before - fee,
            "a new day is a new pact fee — otherwise the churn guard expires after one day");
    }

    [Fact]
    public void Contracts_never_reach_across_players()
    {
        var mine = Mint();
        var other = _store.CreatePlayer("second-summoner");
        _store.AwardSouls(other.Id, 5_000, "seed", "reg-bank-2");
        var theirs = Mint(other.Id);

        // Results credited under the wrong player id move nothing.
        Assert.Equal(0, _store.ApplyContractResults(1, new[] { theirs }, won: true, Day0));
        Assert.Equal(ContractPolicy.BindLoyalty, _store.GetContract(theirs)!.Loyalty);

        // And neither binding nor releasing crosses the line.
        Assert.False(_store.ReleaseContract(1, theirs).Ok);
        Assert.True(_store.GetContract(theirs)!.Bound);
        Assert.Equal("specimen.missing", _store.BindContract(1, theirs, Day0).Reason);
        Assert.Equal(1, _store.CountBoundContracts(1));
        Assert.Equal(1, _store.CountBoundContracts(other.Id));
        Assert.Equal(1, _store.ApplyContractResults(1, new[] { mine }, won: true, Day0));
    }

    [Fact]
    public void A_consumed_creature_cannot_be_re_contracted()
    {
        var id = Mint();
        Assert.True(_store.TryRetireUniqueActor(id).Ok);
        Assert.True(_store.ReleaseContract(1, id).Ok);   // retiring leaves the slot reclaimable

        var rebind = _store.BindContract(1, id, Day0);
        Assert.False(rebind.Ok);
        Assert.Equal("specimen.missing", rebind.Reason);
        Assert.Equal(0, _store.CountBoundContracts(1));
    }

    [Fact]
    public void The_slot_ladder_climbs_past_the_old_ceiling_and_never_refuses_on_capacity()
    {
        // T3.6 (spec-caps-reconcile.md §2.3, SSOT §11.1/§11.1a, 2026-08-24): MaxSlots is deleted.
        // Buy 40 -- past the OLD 48-total-slot (36-purchased) ceiling -- through the REAL live-DB
        // purchase path, not just the pure ContractPolicy math (already covered by
        // ContractPolicyTests.Slot_ladder_rises_forever_no_ceiling). Every purchase must succeed; none
        // may refuse with "capacity.max", because that reason no longer exists.
        const int purchases = 40; // 12 + 40 = 52, past the old 48 ceiling
        long spent = 0;
        for (var k = 0; k < purchases; k++)
        {
            var buy = _store.BuyContractSlot(1, $"ladder-{k}", Day0);
            Assert.True(buy.Ok, $"slot {k} refused: {buy.Reason}");
            spent += ContractPolicy.SlotPriceStep * (k + 1);
        }

        Assert.Equal(52, _store.GetContractState(1)!.Capacity);
        Assert.Equal(300_000 - spent, _store.GetSoulBalance(1).Balance);

        // The warden property (SSOT §11.1a): the Nth slot costs strictly more than the (N-1)th,
        // arbitrarily far past the old ceiling -- because of the price, never because of a cap.
        Assert.True(ContractPolicy.NextSlotPrice(purchases, FusionRpg.Core.Creatures.SoulSinkPolicy.VanillaPvzTheta, FusionRpg.Core.Power.PowerTuningHub.Tuning) > ContractPolicy.NextSlotPrice(purchases - 1, FusionRpg.Core.Creatures.SoulSinkPolicy.VanillaPvzTheta, FusionRpg.Core.Power.PowerTuningHub.Tuning));
    }

    [Fact]
    public void Migration_prefers_the_stronger_creature_when_rarity_ties()
    {
        // Fill every slot with older creatures first, so age alone would keep the newcomers out.
        for (var i = 0; i < ContractPolicy.BaseSlots; i++) Mint();
        var weak = Mint();
        var strong = Mint();
        _store.AwardUniqueActorXp(strong, 5_000, "test");
        Assert.True(_store.GetUniqueActor(strong)!.Level > _store.GetUniqueActor(weak)!.Level,
            "the premise of this test is that XP moved the level");

        _store.ClearContractsForTest(1);
        _store.EnsureContractsMigrated(1, Day0);

        Assert.Equal(ContractPolicy.BaseSlots, _store.CountBoundContracts(1));
        // Same rarity, same stars: level breaks the tie, and it outranks seniority.
        Assert.True(_store.GetContract(strong)!.Bound, "the levelled creature must win a slot");
        Assert.Null(_store.GetContract(weak));
    }
}
