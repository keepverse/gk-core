using System.Linq;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Battle.Timeline;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Siege;

/// <summary>
/// base-defense `siege-ai` R4 (spec-siege-ai.md §5, its own named acceptance test
/// "Acting_order_is_frozen_across_deaths": "Kill an actor mid-round; assert the order is
/// unchanged"), 17.4's OTHER remaining named piece besides R3 — closed 2026-09-07 (session 5).
///
/// <para><b>This is a generic engine guarantee, not something `siege-ai` itself introduces or could
/// break</b> — confirmed by reading `BattleEngine.cs:501-505` directly: `order` is a plain
/// `List&lt;ActorState&gt;` snapshot computed ONCE per round, and the action-phase loop's own
/// `foreach (var attacker in order)` skips an inactive actor in place (`if (!attacker.Active)
/// continue;`) rather than removing/reindexing it — the same shape `TurnCycleRoutingTests.cs`
/// already exercises for the generic FSM. `SiegeAiIntentSource`'s own `TryDeclare`/`ChooseTarget`
/// (checked directly) only decide WHOM an already-turn-holding actor targets — none of it reads or
/// writes `order`/`TurnState` — so proving this through the DEFAULT dispatch (as
/// `TurnCycleRoutingTests.cs` itself does) is fully representative; a `SiegeAiIntentSource`-driven
/// variant would test PLUMBING (wiring a live scoring AI into a real `Resolve()` call) that no
/// production path does yet — a real, separately-named gap (`siege-stage`'s own eventual live
/// human-input channel), not part of this acceptance criterion.</para>
///
/// <para><b>Idiom reused, not invented</b>: `BattleTrace.Turns`, counted by
/// `"{round} {key} Ready->Committed"` occurrences — the SAME pattern
/// `APointsEconomyProducesMoreCommitsPerRoundThanAOneActionEconomy` already established for "how many
/// actions did this actor get this round."</para>
/// </summary>
public class ActingOrderFrozenAcrossDeathsTests
{
    static BattleActorSetup Actor(string key, string side, long speed, long maxHp, long atk, long defense) => new()
    {
        Key = key, Side = side, MaxHp = maxHp, Atk = atk, Defense = defense,
        ChannelMods = new[] { new BattleChannelMod(DerivedTurnChannels.Speed, speed) },
    };

    [Fact]
    public void Killing_the_rounds_second_actor_does_not_give_a_survivor_an_extra_turn()
    {
        // Speed order (fastest first, no board -- StubIntentSource's own SourceOrder fallback makes
        // targeting deterministic): A (squad, fastest) -> B (wave, 2nd, 1 HP) -> C (wave, 3rd) ->
        // D (squad, slowest). A's overwhelming Atk kills B before B ever reaches its own turn.
        var setup = new BattleSetup
        {
            Squad = new[]
            {
                Actor("squad:a", "squad", speed: 1000, maxHp: 1000, atk: 10_000, defense: 0),
                Actor("squad:d", "squad", speed: 400, maxHp: 1000, atk: 100, defense: 100),
            },
            Wave = new[]
            {
                Actor("wave:b", "wave", speed: 800, maxHp: 1, atk: 0, defense: 0),
                Actor("wave:c", "wave", speed: 600, maxHp: 1000, atk: 100, defense: 100),
            },
        };

        var trace = new BattleTrace();
        var report = BattleEngine.Resolve(setup, seed: 1, trace: trace);

        Assert.NotNull(report);
        Assert.Equal(0, report.Actors.Single(a => a.Key == "wave:b").HpRemaining); // confirms the kill actually happened

        int CommittedInRoundOne(string key) =>
            trace.Turns.Count(t => t == $"1 {key} Ready->Committed");

        Assert.Equal(0, CommittedInRoundOne("wave:b")); // dead before its own turn -- never committed at all
        Assert.Equal(1, CommittedInRoundOne("squad:a"));
        Assert.Equal(1, CommittedInRoundOne("wave:c")); // NOT promoted into wave:b's slot -- still exactly one turn
        Assert.Equal(1, CommittedInRoundOne("squad:d")); // NOT promoted either
    }
}
