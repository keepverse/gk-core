using FusionRpg.Core.Combat;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Combat;

public class CombatDerivedReaderTests
{
    static ActorDerivedSnapshot Snap(params (string channel, double value)[] values)
    {
        var snap = ActorDerivedSnapshot.StubNeutral();
        return snap.Overlay(values.Select(v => new KeyValuePair<string, double>(v.channel, v.value)));
    }

    [Fact]
    public void Power_omni_plus_typed()
    {
        var snap = Snap(
            (DerivedStatChannels.CombatPowerOmni, 10),
            (DerivedStatChannels.CombatPowerFire, 40));
        Assert.Equal(50, CombatDerivedReader.Power(snap, ElementTypeId.Fire));
    }

    [Fact]
    public void Defense_omni_plus_typed()
    {
        var snap = Snap(
            (DerivedStatChannels.CombatDefenseOmni, 5),
            (DerivedStatChannels.CombatDefenseIce, 15));
        Assert.Equal(20, CombatDerivedReader.Defense(snap, ElementTypeId.Ice));
    }

    [Fact]
    public void Accuracy_crit_dodge_read_omni_plus_typed()
    {
        var snap = Snap(
            (DerivedStatChannels.CombatAccuracyOmni, 1),
            (DerivedStatChannels.CombatAccuracyAir, 2),
            (DerivedStatChannels.CombatDodgeOmni, 3),
            (DerivedStatChannels.CombatDodgeEarth, 4),
            (DerivedStatChannels.CombatCritRateOmni, 5),
            (DerivedStatChannels.CombatCritRateFire, 6),
            (DerivedStatChannels.CombatCritResistOmni, 7),
            (DerivedStatChannels.CombatCritResistIce, 8),
            (DerivedStatChannels.CombatCritDamageOmni, 9),
            (DerivedStatChannels.CombatCritDamageEarth, 10),
            (DerivedStatChannels.CombatCritResistDamageOmni, 11),
            (DerivedStatChannels.CombatCritResistDamageAir, 12));
        Assert.Equal(3, CombatDerivedReader.Accuracy(snap, ElementTypeId.Air));
        Assert.Equal(7, CombatDerivedReader.Dodge(snap, ElementTypeId.Earth));
        Assert.Equal(11, CombatDerivedReader.CritRate(snap, ElementTypeId.Fire));
        Assert.Equal(15, CombatDerivedReader.CritResist(snap, ElementTypeId.Ice));
        Assert.Equal(19, CombatDerivedReader.CritDamage(snap, ElementTypeId.Earth));
        Assert.Equal(23, CombatDerivedReader.CritResistDamage(snap, ElementTypeId.Air));
    }
}
