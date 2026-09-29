using FusionRpg.Core.PassiveTree.GateCounters;
using FusionRpg.Core.PassiveTree.State;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>Task G5 — D43 / spec-gate-counters.md §16 OQ1 / passive-tree-todo.md G5:
/// <c>RpgStore.SeedExistingSaveGateCounters</c> (<c>RpgStore.GateCounterSeed.cs</c>). The proxy math
/// itself is covered by <c>tests/FusionRpg.Core.Tests/PassiveTree/GateCounters/ExistingSaveSeedTests.cs</c>;
/// this suite is the fixture-save behavioural proof todo.md G5's own verification line names: "a
/// fixture save before/after; running twice changes nothing." Same fixture convention as
/// <c>GateCounterStoreTests.cs</c> (a real temp-directory SQLite store, not a mock).</summary>
[Trait("VerificationId", "data.gate-counter")]
public class GateCounterSeedTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    static readonly GateCountersTuning Tuning = new(
        MasteryCurveFirstCount: 23, MasteryCurveStepCount: 23,
        ElementMasteryRatePoints: 4, StatusMasteryRatePoints: 4,
        FlushIntervalMs: 5000, RateDivergenceWhy: null);

    static readonly string[] StatusIds = { "wither", "poison" };
    static readonly string[] ElementIds = { "fire", "ice" };

    public GateCounterSeedTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    /// <summary>Gives "player:1" a deep primary tree -- 275 Commander-scope points, spec §3.3's own
    /// worked "focused build reaches tier 10" figure -- so seeded counters have something real to be
    /// proportionate TO.</summary>
    void GiveDeepPrimaryTree(string ownerKey)
    {
        var allocation = AptitudeAllocation.Single(AllocationScope.Commander, "Might", 175)
                        + AptitudeAllocation.Single(AllocationScope.Commander, "Vigor", 100);
        _store.SaveAllocation(AllocationScope.Commander, ownerKey, allocation);
    }

    [Fact]
    public void A_new_player_with_zero_primary_tree_investment_seeds_nothing()
    {
        // No SaveAllocation call at all -- exactly a fresh account (§4.1's sparsity: no row means 0).
        var outcome = _store.SeedExistingSaveGateCounters("player", "player:1", StatusIds, ElementIds, Tuning);

        Assert.False(outcome.AlreadySeeded);
        Assert.Equal(0, outcome.SeededSubjectCount);
        Assert.Equal(0L, outcome.CommanderPoints);

        foreach (var statusId in StatusIds)
            Assert.Equal(0L, _store.LoadGateCounter("player", "player:1", "status_applied", statusId));
        foreach (var elementId in ElementIds)
            Assert.Equal(0L, _store.LoadGateCounter("player", "player:1", "element_mastery", elementId));

        // Sparsity holds even after seeding a fresh account -- no row was manufactured for it.
        Assert.Empty(_store.LoadGateCountersForOwner("player", "player:1", "status_applied"));
        Assert.Empty(_store.LoadGateCountersForOwner("player", "player:1", "element_mastery"));
    }

    [Fact]
    public void An_existing_save_with_primary_tree_depth_gets_non_zero_proportionate_counters()
    {
        GiveDeepPrimaryTree("player:1");

        var outcome = _store.SeedExistingSaveGateCounters("player", "player:1", StatusIds, ElementIds, Tuning);

        Assert.False(outcome.AlreadySeeded);
        Assert.Equal(275L, outcome.CommanderPoints);
        Assert.Equal(StatusIds.Length + ElementIds.Length, outcome.SeededSubjectCount);

        foreach (var statusId in StatusIds)
        {
            var seeded = _store.LoadGateCounter("player", "player:1", "status_applied", statusId);
            Assert.True(seeded > 0, $"expected a non-zero seed for status '{statusId}', got {seeded}");
        }
        foreach (var elementId in ElementIds)
        {
            var seeded = _store.LoadGateCounter("player", "player:1", "element_mastery", elementId);
            Assert.True(seeded > 0, $"expected a non-zero seed for element '{elementId}', got {seeded}");
        }
    }

    [Fact]
    public void A_deeper_primary_tree_seeds_a_larger_count_than_a_shallower_one()
    {
        GiveDeepPrimaryTree("player:1"); // 275 points

        var shallow = AptitudeAllocation.Single(AllocationScope.Commander, "Might", 20);
        _store.SaveAllocation(AllocationScope.Commander, "player:2", shallow); // 20 points

        _store.SeedExistingSaveGateCounters("player", "player:1", StatusIds, ElementIds, Tuning);
        _store.SeedExistingSaveGateCounters("player", "player:2", StatusIds, ElementIds, Tuning);

        var deepSeed = _store.LoadGateCounter("player", "player:1", "status_applied", "wither");
        var shallowSeed = _store.LoadGateCounter("player", "player:2", "status_applied", "wither");

        Assert.True(deepSeed > shallowSeed,
            $"a deeper primary tree (275 pts -> {deepSeed}) must seed more than a shallower one (20 pts -> {shallowSeed})");
    }

    [Fact]
    public void Running_the_seed_twice_changes_nothing()
    {
        GiveDeepPrimaryTree("player:1");

        var first = _store.SeedExistingSaveGateCounters("player", "player:1", StatusIds, ElementIds, Tuning);
        var before = SnapshotAll("player:1");

        var second = _store.SeedExistingSaveGateCounters("player", "player:1", StatusIds, ElementIds, Tuning);
        var after = SnapshotAll("player:1");

        Assert.False(first.AlreadySeeded);
        Assert.True(second.AlreadySeeded);
        Assert.Equal(0, second.SeededSubjectCount);
        Assert.Equal(first.CommanderPoints, second.CommanderPoints);
        Assert.Equal(before, after);

        // A third call, and a fourth, for good measure -- "stamped" means it never runs again, not
        // "usually" or "twice at most".
        _store.SeedExistingSaveGateCounters("player", "player:1", StatusIds, ElementIds, Tuning);
        _store.SeedExistingSaveGateCounters("player", "player:1", StatusIds, ElementIds, Tuning);
        Assert.Equal(before, SnapshotAll("player:1"));
    }

    [Fact]
    public void Running_the_seed_twice_for_a_new_player_also_changes_nothing()
    {
        var first = _store.SeedExistingSaveGateCounters("player", "player:1", StatusIds, ElementIds, Tuning);
        var second = _store.SeedExistingSaveGateCounters("player", "player:1", StatusIds, ElementIds, Tuning);

        Assert.False(first.AlreadySeeded);
        Assert.True(second.AlreadySeeded);
        Assert.Empty(_store.LoadGateCountersForOwner("player", "player:1", "status_applied"));
        Assert.Empty(_store.LoadGateCountersForOwner("player", "player:1", "element_mastery"));
    }

    [Fact]
    public void Seeding_never_overwrites_a_real_organic_credit()
    {
        GiveDeepPrimaryTree("player:1");

        // Real play already credited "wither" a small amount, BEFORE the seed pass ever ran.
        _store.FlushGateCounters(new Dictionary<GateCounterKey, long>
        {
            [new GateCounterKey("player", "player:1", "status_applied", "wither")] = 5,
        });

        _store.SeedExistingSaveGateCounters("player", "player:1", StatusIds, ElementIds, Tuning);

        // "wither" is untouched -- the seed never clobbers real progress, even though the proxy
        // formula would have produced a different (and here, larger) starting value.
        Assert.Equal(5L, _store.LoadGateCounter("player", "player:1", "status_applied", "wither"));

        // "poison" had no prior credit, so it DID get seeded.
        var poisonSeed = _store.LoadGateCounter("player", "player:1", "status_applied", "poison");
        Assert.True(poisonSeed > 0, $"expected 'poison' (no prior credit) to be seeded, got {poisonSeed}");
    }

    [Fact]
    public void The_stamp_is_auditable_and_HasSeededGateCounters_reports_it()
    {
        GiveDeepPrimaryTree("player:1");

        Assert.False(_store.HasSeededGateCounters("player", "player:1"));

        var outcome = _store.SeedExistingSaveGateCounters("player", "player:1", StatusIds, ElementIds, Tuning);

        Assert.True(_store.HasSeededGateCounters("player", "player:1"));
        Assert.Equal(275L, outcome.CommanderPoints);
    }

    [Fact]
    public void Different_owners_seed_independently()
    {
        GiveDeepPrimaryTree("player:1");
        // player:2 stays a fresh account.

        _store.SeedExistingSaveGateCounters("player", "player:1", StatusIds, ElementIds, Tuning);
        _store.SeedExistingSaveGateCounters("player", "player:2", StatusIds, ElementIds, Tuning);

        Assert.True(_store.LoadGateCounter("player", "player:1", "status_applied", "wither") > 0);
        Assert.Equal(0L, _store.LoadGateCounter("player", "player:2", "status_applied", "wither"));
    }

    [Fact]
    public void Reset_clears_the_seed_stamp_so_a_reset_save_can_be_reseeded()
    {
        GiveDeepPrimaryTree("player:1");
        _store.SeedExistingSaveGateCounters("player", "player:1", StatusIds, ElementIds, Tuning);
        Assert.True(_store.HasSeededGateCounters("player", "player:1"));

        _store.Reset();

        Assert.False(_store.HasSeededGateCounters("player", "player:1"));
        Assert.Equal(0L, _store.LoadGateCounter("player", "player:1", "status_applied", "wither"));
    }

    Dictionary<string, long> SnapshotAll(string ownerKey)
    {
        var snapshot = new Dictionary<string, long>();
        foreach (var (subjectId, count) in _store.LoadGateCountersForOwner("player", ownerKey, "status_applied"))
            snapshot[$"status_applied:{subjectId}"] = count;
        foreach (var (subjectId, count) in _store.LoadGateCountersForOwner("player", ownerKey, "element_mastery"))
            snapshot[$"element_mastery:{subjectId}"] = count;
        return snapshot;
    }
}
