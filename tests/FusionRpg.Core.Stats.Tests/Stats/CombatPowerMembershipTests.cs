using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Stats;

/// <summary>
/// `combat-membership` (T8, Q5 closed) — <see cref="CombatPowerMembership.Includes"/>'s own locked
/// include/exclude table, proven case by case rather than assumed from the doc comment.
/// </summary>
public class CombatPowerMembershipTests
{
    [Fact]
    public void Every_AllCombatChannelIds_entry_is_included()
    {
        // The whole element-typed combat family, not a sample -- a single miss here is the exact
        // "IsCombatChannel alone would still work" false confidence this predicate exists to avoid
        // testing away.
        foreach (var channel in DerivedStatChannels.AllCombatChannelIds)
            Assert.True(CombatPowerMembership.Includes(channel), $"expected combat channel included: {channel}");
    }

    [Theory]
    [InlineData("skill.cooldown.defense")]
    [InlineData("skill.cooldown.attack")]
    [InlineData("skill.effectiveness.attack")]
    [InlineData("skill.effectiveness.support")]
    public void SkillCooldownAndEffectiveness_areIncluded(string channel) =>
        Assert.True(CombatPowerMembership.Includes(channel));

    [Theory]
    [InlineData("status.power.omni")]
    [InlineData("status.power.dot")]
    [InlineData("status.power.some-future-status-id")] // open-prefix: any suffix, per DerivedStatRegistry.TryResolveChannel
    [InlineData("status.resist.omni")]
    [InlineData("status.resist.cc")]
    public void StatusPowerAndResist_areIncluded_asOpenPrefixFamilies(string channel) =>
        Assert.True(CombatPowerMembership.Includes(channel));

    [Theory]
    [InlineData("progression.power")]
    [InlineData("progression.bonus.atk")]
    [InlineData("progression.bonus.maxHp")]
    [InlineData("resource.max.hp")]
    [InlineData("resource.regen.stamina")]
    [InlineData("status.duration.omni")] // a different axis from power/resist -- not in the locked include list
    [InlineData("status.intensity.dot")]
    [InlineData("status.durationReduction.cc")]
    public void ProgressionResourceAndOtherStatusFamilies_areExcluded(string channel) =>
        Assert.False(CombatPowerMembership.Includes(channel));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void EmptyOrNullChannel_isExcluded_neverThrows(string? channel) =>
        Assert.False(CombatPowerMembership.Includes(channel!));

    [Fact]
    public void A_seventh_elements_combat_channels_are_included_with_no_code_change()
    {
        // Same claim ElementRosterDataTests.A_seventh_element_generates_its_twenty_eight_channels_with_no_code_change
        // makes for AllCombatChannelIds itself: this predicate reads that same live generation, so a
        // roster swap moves it too, automatically.
        var shipped = ElementTable.Shipped();
        var withVoid = shipped.Elements.Append(new ElementRow("void", "Void", 6)).ToList();
        using var _ = ElementTable.UseScoped(new ElementTable(withVoid, shipped.CombatRows, shipped.ShieldRows));

        Assert.True(CombatPowerMembership.Includes("combat.power.void"));
        Assert.True(CombatPowerMembership.Includes("combat.defense.void"));
    }

    [Fact]
    public void Filter_keeps_only_included_members()
    {
        var rows = new[]
        {
            (Channel: DerivedStatChannels.CombatPowerOmni, Amount: 10L),
            (Channel: "progression.power", Amount: 999L),
            (Channel: "skill.cooldown.attack", Amount: 5L),
            (Channel: "resource.max.hp", Amount: 500L),
        };

        var kept = CombatPowerMembership.Filter(rows, r => r.Channel).ToList();

        Assert.Equal(2, kept.Count);
        Assert.Contains(kept, r => r.Channel == DerivedStatChannels.CombatPowerOmni);
        Assert.Contains(kept, r => r.Channel == "skill.cooldown.attack");
    }
}
