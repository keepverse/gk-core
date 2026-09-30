using System.Collections.Generic;
using System.Linq;
using FusionRpg.Contracts;
using FusionRpg.Core.Commanders;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Dungeon.Registry;
using FusionRpg.Core.Dungeon.Tuning;
using FusionRpg.Core.Activity;
using FusionRpg.Core.Saves;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Data.Tests.Saves;

/// <summary>
/// `save-identity` SE4.14 — the one specimen-ownership predicate compares `(player_id, empire_id)`
/// strictly, over the `rpg_unique_actors.empire_id` column a mint now stamps.
/// </summary>
[Trait("VerificationId", "data.save-empires")]
public class SpecimenOwnershipTests : System.IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public SpecimenOwnershipTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose() => _testStore.Dispose();

    // Mint enforces catalog discipline — a real generated species, the same fixture other store tests
    // use. DeployMode != HypnoAlly (SE4.25's own tests deploy this species): HypnoAlly is a NAMED
    // deploy refusal (creature-lawn-deploy T1.4), unrelated to save-identity's ownership predicate.
    static readonly FusionRpg.Core.Creatures.CreatureSpeciesDef CatalogSpecies =
        FusionRpg.Core.Creatures.CreatureSpeciesCatalog.All.First(s =>
            s.Side == "zombie" && s.DeployMode != FusionRpg.Core.Creatures.CreatureDeployMode.HypnoAlly);

    static CreatureMintSpec Spec() => new()
    {
        SpeciesId = CatalogSpecies.SpeciesId,
        Side = "zombie",
        GameTypeId = CatalogSpecies.GameTypeId,
        Rarity = "chaff",
        Variant = "normal",
        ElementPrimary = "fire",
        TraitIds = new List<string> { "swift" },
        Origin = "summon",
    };

    [Fact]
    public void A_mint_on_a_seeded_save_is_owned_by_that_saves_human_empire()
    {
        var (specimen, _) = _store.MintCreature(1, Spec());
        var owner = new EmpireRef(new SaveId(1), _store.HumanEmpireOf(1));

        Assert.True(_store.OwnsSpecimenForTest(owner, specimen.Actor.InstanceId));
    }

    [Fact]
    public void Another_save_is_not_the_owner()
    {
        var (specimen, _) = _store.MintCreature(1, Spec());
        var other = new EmpireRef(new SaveId(_store.CreatePlayer("Second").Id), EmpireId.Dave);

        Assert.False(_store.OwnsSpecimenForTest(other, specimen.Actor.InstanceId));
    }

    [Fact]
    public void Another_empire_of_the_same_save_is_not_the_owner()
    {
        var (specimen, _) = _store.MintCreature(1, Spec());
        var sameSaveOtherEmpire = new EmpireRef(new SaveId(1), EmpireId.Zomboss);

        Assert.False(_store.OwnsSpecimenForTest(sameSaveOtherEmpire, specimen.Actor.InstanceId));
    }

    [Fact]
    public void A_null_empire_id_never_matches_anything()
    {
        // A row whose owner is not a save: the legacy Zomboss player row (before SE4.22 deleted
        // EnsureZombossPlayer, the shape it built) has no empires, so its specimens carry a NULL
        // empire_id until SE4.18 backfills them — and NULL owns nothing.
        var zomboss = _store.CreateUnseededPlayerForTest("Zomboss");
        var (specimen, _) = _store.MintCreature(zomboss.Id, Spec());

        Assert.False(_store.OwnsSpecimenForTest(new EmpireRef(new SaveId(zomboss.Id), EmpireId.Dave),
            specimen.Actor.InstanceId));
        Assert.False(_store.OwnsSpecimenForTest(new EmpireRef(new SaveId(zomboss.Id), EmpireId.Zomboss),
            specimen.Actor.InstanceId));
    }

    [Fact]
    public void An_unknown_or_blank_instance_is_never_owned()
    {
        var owner = new EmpireRef(new SaveId(1), EmpireId.Dave);
        Assert.False(_store.OwnsSpecimenForTest(owner, "no-such-instance"));
        Assert.False(_store.OwnsSpecimenForTest(owner, ""));
    }

    // ---- SE4.24: the one ownership predicate wired at six production specimen-write sites ----

    string ZombossSpecimen(ulong seed) =>
        _store.MintForEmpire(new EmpireRef(new SaveId(1), EmpireId.Zomboss), CatalogSpecies.SpeciesId, seed)
            .Actor.InstanceId;

    [Fact]
    public void BindContract_refuses_a_Zomboss_specimen_of_this_save()
    {
        var instanceId = ZombossSpecimen(20);

        var (ok, reason, contract) = _store.BindContract(1, instanceId);

        Assert.False(ok);
        Assert.Equal("specimen.missing", reason);
        Assert.Null(contract);
        Assert.Null(_store.GetContract(instanceId));
    }

    [Fact]
    public void DispatchExpedition_refuses_a_squad_containing_a_Zomboss_specimen()
    {
        var instanceId = ZombossSpecimen(21);

        var (ok, reason, expedition) = _store.DispatchExpedition(1, "se4.24-exp", "scout-30m", new[] { instanceId }, seed: 1);

        Assert.False(ok);
        Assert.Equal("squad.unknown-specimen", reason);
        Assert.Null(expedition);
        Assert.Empty(_store.ListExpeditions(1));
    }

    [Fact]
    public void ExecuteFusion_refuses_a_Zomboss_specimen_as_the_base()
    {
        var instanceId = ZombossSpecimen(22);

        var (ok, reason, outcome) = _store.ExecuteFusion(1, "se4.24-fuse-base",
            new FusionRequest(FusionModes.Promotion, instanceId, Array.Empty<string>(), null), seed: 1);

        Assert.False(ok);
        Assert.Equal("base.missing", reason);
        Assert.Null(outcome);
    }

    [Fact]
    public void ExecuteFusion_refuses_a_Zomboss_specimen_as_a_sacrifice()
    {
        _store.AwardSouls(1, 5000, "seed", "se4.24-bank");
        _store.AddCreatureMaterials(1, new[]
        {
            ("shard." + CatalogSpecies.BaseRarity.ToId(), 10L),
            ("essence." + CatalogSpecies.ElementPrimary.ToElementId(), 10L),
        });
        var (baseSpecimen, _) = _store.MintCreature(1, Spec());
        var humanSacrifice = _store.MintCreature(1, Spec()).Specimen.Actor.InstanceId;
        var zombossSacrifice = ZombossSpecimen(23);

        var (ok, reason, outcome) = _store.ExecuteFusion(1, "se4.24-fuse-sac",
            new FusionRequest(FusionModes.StarMerge, baseSpecimen.Actor.InstanceId,
                new[] { humanSacrifice, zombossSacrifice }, null), seed: 1);

        Assert.False(ok);
        Assert.Equal("sacrifice.invalid", reason);
        Assert.Null(outcome);
        // Neither sacrifice was consumed (still Roster, still owned) — the refusal wrote nothing.
        Assert.Equal("Roster", _store.GetUniqueActor(humanSacrifice)!.Phase);
    }

    [Fact]
    public void SetPatron_refuses_a_Zomboss_specimen()
    {
        var instanceId = ZombossSpecimen(24);

        var (ok, reason, patron) = _store.SetPatron(1, instanceId, "se4.24-patron");

        Assert.False(ok);
        Assert.Equal("specimen.missing", reason);
        Assert.Null(patron);
    }

    [Fact]
    public void TryPerformRecoveryRitual_refuses_a_Zomboss_specimen()
    {
        var instanceId = ZombossSpecimen(25);
        var repoRoot = FindRepoRoot();
        var registries = DungeonRegistryLoader.LoadAll(Path.Combine(KeepverseRoots.Content(), "data", "seed", "dungeon", "_registry"));
        var tuning = DungeonTuningLoader.Parse(
            File.ReadAllText(Path.Combine(repoRoot, "data", "tuning", "dungeon.v3.json")), registries);

        var (ok, reason, actor) = _store.TryPerformRecoveryRitual(1, instanceId, "se4.24-ritual", tuning);

        Assert.False(ok);
        Assert.Equal("not_found", reason);
        Assert.Null(actor);
    }

    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }

    // ---- SE4.25: the last two ownership sites (species XP from a specimen source; item equip) ----

    void RelabelToZombossEmpire(string instanceId)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE rpg_unique_actors SET empire_id = 'zomboss' WHERE instance_id = $id;";
        cmd.Parameters.AddWithValue("$id", instanceId);
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void A_Zomboss_specimens_progression_claim_is_downgraded_to_untrusted()
    {
        var (specimen, _) = _store.MintCreature(1, Spec());
        var corr = "corr-se4.25";
        Assert.True(_store.TryBeginUniqueDeploy(specimen.Actor.InstanceId, corr, "m-se4.25").Ok);
        // Relabelled AFTER a legitimate human deploy — no production path mints a Zomboss-owned
        // specimen through this exact mint call yet (MintForEmpire ties Side to the catalog species),
        // so this stamps the one column SE4.22's real mint path already sets for a true AI deploy.
        RelabelToZombossEmpire(specimen.Actor.InstanceId);

        var claim = CreatureProgressionSource.UniqueSpecimen(specimen.Actor.InstanceId, corr);
        _store.AppendPvzActivityFact(1, new PvzActivityAppendRequest
        {
            Kind = PvzActivityKinds.ZombieKilled,
            SourceKind = claim.Kind,
            SourceId = claim.Id,
            DedupeKey = "se4.25-zomboss-claim",
        });

        var fact = _store.ListPvzActivityFacts(1, kind: PvzActivityKinds.ZombieKilled)!.Items
            .Single(f => f.DedupeKey == "se4.25-zomboss-claim");
        Assert.Equal("untrusted", fact.SourceKind);
    }

    /// <summary>The falsifier: the SAME claim shape from the human's own specimen is trusted — without
    /// this, the test above would pass on a store that downgrades every unique-specimen claim.</summary>
    [Fact]
    public void The_same_claim_from_the_humans_own_specimen_is_trusted()
    {
        var (specimen, _) = _store.MintCreature(1, Spec());
        var corr = "corr-se4.25-human";
        Assert.True(_store.TryBeginUniqueDeploy(specimen.Actor.InstanceId, corr, "m-se4.25-human").Ok);

        var claim = CreatureProgressionSource.UniqueSpecimen(specimen.Actor.InstanceId, corr);
        _store.AppendPvzActivityFact(1, new PvzActivityAppendRequest
        {
            Kind = PvzActivityKinds.ZombieKilled,
            SourceKind = claim.Kind,
            SourceId = claim.Id,
            DedupeKey = "se4.25-human-claim",
        });

        var fact = _store.ListPvzActivityFacts(1, kind: PvzActivityKinds.ZombieKilled)!.Items
            .Single(f => f.DedupeKey == "se4.25-human-claim");
        Assert.Equal(claim.Kind, fact.SourceKind);
        Assert.Equal(claim.Id, fact.SourceId);
    }

    [Fact]
    public void ItemEquip_ownership_refuses_a_Zomboss_specimen()
    {
        var instanceId = ZombossSpecimen(26);
        var owner = new EmpireRef(new SaveId(1), EmpireId.Zomboss);
        Assert.True(_store.OwnsSpecimenForTest(owner, instanceId));

        Assert.False(_store.OwnsSpecimen(new EmpireRef(new SaveId(1), _store.HumanEmpireOf(1)), instanceId));
    }
}
