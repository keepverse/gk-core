using FusionRpg.Core.Activity;
using Xunit;

namespace FusionRpg.Core.Tests;

public class PvzActivityRollupBuilderTests
{
    [Fact]
    public void Build_counts_kinds_and_victory()
    {
        var facts = new (string, string?)[]
        {
            (PvzActivityKinds.MatchStarted, null),
            (PvzActivityKinds.ZombieKilled, null),
            (PvzActivityKinds.ZombieKilled, null),
            (PvzActivityKinds.PlantPlaced, null),
            (PvzActivityKinds.PlantLost, null),
            (PvzActivityKinds.ExtraSpawnFired, null),
            (PvzActivityKinds.MatchEnded, """{"result":"victory"}""")
        };
        var c = PvzActivityRollupBuilder.Build(facts);
        Assert.Equal(1, c.MatchesStarted);
        Assert.Equal(1, c.MatchesEnded);
        Assert.Equal(1, c.Victories);
        Assert.Equal(0, c.Defeats);
        Assert.Equal(2, c.ZombiesKilled);
        Assert.Equal(1, c.PlantsLost);
        Assert.Equal(1, c.PlantsPlaced);
        Assert.Equal(1, c.ExtraSpawnsFired);
    }

    [Fact]
    public void ApplyDelta_loop_matches_Build_fixed_sequence()
    {
        var facts = new (string, string?)[]
        {
            (PvzActivityKinds.MatchStarted, null),
            (PvzActivityKinds.ZombieKilled, null),
            (PvzActivityKinds.PlantPlaced, null),
            (PvzActivityKinds.PlantLost, null),
            (PvzActivityKinds.ExtraSpawnFired, null),
            (PvzActivityKinds.MatchEnded, """{"result":"victory"}"""),
            (PvzActivityKinds.MatchEnded, """{"result":"lose"}""")
        };
        AssertCountersEqual(PvzActivityRollupBuilder.Build(facts), ApplyAll(facts));
    }

    [Fact]
    public void ApplyDelta_loop_matches_Build_random_stream()
    {
        var rng = new Random(42);
        var kinds = new[]
        {
            PvzActivityKinds.MatchStarted,
            PvzActivityKinds.MatchEnded,
            PvzActivityKinds.ZombieKilled,
            PvzActivityKinds.PlantLost,
            PvzActivityKinds.PlantPlaced,
            PvzActivityKinds.ExtraSpawnFired,
            PvzActivityKinds.MowerUsed,
            PvzActivityKinds.ZombieSpawned
        };
        var endPayloads = new[] { """{"result":"victory"}""", """{"result":"win"}""", """{"result":"defeat"}""", """{"result":"lose"}""", "{}" };
        var facts = new List<(string, string?)>(200);
        for (var i = 0; i < 200; i++)
        {
            var kind = kinds[rng.Next(kinds.Length)];
            string? payload = kind == PvzActivityKinds.MatchEnded
                ? endPayloads[rng.Next(endPayloads.Length)]
                : null;
            facts.Add((kind, payload));
        }
        AssertCountersEqual(PvzActivityRollupBuilder.Build(facts), ApplyAll(facts));
    }

    [Fact]
    public void Build_win_lose_aliases()
    {
        var win = PvzActivityRollupBuilder.Build(new[] { (PvzActivityKinds.MatchEnded, """{"result":"win"}""") });
        Assert.Equal(1, win.Victories);
        var lose = PvzActivityRollupBuilder.Build(new[] { (PvzActivityKinds.MatchEnded, """{"result":"lose"}""") });
        Assert.Equal(1, lose.Defeats);
    }

    [Fact]
    public void FromCaptureKind_maps_v1_kinds()
    {
        Assert.Equal(PvzActivityKinds.MatchStarted, PvzActivityKinds.FromCaptureKind("board.start"));
        Assert.Equal(PvzActivityKinds.MatchEnded, PvzActivityKinds.FromCaptureKind("match.result"));
        Assert.Equal(PvzActivityKinds.ZombieKilled, PvzActivityKinds.FromCaptureKind("zombie.die"));
        Assert.Equal(PvzActivityKinds.PlantLost, PvzActivityKinds.FromCaptureKind("plant.die"));
        Assert.Equal(PvzActivityKinds.PlantPlaced, PvzActivityKinds.FromCaptureKind("plant.place"));
        Assert.Equal(PvzActivityKinds.MowerUsed, PvzActivityKinds.FromCaptureKind("mower.start"));
        Assert.Equal(PvzActivityKinds.ZombieSpawned, PvzActivityKinds.FromCaptureKind("zombie.spawn"));
        Assert.Null(PvzActivityKinds.FromCaptureKind("bullet.init"));
    }

    [Fact]
    public void DedupeKeyForCapture_prefers_ptr_for_place()
    {
        Assert.Equal("P9", PvzActivityKinds.DedupeKeyForCapture(PvzActivityKinds.PlantPlaced, "P9", 1, 2, "t"));
        Assert.Equal("1:2", PvzActivityKinds.DedupeKeyForCapture(PvzActivityKinds.PlantPlaced, null, 1, 2, "t"));
        Assert.Equal("run", PvzActivityKinds.DedupeKeyForCapture(PvzActivityKinds.MatchStarted, null, null, null, "t"));
    }

    [Fact]
    public void DedupeKeyForCapture_usesLifecycleOccurrence_forDeaths()
    {
        Assert.Equal("occ:7", PvzActivityKinds.DedupeKeyForCapture(
            PvzActivityKinds.ZombieKilled, "reused-ptr", null, null, "t", lifecycleOccurrence: 7));
        Assert.Equal("reused-ptr", PvzActivityKinds.DedupeKeyForCapture(
            PvzActivityKinds.ZombieKilled, "reused-ptr", null, null, "t"));
    }

    [Fact]
    public void IsKnown_allowlist()
    {
        Assert.True(PvzActivityKinds.IsKnown("ZombieKilled"));
        Assert.False(PvzActivityKinds.IsKnown("QuestDone"));
    }

    static PvzActivityRollupCounters ApplyAll(IEnumerable<(string Kind, string? Payload)> facts)
    {
        var c = new PvzActivityRollupCounters();
        foreach (var (kind, payload) in facts)
            PvzActivityRollupBuilder.ApplyDelta(c, kind, payload);
        return c;
    }

    static void AssertCountersEqual(PvzActivityRollupCounters expected, PvzActivityRollupCounters actual)
    {
        Assert.Equal(expected.MatchesStarted, actual.MatchesStarted);
        Assert.Equal(expected.MatchesEnded, actual.MatchesEnded);
        Assert.Equal(expected.Victories, actual.Victories);
        Assert.Equal(expected.Defeats, actual.Defeats);
        Assert.Equal(expected.ZombiesKilled, actual.ZombiesKilled);
        Assert.Equal(expected.PlantsLost, actual.PlantsLost);
        Assert.Equal(expected.PlantsPlaced, actual.PlantsPlaced);
        Assert.Equal(expected.ExtraSpawnsFired, actual.ExtraSpawnsFired);
    }
}
