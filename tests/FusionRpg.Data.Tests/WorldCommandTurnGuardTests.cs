using FusionRpg.Core.World;
using FusionRpg.Core.World.Turn;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// Review findings (2026-08-22): orders may only be filed against the turn that is actually open,
/// and filing a batch must not re-read the whole world once per order.
/// </summary>
public class WorldCommandTurnGuardTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public WorldCommandTurnGuardTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _store.CreateWorld(1, WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, 1, "w"));
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    static WorldCommand Cmd(string id) => new()
    {
        CommanderId = "dave",
        CommandId = id,
        Kind = WorldCommandKinds.StandFast
    };

    [Fact]
    public void Orders_are_filed_against_the_open_turn_and_nowhere_else()
    {
        // The store owns which turn is open; a caller cannot file into a resolved or future turn,
        // which would silently corrupt a replay of that turn.
        var (ok, reason, _) = _store.SubmitWorldCommand("w", Cmd("c1"));
        Assert.True(ok, reason);

        Assert.Single(_store.ListWorldCommands("w", 0));
        Assert.Empty(_store.ListWorldCommands("w", 1));
    }

    [Fact]
    public void A_batch_is_filed_in_one_pass_with_per_command_results()
    {
        var results = _store.SubmitWorldCommands("w", new[]
        {
            Cmd("b1"),
            Cmd("b2") with { EntityId = "e-wild-pack-1" },  // not Dave's
            Cmd("b3")
        });

        Assert.Equal(3, results.Count);
        Assert.True(results[0].Ok);
        Assert.False(results[1].Ok);
        Assert.Equal("entity.not-yours", results[1].Reason);
        Assert.True(results[2].Ok);

        // The refused order never reached the log; the other two did.
        Assert.Equal(2, _store.ListWorldCommands("w", 0).Count);
    }

    [Fact]
    public void A_replayed_batch_reports_replays_and_writes_nothing_new()
    {
        var batch = new[] { Cmd("r1"), Cmd("r2") };
        _store.SubmitWorldCommands("w", batch);

        var again = _store.SubmitWorldCommands("w", batch);
        Assert.All(again, r => Assert.True(r.Replayed));
        Assert.Equal(2, _store.ListWorldCommands("w", 0).Count);
    }

    [Fact]
    public void An_unknown_world_refuses_the_whole_batch_once()
    {
        var results = _store.SubmitWorldCommands("nope", new[] { Cmd("x1"), Cmd("x2") });
        Assert.Equal(2, results.Count);
        Assert.All(results, r =>
        {
            Assert.False(r.Ok);
            Assert.Equal("world.unknown", r.Reason);
        });
    }
}
