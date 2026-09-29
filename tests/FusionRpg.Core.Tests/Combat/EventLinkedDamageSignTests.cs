using FusionRpg.Contracts;
using FusionRpg.Core.Combat;
using Xunit;

namespace FusionRpg.Core.Tests.Combat;

/// <summary>
/// lawn-combat-wire L-N19: <c>eventField:"damage"</c> reads the event's damage MAGNITUDE; the authored
/// <c>multiplierMilli</c> carries the sign. Producers disagree on <c>EffectEventDto.Damage</c>'s sign —
/// lawn <c>EventDrain</c> stamps <c>-|damage|</c>, battle <c>BasicAttack</c> and <c>EffectBag</c> overlay
/// procs stamp a positive value — so a sign-carrying read made <c>fx.overlay_damage</c> damage on the
/// lawn and heal everywhere else.
/// </summary>
public class EventLinkedDamageSignTests
{
    static Dictionary<string, object?> Overlay(int multiplierMilli) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["channel"] = "hp",
        ["amount"] = new Dictionary<string, object?> { ["eventField"] = "damage", ["multiplierMilli"] = multiplierMilli }
    };

    static EffectEventDto Hit(long damage) => new() { Trigger = EffectTriggers.OnDamageDealt, Damage = damage, Tick = 1 };

    [Theory]
    [InlineData(-20)] // lawn EventDrain record shape
    [InlineData(20)]  // battle BasicAttack / EffectBag overlay-proc shape
    public void Damage_atom_deals_damage_whatever_sign_the_producer_used(long producerDamage)
    {
        var packet = DamagePacketBuilder.FromOverlay(Overlay(-1000), Hit(producerDamage));
        Assert.Equal(-20, packet.SignedAmount);
    }

    [Theory]
    [InlineData(-20)]
    [InlineData(20)]
    public void Positive_multiplier_is_a_heal_of_that_share_whatever_sign_the_producer_used(long producerDamage)
    {
        var packet = DamagePacketBuilder.FromOverlay(Overlay(500), Hit(producerDamage));
        Assert.Equal(10, packet.SignedAmount);
    }

    [Fact]
    public void Absent_damage_resolves_zero()
    {
        var packet = DamagePacketBuilder.FromOverlay(Overlay(-1000), new EffectEventDto { Trigger = EffectTriggers.OnDamageDealt, Tick = 1 });
        Assert.Equal(0, packet.SignedAmount);
    }
}
