using FusionRpg.Core.Stats;
using Xunit;

namespace FusionRpg.Core.Tests.Stats;

/// <summary>
/// E22 (completeness-audit.md finding B1): <c>effect_channel_policy</c> shipped at E16 with zero
/// production readers. <see cref="ChannelPolicyTable"/> gives its one column with an existing
/// consumer — <c>direction</c> — a live read, mirroring <see cref="Combat.Element.ElementTable"/> and
/// <see cref="Effects.Atoms.Power.PowerTables"/>.
/// </summary>
public class ChannelPolicyTableTests
{
    [Fact]
    public void An_empty_table_changes_nothing_direction_falls_through_to_the_code_default()
    {
        using var _ = ChannelPolicyTable.UseScoped(ChannelPolicyTable.Empty);

        Assert.Equal(ChannelDirection.HigherIsBetter, StatChannels.DirectionOf(StatChannels.Atk));
        Assert.Equal(ChannelDirection.LowerIsBetter, StatChannels.DirectionOf(StatChannels.AttackInterval));
    }

    [Fact]
    public void A_stored_direction_overrides_the_code_default()
    {
        var table = new ChannelPolicyTable(
            new Dictionary<string, int> { [StatChannels.Defense] = (int)ChannelDirection.LowerIsBetter });
        using var _ = ChannelPolicyTable.UseScoped(table);

        Assert.Equal(ChannelDirection.LowerIsBetter, StatChannels.DirectionOf(StatChannels.Defense));
        // Everything not named in the row is untouched.
        Assert.Equal(ChannelDirection.HigherIsBetter, StatChannels.DirectionOf(StatChannels.Atk));
    }

    [Fact]
    public void IsLowerBetter_reads_through_the_same_table()
    {
        var table = new ChannelPolicyTable(
            new Dictionary<string, int> { [StatChannels.Atk] = (int)ChannelDirection.LowerIsBetter });
        using var _ = ChannelPolicyTable.UseScoped(table);

        Assert.True(StatChannels.IsLowerBetter(StatChannels.Atk));
    }

    [Fact]
    public void UseScoped_restores_the_outer_table_on_dispose()
    {
        var outer = new ChannelPolicyTable(
            new Dictionary<string, int> { [StatChannels.Atk] = (int)ChannelDirection.LowerIsBetter });
        using (ChannelPolicyTable.UseScoped(outer))
        {
            using (ChannelPolicyTable.UseScoped(ChannelPolicyTable.Empty))
                Assert.Equal(ChannelDirection.HigherIsBetter, StatChannels.DirectionOf(StatChannels.Atk));

            Assert.Equal(ChannelDirection.LowerIsBetter, StatChannels.DirectionOf(StatChannels.Atk));
        }
    }

    [Fact]
    public void Direction_2_and_above_is_treated_as_higher_is_better_not_thrown()
    {
        // The store-side validator (RpgStore.ChannelPolicy.cs) is what refuses an out-of-range
        // direction at write time — this constructor takes whatever GetChannelPolicies() already
        // returned, and a defensive clamp here is the second half of that guarantee, not a duplicate.
        var table = new ChannelPolicyTable(new Dictionary<string, int> { ["weird"] = 7 });
        Assert.True(table.TryGetDirection("weird", out var dir));
        Assert.Equal(ChannelDirection.HigherIsBetter, dir);
    }
}
