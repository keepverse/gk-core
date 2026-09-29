using FusionRpg.Contracts;
using FusionRpg.Core.Battle;
using Xunit;

namespace FusionRpg.Core.Tests.Battle;

/// <summary>
/// C3a: BattleReportEmitter — lean event vocabulary only (board.start, spawns, dies,
/// match.result, board.end), synthetic `web:{matchKey}:{n}` ptrs, NO timestamps (the
/// WebMatchService stamps monotonic t at ingest).
/// </summary>
public class BattleReportEmitterTests
{
    static BattleActorSetup Actor(string key, string side, int level) => new()
    {
        Key = key,
        Side = side,
        SpeciesId = "test-species",
        TypeId = 10_001,
        Level = level,
        MaxHp = BattleRuleset.BaseHp(level),
        Atk = BattleRuleset.BaseAtk(level),
        Defense = BattleRuleset.BaseDefense(level)
    };

    static BattleReport Stomp() => BattleEngine.Resolve(new BattleSetup
    {
        WaveId = "emit-wave",
        Squad = Enumerable.Range(0, 2).Select(i => Actor($"squad:{i}", "squad", 10)).ToList(),
        Wave = Enumerable.Range(0, 3).Select(i => Actor($"wave:{i}", "wave", 1)).ToList()
    }, 21);

    static Dictionary<string, object?> PayloadOf(EventEnvelope env) =>
        Assert.IsType<Dictionary<string, object?>>(env.Payload);

    [Fact]
    public void Emits_the_lean_profile_in_locked_order()
    {
        var report = Stomp();
        var events = BattleReportEmitter.Emit(report, "match-abc");

        Assert.Equal("board.start", events.First().Kind);
        Assert.Equal("board.end", events.Last().Kind);
        Assert.Equal("match.result", events[^2].Kind);

        var kinds = events.Select(e => e.Kind).Distinct().ToList();
        Assert.Subset(new HashSet<string>(new[]
        {
            "board.start", "plant.spawn", "zombie.spawn", "plant.die", "zombie.die", "match.result", "board.end"
        }), new HashSet<string>(kinds));

        Assert.Equal(2, events.Count(e => e.Kind == "plant.spawn"));
        Assert.Equal(3, events.Count(e => e.Kind == "zombie.spawn"));
        Assert.Equal(3, events.Count(e => e.Kind == "zombie.die")); // stomp wipes the wave

        // Lean: 1 start + 5 spawns + ≤5 dies + result + end — never per-attack chatter.
        Assert.InRange(events.Count, 8, 13);
    }

    [Fact]
    public void Envelopes_are_game_stamped_and_clockless()
    {
        var events = BattleReportEmitter.Emit(Stomp(), "match-abc");
        Assert.All(events, e =>
        {
            Assert.Equal(RpgConstants.GameIdWebRpg, e.Game);
            Assert.Equal("match-abc", e.MatchKey);
            Assert.Equal("", e.T); // the service stamps monotonic t at ingest
        });
    }

    [Fact]
    public void Ptrs_use_the_synthetic_web_scheme()
    {
        var events = BattleReportEmitter.Emit(Stomp(), "match-abc");
        var spawnPtrs = events.Where(e => e.Kind is "plant.spawn" or "zombie.spawn")
            .Select(e => Assert.IsType<string>(PayloadOf(e)["ptr"])).ToList();

        Assert.Equal(5, spawnPtrs.Distinct(StringComparer.Ordinal).Count());
        Assert.All(spawnPtrs, p => Assert.Matches("^web:match-abc:[0-9]+$", p));

        var diePtrs = events.Where(e => e.Kind is "plant.die" or "zombie.die")
            .Select(e => Assert.IsType<string>(PayloadOf(e)["ptr"]));
        Assert.All(diePtrs, p => Assert.Contains(p, spawnPtrs));
    }

