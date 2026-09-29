using FusionRpg.Contracts;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>PT2: patron designation (first free, soul-priced switch), the fusion guard, and the
/// patron kill-earn hook inside the fact transaction.</summary>
public class PatronStoreTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public PatronStoreTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

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
        return specimen.Actor.InstanceId;
    }

    [Fact]
    public void First_set_is_free_and_switching_costs_souls()
    {
        _store.AwardSouls(1, 500, "seed", "patron-bank");
        var first = Mint();
        var second = Mint();

        var set = _store.SetPatron(1, first, "pt-set-1");
        Assert.True(set.Ok, set.Reason);
        Assert.Equal(500, _store.GetSoulBalance(1).Balance); // free

        var sw = _store.SetPatron(1, second, "pt-set-2");
        Assert.True(sw.Ok, sw.Reason);
        Assert.Equal(400, _store.GetSoulBalance(1).Balance); // -100
        Assert.Equal(second, _store.GetPatron(1)!.InstanceId);
    }

    [Fact]
    public void Same_target_is_a_free_replay_and_reused_correlations_mismatch()
    {
        _store.AwardSouls(1, 500, "seed", "patron-bank2");
        var a = Mint();
        var b = Mint();
        Assert.True(_store.SetPatron(1, a, "pt-a").Ok);
        Assert.True(_store.SetPatron(1, b, "pt-b").Ok); // -100

        // Same target again: natural idempotency, no charge, any correlation.
        var again = _store.SetPatron(1, b, "pt-b-again");
        Assert.True(again.Ok);
        Assert.Equal("replay", again.Reason);
        Assert.Equal(400, _store.GetSoulBalance(1).Balance);

        // Correlation reused for a DIFFERENT target: refuse, nothing written.
        var mismatch = _store.SetPatron(1, a, "pt-b");
        Assert.False(mismatch.Ok);
        Assert.Equal("correlation.mismatch", mismatch.Reason);
        Assert.Equal(b, _store.GetPatron(1)!.InstanceId);
        Assert.Equal(400, _store.GetSoulBalance(1).Balance);
    }

    [Fact]
    public void Switch_refuses_without_souls_and_bad_specimens_refuse()
    {
        var a = Mint();
        var b = Mint();
        Assert.True(_store.SetPatron(1, a, "pt-poor-1").Ok); // free
        var broke = _store.SetPatron(1, b, "pt-poor-2");
        Assert.False(broke.Ok);
        Assert.Equal("souls.insufficient", broke.Reason);
        Assert.Equal(a, _store.GetPatron(1)!.InstanceId);

        Assert.Equal("specimen.missing", _store.SetPatron(1, "ghost", "pt-ghost").Reason);
        var eaten = Mint();
        Assert.True(_store.TryRetireUniqueActor(eaten).Ok);
        Assert.Equal("specimen.missing", _store.SetPatron(1, eaten, "pt-dead").Reason);
    }

    [Fact]
    public void Fusion_cannot_eat_the_patron_but_the_patron_may_lead()
    {
        _store.AwardSouls(1, 5000, "seed", "patron-fus-bank");
        _store.AddCreatureMaterials(1, new[]
        {
            ("shard." + Species.BaseRarity.ToId(), 10L),
            ("essence." + Species.ElementPrimary.ToElementId(), 10L)
        });
        var patron = Mint();
        Assert.True(_store.SetPatron(1, patron, "pt-fus").Ok);
        var baseId = Mint();
        var free = Mint();

        // Patron as sacrifice: refused.
        var eat = _store.ExecuteFusion(1, "pt-eat", new FusionRequest(
            FusionModes.StarMerge, baseId, new[] { patron, free }, null), 1);
        Assert.False(eat.Ok);
        Assert.Equal("sacrifice.is-patron", eat.Reason);

        // Patron as BASE: allowed — designation protects consumption, not evolution.
        var evolve = _store.ExecuteFusion(1, "pt-evolve", new FusionRequest(
            FusionModes.StarMerge, patron, new[] { baseId, free }, null), 1);
        Assert.True(evolve.Ok, evolve.Reason);
        Assert.Equal(1, evolve.Outcome!.Base!.Profile.Star);
    }

    [Fact]
    public void Patron_kill_earns_include_the_tenth_kill_bonus()
    {
        var patron = Mint();
        Assert.True(_store.SetPatron(1, patron, "pt-earn").Ok);
        PlayMatch(10);
        // 10 kills: 9×1 + 1×2 (the 10th) = 11, plus first-victory 100.
        Assert.Equal(111 + BankSeed, _store.GetSoulBalance(1).Balance);
    }

    [Fact]
    public void Without_a_patron_the_audited_earn_shape_is_untouched()
    {
        PlayMatch(10);
        Assert.Equal(110 + BankSeed, _store.GetSoulBalance(1).Balance); // 10 + victory 100
    }

    const long BankSeed = 0;

    void PlayMatch(int kills)
    {
        var matchKey = Guid.NewGuid().ToString("N");
        var t = DateTime.UtcNow.ToString("o");
        var events = new List<EventEnvelope>
        {
            Ev("board.start", matchKey, t, new { levelName = "patron" })
        };
        for (var i = 0; i < kills; i++)
        {
            events.Add(Ev("zombie.spawn", matchKey, t, new { ptr = $"PZ{i}", type = 0, typeName = "T", stats = new { hp = 10 } }));
            events.Add(Ev("zombie.die", matchKey, t, new { ptr = $"PZ{i}", type = 0 }));
        }

        events.Add(Ev("match.result", matchKey, t, new { result = "victory" }));
        events.Add(Ev("board.end", matchKey, t, new { levelName = "patron" }));
        _store.InsertEvents(events);
    }

    static EventEnvelope Ev(string kind, string matchKey, string t, object payload) => new()
    {
        T = t,
        Game = RpgConstants.GameId,
        Kind = kind,
        MatchKey = matchKey,
        Payload = payload
    };
}
