using FusionRpg.Contracts;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>`commander-roster` EP3.4 — the seat on a lawn session (spec-lawn-commander-seat.md).
///
/// `rpg_unique_lawn_sessions.seat` is a new column on an existing table: added through `EnsureColumn` (a
/// migration is for re-keying, H2), backfilled to `'board'`, and written by every new bound row. An
/// ordinary bound actor is a BOARD seat — the commander seat is a later task's write.
/// </summary>
public class UniqueLawnSeatTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    readonly long _playerId;

    public UniqueLawnSeatTests()
    {
        _testStore = DataTestStore.Create();       // in memory, per the test-substrate standard
        _store = _testStore.Store;
        _playerId = _store.GetCurrentPlayerId();
    }

    public void Dispose() => _testStore.Dispose();

    [Fact]
    public void A_bound_actor_is_seated_on_the_board()
    {
        var actor = _store.CreateUniqueActor(_playerId, "plant", 3);
        Assert.True(_store.TryBeginUniqueDeploy(actor.InstanceId, "corr-seat-1", "match-seat-1").Ok);
        Assert.True(_store.TryAckUniqueSpawn("corr-seat-1", "ptr-seat-1", "match-seat-1").Ok);

        // Read back through the store's own rows, not through a field this test remembered to name.
        var sessionRows = _store.SnapshotInstanceRowsForTests(actor.InstanceId)
            .Where(row => row.StartsWith("rpg_unique_lawn_sessions:", StringComparison.Ordinal))
            .ToList();

        var row = Assert.Single(sessionRows);
        Assert.Contains(UniqueLawnSeats.Board, row);
        Assert.Equal(UniqueActorPhases.ActiveBound, _store.GetUniqueActor(actor.InstanceId)!.Phase);
    }

    [Fact]
    public void The_seat_vocabulary_is_closed_and_pinned_with_its_reason()
    {
        // A CLOSED vocabulary the code owns and a human changes by review (a new seat is a reviewed
        // decision, not a loose string at a call site) — the one literal pin this suite allows for it.
        Assert.Equal(new[] { "board", "commander" }, UniqueLawnSeats.All);
        Assert.Equal("board", UniqueLawnSeats.Board);
    }
}
