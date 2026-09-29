using FusionRpg.Core.Combat;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Status;
using Xunit;

namespace FusionRpg.Core.Tests.Battle;

/// <summary>
/// status-rail C2 — contagion on a battle-shaped board (adapter sides <c>squad</c>/<c>wave</c>),
/// and the boardless no-hop contract.
/// </summary>
public class BattleContagionBoardTests
{
    static StatusRuntime Runtime() =>
        new(StatusCatalogBootstrap.CreateDefault(), (_, attackerLess) =>
            attackerLess ? ActorDerivedSnapshot.AttackerLess() : ActorDerivedSnapshot.StubNeutral());

    /// <summary>Mirrors <c>BoardSnapshotAdapter</c> side vocabulary — not lawn zombie/plant.</summary>
    static BoardSnapshot WaveRowBoard() => new(new[]
    {
        new BoardEntitySnap { Ptr = "wave:0", Row = 2, Col = 5, Side = "wave", Living = true },
        new BoardEntitySnap { Ptr = "wave:1", Row = 2, Col = 6, Side = "wave", Living = true },
        new BoardEntitySnap { Ptr = "squad:0", Row = 2, Col = 3, Side = "squad", Living = true }
    });

    [Fact]
    public void Row_neighbors_on_adapter_sides_see_same_row_without_zombie_filter()
    {
        var neighbors = StatusSpread.RowNeighbors("wave:0", WaveRowBoard());
        Assert.Contains("wave:1", neighbors);
        Assert.Contains("squad:0", neighbors);
    }

    [Fact]
    public void Row_neighbors_side_filter_wave_excludes_squad()
    {
        var neighbors = StatusSpread.RowNeighbors("wave:0", WaveRowBoard(), sideFilter: "wave");
        Assert.Equal(new[] { "wave:1" }, neighbors);
    }

    [Fact]
    public void Tick_spreads_blight_on_battle_board_when_chance_forces()
    {
        var rt = Runtime();
        var now = DateTimeOffset.UtcNow;
        var applied = rt.Apply(
            new StatusApplyInput(
                "blight",
                HostPtr: "wave:0",
                AttackerPtr: "squad:0",
                GrantId: "g-blight",
                BaseMagnitude: -12,
                BaseDuration: 5000,
                PeriodMs: 1000,
                DurationMs: 5000,
                SpreadChance: 1.0,
                SpreadStatusId: "blight",
                SpreadMaxHops: 2,
                HopDepth: 0),
            new FixedStatusRng(0.0),
            now);
        Assert.True(applied.Applied);
        Assert.Empty(rt.ForHost("wave:1"));

        rt.Tick(now.AddMilliseconds(1000), sink: null, board: WaveRowBoard(), spreadRng: new FixedStatusRng(0.0));
        Assert.NotEmpty(rt.ForHost("wave:1"));
    }

    [Fact]
    public void Tick_does_not_spread_when_board_is_null()
    {
        var rt = Runtime();
        var now = DateTimeOffset.UtcNow;
        rt.Apply(
            new StatusApplyInput(
                "blight",
                HostPtr: "wave:0",
                AttackerPtr: "squad:0",
                GrantId: "g-blight",
                BaseMagnitude: -12,
                BaseDuration: 5000,
                PeriodMs: 1000,
                DurationMs: 5000,
                SpreadChance: 1.0,
                SpreadStatusId: "blight",
                SpreadMaxHops: 2),
            new FixedStatusRng(0.0),
            now);

        rt.Tick(now.AddMilliseconds(1000), sink: null, board: null, spreadRng: new FixedStatusRng(0.0));
        Assert.Empty(rt.ForHost("wave:1"));
    }
}
