using FusionRpg.Contracts;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>creature-lawn-deploy T1.4: owner-confirmed 2026-09-06 — a creature's own species side/typeId
/// pass through unchanged for deploy (PvZ's own engine makes any spawned zombie hostile to the plant
/// side unless natively hypnotized). `PlantAvatar` species deploy normally. `HypnoAlly` species are a
/// NAMED refusal (`"deploy.hypno-ally-not-implemented"`) until the native hypnotize operation this
/// codebase already investigated once (content-stack program) and found genuinely hard is solved for
/// real — matching that program's own "named refusal over an unverified guess" discipline.</summary>
public class CreatureLawnDeployHypnoRefusalTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public CreatureLawnDeployHypnoRefusalTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    // Queried by DeployMode, not a hardcoded id — this test project's own global bootstrap configures
    // CreatureSpeciesCatalog from the compiled default (not the anchor-pipeline corpus), and a hardcoded
    // anchor-pipeline id (e.g. "armoredimpzombie") does not exist there. Both real DeployMode values
    // exist in the compiled catalog too (the field is shared), so this is robust either way.
    static readonly CreatureSpeciesDef HypnoSpecies = CreatureSpeciesCatalog.All
        .First(s => s.DeployMode == CreatureDeployMode.HypnoAlly && s.Acquisition != CreatureAcquisition.CaptureOnly);
    static readonly CreatureSpeciesDef AvatarSpecies = CreatureSpeciesCatalog.All
        .First(s => s.DeployMode == CreatureDeployMode.PlantAvatar && s.Acquisition != CreatureAcquisition.CaptureOnly);

    string Mint(CreatureSpeciesDef species)
    {
        var (specimen, _) = _store.MintCreature(1, new CreatureMintSpec
        {
            SpeciesId = species.SpeciesId,
            Side = species.Side,
            GameTypeId = species.GameTypeId,
            Rarity = species.BaseRarity.ToId(),
            Variant = "normal",
            ElementPrimary = species.ElementPrimary.ToElementId(),
            ElementSecondary = species.ElementSecondary?.ToElementId(),
            TraitIds = species.TraitPool.Count > 0 ? new List<string> { species.TraitPool[0] } : new List<string>(),
            Origin = "summon"
        });
        return specimen.Actor.InstanceId;
    }

    [Fact]
    public void Confirms_the_real_committed_corpus_has_both_deploy_modes_under_test()
    {
        Assert.Equal(CreatureDeployMode.HypnoAlly, HypnoSpecies.DeployMode);
        Assert.Equal(CreatureDeployMode.PlantAvatar, AvatarSpecies.DeployMode);
    }

    [Fact]
    public void A_hypno_ally_species_refuses_deploy_with_a_named_reason()
    {
        var id = Mint(HypnoSpecies);

        var deploy = _store.TryBeginUniqueDeploy(id, "deploy-hypno-1");

        Assert.False(deploy.Ok);
        Assert.Equal("deploy.hypno-ally-not-implemented", deploy.Reason);
        Assert.False(deploy.Queued);
        Assert.Equal(UniqueActorPhases.Roster, _store.GetUniqueActor(id)!.Phase); // untouched, not half-deployed
    }

    [Fact]
    public void A_plant_avatar_species_deploys_normally_side_and_type_unchanged()
    {
        var id = Mint(AvatarSpecies);

        var deploy = _store.TryBeginUniqueDeploy(id, "deploy-avatar-1");

        Assert.True(deploy.Ok, deploy.Reason);
        Assert.True(deploy.Queued);
        Assert.Equal(AvatarSpecies.Side, deploy.Actor!.Side);
        Assert.Equal(AvatarSpecies.GameTypeId, deploy.Actor.TypeId);
    }
}
