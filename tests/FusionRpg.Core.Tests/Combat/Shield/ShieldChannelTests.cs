using FusionRpg.Core.Combat;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Combat.Shield;

/// <summary>
/// Exhaustiveness walk for the four combat.shield.* families — every roster element (and the
/// untyped/none case) resolves through the channel builders, the registry, and the reader
/// without throwing (shield-system-spec.md §2.3/§7).
/// </summary>
public class ShieldChannelTests
{
    [Fact]
    public void Shield_families_are_in_the_combat_catalog()
    {
        Assert.Contains(DerivedStatChannels.CombatShieldCapacityPrefix, DerivedStatChannels.CombatChannelFamilies);
        Assert.Contains(DerivedStatChannels.CombatShieldToughnessPrefix, DerivedStatChannels.CombatChannelFamilies);
        Assert.Contains(DerivedStatChannels.CombatShieldPenPrefix, DerivedStatChannels.CombatChannelFamilies);
        Assert.Contains(DerivedStatChannels.CombatShieldRegenPrefix, DerivedStatChannels.CombatChannelFamilies);
    }

    [Fact]
    public void Shield_channel_ids_registered_for_full_roster()
    {
        var reg = DerivedStatRegistry.CreateDefault();
        foreach (var prefix in new[]
                 {
                     DerivedStatChannels.CombatShieldCapacityPrefix,
                     DerivedStatChannels.CombatShieldToughnessPrefix,
                     DerivedStatChannels.CombatShieldPenPrefix,
                     DerivedStatChannels.CombatShieldRegenPrefix
                 })
        {
            Assert.True(reg.IsKnown($"{prefix}.{ElementRoster.OmniId}"), $"Missing {prefix} omni");
            foreach (var element in ElementRoster.Concrete)
            {
                var id = $"{prefix}.{element.ToElementId()}";
                Assert.True(reg.IsKnown(id), $"Missing shield channel: {id}");
                Assert.Contains(id, DerivedStatChannels.AllCombatChannelIds);
            }
        }
    }

    [Fact]
    public void Reader_resolves_every_element_and_none_without_throwing()
    {
        var snap = ActorDerivedSnapshot.StubNeutral();
        foreach (var element in ElementRoster.Concrete)
        {
            Assert.Equal(0, CombatDerivedReader.ShieldCapacity(snap, element));
            Assert.Equal(0, CombatDerivedReader.ShieldToughness(snap, element));
            Assert.Equal(0, CombatDerivedReader.ShieldPen(snap, element));
            Assert.Equal(0, CombatDerivedReader.ShieldRegen(snap, element));
        }

        // Untyped shield (element = none): omni half only, never throws.
        Assert.Equal(0, CombatDerivedReader.ShieldCapacity(snap, null));
        Assert.Equal(0, CombatDerivedReader.ShieldToughness(snap, null));
        Assert.Equal(0, CombatDerivedReader.ShieldPen(snap, null));
        Assert.Equal(0, CombatDerivedReader.ShieldRegen(snap, null));
    }

    [Fact]
    public void Reader_applies_additive_omni_rule()
    {
        var composer = new DerivedComposer();
        var snap = composer.Compose(new[]
        {
            new DerivedModifier(DerivedStatChannels.CombatShieldPenOmni, DerivedModifierOp.Flat, 5.0),
            new DerivedModifier(DerivedStatChannels.CombatShieldPen(ElementTypeId.Fire), DerivedModifierOp.Flat, 3.0)
        });
        Assert.Equal(8, CombatDerivedReader.ShieldPen(snap, ElementTypeId.Fire));
        Assert.Equal(5, CombatDerivedReader.ShieldPen(snap, ElementTypeId.Ice));   // element half 0
        Assert.Equal(5, CombatDerivedReader.ShieldPen(snap, null));                // omni only
    }
}