    [Fact]
    public void Match_result_and_summary_carry_the_report()
    {
        var report = Stomp();
        var events = BattleReportEmitter.Emit(report, "match-abc");

        Assert.Equal("victory", PayloadOf(events[^2])["result"]);

        var summary = Assert.IsType<Dictionary<string, object?>>(PayloadOf(events[^1])["summary"]);
        Assert.Equal(report.Rounds, summary["rounds"]);
        Assert.Equal(report.SoulLootMilli, summary["soulLootMilli"]);
        var tallies = Assert.IsAssignableFrom<IReadOnlyList<Dictionary<string, object?>>>(summary["actors"]);
        Assert.Equal(5, tallies.Count);
        Assert.All(tallies, t => Assert.True(t.ContainsKey("xpMilli")));
    }

    [Fact]
    public void Start_carries_the_version_stamps()
    {
        var payload = PayloadOf(BattleReportEmitter.Emit(Stomp(), "m").First());
        Assert.Equal(BattleRuleset.EngineVersion, payload["engineVersion"]);
        Assert.Equal(BattleRuleset.RulesetVersion, payload["rulesetVersion"]);
        Assert.Equal(SeededRng.RngAlgoVersion, payload["rngAlgoVersion"]);
        Assert.Equal("21", payload["seed"]); // string — ulong seeds overflow JSON numbers
    }

    [Fact]
    public void Die_event_maps_a_valid_killer_key_to_the_synthetic_pointer()
    {
        var report = new BattleReport
        {
            WaveId = "killer-wave",
            Actors = new[]
            {
                new BattleActorResult("squad:0", "squad", "squad-species", 10_001, 100, 100, 1, true, false, 1000),
                new BattleActorResult("wave:0", "wave", "wave-species", 10_002, 0, 100, 0, false, false, 1000)
            },
            Events = new[]
            {
                new BattleEventRec(1, BattleEventKinds.Die, "wave:0", 10_002, "wave", KillerActorKey: "squad:0")
            }
        };

        var die = Assert.Single(BattleReportEmitter.Emit(report, "killer-match"), e => e.Kind == "zombie.die");
        var payload = PayloadOf(die);

        Assert.Equal("web:killer-match:1", payload["ptr"]);
        Assert.Equal("web:killer-match:0", payload["killerPtr"]);
    }

    [Fact]
    public void Die_event_omits_killer_pointer_when_the_cause_is_unknown()
    {
        var report = new BattleReport
        {
            WaveId = "unknown-cause-wave",
            Actors = new[]
            {
                new BattleActorResult("squad:0", "squad", "squad-species", 10_001, 100, 100, 0, true, false, 1000),
                new BattleActorResult("wave:0", "wave", "wave-species", 10_002, 0, 100, 0, false, false, 1000)
            },
            Events = new[]
            {
                new BattleEventRec(1, BattleEventKinds.Die, "wave:0", 10_002, "wave")
            }
        };

        var die = Assert.Single(BattleReportEmitter.Emit(report, "unknown-match"), e => e.Kind == "zombie.die");

        Assert.False(PayloadOf(die).ContainsKey("killerPtr"));
    }

    [Fact]
    public void Die_event_rejects_a_killer_key_missing_from_report()
    {
        var report = new BattleReport
        {
            WaveId = "missing-killer-wave",
            Actors = new[]
            {
                new BattleActorResult("wave:0", "wave", "wave-species", 10_002, 0, 100, 0, false, false, 1000)
            },
            Events = new[]
            {
                new BattleEventRec(1, BattleEventKinds.Die, "wave:0", 10_002, "wave", KillerActorKey: "missing")
            }
        };

        Assert.Throws<InvalidOperationException>(() => BattleReportEmitter.Emit(report, "missing-killer-match"));
    }

    [Fact]
    public void Blank_match_keys_reject()
    {
        Assert.Throws<ArgumentException>(() => BattleReportEmitter.Emit(Stomp(), " "));
    }
}
