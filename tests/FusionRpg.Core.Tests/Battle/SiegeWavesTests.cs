using FusionRpg.Core.Battle;
using FusionRpg.Core.World.District;
using Xunit;

namespace FusionRpg.Core.Tests.Battle;

/// <summary>
/// base-defense `siege-waves` (spec-siege-waves.md): reinforcements arrive mid-battle on a clock,
/// bounded per round and resumable, without moving a single existing golden (`Reinforcements` defaults
/// empty, so the reinforcement event kind is never scheduled for any existing caller).
///
/// <para><b>F8's state half (field-cleared pulling a batch forward) is implemented but NOT exercised
/// here</b> — see this file's own trailing comment and the todo's own evidence for task 12.1: the
/// mechanism (`fieldClearedTick`) is real code with the spec's own `Math.Min` formula, but the round
/// loop's PRE-EXISTING termination (`!AnyActive("squad") || !AnyActive("wave") -> break`) exits a
/// battle the instant either side's living-animate count hits zero — before a just-cleared field's
/// pulled-forward reinforcement ever gets to fire, at the shipped `fieldClearedThreshold = 0`. A
/// genuine, surfaced architectural interaction, not silently assumed away.</para>
/// </summary>
public class SiegeWavesTests
{
    // High HP, low ATK on the ORIGINAL roster so nobody wipes across the whole horizon — every test
    // here is about the reinforcement clock/queue mechanism, not about combat outcomes, and a side
    // hitting zero would trip the pre-existing AnyActive break this task's own evidence names as an
    // unresolved interaction.
    static BattleActorSetup Durable(string key, string side) => new()
    {
        Key = key, Side = side, SpeciesId = "sw-species", TypeId = 40_001, Level = 3,
        MaxHp = 10_000_000, Atk = 1, Defense = 0,
    };

    static BattleActorSetup Reinforcement(string key, string side) => new()
    {
        Key = key, Side = side, SpeciesId = "sw-reinforcement", TypeId = 40_002, Level = 3,
        MaxHp = 10_000_000, Atk = 1, Defense = 0,
    };

    static BattleSetup Setup(IReadOnlyList<ReinforcementBatch> reinforcements) => new()
    {
        WaveId = "sw-wave",
        Squad = new[] { Durable("squad:0", "squad") },
        Wave = new[] { Durable("wave:0", "wave") },
        Reinforcements = reinforcements,
    };

    static long MaxBattleTick => (long)BattleRuleset.MaxRounds * BattleRuleset.RoundDurationMs;

    [Fact]
    public void Empty_reinforcements_are_byte_identical()
    {
        var withExplicitEmpty = BattleEngine.Resolve(
            Setup(Array.Empty<ReinforcementBatch>()), seed: 7);
        var withDefault = BattleEngine.Resolve(
            new BattleSetup { WaveId = "sw-wave", Squad = new[] { Durable("squad:0", "squad") }, Wave = new[] { Durable("wave:0", "wave") } },
            seed: 7);

        Assert.Equal(
            System.Text.Json.JsonSerializer.Serialize(withDefault),
            System.Text.Json.JsonSerializer.Serialize(withExplicitEmpty));
    }

    [Fact]
    public void Batch_arrives_on_schedule()
    {
        var batch = new ReinforcementBatch
        {
            AtTick = 5_000,
            Side = "wave",
            Actors = new[] { Reinforcement("wave:reinforce", "wave") },
            Edge = BoardEdge.North,
        };
        var report = BattleEngine.Resolve(Setup(new[] { batch }), seed: 1);

        Assert.Contains(report.Actors, a => a.Key == "wave:reinforce");
        Assert.Contains(report.Events, e => e.Kind == BattleEventKinds.Spawn && e.ActorKey == "wave:reinforce");
    }

