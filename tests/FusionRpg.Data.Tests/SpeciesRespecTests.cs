using FusionRpg.Core.Creatures.Generation;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>species-build-todo.md T4.2 — <see cref="RpgStore.TryRespecSpecies"/> (spec-species-respec.md,
/// read in full this session). Covers the store's own slice of the spec's testing strategy: free
/// cases, escalation, decay, the atomicity a refusal must preserve, idempotence, insufficient balance,
/// never-a-cap, and the shipped ledger path.</summary>
[Trait("VerificationId", "data.species-respec")]
public class SpeciesRespecTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    const string SpeciesId = "peashooter";
    const long PlayerId = 1;

    public SpeciesRespecTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;

        SpeciesBuildTuningHub.Configure(new SpeciesBuildTuning(
            SchemaVersion: 1, Version: 1,
            ParityFloorPermille: 50, ParityCeilingPermille: 200,
            LeanMinPermille: 350, LeanMaxPermille: 600,
            CrowdingFactor: 633, SecondarySharePermille: 300,
            MaxAptitudesPerSpecies: 5, MinAptitudesPerSpecies: 2,
            RespecBasePrice: 50, RespecEscalationPermille: 500, RespecDecayDays: 3,
            UniqueRespecBasePrice: 50, UniqueRespecEscalationPermille: 500, UniqueRespecDecayDays: 3,
            LeanSignalWeights: LeanSignalWeights.Zero));
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    static AptitudeAllocation Build(long points) =>
        AptitudeAllocation.Single(AllocationScope.CreatureType, "Might", points);

    [Fact]
    public void First_override_is_free_and_does_not_touch_the_respec_counter()
    {
        _store.AwardSouls(PlayerId, 1000, "seed", "bank-1");
        var result = _store.TryRespecSpecies(PlayerId, SpeciesId, Build(3), "r-1");

        Assert.True(result.Ok, result.Reason);
        Assert.False(result.Priced);
        Assert.Equal(0, result.PriceAmount);
        Assert.Equal(1000, _store.GetSoulBalance(PlayerId).Balance);
        Assert.Equal(0, _store.GetSpeciesRespecCount(PlayerId, SpeciesId));
        Assert.Equal(3, _store.LoadAllocation(AllocationScope.CreatureType,
            Core.Stats.Aptitudes.SpeciesAllocation.ScopeKey(PlayerId, FusionRpg.Core.Commanders.EmpireId.Dave, SpeciesId)).TotalForScope(AllocationScope.CreatureType));
    }

    [Fact]
    public void Reverting_to_baseline_is_free()
    {
        _store.AwardSouls(PlayerId, 1000, "seed", "bank-2");
        _store.TryRespecSpecies(PlayerId, SpeciesId, Build(3), "r-1"); // first override, free
        _store.TryRespecSpecies(PlayerId, SpeciesId, Build(5), "r-2"); // priced change, count -> 1

        var balanceBeforeRevert = _store.GetSoulBalance(PlayerId).Balance;
        var revert = _store.TryRespecSpecies(PlayerId, SpeciesId, AptitudeAllocation.Empty, "r-3");

        Assert.True(revert.Ok, revert.Reason);
        Assert.False(revert.Priced);
        Assert.Equal(balanceBeforeRevert, _store.GetSoulBalance(PlayerId).Balance); // no charge
        // Reverting does not move the churn clock -- the count from the prior priced change persists.
        Assert.Equal(1, _store.GetSpeciesRespecCount(PlayerId, SpeciesId));
    }

    [Fact]
    public void Escalation_matches_the_linear_formula_and_strictly_increases()
    {
        _store.AwardSouls(PlayerId, 10_000, "seed", "bank-3");
        _store.TryRespecSpecies(PlayerId, SpeciesId, Build(1), "free-first"); // free, count stays 0

        var balance0 = _store.GetSoulBalance(PlayerId).Balance;
        var second = _store.TryRespecSpecies(PlayerId, SpeciesId, Build(2), "chg-1"); // count 0 -> price 50
        Assert.Equal(50, second.PriceAmount);
        Assert.Equal(balance0 - 50, _store.GetSoulBalance(PlayerId).Balance);

        var third = _store.TryRespecSpecies(PlayerId, SpeciesId, Build(3), "chg-2"); // count 1 -> price 75
        Assert.Equal(75, third.PriceAmount);

        var fourth = _store.TryRespecSpecies(PlayerId, SpeciesId, Build(4), "chg-3"); // count 2 -> price 100
        Assert.Equal(100, fourth.PriceAmount);

        Assert.True(third.PriceAmount > second.PriceAmount);
        Assert.True(fourth.PriceAmount > third.PriceAmount);
        Assert.Equal(3, _store.GetSpeciesRespecCount(PlayerId, SpeciesId));
    }

    [Fact]
    public void Decay_lowers_the_count_and_the_next_price_after_enough_elapsed_time()
    {
        var day0 = DateTimeOffset.UtcNow.Date;
        _store.AwardSouls(PlayerId, 10_000, "seed", "bank-4");
        _store.TryRespecSpecies(PlayerId, SpeciesId, Build(1), "free-first", day0);
        _store.TryRespecSpecies(PlayerId, SpeciesId, Build(2), "chg-1", day0); // count -> 1

        // RespecDecayDays is 3 -- one day later, nothing has decayed yet.
        Assert.Equal(1, _store.GetSpeciesRespecCount(PlayerId, SpeciesId, day0.AddDays(1)));
        // Three whole days later, the counter drops by exactly one tick.
        Assert.Equal(0, _store.GetSpeciesRespecCount(PlayerId, SpeciesId, day0.AddDays(3)));

        // A respec priced far enough in the future reads the decayed (lower) count -- base price again.
        var later = _store.TryRespecSpecies(PlayerId, SpeciesId, Build(3), "chg-2", day0.AddDays(3));
        Assert.Equal(50, later.PriceAmount);
    }

    [Fact]
    public void A_refusal_for_insufficient_balance_leaves_the_balance_counter_and_override_untouched()
    {
        _store.TryRespecSpecies(PlayerId, SpeciesId, Build(1), "free-first"); // free, no souls needed
        var scopeKey = Core.Stats.Aptitudes.SpeciesAllocation.ScopeKey(PlayerId, FusionRpg.Core.Commanders.EmpireId.Dave, SpeciesId);
        var before = _store.LoadAllocation(AllocationScope.CreatureType, scopeKey).PointsAt(AllocationScope.CreatureType, "Might");

        // Balance is 0 -- the very next change is priced (50) and must be refused outright.
        var refused = _store.TryRespecSpecies(PlayerId, SpeciesId, Build(2), "poor-1");

        Assert.False(refused.Ok);
        Assert.Equal("souls.insufficient", refused.Reason);
        Assert.Equal(0, _store.GetSoulBalance(PlayerId).Balance);
        Assert.Equal(0, _store.GetSpeciesRespecCount(PlayerId, SpeciesId)); // never a cap, and never incremented on refusal
        Assert.Equal(before, _store.LoadAllocation(AllocationScope.CreatureType, scopeKey).PointsAt(AllocationScope.CreatureType, "Might")); // override unchanged
    }

    [Fact]
    public void A_replayed_correlation_returns_the_original_result_without_spending_again()
    {
        _store.AwardSouls(PlayerId, 1000, "seed", "bank-5");
        _store.TryRespecSpecies(PlayerId, SpeciesId, Build(1), "free-first");
        var first = _store.TryRespecSpecies(PlayerId, SpeciesId, Build(2), "chg-1");
        Assert.True(first.Ok);
        var balanceAfterFirst = _store.GetSoulBalance(PlayerId).Balance;
        var countAfterFirst = _store.GetSpeciesRespecCount(PlayerId, SpeciesId);

        var replay = _store.TryRespecSpecies(PlayerId, SpeciesId, Build(2), "chg-1");
        Assert.True(replay.Ok);
        Assert.Equal("replay", replay.Reason);
        Assert.Equal(balanceAfterFirst, _store.GetSoulBalance(PlayerId).Balance); // not charged twice
        Assert.Equal(countAfterFirst, _store.GetSpeciesRespecCount(PlayerId, SpeciesId)); // not incremented twice
    }

    [Fact]
    public void An_arbitrarily_high_respec_count_is_never_refused_for_being_a_respec()
    {
        _store.AwardSouls(PlayerId, 1_000_000, "seed", "bank-6");
        _store.TryRespecSpecies(PlayerId, SpeciesId, Build(1), "free-first");

        for (var i = 0; i < 25; i++)
        {
            var result = _store.TryRespecSpecies(PlayerId, SpeciesId, Build(i + 2), $"grind-{i}");
            Assert.True(result.Ok, $"attempt {i}: {result.Reason}");
        }

        Assert.Equal(25, _store.GetSpeciesRespecCount(PlayerId, SpeciesId));
    }

    [Fact]
    public void The_spend_appears_in_the_shipped_soul_ledger_with_its_own_reason()
    {
        _store.AwardSouls(PlayerId, 1000, "seed", "bank-7");
        _store.TryRespecSpecies(PlayerId, SpeciesId, Build(1), "free-first");
        _store.TryRespecSpecies(PlayerId, SpeciesId, Build(2), "chg-1");

        var ledger = _store.ListSoulLedger(PlayerId);
        Assert.Contains(ledger.Items, e => e.Reason == "respec" && e.Delta == -50);
    }
}
