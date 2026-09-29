using FusionRpg.Contracts;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// creature-lawn-deploy T1.5: a creature specimen's own SPECIES magnitude (base stats, distinct from
/// `TraitIds`) reconciles into a real `effect_binding` row on every deploy, mirroring T1.2's own
/// trait-binding shape exactly. No real committed species-magnitude atom/container content exists yet
/// (this is a brand-new mechanism, unlike T1.2's trait case which already had `trait.critical-hunter`
/// live) — content is hand-authored here via `_store.UpsertAtom`/`UpsertContainer`, the same pattern
/// `MultiOwnerPushTests.cs` already uses to prove a NEW atom mechanism before real seed files exist.
/// The species itself is a scoped, hand-crafted `CreatureSpeciesDef` (`CreatureSpeciesCatalog.UseScoped`,
/// the same isolation `SpeciesCatalogDiffTests.cs`/`CreatureRecipeCatalogTests.cs` already use) carrying a
/// real value taken from a real committed species (`AbyssSwordStar.json`'s own
/// `combat.power.omni: 362`), not an arbitrary number.
/// </summary>
public class CreatureLawnDeployMagnitudeTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public CreatureLawnDeployMagnitudeTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    const string SpeciesId = "test-magnitude-creature";
    const string NoMagnitudeSpeciesId = "test-no-magnitude-creature";
    const string Channel = "combat.power.omni";
    const long RealAbyssSwordStarValue = 362; // data/generated/creatures/AbyssSwordStar.json's own value

    static CreatureSpeciesDef Species(string speciesId, int creatureTypeId, IReadOnlyDictionary<string, long>? magnitudes = null) => new()
    {
        SpeciesId = speciesId,
        Name = speciesId,
        Side = "zombie",
        GameTypeId = 900,
        CreatureTypeId = creatureTypeId,
        ElementPrimary = ElementTypeId.Fire,
        BaseRarity = CreatureRarity.Chaff,
        DeployMode = CreatureDeployMode.PlantAvatar, // avoid T1.4's HypnoAlly refusal — unrelated concern
        Acquisition = CreatureAcquisition.Summonable,
        Magnitudes = magnitudes ?? new Dictionary<string, long>(),
    };

    void SeedMagnitudeContainer(string speciesId, string channel, long amount)
    {
        var atomFamily = $"atom.species-magnitude-{speciesId}";
        var atomId = AtomRow.DeriveId(atomFamily, "", 1);
        var upserted = _store.UpsertAtom(new AtomRow
        {
            AtomId = atomId,
            KindId = "stat.derived",
            FamilyId = atomFamily,
            Variant = "",
            Tier = 1,
            Name = atomFamily,
            ParamsJson = $$"""{"channel":"{{channel}}","op":"flat","amount":{{amount}}}""",
            WhenJson = "{}",
        });
        Assert.True(upserted.IsOk, upserted.ToString());

        var containerId = $"trait.species-magnitude-{speciesId}";
        var containerUpserted = _store.UpsertContainer(new ContainerRow
        {
            ContainerId = containerId,
            Kind = ContainerKind.Trait,
            Atoms = new[] { new ContainerAtomRow(1, atomId) },
        });
        Assert.True(containerUpserted.IsOk, containerId);
    }

    string Mint(string speciesId)
    {
        var (specimen, _) = _store.MintCreature(1, new CreatureMintSpec
        {
            SpeciesId = speciesId,
            Side = "zombie",
            GameTypeId = 900,
            Rarity = CreatureRarity.Chaff.ToId(),
            Variant = "normal",
            ElementPrimary = ElementTypeId.Fire.ToElementId(),
            TraitIds = new List<string>(),
            Origin = "summon"
        });
        return specimen.Actor.InstanceId;
    }

    [Fact]
    public void A_specimens_species_magnitude_binds_on_first_deploy()
    {
        SeedMagnitudeContainer(SpeciesId, Channel, RealAbyssSwordStarValue);
        using (CreatureSpeciesCatalog.UseScoped(new[]
            { Species(SpeciesId, 10_101, new Dictionary<string, long> { [Channel] = RealAbyssSwordStarValue }) }))
        {
            var id = Mint(SpeciesId);
            var deploy = _store.TryBeginUniqueDeploy(id, "deploy-mag-1");
            Assert.True(deploy.Ok, deploy.Reason);

            var bindings = _store.ListBindings(new OwnerScope(OwnerKind.UniqueActor, id));
            Assert.Contains(bindings, b =>
                b.Source == "creature-magnitude" && b.Slot == $"trait.species-magnitude-{SpeciesId}");
        }
    }

    [Fact]
    public void Redeploying_with_the_same_species_writes_no_new_binding()
    {
        SeedMagnitudeContainer(SpeciesId, Channel, RealAbyssSwordStarValue);
        using (CreatureSpeciesCatalog.UseScoped(new[]
            { Species(SpeciesId, 10_102, new Dictionary<string, long> { [Channel] = RealAbyssSwordStarValue }) }))
        {
            var id = Mint(SpeciesId);
            var owner = new OwnerScope(OwnerKind.UniqueActor, id);

            _store.TryBeginUniqueDeploy(id, "deploy-mag-2");
            var afterFirst = _store.ListBindings(owner)
                .Where(b => b.Source == "creature-magnitude").Select(b => b.BindingId).OrderBy(x => x).ToArray();

            _store.TryAckUniqueSpawn("deploy-mag-2", "ptr-mag-1", "m-mag-1");
            _store.ObserveUniqueActorEvents(new (string Kind, string? MatchKey, string PayloadJson)[]
            {
                ("board.end", "m-mag-1", "{}")
            });
            Assert.Equal(UniqueActorPhases.Roster, _store.GetUniqueActor(id)!.Phase);

            _store.TryBeginUniqueDeploy(id, "deploy-mag-3");
            var afterSecond = _store.ListBindings(owner)
                .Where(b => b.Source == "creature-magnitude").Select(b => b.BindingId).OrderBy(x => x).ToArray();

            Assert.Equal(afterFirst, afterSecond); // same binding id — nothing withdrawn or re-produced
        }
    }

    [Fact]
    public void A_species_with_no_magnitude_data_deploys_with_zero_magnitude_bindings()
    {
        using (CreatureSpeciesCatalog.UseScoped(new[] { Species(NoMagnitudeSpeciesId, 10_103) }))
        {
            var id = Mint(NoMagnitudeSpeciesId);
            var deploy = _store.TryBeginUniqueDeploy(id, "deploy-mag-4");
            Assert.True(deploy.Ok, deploy.Reason);

            var bindings = _store.ListBindings(new OwnerScope(OwnerKind.UniqueActor, id));
            Assert.DoesNotContain(bindings, b => b.Source == "creature-magnitude");
        }
    }
}
