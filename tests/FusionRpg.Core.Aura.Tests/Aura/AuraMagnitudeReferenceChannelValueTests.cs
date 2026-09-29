using FusionRpg.Core.Aura;
using FusionRpg.Core.Stats.Aptitudes;
using Xunit;

namespace FusionRpg.Core.Tests.Aura;

/// <summary>
/// backlog-clear AU2: <see cref="AuraMagnitude.ReferenceChannelValue"/>, the even-split reference the
/// twelve `world-buff.aura-*` seed containers' `externalRef` amounts resolve to.
/// </summary>
public class AuraMagnitudeReferenceChannelValueTests
{
    static AuraTuning Rung7To10() => new(new Dictionary<int, long>
    {
        [7] = 5359, [8] = 7090, [9] = 9379, [10] = 12407,
    }, MaxActiveAuras: 1);

    static AptitudeTuning LinearGammaTuning() => AptitudeTuningLoader.Parse("""
        {
          "schemaVersion": 1, "version": 1,
          "grant": { "aptitudePointsPerTheta": 3, "skillPointsPerTheta": 1 },
          "pointEconomy": { "aptitudePointsPerThetaMilliByScope": { "commander": 1, "creatureType": 4, "aspect": 4, "uniqueCreature": 6 }, "respecPrice": 10 }, "guardEconomy": { "flatCommitCost": 50, "absorbDrainSharePermille": 300, "riposteShareCapPermille": 400 }, "mitigation": { "scaleMilli": 1000, "families": ["combat.defense", "combat.dodge", "combat.parry", "combat.block", "combat.absorption", "combat.heal"] },
          "read": { "contest": { "spanPoints": 100.0, "shareExponentMilli": 1000 }, "magnitude": { "shareExponentMilli": 1000 } },
          "recovery": { "scaleMilli": 374, "targetRecoveryShareMilli": 670, "families": ["resource.regen"] },
          "familyRead": { "combat.power": "magnitude" },
          "edges": [ { "channel": "combat.power.omni", "source": "Might", "kMilli": 1000 } ]
        }
        """);

    [Fact]
    public void A_single_channel_aura_gets_the_whole_reference_total()
    {
        var total = AuraMagnitude.Compute(AuraTuning.MinRung, 1.0, 680, Rung7To10(), LinearGammaTuning());
        var value = AuraMagnitude.ReferenceChannelValue(0, 1, 680, Rung7To10(), LinearGammaTuning());
        Assert.Equal(total, value);
    }

    [Fact]
    public void Two_channels_split_evenly_and_conserve_the_total()
    {
        var total = AuraMagnitude.Compute(AuraTuning.MinRung, 1.0, 680, Rung7To10(), LinearGammaTuning());
        var a = AuraMagnitude.ReferenceChannelValue(0, 2, 680, Rung7To10(), LinearGammaTuning());
        var b = AuraMagnitude.ReferenceChannelValue(1, 2, 680, Rung7To10(), LinearGammaTuning());

        Assert.Equal(total, a + b); // budget conservation -- no remainder silently dropped
        Assert.True(Math.Abs(a - b) <= 1); // "even" means within one unit, not literally identical
    }

    [Fact]
    public void Uses_the_floor_rung_not_the_ceiling()
    {
        // MinRung (7) is the documented reference point -- this pins that choice as a regression, not
        // a coincidence: swapping to MaxRung would silently change every aura's shipped number.
        var atMin = AuraMagnitude.Compute(AuraTuning.MinRung, 1.0, 680, Rung7To10(), LinearGammaTuning());
        var atMax = AuraMagnitude.Compute(AuraTuning.MaxRung, 1.0, 680, Rung7To10(), LinearGammaTuning());
        Assert.NotEqual(atMin, atMax);

        var value = AuraMagnitude.ReferenceChannelValue(0, 1, 680, Rung7To10(), LinearGammaTuning());
        Assert.Equal(atMin, value);
    }

    [Fact]
    public void Changing_the_rung_tuning_changes_the_shipped_value_no_hand_picked_constant()
    {
        var baseline = AuraMagnitude.ReferenceChannelValue(0, 1, 680, Rung7To10(), LinearGammaTuning());

        var doubledFloor = new AuraTuning(new Dictionary<int, long>
        {
            [7] = 5359 * 2, [8] = 7090, [9] = 9379, [10] = 12407,
        }, MaxActiveAuras: 1);
        var retuned = AuraMagnitude.ReferenceChannelValue(0, 1, 680, doubledFloor, LinearGammaTuning());

        Assert.Equal(baseline * 2, retuned); // a tuning-file change alone moves the shipped number
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Refuses_a_non_positive_channel_count(int count)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AuraMagnitude.ReferenceChannelValue(0, count, 680, Rung7To10(), LinearGammaTuning()));
    }

    [Fact]
    public void Refuses_an_out_of_range_channel_index()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AuraMagnitude.ReferenceChannelValue(2, 2, 680, Rung7To10(), LinearGammaTuning()));
    }
}
