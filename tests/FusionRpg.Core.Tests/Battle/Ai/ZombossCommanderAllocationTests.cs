using FusionRpg.Core.Battle.Ai;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Aptitudes;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Ai;

/// <summary>aura-skill T9b: `ZombossCommanderAllocation` is the first production caller of
/// `ZombossPattern.ToAllocation` — nine authored patterns existed with zero production callers before
/// this. Mirrors `CommanderAllocationSourceTests`' own shape for Dave's half of the same acceptance
/// bullet ("each commander resolves an aptitude allocation").</summary>
public class ZombossCommanderAllocationTests
{
    static AptitudeTuning CommanderRateOneTuning() => AptitudeTuningLoader.Parse("""
        {
          "schemaVersion": 1, "version": 1,
          "grant": { "aptitudePointsPerTheta": 3, "skillPointsPerTheta": 1 },
          "pointEconomy": { "aptitudePointsPerThetaMilliByScope": { "commander": 1, "creatureType": 4, "aspect": 4, "uniqueCreature": 6 }, "respecPrice": 10 }, "guardEconomy": { "flatCommitCost": 50, "absorbDrainSharePermille": 300, "riposteShareCapPermille": 400 }, "mitigation": { "scaleMilli": 1000, "families": ["combat.defense", "combat.dodge", "combat.parry", "combat.block", "combat.absorption", "combat.heal"] },
          "read": { "contest": { "spanPoints": 100.0, "shareExponentMilli": 1000 }, "magnitude": { "shareExponentMilli": 1000 }, "layerWeightMilliByScope": {"commander":1000,"creatureType":1000,"aspect":1000,"uniqueCreature":1000} },
          "recovery": { "scaleMilli": 374, "targetRecoveryShareMilli": 670, "families": ["resource.regen"] },
          "familyRead": { "combat.power": "magnitude" },
          "edges": [ { "channel": "combat.power.omni", "source": "Might", "kMilli": 1000 } ]
        }
        """);

    [Fact]
    public void Constructing_with_an_unknown_pattern_id_throws()
    {
        Assert.Throws<ArgumentException>(() => new ZombossCommanderAllocation("not-a-real-pattern"));
    }

    [Fact]
    public void Setting_an_unknown_pattern_id_throws_and_leaves_the_active_pattern_unchanged()
    {
        var source = new ZombossCommanderAllocation("force-pure");
        Assert.Throws<ArgumentException>(() => source.SetActivePattern("not-a-real-pattern"));
        Assert.Equal("force-pure", source.ActivePatternId);
    }

    [Fact]
    public void Before_any_refresh_resolves_to_empty_allocation()
    {
        var source = new ZombossCommanderAllocation("force-pure");
        var result = source.Resolve(new StatContext());
        Assert.Equal(0, result.PointsAt(AllocationScope.Commander, "Might"));
    }

    [Fact]
    public void After_refresh_resolves_the_active_patterns_shares_scaled_by_the_real_point_budget()
    {
        var tuning = CommanderRateOneTuning();
        var source = new ZombossCommanderAllocation("force-pure");
        source.Refresh(AllocationScope.Commander, theta: 1000, tuning); // budget = PointsFor(Commander, 1000, tuning) = 1000*1 = 1000

        var result = source.Resolve(new StatContext());

        // force-pure: Might=396, Vigor=150, Onslaught=153, Retribution=300 (permille) -- ZombossPattern.
        // ToAllocation's own formula: points = budget * permille / 1000 = 1000 * permille / 1000 = permille.
        Assert.Equal(396, result.PointsAt(AllocationScope.Commander, "Might"));
        Assert.Equal(150, result.PointsAt(AllocationScope.Commander, "Vigor"));
        Assert.Equal(153, result.PointsAt(AllocationScope.Commander, "Onslaught"));
        Assert.Equal(300, result.PointsAt(AllocationScope.Commander, "Retribution"));
    }

    [Fact]
    public void Switching_pattern_and_refreshing_changes_the_resolved_allocation()
    {
        var tuning = CommanderRateOneTuning();
        var source = new ZombossCommanderAllocation("force-pure");
        source.Refresh(AllocationScope.Commander, theta: 1000, tuning);
        var forcePureMight = source.Resolve(new StatContext()).PointsAt(AllocationScope.Commander, "Might");

        source.SetActivePattern("bastion-pure");
        source.Refresh(AllocationScope.Commander, theta: 1000, tuning);
        var bastionPureMight = source.Resolve(new StatContext()).PointsAt(AllocationScope.Commander, "Might");
        var bastionPureFerocity = source.Resolve(new StatContext()).PointsAt(AllocationScope.Commander, "Ferocity");

        Assert.Equal(396, forcePureMight);
        Assert.Equal(0, bastionPureMight); // bastion-pure has no Might share at all
        Assert.Equal(402, bastionPureFerocity); // bastion-pure: Ferocity=402 permille
    }

    [Fact]
    public void Every_authored_pattern_resolves_without_throwing_at_a_real_theta()
    {
        var tuning = CommanderRateOneTuning();
        foreach (var patternId in ZombossPatterns.All)
        {
            var source = new ZombossCommanderAllocation(patternId);
            var exception = Record.Exception(() => source.Refresh(AllocationScope.Commander, theta: 5000, tuning));
            Assert.Null(exception);
        }
    }

    [Fact]
    public void Refresh_takesTheScopeAsAnArgument_notAHardCodedCommanderConstant()
    {
        // species-build-todo.md T4.5: a Zomboss pattern is a named allocation, not a player's commander
        // build -- resolving under CreatureType must land in the CreatureType scope bucket, not Commander's.
        var tuning = CommanderRateOneTuning(); // creatureType rate = 4 (vs commander's 1) -- budget = 1000*4 = 4000
        var source = new ZombossCommanderAllocation("force-pure");
        source.Refresh(AllocationScope.CreatureType, theta: 1000, tuning);

        var result = source.Resolve(new StatContext());
        Assert.Equal(1584, result.PointsAt(AllocationScope.CreatureType, "Might")); // 4000 * 396 / 1000
        Assert.Equal(0, result.PointsAt(AllocationScope.Commander, "Might")); // never leaks into Commander
    }

    [Fact]
    public void BudgetCap_holdsForEveryPatternThroughTheScopeArgumentWiring()
    {
        // Spec test 5, re-asserted at the wiring layer (ZombossPatternTests.cs already proves the math
        // on ZombossPattern.ToAllocation directly) -- this is what makes that property REACHABLE.
        var tuning = CommanderRateOneTuning();
        foreach (var scope in new[] { AllocationScope.Commander, AllocationScope.CreatureType, AllocationScope.Aspect, AllocationScope.UniqueCreature })
        foreach (var patternId in ZombossPatterns.All)
        {
            var source = new ZombossCommanderAllocation(patternId);
            source.Refresh(scope, theta: 7777, tuning);
            var budget = PointBudget.PointsFor(scope, 7777, tuning);
            var spent = source.Resolve(new StatContext()).TotalForScope(scope);
            Assert.True(spent <= budget, $"{patternId}@{scope}: spent {spent} against a budget of {budget}");
        }
    }
}
