using FusionRpg.Core.Delve.Difficulty;
using FusionRpg.Core.Dungeon.Tuning;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Difficulty;

/// <summary>
/// D1.21 (gate half) — decision 1: some rungs and above permanently kill, and it is tunable
/// (spec-difficulty-ladder.md §4). The shipped default is `very-hard` (rung 5); a domain may only
/// RAISE the gate, never lower it.
/// </summary>
public class PermadeathGateTests
{
    static DungeonTuning Dungeon => DungeonTuningHub.Tuning;
    static readonly DomainThetaInputs NoOverride = new(EntranceBand: 3, IsOnceEntry: false);

    [Fact]
    public void The_shipped_default_gate_is_very_hard()
    {
        Assert.Equal("very-hard", Dungeon.Domain.PermadeathFromRung);
    }

    [Theory]
    [InlineData("very-easy", false)]
    [InlineData("easy", false)]
    [InlineData("medium", false)]
    [InlineData("hard", false)]
    [InlineData("very-hard", true)]
    [InlineData("nightmare", true)]
    [InlineData("impossible", true)]
    public void Applies_at_and_above_the_default_gate_only(string rungId, bool expected)
    {
        Assert.Equal(expected, PermadeathGate.Applies(Dungeon, NoOverride, rungId));
    }

    [Fact]
    public void A_domain_override_can_raise_the_gate_above_the_default()
    {
        var raised = new DomainThetaInputs(EntranceBand: 3, IsOnceEntry: false, PermadeathFromRungOverride: "hell");
        Assert.False(PermadeathGate.Applies(Dungeon, raised, "very-hard")); // below the raised gate now
        Assert.False(PermadeathGate.Applies(Dungeon, raised, "nightmare"));
        Assert.True(PermadeathGate.Applies(Dungeon, raised, "hell"));
        Assert.True(PermadeathGate.Applies(Dungeon, raised, "impossible"));
    }

    [Fact]
    public void ValidateOverride_accepts_a_gate_at_or_above_the_default()
    {
        var ex = Record.Exception(() => PermadeathGate.ValidateOverride(Dungeon, "very-hard"));
        Assert.Null(ex);
        ex = Record.Exception(() => PermadeathGate.ValidateOverride(Dungeon, "impossible"));
        Assert.Null(ex);
    }

    [Fact]
    public void ValidateOverride_accepts_no_override_at_all()
    {
        var ex = Record.Exception(() => PermadeathGate.ValidateOverride(Dungeon, null));
        Assert.Null(ex);
    }

    [Fact]
    public void ValidateOverride_rejects_a_gate_below_the_default()
    {
        // The default is very-hard (ordinal 5); medium (ordinal 3) would make a domain SAFER than
        // the difficulty default, which decision 1 forbids -- a domain may only raise the gate.
        var ex = Assert.Throws<InvalidOperationException>(() => PermadeathGate.ValidateOverride(Dungeon, "medium"));
        Assert.Contains("permadeathFromRung", ex.Message);
    }
}
