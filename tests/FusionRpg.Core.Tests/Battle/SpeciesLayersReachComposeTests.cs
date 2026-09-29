using FusionRpg.Core.Battle;
using FusionRpg.Core.Creatures.Layers;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Stats.Derived.Subsystems;
using Xunit;

namespace FusionRpg.Core.Tests.Battle;

/// <summary>species-progression module 6 (`species-layer-delivery`) step 6.2 (SP6.7) — mirrors
/// `SpeciesTermReachesComposeTests`' own shape exactly (the 2b/`Aptitude` sibling proof), for 1a/1b
/// `BattleHubInputs.SpeciesLayers` instead.</summary>
public class SpeciesLayersReachComposeTests
{
    static readonly ProjectedLayerRow BaseRow = new(
        "resource.max.hp", DerivedModifierOp.Flat, new LayerValue.Fixed(500), "species-base:melon-pult");
    static readonly ProjectedLayerRow ModRow = new(
        "combat.power.omni", DerivedModifierOp.Flat, new LayerValue.Fixed(40), "species-player:melon-pult:fusion-pick");

    static BattleActorSetup Actor(BattleHubInputs? inputs) => new()
    {
        Key = "squad:0",
        Side = "squad",
        SpeciesId = "fumeshroom",
        TypeId = 10_001,
        Level = 6,
        MaxHp = BattleRuleset.BaseHp(6),
        Atk = BattleRuleset.BaseAtk(6),
        Defense = BattleRuleset.BaseDefense(6),
        HubInputs = inputs,
    };

    [Fact]
    public void SpeciesLayers_reaching_the_compose_changes_what_it_composes()
    {
        var without = BattleHubCompose.Compose(Actor(new BattleHubInputs()));
        var with = BattleHubCompose.Compose(Actor(new BattleHubInputs { SpeciesLayers = new[] { BaseRow, ModRow } }));

        // seedResourceBaseline already seeds a nonzero resource.max.hp for every battle actor
        // (BattleRuleset.BaseResourceMax), so the species term's own contribution is asserted as the
        // DELTA it adds on top, not an absolute value.
        Assert.Equal(500.0,
            with.Get(DerivedStatChannels.ResourceMax("hp"), 0) - without.Get(DerivedStatChannels.ResourceMax("hp"), 0),
            6);
        Assert.NotEqual(
            without.Get(DerivedStatChannels.CombatPowerOmni, 0),
            with.Get(DerivedStatChannels.CombatPowerOmni, 0));
    }

    [Fact]
    public void SpeciesLayers_composesAtTheSameThetaEveryOtherHubInputUses()
    {
        // Theta comes from FixedPowerIndexProvider(setup.ThetaActor ?? setup.Level) -- the SAME index
        // every other Hub contribution in this compose already reads. A LadderMicro row proves it: a
        // higher setup Theta must move the resolved value.
        var low = BattleHubCompose.Compose(new BattleActorSetup
        {
            Key = "squad:0", Side = "squad", SpeciesId = "fumeshroom", TypeId = 10_001, Level = 1,
            MaxHp = BattleRuleset.BaseHp(1), Atk = BattleRuleset.BaseAtk(1), Defense = BattleRuleset.BaseDefense(1),
            HubInputs = new BattleHubInputs { SpeciesLayers = new[] { new ProjectedLayerRow(
                "resource.max.hp", DerivedModifierOp.Flat, new LayerValue.LadderMicro(2_200_000), "species-base:x") } },
        });
        var high = BattleHubCompose.Compose(new BattleActorSetup
        {
            Key = "squad:0", Side = "squad", SpeciesId = "fumeshroom", TypeId = 10_001, Level = 74,
            MaxHp = BattleRuleset.BaseHp(74), Atk = BattleRuleset.BaseAtk(74), Defense = BattleRuleset.BaseDefense(74),
            HubInputs = new BattleHubInputs { SpeciesLayers = new[] { new ProjectedLayerRow(
                "resource.max.hp", DerivedModifierOp.Flat, new LayerValue.LadderMicro(2_200_000), "species-base:x") } },
        });

        Assert.True(high.Get(DerivedStatChannels.ResourceMax("hp"), 0) > low.Get(DerivedStatChannels.ResourceMax("hp"), 0));
    }

    [Fact]
    public void The_species_layer_rows_arrive_through_ActorHub_carrying_their_own_GG49_grammar_ids()
    {
        // The acceptance's own wording: "read back through ResolveDerivedWithContributions". Mirrors
        // SpeciesTermReachesComposeTests' own last test exactly, for SpeciesLayerSubsystem instead of
        // AptitudeSubsystem -- the registered seam, not a private fold beside the Hub.
        var hub = ActorHubBootstrap.CreateDefault(speciesLayers: _ => new[] { BaseRow, ModRow });
        var (_, bag) = hub.ResolveDerivedWithContributions(
            new StatContextFactory().ForBattle(
                "squad:0", new EntityBaseline { MaxHp = 100, Atk = 10 }, side: StatSide.Plant, typeId: 10_001));

        var maxHpSources = bag.ContributionsFor(DerivedStatChannels.ResourceMax("hp")).Select(c => c.SourceId).ToList();
        var powerSources = bag.ContributionsFor(DerivedStatChannels.CombatPowerOmni).Select(c => c.SourceId).ToList();

        Assert.Contains("species-base:melon-pult", maxHpSources);
        Assert.Contains("species-player:melon-pult:fusion-pick", powerSources);
    }
}
