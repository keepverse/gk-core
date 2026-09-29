using FusionRpg.Core.Battle;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Tests.Battle;
using Xunit;

namespace FusionRpg.Core.Tests.Actions;

/// <summary>
/// T11 (action-todo.md, spec-basic-attack-adoption.md §"Testing strategy"): the parity ladder,
/// captured BEFORE any engine change — once `A5`'s rewire lands there is nothing left to capture.
///
/// <para><b>Values, not counts.</b> Per-stream draw sequences, the resolved target ptr per attack,
/// and the signed delta per apply, across eight fixtures. A count-matching, value-differing run is
/// exactly the failure a count-only comparison would miss — <see cref="BattleTrace.Digest"/> already
/// records draw values and phase order; <see cref="BattleTrace.Targets"/> and
/// <see cref="BattleTrace.Applies"/> (added here) close the two gaps the digest did not cover.</para>
/// </summary>
public class BasicAttackAdoptionTests
{
    static string CombinedTrace(BattleSetup setup, ulong seed)
    {
        var trace = new BattleTrace();
        BattleEngine.Resolve(setup, seed, trace);
        return string.Join("\n",
            "== digest ==", trace.Digest,
            "== targets ==", string.Join("\n", trace.Targets),
            "== applies ==", string.Join("\n", trace.Applies));
    }

    [Theory]
    [InlineData("stomp", 1001)]
    [InlineData("close", 2002)]
    [InlineData("wipe", 3003)]
    [InlineData("close-seed-10", 10)]
    [InlineData("close-seed-20", 20)]
    [InlineData("close-seed-30", 30)]
    [InlineData("close-seed-40", 40)]
    [InlineData("close-seed-50", 50)]
    public void Parity_fixtures_are_captured_before_any_engine_change(string name, ulong seed)
    {
        var setup = name switch
        {
            "stomp" => BattleGoldenTests.StompSetup(),
            "wipe" => BattleGoldenTests.WipeSetup(),
            _ => BattleGoldenTests.CloseSetup(),
        };

        var actual = CombinedTrace(setup, seed);

        // Non-trivial and self-consistent, so a silently empty capture can never pass for a match.
        Assert.Contains("draw initiative", actual, StringComparison.Ordinal);
        Assert.Contains("== targets ==", actual, StringComparison.Ordinal);
        Assert.Contains("== applies ==", actual, StringComparison.Ordinal);

        Assert.Equal(ActionAdoptionFixtures.LoadOrCapture(name, actual), actual);
    }

    [Fact]
    public void The_parity_trace_replays_identically_for_the_same_battle()
    {
        string Once() => CombinedTrace(BattleGoldenTests.CloseSetup(), 2002);
        Assert.Equal(Once(), Once());
    }

    [Fact]
    public void Every_recorded_target_names_an_actor_that_actually_exists_in_the_setup()
    {
        var trace = new BattleTrace();
        BattleEngine.Resolve(BattleGoldenTests.CloseSetup(), 2002, trace);

        var validKeys = new[] { "squad:0", "squad:1", "wave:0", "wave:1" };
        foreach (var line in trace.Targets)
        {
            var arrow = line.IndexOf("->", StringComparison.Ordinal);
            Assert.True(arrow > 0, $"malformed target line: {line}");
            var targetKey = line[(arrow + 2)..];
            Assert.Contains(targetKey, validKeys);
        }
    }

    [Fact]
    public void At_least_one_apply_is_recorded_for_a_battle_that_actually_fights()
    {
        var trace = new BattleTrace();
        BattleEngine.Resolve(BattleGoldenTests.CloseSetup(), 2002, trace);
        Assert.NotEmpty(trace.Applies);
        Assert.NotEmpty(trace.Targets);
    }
}
