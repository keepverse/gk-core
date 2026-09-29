using FusionRpg.Contracts;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Contracts;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// build-preset BP2.2 (spec-piece-appliers.md) — <see cref="RpgStore.QuoteBind"/> and
/// <see cref="RpgStore.QuoteRelease"/> share <see cref="RpgStore.BindPreconditionsUnlocked"/> /
/// <see cref="RpgStore.ReleasePreconditionsUnlocked"/> with <see cref="RpgStore.BindContract"/> and
/// <see cref="RpgStore.ReleaseContract"/>, so a preview can never disagree with what the write then
/// refuses or charges.
/// </summary>
public class ContractQuoteTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public ContractQuoteTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _store.AwardSouls(1, 10_000, "seed", "contract-quote-bank");
    }

    public void Dispose() => _testStore.Dispose();

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

    // ---- QuoteBind ------------------------------------------------------------------------------

    [Fact]
    public void QuoteBind_on_an_already_bound_specimen_is_a_free_replay()
    {
        var id = Mint(); // mint auto-binds

        var q = _store.QuoteBind(1, id);

        Assert.True(q.Ok, q.Reason);
        Assert.True(q.Replay);
        Assert.Equal(0, q.Price);
    }

    [Fact]
    public void QuoteBind_prices_the_upkeep_fee_exactly_like_BindContract_charges()
    {
        var id = Mint();
        Assert.True(_store.ReleaseContract(1, id).Ok);

        var quote = _store.QuoteBind(1, id);
        var before = _store.GetSoulBalance(1).Balance;
        var bind = _store.BindContract(1, id);
        var after = _store.GetSoulBalance(1).Balance;

        Assert.True(quote.Ok, quote.Reason);
        Assert.False(quote.Replay);
        Assert.True(bind.Ok, bind.Reason);
        Assert.Equal(quote.Price, before - after);
    }

    [Fact]
    public void QuoteBind_refuses_capacity_full_exactly_like_BindContract_does()
    {
        for (var i = 0; i < ContractPolicy.Capacity(0); i++) Mint();
        var overflow = Mint(); // capacity already full -- mint leaves this one unbound

        var quote = _store.QuoteBind(1, overflow);
        var bind = _store.BindContract(1, overflow);

        Assert.False(quote.Ok);
        Assert.Equal("capacity.full", quote.Reason);
        Assert.False(bind.Ok);
        Assert.Equal(quote.Reason, bind.Reason);
    }

    [Fact]
    public void QuoteBind_never_writes()
    {
        var id = Mint();
        Assert.True(_store.ReleaseContract(1, id).Ok);
        var balanceBefore = _store.GetSoulBalance(1).Balance;
        var boundBefore = _store.GetPatron(1); // unrelated table, cheap "nothing else moved" signal

        _store.QuoteBind(1, id);

        Assert.Equal(balanceBefore, _store.GetSoulBalance(1).Balance);
        Assert.False(_store.ListContracts(1).Single(c => c.InstanceId == id).Bound);
        Assert.Equal(boundBefore, _store.GetPatron(1));
    }

    // ---- QuoteRelease -----------------------------------------------------------------------------

    [Fact]
    public void QuoteRelease_on_a_bound_specimen_is_free_and_matches_what_ReleaseContract_then_does()
    {
        var id = Mint();

        var quote = _store.QuoteRelease(1, id);
        var before = _store.GetSoulBalance(1).Balance;
        var release = _store.ReleaseContract(1, id);

        Assert.True(quote.Ok);
        Assert.False(quote.Replay);
        Assert.Equal(0, quote.Price);
        Assert.True(release.Ok, release.Reason);
        Assert.Equal(before, _store.GetSoulBalance(1).Balance); // release never charges
    }

    [Fact]
    public void QuoteRelease_on_an_already_unbound_specimen_is_a_free_replay()
    {
        var id = Mint();
        Assert.True(_store.ReleaseContract(1, id).Ok);

        var q = _store.QuoteRelease(1, id);

        Assert.True(q.Ok);
        Assert.True(q.Replay);
        Assert.Equal(0, q.Price);
    }

    [Fact]
    public void QuoteRelease_refuses_a_warden_exactly_like_ReleaseContract_does()
    {
        var id = Mint();
        Assert.True(_store.ReleaseContract(1, id).Ok);
        Assert.True(_store.BindAsWarden(1, id).Ok);

        var quote = _store.QuoteRelease(1, id);
        var release = _store.ReleaseContract(1, id);

        Assert.False(quote.Ok);
        Assert.Equal("contract.warden-permanent", quote.Reason);
        Assert.False(release.Ok);
        Assert.Equal(quote.Reason, release.Reason);
    }

    [Fact]
    public void QuoteRelease_never_writes()
    {
        var id = Mint();

        _store.QuoteRelease(1, id);

        Assert.True(_store.ListContracts(1).Single(c => c.InstanceId == id).Bound);
    }
}
