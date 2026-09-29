using FusionRpg.Core.Power;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Stats.Derived.Subsystems;
using Xunit;

namespace FusionRpg.Core.Tests.ActorHub;

/// <summary>aura-skill T18 (GG-49): `ActorHub.ResolveDerivedWithContributions` is what makes "why did
/// my attack drop" answerable for the first time — the same compose `ResolveDerived` already runs,
/// with per-source provenance retained rather than discarded.</summary>
public class ResolveDerivedWithContributionsTests
{
    static AptitudeTuning MightOnlyTuning() => AptitudeTuningLoader.Parse("""
        {
          "schemaVersion": 1, "version": 1,
          "grant": { "aptitudePointsPerTheta": 3, "skillPointsPerTheta": 1 },
          "pointEconomy": { "aptitudePointsPerThetaMilliByScope": { "commander": 3, "creatureType": 4, "aspect": 4, "uniqueCreature": 6 }, "respecPrice": 10 }, "guardEconomy": { "flatCommitCost": 50, "absorbDrainSharePermille": 300, "riposteShareCapPermille": 400 }, "mitigation": { "scaleMilli": 1000, "families": ["combat.defense", "combat.dodge", "combat.parry", "combat.block", "combat.absorption", "combat.heal"] },
          "read": { "contest": { "spanPoints": 100.0, "shareExponentMilli": 1000 }, "magnitude": { "shareExponentMilli": 1000 }, "layerWeightMilliByScope": {"commander":1000,"creatureType":1000,"aspect":1000,"uniqueCreature":1000} },
          "recovery": { "scaleMilli": 374, "targetRecoveryShareMilli": 670, "families": ["resource.regen"] },
          "familyRead": { "combat.power": "magnitude" },
          "edges": [ { "channel": "combat.power.omni", "source": "Might", "kMilli": 2200 } ]
        }
        """);

    static PowerLadder Ladder() => new(PowerTuningHub.Tuning);

    [Fact]
    public void The_snapshot_matches_ResolveDerived_exactly_this_is_not_a_second_compose()
    {
        var stats = StatSystemBootstrap.CreateDefault();
        var hub = new FusionRpg.Core.Stats.Derived.ActorHub(stats);
        hub.Register(new AptitudeSubsystem(
            MightOnlyTuning(), Ladder(),
            allocation: _ => AptitudeAllocation.Single(AllocationScope.Commander, "Might", 100)));
        var ctx = stats.Contexts.ForPlant("P1", new EntityBaseline { Hp = 100, MaxHp = 100, Atk = 10 });

        var plain = hub.ResolveDerived(ctx);
        var (withContrib, _) = hub.ResolveDerivedWithContributions(ctx);

        Assert.Equal(plain.Get("combat.power.omni"), withContrib.Get("combat.power.omni"));
    }

    [Fact]
    public void GG_49_the_contributions_name_the_real_source_of_a_nonzero_channel()
    {
        var stats = StatSystemBootstrap.CreateDefault();
        var hub = new FusionRpg.Core.Stats.Derived.ActorHub(stats);
        hub.Register(new AptitudeSubsystem(
            MightOnlyTuning(), Ladder(),
            allocation: _ => AptitudeAllocation.Single(AllocationScope.Commander, "Might", 100)));
        var ctx = stats.Contexts.ForPlant("P1", new EntityBaseline { Hp = 100, MaxHp = 100, Atk = 10 });

        var (snapshot, contributions) = hub.ResolveDerivedWithContributions(ctx);

        var channelValue = snapshot.Get("combat.power.omni");
        Assert.True(channelValue > 0);
        var sources = contributions.ContributionsFor("combat.power.omni");
        var contribution = Assert.Single(sources);
        Assert.Equal("aptitude.Might", contribution.SourceId);
        Assert.Equal(channelValue, contribution.Value, 6); // the one contribution accounts for the whole channel value
        Assert.Equal(DerivedModifierOp.Flat, contribution.Op); // FlatSum reads only Flat
    }

    [Fact]
    public void An_empty_hub_resolves_with_no_contributions_never_throws()
    {
        var stats = StatSystemBootstrap.CreateDefault();
        var hub = new FusionRpg.Core.Stats.Derived.ActorHub(stats);
        var ctx = stats.Contexts.ForPlant("P1", new EntityBaseline { Hp = 100, MaxHp = 100, Atk = 10 });

        var (snapshot, contributions) = hub.ResolveDerivedWithContributions(ctx);

        Assert.Equal(0.0, snapshot.Get("combat.power.omni", 0.0));
        Assert.Empty(contributions.ContributionsFor("combat.power.omni"));
    }
}
