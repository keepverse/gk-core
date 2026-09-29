using FusionRpg.Contracts;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Contracts;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// G5: contracts guard every path that FIELDS a creature — and nothing else. Each refusal names its
/// condition (`unbound` and `insubordinate` are different problems with different fixes).
/// </summary>
public class ContractGateTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    // Midday *today*, not a hard-coded date. `Mint` stamps contract state from the real clock, so a
    // fixed anchor only matches on the day it was written: the day after, every "N days elapsed"
    // assertion is off by one, and the day after that by two. Relative keeps the intent — "day zero
    // is the day this state was created" — permanently true.
    static readonly DateTimeOffset Day0 =
        new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero).AddHours(12);

    public ContractGateTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _store.AwardSouls(1, 50_000, "seed", "gate-bank");
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    // DeployMode != HypnoAlly: this file's subject is contract binding/loyalty gating, unrelated to
    // creature-lawn-deploy T1.4's DeployMode — excluding HypnoAlly keeps the deploy-accepting tests here
    // from incidentally tripping T1.4's own deploy.hypno-ally-not-implemented refusal.
    static readonly CreatureSpeciesDef Species = CreatureSpeciesCatalog.All
        .First(s => s.Acquisition != CreatureAcquisition.CaptureOnly && s.TraitPool.Count > 0
            && s.DeployMode != CreatureDeployMode.HypnoAlly);

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

    string MintUnbound()
    {
        for (var i = _store.CountBoundContracts(1); i < ContractPolicy.BaseSlots; i++) Mint();
        var id = Mint();
        Assert.Null(_store.GetContract(id));
        return id;
    }

    string MintInsubordinate()
    {
        var id = Mint();
        _store.SetLoyaltyForTest(id, ContractPolicy.DeployFloor - 1);
        return id;
    }

    [Fact]
    public void PvZ_deploy_refuses_an_unbound_creature()
    {
        var id = MintUnbound();
        var (ok, reason, _, _) = _store.TryBeginUniqueDeploy(id, "deploy-1");
        Assert.False(ok);
        Assert.Equal("contract.unbound", reason);
        Assert.Equal(UniqueActorPhases.Roster, _store.GetUniqueActor(id)!.Phase);   // nothing written
    }

    [Fact]
    public void PvZ_deploy_refuses_an_insubordinate_creature()
    {
        var id = MintInsubordinate();
        var (ok, reason, _, _) = _store.TryBeginUniqueDeploy(id, "deploy-2");
        Assert.False(ok);
        Assert.Equal("contract.insubordinate", reason);
    }

    [Fact]
    public void PvZ_deploy_of_a_plain_unique_actor_is_untouched()
    {
        // This path predates creatures entirely: an actor with no creature profile has no contract to check.
        var actor = _store.CreateUniqueActor(1, "plant", 1);
        var (ok, reason, _, _) = _store.TryBeginUniqueDeploy(actor.InstanceId, "deploy-3");
        Assert.True(ok, reason);
    }

    [Fact]
    public void PvZ_deploy_accepts_a_bound_creature()
    {
        var id = Mint();
        var (ok, reason, _, _) = _store.TryBeginUniqueDeploy(id, "deploy-4");
        Assert.True(ok, reason);
    }

    [Fact]
    public void Expedition_dispatch_refuses_unbound_and_insubordinate_squad_members()
    {
        // Insubordinate first: MintUnbound fills capacity, after which nothing else can be bound.
        var sulking = MintInsubordinate();
        var unbound = MintUnbound();

        var refusal = _store.DispatchExpedition(1, "exp-corr-1", "scout-30m", new[] { unbound }, 7UL, Day0);
        Assert.False(refusal.Ok);
        Assert.Equal("specimen.unbound", refusal.Reason);

        var second = _store.DispatchExpedition(1, "exp-corr-2", "scout-30m", new[] { sulking }, 7UL, Day0);
        Assert.False(second.Ok);
        Assert.Equal("specimen.insubordinate", second.Reason);
    }

    // ---- G6: loyalty movement from results ----

    [Fact]
    public void A_win_raises_loyalty_by_the_personality_scaled_amount()
    {
        var id = Mint();
        var row = _store.GetContract(id)!;
        var expected = ContractPolicy.ApplyGain(
            row.Loyalty, 0, ContractPolicy.WinGain, row.Personality).Loyalty;

        Assert.Equal(1, _store.ApplyContractResults(1, new[] { id }, won: true, Day0));
        Assert.Equal(expected, _store.GetContract(id)!.Loyalty);
    }

    [Fact]
    public void Wins_stop_at_the_daily_cap_and_the_window_reopens_tomorrow()
    {
        var id = Mint();
        var personality = _store.GetContract(id)!.Personality;
        var start = _store.GetContract(id)!.Loyalty;

        for (var i = 0; i < 20; i++) _store.ApplyContractResults(1, new[] { id }, won: true, Day0);
        Assert.Equal(start + ContractPolicy.DailyGainCap, _store.GetContract(id)!.Loyalty);

        // A new UTC day reopens the window; the first win of the day lands in full.
        var afterCap = _store.GetContract(id)!.Loyalty;
        _store.ApplyContractResults(1, new[] { id }, won: true, Day0.AddDays(1));
        Assert.Equal(afterCap + ContractPolicy.WinGain * ContractPolicy.Rates(personality).GainPct / 100,
            _store.GetContract(id)!.Loyalty);
    }

    [Fact]
    public void A_defeat_streak_ends_in_insubordination_and_the_gate_closes()
    {
        var id = Mint();
        // Losses are uncapped: 10 defeats take a fresh contract from 300 to under the 200 floor.
        for (var i = 0; i < 11; i++) _store.ApplyContractResults(1, new[] { id }, won: false, Day0);

        var row = _store.GetContract(id)!;
        Assert.True(row.Loyalty < ContractPolicy.DeployFloor);
        Assert.False(row.Deployable);
        var (ok, reason, _, _) = _store.TryBeginUniqueDeploy(id, "deploy-streak");
        Assert.False(ok);
        Assert.Equal("contract.insubordinate", reason);
    }

    [Fact]
    public void Unbound_creatures_neither_earn_nor_suffer()
    {
        var id = MintUnbound();
        Assert.Equal(0, _store.ApplyContractResults(1, new[] { id }, won: true, Day0));
        Assert.Equal(0, _store.ApplyContractResults(1, new[] { id }, won: false, Day0));
        Assert.Null(_store.GetContract(id));
    }

    [Fact]
    public void Patron_designation_refuses_a_creature_that_cannot_serve()
    {
        var sulking = MintInsubordinate();
        var unbound = MintUnbound();

        var refusal = _store.SetPatron(1, unbound, "patron-corr-1");
        Assert.False(refusal.Ok);
        Assert.Equal("patron.unbound", refusal.Reason);
        Assert.Null(_store.GetPatron(1));

        var second = _store.SetPatron(1, sulking, "patron-corr-2");
        Assert.False(second.Ok);
        Assert.Equal("patron.insubordinate", second.Reason);
    }
}
