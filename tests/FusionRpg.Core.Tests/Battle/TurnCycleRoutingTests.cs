using FusionRpg.Core.Battle;
using FusionRpg.Core.Battle.Timeline;
using Xunit;

namespace FusionRpg.Core.Tests.Battle;

/// <summary>
/// **B38 — the per-actor turn cycle is driven by a real battle.**
///
/// <para>`ActorTurnMachine` was fully built and fully tested but appeared **nowhere** in
/// `BattleEngine` or `BattleRunState`. That is why B20/B21/B22 were blocked: an interactive dwell
/// needs a `Ready` state to occupy, and no battle ever produced one. These tests prove a battle now
/// walks every actor through the cycle.</para>
///
/// <para>Observed through <see cref="BattleTrace.Turns"/>, which is deliberately **outside**
/// <see cref="BattleTrace.Digest"/> — the digest is the fixture the parity ladder compares, so adding
/// lines to it would move every trace golden and make an observability addition indistinguishable
/// from a behaviour change.</para>
/// </summary>
public class TurnCycleRoutingTests
{
    static BattleTrace Trace(BattleModeProfile? profile = null)
    {
        var trace = new BattleTrace();
        BattleEngine.Resolve(BattleGoldenTests.CloseSetup(), 2002, trace, profile: profile);
        return trace;
    }

    [Fact]
    public void ABattleWalksAnActorThroughTheWholeCycle()
    {
        var turns = Trace().Turns;

        Assert.NotEmpty(turns);
        // Round 1, squad:0 — the full action cycle, in order.
        Assert.Contains("1 squad:0 Charging->Ready", turns);
        Assert.Contains("1 squad:0 Ready->Committed", turns);
        Assert.Contains("1 squad:0 Committed->Resolving", turns);
        Assert.Contains("1 squad:0 Resolving->Recovering", turns);
        Assert.Contains("1 squad:0 Recovering->Charging", turns);
    }

    /// <summary>Every transition recorded must be one the kernel's own table allows — the machine
    /// throws on an illegal one, so this also proves no path silently skips a state.</summary>
    [Fact]
    public void EveryRecordedTransitionIsALegalOne()
    {
        var legal = TurnTransitions.All
            .Select(t => $"{t.From}->{t.To}")
            .ToHashSet(StringComparer.Ordinal);

        foreach (var line in Trace().Turns)
        {
            var arrow = line[(line.LastIndexOf(' ') + 1)..];
            Assert.True(legal.Contains(arrow), $"'{arrow}' is not a legal turn transition");
        }
    }

    /// <summary>
    /// ⭐ The economy — not the state machine — decides how many actions an actor gets. Readiness is
    /// re-offered every pass, so a points economy grants a second turn where a one-action economy
    /// refuses it.
    ///
    /// <para>This is the test that caught a real defect: offering readiness only ONCE per round
    /// silently capped every economy at one action, which made `hybrid-atb` identical to
    /// `classic-round` and would have quietly invalidated B34's whole sweep.</para>
    /// </summary>
    [Fact]
    public void APointsEconomyProducesMoreCommitsPerRoundThanAOneActionEconomy()
    {
        int CommitsInRoundOne(BattleModeProfile p) =>
            Trace(p).Turns.Count(t => t.StartsWith("1 ", StringComparison.Ordinal)
                                      && t.EndsWith("Ready->Committed", StringComparison.Ordinal));

        var oneAction = CommitsInRoundOne(BattleModeProfileCatalog.ClassicRound);
        var points = CommitsInRoundOne(BattleModeProfileCatalog.HybridAtb);

        Assert.True(points > oneAction,
            $"a 2-point economy should commit more often in round 1: one-action {oneAction}, points {points}");
    }

    /// <summary>An actor that never got a turn must not be left dangling in `Ready` — the kernel's own
    /// "passed turn" edge exists for exactly this, and a stuck actor would be skipped forever.</summary>
    [Fact]
    public void NoActorIsLeftStrandedInReady()
    {
        var turns = Trace().Turns;

        foreach (var actor in new[] { "squad:0", "squad:1", "wave:0", "wave:1" })
        {
            var mine = turns.Where(t => t.Contains($" {actor} ", StringComparison.Ordinal)).ToList();
            if (mine.Count == 0) continue;

            // Whatever the last transition for this actor was, it must have left it out of Ready.
            Assert.False(mine[^1].EndsWith("->Ready", StringComparison.Ordinal),
                $"{actor} ended the battle stranded in Ready: {mine[^1]}");
        }
    }

    /// <summary>Tracing is opt-in and inert: the battle is identical whether or not it is observed.</summary>
    [Fact]
    public void ObservingTheCycleDoesNotChangeTheBattle()
    {
        var untraced = BattleEngine.Resolve(BattleGoldenTests.CloseSetup(), 2002);
        var traced = BattleEngine.Resolve(BattleGoldenTests.CloseSetup(), 2002, new BattleTrace());

        Assert.Equal(untraced.Outcome, traced.Outcome);
        Assert.Equal(untraced.Rounds, traced.Rounds);
    }
}
