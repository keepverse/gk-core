using FusionRpg.Core.Actions.Ai;
using Xunit;

namespace FusionRpg.Core.Tests.Actions.Ai;

/// <summary>
/// combat-ai `core-scorer` (module 1, CAI1.4, spec-core-scorer.md §6): extends, never replaces, the
/// shipped siege coverage for `TryGetHeld`/`RecordRetarget`/`Forget` (`SiegeAiIntentSourceTests`).
/// This is the ONLY anti-repeat mechanism in the repo.
/// </summary>
public class RetargetLedgerTests
{
    [Fact]
    public void Latency_zero_holds_nothing()
    {
        var ledger = new RetargetLedger();
        ledger.RecordRetarget("me", "target1", nowTick: 0);

        var held = ledger.TryGetHeld("me", nowTick: 1, retargetLatencyTicks: 0, stillValid: _ => true, out _);

        Assert.False(held);
    }

    [Fact]
    public void Commitment_bonus_zero_is_byte_identical()
    {
        var ledger = new RetargetLedger();
        ledger.RecordRetarget("me", "target1", nowTick: 0);

        Assert.Equal(0, ledger.CommitmentBonusFor("me", "target1", bonus: 0));
        Assert.Equal(0, ledger.CommitmentBonusFor("me", "somebody-else", bonus: 0));
    }

    [Fact]
    public void Commitment_bonus_applies_only_to_the_currently_held_target()
    {
        var ledger = new RetargetLedger();
        ledger.RecordRetarget("me", "target1", nowTick: 0);

        Assert.Equal(50, ledger.CommitmentBonusFor("me", "target1", bonus: 50));
        Assert.Equal(0, ledger.CommitmentBonusFor("me", "target2", bonus: 50));
        Assert.Equal(0, ledger.CommitmentBonusFor("someone-else", "target1", bonus: 50));
    }

    [Fact]
    public void Repeat_decay_1000_is_byte_identical()
    {
        var ledger = new RetargetLedger();
        ledger.RecordActionChosen("me", "act.big", nowTick: 0);

        Assert.Equal(1000, ledger.RepeatDecayFor("me", "act.big", nowTick: 1, halfLifeTicks: 0));
        Assert.Equal(1000, ledger.RepeatDecayFor("me", "act.never-chosen", nowTick: 1, halfLifeTicks: 100));
    }

    [Fact]
    public void Repeat_decay_recovers_toward_1000_as_ticks_elapse()
    {
        var ledger = new RetargetLedger();
        ledger.RecordActionChosen("me", "act.big", nowTick: 0);

        var oneHalfLifeLater = ledger.RepeatDecayFor("me", "act.big", nowTick: 100, halfLifeTicks: 100);
        var manyHalfLivesLater = ledger.RepeatDecayFor("me", "act.big", nowTick: 10_000, halfLifeTicks: 100);

        Assert.Equal(500, oneHalfLifeLater);
        Assert.Equal(1000, manyHalfLivesLater);
    }

    [Fact]
    public void Ledger_state_is_battle_scoped()
    {
        var a = new RetargetLedger();
        var b = new RetargetLedger();

        a.RecordRetarget("me", "target1", nowTick: 0);
        a.RecordActionChosen("me", "act.big", nowTick: 0);

        Assert.False(b.TryGetHeld("me", nowTick: 0, retargetLatencyTicks: 1000, stillValid: _ => true, out _));
        Assert.Equal(1000, b.RepeatDecayFor("me", "act.big", nowTick: 1, halfLifeTicks: 100));
    }
}
