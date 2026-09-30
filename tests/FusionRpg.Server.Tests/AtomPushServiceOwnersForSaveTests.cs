using FusionRpg.Contracts;
using FusionRpg.Core.Commanders;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Saves;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Data;
using FusionRpg.Data.Tests;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Server.Tests;

/// <summary>
/// save-identity SE4.26 ("Specimen reads are classified by purpose") — two different questions over
/// the same table, and they must not answer alike. `ListUniqueActors` (an empire's own roster) must
/// EXCLUDE another empire's specimen sharing the save's row; `AtomPushService.OwnersForSave` (the
/// match's runtime — every side on the board) must INCLUDE it, because Zomboss's own ActiveBound
/// specimens still need their trait atoms pushed.
/// </summary>
[Trait("VerificationId", "server.atom-push")]
public class AtomPushServiceOwnersForSaveTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public AtomPushServiceOwnersForSaveTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;

        // MintForEmpire -> SummonRoller.RollTraits -> FusionRoller.SlotsFor reads StarPolicy.Tuning
        // (data/tuning/fusion.v{n}.json), which this assembly's own [ModuleInitializer] bootstrap does
        // not cover -- configured here from the real shipped file, exactly like
        // ZombossDeployEndpointsTests' identical comment for the same gap.
        FusionRpg.Core.Creatures.Fusion.StarPolicy.Configure(
            FusionRpg.Core.Creatures.Fusion.FusionTuningLoader.Parse(
                File.ReadAllText(Path.Combine(FindRepoRoot(), "data", "tuning", "fusion.v2.json"))));
    }

    public void Dispose() => _testStore.Dispose();

    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }

    static readonly CreatureSpeciesDef CatalogSpecies = CreatureSpeciesCatalog.All.First(s =>
        s.DeployMode != CreatureDeployMode.HypnoAlly);

    [Fact]
    public void OwnersForSave_includes_an_ActiveBound_Zomboss_specimen_of_the_same_save()
    {
        var zomboss = _store.MintForEmpire(new EmpireRef(new SaveId(1), EmpireId.Zomboss), CatalogSpecies.SpeciesId, seed: 40);
        var corr = Guid.NewGuid().ToString("N");
        Assert.True(_store.TryBeginUniqueDeploy(zomboss.Actor.InstanceId, corr, "m-owners-for-save").Ok);
        var ptr = "ptr-" + Guid.NewGuid().ToString("N");
        Assert.True(_store.TryAckUniqueSpawn(corr, ptr, "m-owners-for-save").Ok);

        var owners = AtomPushService.OwnersForSave(_store, 1);

        Assert.Contains(owners, o => o.Kind == OwnerKind.UniqueActor && o.Key == zomboss.Actor.InstanceId);
    }

    [Fact]
    public void OwnersForSave_still_carries_the_saves_own_player_scope()
    {
        var owners = AtomPushService.OwnersForSave(_store, 1);

        Assert.Contains(owners, o => o.Kind == OwnerKind.Player && o.Key == "1");
    }

    /// <summary>The other half of the split: the SAME specimen that must appear in the runtime read
    /// must NOT appear in the human empire's own roster read.</summary>
    [Fact]
    public void ListUniqueActors_the_roster_read_excludes_the_same_Zomboss_specimen()
    {
        var zomboss = _store.MintForEmpire(new EmpireRef(new SaveId(1), EmpireId.Zomboss), CatalogSpecies.SpeciesId, seed: 41);

        var roster = _store.ListUniqueActors(1);

        Assert.DoesNotContain(roster.Items, a => a.InstanceId == zomboss.Actor.InstanceId);
    }

    [Fact]
    public void ListUniqueActors_by_EmpireRef_returns_only_that_empires_own_specimens()
    {
        var zomboss = _store.MintForEmpire(new EmpireRef(new SaveId(1), EmpireId.Zomboss), CatalogSpecies.SpeciesId, seed: 42);
        var (human, _) = _store.MintCreature(1, new()
        {
            SpeciesId = CatalogSpecies.SpeciesId,
            Side = CatalogSpecies.Side,
            GameTypeId = CatalogSpecies.GameTypeId,
            Rarity = CatalogSpecies.BaseRarity.ToId(),
            Variant = "normal",
            ElementPrimary = CatalogSpecies.ElementPrimary.ToElementId(),
            Origin = "summon",
        });

        var humanRoster = _store.ListUniqueActors(new EmpireRef(new SaveId(1), EmpireId.Dave));
        var zombossRoster = _store.ListUniqueActors(new EmpireRef(new SaveId(1), EmpireId.Zomboss));

        Assert.Contains(humanRoster.Items, a => a.InstanceId == human.Actor.InstanceId);
        Assert.DoesNotContain(humanRoster.Items, a => a.InstanceId == zomboss.Actor.InstanceId);
        Assert.Contains(zombossRoster.Items, a => a.InstanceId == zomboss.Actor.InstanceId);
        Assert.DoesNotContain(zombossRoster.Items, a => a.InstanceId == human.Actor.InstanceId);
    }
}
