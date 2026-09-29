using System.Linq;
using FusionRpg.Contracts;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Progression;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>Dedicated unique-specimen expedition progression remains isolated from the empire species
/// fallback. The game-closed path awards the specimen only; species progression is lawn-general-only.</summary>
public class SpeciesExpeditionTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public SpeciesExpeditionTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    // Same "pick one from the real roster" convention as ExpeditionRewardApplyTests, decoupled from
    // any specific compiled id so a roster edit doesn't rot this test.
    static readonly CreatureSpeciesDef CatalogSpecies = CreatureSpeciesCatalog.All.First(s => s.Side == "zombie");

    CreatureMintSpec Spec() => new()
    {
        SpeciesId = CatalogSpecies.SpeciesId,
        Side = "zombie",
        GameTypeId = CatalogSpecies.GameTypeId,
        Rarity = "chaff",
        Variant = "normal",
        ElementPrimary = "fire",
        TraitIds = new List<string> { "swift" },
        Origin = "expedition"
    };

    [Fact]
    public void Expedition_win_levels_only_the_specimen_with_no_lawn_run_at_all()
    {
        // The game-closed proof itself: nothing in this test ever calls AppendPvzActivityFact or
        // InsertEvent — the ONLY progression source touched is the expedition reward path.
        var (specimen, _) = _store.MintCreature(1, Spec());
        var instanceId = specimen.Actor.InstanceId;
        var (_, _, row) = _store.DispatchExpedition(1, "species-exp-1", "scout-30m", new[] { instanceId }, 1);

        var rewards = new RpgStore.ExpeditionRewardApply(
            EventSouls: 0,
            Materials: Array.Empty<(string, long)>(),
            SpecimenXp: new[] { (instanceId, 30L) },
            WildMints: Array.Empty<CreatureMintSpec>());

        var applied = _store.ApplyExpeditionRewards(row!.Id, 1, ExpeditionStates.Collected, rewards);
        Assert.True(applied.Applied);

        var species = _store.GetRpgActor(1, RpgActorKinds.Species, CatalogSpecies.CreatureTypeId);
        Assert.Null(species);
        var after = _store.ListCreatureRoster(1).Items.Single(s => s.Profile.InstanceId == instanceId).Actor;
        Assert.Equal(30, after.Xp);
    }

    [Fact]
    public void Dedicated_specimen_award_shares_the_reward_transaction()
    {
        // Mirrors ExpeditionRewardApplyTests' own "Bad_material_in_rewards_applies_nothing": when the
        // WHOLE reward apply throws (bad material id), the specimen must not gain anything.
        var (specimen, _) = _store.MintCreature(1, Spec());
        var instanceId = specimen.Actor.InstanceId;
        var (_, _, row) = _store.DispatchExpedition(1, "species-exp-2", "scout-30m", new[] { instanceId }, 1);

        Assert.ThrowsAny<Exception>(() => _store.ApplyExpeditionRewards(
            row!.Id, 1, ExpeditionStates.Collected,
            new RpgStore.ExpeditionRewardApply(
                EventSouls: 0,
                Materials: new[] { ("not.a.real.material", 1L) },
                SpecimenXp: new[] { (instanceId, 30L) },
                WildMints: Array.Empty<CreatureMintSpec>())));

        Assert.Null(_store.GetRpgActor(1, RpgActorKinds.Species, CatalogSpecies.CreatureTypeId));
        var specimenXp = _store.ListCreatureRoster(1).Items.Single(s => s.Profile.InstanceId == instanceId).Actor.Xp;
        Assert.Equal(0, specimenXp);
    }

    [Fact]
    public void Replayed_collect_never_double_pays_the_species_either()
    {
        // Same exactly-once gate ExpeditionRewardApplyTests proves for souls/specimen xp/materials --
        // this test proves the species award inherits it too, since it rides the same transaction.
        var (specimen, _) = _store.MintCreature(1, Spec());
        var instanceId = specimen.Actor.InstanceId;
        var (_, _, row) = _store.DispatchExpedition(1, "species-exp-3", "scout-30m", new[] { instanceId }, 1);

        var rewards = new RpgStore.ExpeditionRewardApply(
            EventSouls: 0, Materials: Array.Empty<(string, long)>(),
            SpecimenXp: new[] { (instanceId, 30L) }, WildMints: Array.Empty<CreatureMintSpec>());

        _store.ApplyExpeditionRewards(row!.Id, 1, ExpeditionStates.Collected, rewards);
        var retry = _store.ApplyExpeditionRewards(row.Id, 1, ExpeditionStates.Collected, rewards);
        Assert.False(retry.Applied);

        var species = _store.GetRpgActor(1, RpgActorKinds.Species, CatalogSpecies.CreatureTypeId);
        Assert.Null(species); // no empire fallback award on a dedicated expedition source
    }
}
