using FusionRpg.Core.Stats.Derived;
using FusionRpg.Data.Sqlite;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// T18 (action-todo.md, spec-action-costs.md §9): run pools and rest. Pools survive an encounter
/// boundary (save, then a fresh store instance still loads them); "no run row means a run of one"
/// (a lookup that never happened returns null, never an empty dictionary or a throw); "refill at
/// rest" is a delete, not a rewrite, since this store holds no derived snapshot to recompute a max
/// from.
/// </summary>
[Trait("VerificationId", "data.run-pool")]
public class RunPoolStoreTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public RunPoolStoreTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose() => _testStore.Dispose();

    static Dictionary<string, long> AllSix(long value) =>
        DerivedStatChannels.ResourceIds.ToDictionary(id => id, _ => value);

    [Fact]
    public void NoRunRowMeansARunOfOne()
    {
        // Nothing was ever saved for this (run, actor) pair -- the skirmish default.
        Assert.Null(_store.LoadRunPools("run-1", "wave:0"));
    }

    [Fact]
    public void SavedPoolsSurviveAcrossAFreshStoreInstanceIncludingHp()
    {
        var values = AllSix(50);
        values["hp"] = 777; // hp included, per spec, not special-cased to 0/omitted

        _store.SaveRunPools("run-1", "wave:0", values);

        // A fresh store over the SAME storage -- proves this is real persistence, not an in-memory
        // cache the same instance happens to still hold.
        var reopened = _testStore.Reopen();
        var loaded = reopened.LoadRunPools("run-1", "wave:0");

        Assert.NotNull(loaded);
        Assert.Equal(DerivedStatChannels.ResourceIds.Count, loaded!.Count);
        foreach (var id in DerivedStatChannels.ResourceIds)
            Assert.Equal(values[id], loaded[id]);
        Assert.Equal(777, loaded["hp"]);
    }

    [Fact]
    public void SavingTwiceOverwritesRatherThanDuplicating()
    {
        _store.SaveRunPools("run-1", "wave:0", AllSix(10));
        _store.SaveRunPools("run-1", "wave:0", AllSix(90));

        var loaded = _store.LoadRunPools("run-1", "wave:0")!;
        foreach (var id in DerivedStatChannels.ResourceIds)
            Assert.Equal(90, loaded[id]);
    }

    [Fact]
    public void DifferentRunsAndDifferentActorsAreIsolated()
    {
        _store.SaveRunPools("run-1", "wave:0", AllSix(10));
        _store.SaveRunPools("run-2", "wave:0", AllSix(20)); // same actor, different run
        _store.SaveRunPools("run-1", "wave:1", AllSix(30)); // same run, different actor

        Assert.Equal(10, _store.LoadRunPools("run-1", "wave:0")!["stamina"]);
        Assert.Equal(20, _store.LoadRunPools("run-2", "wave:0")!["stamina"]);
        Assert.Equal(30, _store.LoadRunPools("run-1", "wave:1")!["stamina"]);
    }

    [Fact]
    public void RestDeletesTheRowSoTheNextLoadMissesAndFullRefillFollows()
    {
        _store.SaveRunPools("run-1", "wave:0", AllSix(1));
        Assert.NotNull(_store.LoadRunPools("run-1", "wave:0"));

        _store.DeleteRunPools("run-1", "wave:0");

        // "Refill at rest" is this null -- the caller's next ActorResourcePools.CreateFull(derived, ...)
        // starts every pool at max, exactly as if the actor had never been in a run at all.
        Assert.Null(_store.LoadRunPools("run-1", "wave:0"));
    }

    [Fact]
    public void SaveRejectsAPartialResourceSet()
    {
        var partial = new Dictionary<string, long> { ["stamina"] = 10, ["qi"] = 5 }; // missing 4 of 6
        Assert.Throws<ArgumentException>(() => _store.SaveRunPools("run-1", "wave:0", partial));
    }
}
