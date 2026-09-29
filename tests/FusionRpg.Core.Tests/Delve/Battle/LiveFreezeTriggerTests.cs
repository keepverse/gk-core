using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Delve.Battle;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Battle;

/// <summary>
/// D2.16 — the pure half of the freeze trigger: consecutive-timeout counting and the `steer{from,to}`
/// payload shape (spec-delve-battle-profile.md §3, §4a). No threading, no store, no Task -- see
/// `DelveBattleSession`/`DelveBattleSessionManager` (Server layer) for the actual freeze mechanics
/// this decides for.
/// </summary>
public class LiveFreezeTriggerTests
{
    [Fact]
    public void A_player_decision_never_freezes_and_resets_the_count()
    {
        var trigger = new LiveFreezeTrigger();
        Assert.False(trigger.OnDecisionRecorded(DecisionSource.Timeout));
        Assert.False(trigger.OnDecisionRecorded(DecisionSource.Player));
        Assert.Equal(0, trigger.ConsecutiveTimeouts);
    }

    [Fact]
    public void The_third_consecutive_timeout_freezes_matching_BattleSessionRegistrys_own_constant()
    {
        var trigger = new LiveFreezeTrigger();

        Assert.Equal(3, BattleSessionRegistry.MaxConsecutiveTimeouts); // this test's own assumption, stated
        Assert.False(trigger.OnDecisionRecorded(DecisionSource.Timeout));
        Assert.False(trigger.OnDecisionRecorded(DecisionSource.Timeout));
        Assert.True(trigger.OnDecisionRecorded(DecisionSource.Timeout));
        Assert.Equal(3, trigger.ConsecutiveTimeouts);
    }

    [Fact]
    public void A_player_decision_between_timeouts_resets_the_run()
    {
        var trigger = new LiveFreezeTrigger();
        Assert.False(trigger.OnDecisionRecorded(DecisionSource.Timeout));
        Assert.False(trigger.OnDecisionRecorded(DecisionSource.Timeout));
        Assert.False(trigger.OnDecisionRecorded(DecisionSource.Player)); // resets
        Assert.False(trigger.OnDecisionRecorded(DecisionSource.Timeout));
        Assert.False(trigger.OnDecisionRecorded(DecisionSource.Timeout));
        Assert.True(trigger.OnDecisionRecorded(DecisionSource.Timeout)); // three AGAIN, freezes again
    }

    [Fact]
    public void Reset_clears_the_count_for_a_fresh_resumed_session()
    {
        var trigger = new LiveFreezeTrigger();
        trigger.OnDecisionRecorded(DecisionSource.Timeout);
        trigger.OnDecisionRecorded(DecisionSource.Timeout);
        trigger.Reset();
        Assert.Equal(0, trigger.ConsecutiveTimeouts);
        Assert.False(trigger.OnDecisionRecorded(DecisionSource.Timeout));
        Assert.False(trigger.OnDecisionRecorded(DecisionSource.Timeout));
    }

    [Fact]
    public void FreezeAwayPayload_names_the_party_and_a_null_destination()
    {
        var payload = LiveFreezeTrigger.FreezeAwayPayload(partyIndex: 1);
        Assert.Equal(1, payload.From);
        Assert.Null(payload.To);
    }

    [Fact]
    public void SteerPayload_carries_both_ends_of_an_explicit_switch()
    {
        var payload = LiveFreezeTrigger.SteerPayload(from: 0, to: 2);
        Assert.Equal(0, payload.From);
        Assert.Equal(2, payload.To);
    }
}