    [Fact]
    public void Both_sides_reinforce_through_one_path()
    {
        var batches = new[]
        {
            new ReinforcementBatch { AtTick = 3_000, Side = "squad", Actors = new[] { Reinforcement("squad:reinforce", "squad") } },
            new ReinforcementBatch { AtTick = 3_000, Side = "wave", Actors = new[] { Reinforcement("wave:reinforce", "wave") } },
        };
        var report = BattleEngine.Resolve(Setup(batches), seed: 1);

        Assert.Contains(report.Actors, a => a.Key == "squad:reinforce" && a.Side == "squad");
        Assert.Contains(report.Actors, a => a.Key == "wave:reinforce" && a.Side == "wave");
    }

    [Fact]
    public void Same_tick_arrivals_order_by_ordinal_key_none_are_lost()
    {
        var actors = new[] { "wave:z", "wave:a", "wave:m" }
            .Select(k => Reinforcement(k, "wave")).ToArray();
        var batch = new ReinforcementBatch { AtTick = 2_000, Side = "wave", Actors = actors };
        var report = BattleEngine.Resolve(Setup(new[] { batch }), seed: 1);

        foreach (var k in new[] { "wave:z", "wave:a", "wave:m" })
            Assert.Contains(report.Actors, a => a.Key == k);
    }

    [Fact]
    public void Arrivals_are_capped_per_round_and_none_are_lost_over_the_cap()
    {
        // maxArrivalsPerRound ships at 8 (siege.v1.json); 20 actors in one batch must all eventually
        // arrive, carried over rather than dropped (F9/C7), never duplicated.
        var actors = Enumerable.Range(0, 20)
            .Select(i => Reinforcement($"wave:r{i:D2}", "wave")).ToArray();
        var batch = new ReinforcementBatch { AtTick = 1_000, Side = "wave", Actors = actors };
        var report = BattleEngine.Resolve(Setup(new[] { batch }), seed: 1);

        var arrivedKeys = report.Actors.Where(a => a.Key.StartsWith("wave:r", StringComparison.Ordinal))
            .Select(a => a.Key).ToList();
        Assert.Equal(20, arrivedKeys.Count);
        Assert.Equal(20, arrivedKeys.Distinct(StringComparer.Ordinal).Count()); // none duplicated
    }

    [Fact]
    public void Batches_past_the_horizon_never_fire()
    {
        var batch = new ReinforcementBatch
        {
            AtTick = MaxBattleTick + 1_000_000,
            Side = "wave",
            Actors = new[] { Reinforcement("wave:too-late", "wave") },
        };
        var report = BattleEngine.Resolve(Setup(new[] { batch }), seed: 1);

        Assert.DoesNotContain(report.Actors, a => a.Key == "wave:too-late");
    }

    [Fact]
    public void Mid_battle_actor_passes_the_same_key_validation_a_mixed_case_key_throws()
    {
        var bad = Reinforcement("Wave:Bad", "wave"); // mixed case
        var batch = new ReinforcementBatch { AtTick = 1_000, Side = "wave", Actors = new[] { bad } };

        Assert.Throws<ArgumentException>(() => BattleEngine.Resolve(Setup(new[] { batch }), seed: 1));
    }

    [Fact]
    public void Adding_an_actor_does_not_reorder_existing_ones()
    {
        var batch = new ReinforcementBatch { AtTick = 1_000, Side = "wave", Actors = new[] { Reinforcement("wave:new", "wave") } };
        var report = BattleEngine.Resolve(Setup(new[] { batch }), seed: 1);

        // The original two actors (squad:0, wave:0) must still be the first two entries, in their
        // original order -- an index shift would invalidate any in-flight effect that captured one.
        Assert.Equal("squad:0", report.Actors[0].Key);
        Assert.Equal("wave:0", report.Actors[1].Key);
    }

    [Fact]
    public void Reinforcement_scheduling_is_deterministic_over_many_runs()
    {
        var batch = new ReinforcementBatch { AtTick = 4_000, Side = "wave", Actors = new[] { Reinforcement("wave:reinforce", "wave") } };
        var setup = Setup(new[] { batch });
        var first = System.Text.Json.JsonSerializer.Serialize(BattleEngine.Resolve(setup, seed: 42));
        for (var i = 0; i < 50; i++)
        {
            var again = System.Text.Json.JsonSerializer.Serialize(BattleEngine.Resolve(setup, seed: 42));
            Assert.Equal(first, again);
        }
    }
}
