using FusionRpg.Contracts;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Stats.Derived;
using CoreStatus = FusionRpg.Core.Status;
using Xunit;

namespace FusionRpg.Core.Tests.Status;

public class StatusSpreadTests
{
    static CoreStatus.StatusRuntime Runtime() =>
        new(CoreStatus.StatusCatalogBootstrap.CreateDefault(), (_, attackerLess) =>
            attackerLess ? ActorDerivedSnapshot.AttackerLess() : ActorDerivedSnapshot.StubNeutral());

    static BoardSnapshot RowBoard() => new(new[]
    {
        new BoardEntitySnap { Ptr = "Z1", Row = 2, Col = 5, Side = "zombie", Living = true },
        new BoardEntitySnap { Ptr = "Z2", Row = 2, Col = 6, Side = "zombie", Living = true },
        new BoardEntitySnap { Ptr = "Z3", Row = 3, Col = 5, Side = "zombie", Living = true }
    });

    [Fact]
    public void Row_neighbors_same_row_only()
    {
        var board = RowBoard();
        var neighbors = CoreStatus.StatusSpread.RowNeighbors("Z1", board);
        Assert.Single(neighbors);
        Assert.Equal("Z2", neighbors[0]);
    }

    [Fact]
    public void Spread_respects_proc_depth_limit()
    {
        var rt = Runtime();
        var now = DateTimeOffset.UtcNow;
        var input = new CoreStatus.StatusApplyInput(
            "blight",
            "Z1",
            "P1",
            "g-blight",
            -12,
            5000,
            PeriodMs: 1000,
            DurationMs: 5000,
            SpreadChance: 1.0,
            SpreadStatusId: "blight",
            SpreadMaxHops: 2,
            HopDepth: 0);
        rt.Apply(input, new CoreStatus.FixedStatusRng(0.0), now);

        var inst = rt.ForHost("Z1").Single();
        var template = input with { HostPtr = "" };
        var atLimit = CoreStatus.StatusSpread.Execute(
            rt,
            new CoreStatus.StatusSpreadRequest(inst, new[] { "Z2" }, CoreStatus.StatusPolicy.ProcDepthLimitDefault, 1.0, template),
            new CoreStatus.FixedStatusRng(0.0),
            now);
        Assert.Empty(atLimit);
    }

    [Fact]
    public void Spread_applies_to_neighbor_when_chance_passes()
    {
        var rt = Runtime();
        var now = DateTimeOffset.UtcNow;
        var input = new CoreStatus.StatusApplyInput(
            "blight",
            "Z1",
            "P1",
            "g-blight",
            -12,
            5000,
            SpreadChance: 1.0,
            SpreadStatusId: "blight",
            SpreadMaxHops: 2);
        rt.Apply(input, new CoreStatus.FixedStatusRng(0.0), now);
        var inst = rt.ForHost("Z1").Single();
        var template = input with { HostPtr = "", HopDepth = 1 };
        var outcomes = CoreStatus.StatusSpread.Execute(
            rt,
            new CoreStatus.StatusSpreadRequest(inst, new[] { "Z2" }, 1, 1.0, template),
            new CoreStatus.FixedStatusRng(0.0),
            now);
        Assert.Single(outcomes);
        Assert.True(outcomes[0].Applied);
        Assert.NotEmpty(rt.ForHost("Z2"));
    }

    [Fact]
    public void Resisted_apply_records_telemetry_event()
    {
        var rt = Runtime();
        rt.OnResisted = _ => { };
        var now = DateTimeOffset.UtcNow;
        // rng=1.0 fails apply roll at p_final≈0.5 for neutral delta
        rt.Apply(
            new CoreStatus.StatusApplyInput("wither", "Z1", "P1", "g1", 20, 5000),
            new CoreStatus.FixedStatusRng(1.0),
            now);
        Assert.Single(rt.ResistedEvents);
        Assert.Equal(CoreStatus.StatusResistReason.ApplyRoll, rt.ResistedEvents[0].Reason);
    }

    [Fact]
    public void ResolveCandidates_area_row_excludes_other_rows()
    {
        var board = RowBoard();
        var rt = Runtime();
        var now = DateTimeOffset.UtcNow;
        var input = new CoreStatus.StatusApplyInput(
            "blight",
            "Z1",
            "P1",
            "g-blight",
            -12,
            5000,
            SpreadTarget: new TargetSpec
            {
                Mode = TargetModes.Area,
                Shape = AreaShapes.Row,
                Anchor = "EventTarget",
                Filters = new Dictionary<string, object?> { ["side"] = "zombie" }
            });
        rt.Apply(input, new CoreStatus.FixedStatusRng(0.0), now);
        var inst = rt.ForHost("Z1").Single();
        var candidates = CoreStatus.StatusSpread.ResolveCandidates(inst, inst.SpreadTarget, board);
        Assert.Single(candidates);
        Assert.Equal("Z2", candidates[0]);
        Assert.DoesNotContain("Z3", candidates);
    }

    [Fact]
    public void Spread_icd_blocks_second_wave_within_window()
    {
        var rt = Runtime();
        var board = RowBoard();
        var now = DateTimeOffset.UtcNow;
        var input = new CoreStatus.StatusApplyInput(
            "blight",
            "Z1",
            "P1",
            "g-blight",
            -12,
            5000,
            PeriodMs: 1000,
            DurationMs: 5000,
            SpreadChance: 1.0,
            SpreadStatusId: "blight",
            SpreadMaxHops: 2,
            SpreadIcdMs: 5000,
            SpreadTarget: new TargetSpec
            {
                Mode = TargetModes.Area,
                Shape = AreaShapes.Row,
                Anchor = "EventTarget"
            });
        rt.Apply(input, new CoreStatus.FixedStatusRng(0.0), now);
        var sink = new RecordingPulseSink();
        rt.Tick(now.AddMilliseconds(1000), sink, board, new CoreStatus.FixedStatusRng(0.0));
        Assert.NotEmpty(rt.ForHost("Z2"));

        rt.Tick(now.AddMilliseconds(2000), sink, board, new CoreStatus.FixedStatusRng(0.0));
        Assert.Single(rt.ForHost("Z2"));
    }

    sealed class RecordingPulseSink : CoreStatus.IStatusPulseSink
    {
        public void PulseHp(CoreStatus.StatusInstance instance, double amount) { }
    }
}
