using FusionRpg.Core.Actions.Unlock;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Data.Sqlite;
using Xunit;

namespace FusionRpg.Data.Tests.Actions;

/// <summary>
/// T59.2 (spec-action-instance-and-grant.md §3): `rpg_actor_unlock_state` +
/// `rpg_actor_held_unlock` — the unlock ladder's own persistence, named in spec-unlock-ladder.md and
/// never built until now. Pure round-trip for an already-tested class (<see cref="UnlockState"/>) —
/// no new game logic is exercised here, only that persistence returns exactly what was saved.
/// </summary>
[Trait("VerificationId", "data.action-unlock")]
public class ActionUnlocksStoreTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public ActionUnlocksStoreTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose() => _testStore.Dispose();

    static OwnerScope Owner(string instanceId) => new(OwnerKind.UniqueActor, instanceId);

    [Fact]
    public void ANoRowOwnerReadsBackEmpty()
    {
        var state = _store.GetUnlockState(Owner("specimen-never-seen"));

        Assert.Equal(0, state.EarnCount);
        Assert.Empty(state.Held);
    }

    [Fact]
    public void ASavedStateReadsBackIdenticalEarnCountAndEveryHeldUnlock()
    {
        var owner = Owner("specimen-alpha");
        var saved = UnlockState.FromPersisted(7, new[]
        {
            new HeldUnlock("action.family.cactus.001", EarnCountAtAcceptance: 3),
            new HeldUnlock("action.family.sporing.002", EarnCountAtAcceptance: 7),
        });

        _store.SaveUnlockState(owner, saved);
        var loaded = _store.GetUnlockState(owner);

        Assert.Equal(7, loaded.EarnCount);
        Assert.Equal(2, loaded.Held.Count);
        Assert.Contains(loaded.Held, h => h.UnlockId == "action.family.cactus.001" && h.EarnCountAtAcceptance == 3);
        Assert.Contains(loaded.Held, h => h.UnlockId == "action.family.sporing.002" && h.EarnCountAtAcceptance == 7);
    }

    [Fact]
    public void TwoOwnersAreCompletelyIsolated()
    {
        var ownerA = Owner("specimen-a");
        var ownerB = Owner("specimen-b");

        _store.SaveUnlockState(ownerA, UnlockState.FromPersisted(4, new[] { new HeldUnlock("action.a", 4) }));
        _store.SaveUnlockState(ownerB, UnlockState.FromPersisted(9, new[] { new HeldUnlock("action.b", 9) }));

        var loadedA = _store.GetUnlockState(ownerA);
        var loadedB = _store.GetUnlockState(ownerB);

        Assert.Equal(4, loadedA.EarnCount);
        Assert.Single(loadedA.Held);
        Assert.Equal("action.a", loadedA.Held[0].UnlockId);

        Assert.Equal(9, loadedB.EarnCount);
        Assert.Single(loadedB.Held);
        Assert.Equal("action.b", loadedB.Held[0].UnlockId);
    }

    /// <summary>`SaveUnlockState` is a FULL rebuild, not a delta (matching `ApplyEquippedGrants`'s own
    /// established convention) — a second save with a smaller held set must not leave the first
    /// save's now-discarded entry behind.</summary>
    [Fact]
    public void SavingASmallerHeldSetActuallyRemovesTheDroppedEntry()
    {
        var owner = Owner("specimen-shrink");
        _store.SaveUnlockState(owner, UnlockState.FromPersisted(5, new[]
        {
            new HeldUnlock("action.keep", 2),
            new HeldUnlock("action.drop", 5),
        }));

        _store.SaveUnlockState(owner, UnlockState.FromPersisted(5, new[] { new HeldUnlock("action.keep", 2) }));
        var loaded = _store.GetUnlockState(owner);

        Assert.Single(loaded.Held);
        Assert.Equal("action.keep", loaded.Held[0].UnlockId);
    }

    [Fact]
    public void ReSavingUpdatesEarnCountInPlaceRatherThanDuplicatingTheRow()
    {
        var owner = Owner("specimen-reup");
        _store.SaveUnlockState(owner, UnlockState.FromPersisted(1, Array.Empty<HeldUnlock>()));
        _store.SaveUnlockState(owner, UnlockState.FromPersisted(2, Array.Empty<HeldUnlock>()));

        var loaded = _store.GetUnlockState(owner);
        Assert.Equal(2, loaded.EarnCount);
    }
}
