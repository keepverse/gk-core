using FusionRpg.Core.Commanders;
using FusionRpg.Core.Saves;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>zomboss-deploy-ai T3.4, widened by save-identity SE4.22 ("Zomboss stops being a player
/// row") — the store-side half of the deploy wiring: minting a fresh specimen owned by a save's
/// empire via the real `MintCreature` primitive, with no separate "Zomboss player" row.</summary>
[Trait("VerificationId", "data.zomboss-deploy")]
public class ZombossDeployStoreTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public ZombossDeployStoreTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    static readonly FusionRpg.Core.Creatures.CreatureSpeciesDef CatalogSpecies =
        FusionRpg.Core.Creatures.CreatureSpeciesCatalog.All.First(s =>
            s.DeployMode != FusionRpg.Core.Creatures.CreatureDeployMode.HypnoAlly);

    [Fact]
    public void MintForEmpire_mints_a_specimen_owned_by_that_saves_zomboss_empire_not_the_human()
    {
        var owner = new EmpireRef(new SaveId(1), EmpireId.Zomboss);

        var specimen = _store.MintForEmpire(owner, CatalogSpecies.SpeciesId, seed: 12345);

        Assert.Equal(1, specimen.Actor.PlayerId);
        Assert.Equal(CatalogSpecies.SpeciesId, specimen.Profile.SpeciesId);
        Assert.True(_store.OwnsSpecimenForTest(owner, specimen.Actor.InstanceId));
        Assert.False(_store.OwnsSpecimenForTest(
            new EmpireRef(new SaveId(1), _store.HumanEmpireOf(1)), specimen.Actor.InstanceId));
    }

    [Fact]
    public void MintForEmpire_is_deterministic_same_seed_same_traits()
    {
        var traitSpecies = FusionRpg.Core.Creatures.CreatureSpeciesCatalog.All.First(s =>
            s.DeployMode != FusionRpg.Core.Creatures.CreatureDeployMode.HypnoAlly && s.TraitPool.Count > 0);
        var owner = new EmpireRef(new SaveId(1), EmpireId.Zomboss);

        var first = _store.MintForEmpire(owner, traitSpecies.SpeciesId, seed: 999);
        var second = _store.MintForEmpire(owner, traitSpecies.SpeciesId, seed: 999);

        Assert.Equal(first.Profile.TraitIds.OrderBy(t => t), second.Profile.TraitIds.OrderBy(t => t));
    }

    [Fact]
    public void MintForEmpire_rejects_an_unknown_species_id()
    {
        var owner = new EmpireRef(new SaveId(1), EmpireId.Zomboss);
        Assert.Throws<ArgumentException>(() => _store.MintForEmpire(owner, "not-a-real-species-id", seed: 1));
    }

    [Fact]
    public void A_Zomboss_minted_specimen_can_deploy_through_the_real_TryBeginUniqueDeploy_path()
    {
        var owner = new EmpireRef(new SaveId(1), EmpireId.Zomboss);
        var specimen = _store.MintForEmpire(owner, CatalogSpecies.SpeciesId, seed: 1);

        var (ok, reason, _, _) = _store.TryBeginUniqueDeploy(
            specimen.Actor.InstanceId, Guid.NewGuid().ToString("N"), matchKey: "test-match");

        Assert.True(ok, reason);
    }

    [Fact]
    public void Zomboss_minted_into_save_1_is_not_save_2s()
    {
        var save2 = _store.CreatePlayer("Second");
        var owner1 = new EmpireRef(new SaveId(1), EmpireId.Zomboss);
        var owner2 = new EmpireRef(new SaveId(save2.Id), EmpireId.Zomboss);

        var specimen1 = _store.MintForEmpire(owner1, CatalogSpecies.SpeciesId, seed: 5);
        var specimen2 = _store.MintForEmpire(owner2, CatalogSpecies.SpeciesId, seed: 5);

        Assert.Equal(1, specimen1.Actor.PlayerId);
        Assert.Equal(save2.Id, specimen2.Actor.PlayerId);
        Assert.True(_store.OwnsSpecimenForTest(owner1, specimen1.Actor.InstanceId));
        Assert.False(_store.OwnsSpecimenForTest(owner2, specimen1.Actor.InstanceId));
        Assert.True(_store.OwnsSpecimenForTest(owner2, specimen2.Actor.InstanceId));
        Assert.False(_store.OwnsSpecimenForTest(owner1, specimen2.Actor.InstanceId));
    }
}
