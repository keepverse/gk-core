using FusionRpg.Contracts;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Patron;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// build-preset BP2.1 (spec-piece-appliers.md) — <see cref="RpgStore.QuotePatron"/> shares
/// <see cref="RpgStore.PatronPreconditionsUnlocked"/> with <see cref="RpgStore.SetPatron"/>, so a
/// preview can never disagree with what the write then refuses or charges.
/// </summary>
public class PatronQuoteTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public PatronQuoteTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose() => _testStore.Dispose();

    static readonly CreatureSpeciesDef Species = CreatureSpeciesCatalog.All
        .First(s => s.Side == "zombie" && s.Acquisition != CreatureAcquisition.CaptureOnly);

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
        return specimen.Actor.InstanceId; // mint auto-binds
    }

    [Fact]
    public void A_fresh_designation_quotes_free_and_not_a_replay()
    {
        var id = Mint();

        var q = _store.QuotePatron(1, id, assumeBound: false);

        Assert.True(q.Ok, q.Reason);
        Assert.Equal(0, q.Price);
        Assert.False(q.Replay);
    }

    [Fact]
    public void Quoting_the_current_patron_is_a_free_replay_exactly_like_SetPatron()
    {
        var id = Mint();
        Assert.True(_store.SetPatron(1, id, "pt-quote-1").Ok);

        var q = _store.QuotePatron(1, id, assumeBound: false);

        Assert.True(q.Ok);
        Assert.True(q.Replay);
        Assert.Equal(0, q.Price);
    }

    [Fact]
    public void Quoting_a_switch_prices_SwitchCostSouls_exactly_like_SetPatron_charges()
    {
        var first = Mint();
        var second = Mint();
        Assert.True(_store.SetPatron(1, first, "pt-quote-2").Ok);

        var q = _store.QuotePatron(1, second, assumeBound: false);

        Assert.True(q.Ok);
        Assert.False(q.Replay);
        Assert.Equal(PatronPolicy.SwitchCostSouls, q.Price);
    }

    [Fact]
    public void Quote_refuses_exactly_the_same_reason_SetPatron_refuses_for_an_unbound_creature()
    {
        var id = Mint();
        Assert.True(_store.ReleaseContract(1, id).Ok);

        var quoteRefusal = _store.QuotePatron(1, id, assumeBound: false);
        var setRefusal = _store.SetPatron(1, id, "pt-quote-3");

        Assert.False(quoteRefusal.Ok);
        Assert.Equal("patron.unbound", quoteRefusal.Reason);
        Assert.False(setRefusal.Ok);
        Assert.Equal(quoteRefusal.Reason, setRefusal.Reason);
    }

    /// <summary>The build-preset field piece may bind AND designate the same creature as patron in
    /// one apply — at preview time it is not bound yet, but <c>PlanState.WillBeBound</c> says it
    /// will be, so the bound check alone is skipped.</summary>
    [Fact]
    public void AssumeBound_admits_a_not_yet_bound_creature_that_SetPatron_would_otherwise_refuse()
    {
        var id = Mint();
        Assert.True(_store.ReleaseContract(1, id).Ok);

        var withoutAssume = _store.QuotePatron(1, id, assumeBound: false);
        var withAssume = _store.QuotePatron(1, id, assumeBound: true);

        Assert.False(withoutAssume.Ok);
        Assert.Equal("patron.unbound", withoutAssume.Reason);
        Assert.True(withAssume.Ok, withAssume.Reason);
    }

    [Fact]
    public void Quote_never_writes()
    {
        var first = Mint();
        var second = Mint();
        Assert.True(_store.SetPatron(1, first, "pt-quote-4").Ok);
        var balanceBefore = _store.GetSoulBalance(1).Balance;
        var patronBefore = _store.GetPatron(1);

        _store.QuotePatron(1, second, assumeBound: false);

        Assert.Equal(balanceBefore, _store.GetSoulBalance(1).Balance);
        Assert.Equal(patronBefore, _store.GetPatron(1));
    }
}
