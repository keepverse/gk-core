using FusionRpg.Core.Progression;
using Xunit;

namespace FusionRpg.Core.Tests;

public class RpgXpApplyTests
{
    [Fact]
    public void XpToNext_is_arithmetic_per_kind()
    {
        Assert.Equal(100, RpgXpCurve.XpToNext(RpgActorKinds.Player, 1));
        Assert.Equal(145, RpgXpCurve.XpToNext(RpgActorKinds.Player, 2));
        Assert.Equal(80, RpgXpCurve.XpToNext(RpgActorKinds.Plant, 1));
        Assert.Equal(70, RpgXpCurve.XpToNext(RpgActorKinds.Zombie, 1));
    }

    [Fact]
    public void Apply_levels_up_and_carries_remainder()
    {
        var s = new RpgActorState();
        var r = RpgXpApply.Apply(RpgActorKinds.Player, s, 260, reason: "kill");
        // L1 need 100 → L2; L2 need 145 → L3 rem 15
        Assert.Equal(3, r.State.Level);
        Assert.Equal(15, r.State.Xp);
        Assert.Equal(2, r.LevelChanges.Count);
        Assert.All(r.LevelChanges, c => Assert.Equal("up", c.Direction));
    }

    [Fact]
    public void Apply_demotes_and_increments_debt()
    {
        var s = new RpgActorState { Level = 3, Xp = 10, HighestLevel = 3 };
        var r = RpgXpApply.Apply(RpgActorKinds.Player, s, -200, reason: "defeat");
        Assert.True(r.State.Level < 3);
        Assert.True(r.State.DemotionCount >= 1);
        Assert.Contains(r.LevelChanges, c => c.Direction == "down");
        Assert.Equal(3, r.State.HighestLevel);
    }

    [Fact]
    public void Apply_floors_at_level_one()
    {
        var s = new RpgActorState { Level = 1, Xp = 10 };
        var r = RpgXpApply.Apply(RpgActorKinds.Player, s, -500, reason: "defeat");
        Assert.Equal(1, r.State.Level);
        Assert.Equal(0, r.State.Xp);
        Assert.Equal(0, r.State.DemotionCount);
    }

    [Fact]
    public void Pipeline_runs_handlers_in_order()
    {
        var log = new List<int>();
        var pipe = new LevelChangePipeline(new ILevelChangeHandler[]
        {
            new RecordingHandler(20, log),
            new RecordingHandler(10, log)
        });
        pipe.Run(new LevelChangeEvent { LevelBefore = 1, LevelAfter = 2, Direction = "up" });
        Assert.Equal(new[] { 10, 20 }, log);
    }

    sealed class RecordingHandler : ILevelChangeHandler
    {
        readonly List<int> _log;
        public RecordingHandler(int order, List<int> log) { Order = order; _log = log; }
        public int Order { get; }
        public void Handle(LevelChangeEvent e, Action next)
        {
            _log.Add(Order);
            next();
        }
    }
}
