using FusionRpg.Contracts;
using FusionRpg.Core.Commanders;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Layers;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Power;
using FusionRpg.Core.Saves;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using Xunit;
using FusionRpg.Data.Tests;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Server.Tests;

/// <summary>species-progression module 6 (`species-layer-delivery`) step 6.2 (SP6.8) — one test per
/// compose path (`layer-source-selector`'s own six-cell table), asserting which SourceId FAMILIES
/// (`species-base:`, `species-player:`) reach the fold, and that a unique's 1b follows its OWNER
/// empire, never its side, and never crosses a save boundary.
///
/// <para>Lawn general plant/zombie and a Bound unique are injector concepts with no lawn/injector
/// code reachable from this test project — those three are proven against
/// `SpeciesLayerSource` directly (the SAME mechanism `CheatState.SpeciesLayer` wraps, SP6.4), with
/// fake resolvers, exactly like `SpeciesLayerSourceTests` (Core.Tests) already does for the general
/// mechanism; this file's own contribution is the PATH-SPECIFIC claim (which SourceId family, per
/// path), consolidated in one place rather than restating the mechanism proof.</para></summary>
public class SpeciesLayerPathTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public SpeciesLayerPathTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        AptitudeTuningHub.Configure(AptitudeTuningLoader.Parse(File.ReadAllText(LatestAptitudesPath())));
        PowerTuningHub.Configure(PowerTuningLoader.Parse(File.ReadAllText(Path.Combine(RepoTuningDir(), "power-scale.v2.json"))));
        FusionRpg.Core.Progression.ProgressionTuningHub.Configure(
            FusionRpg.Core.Progression.ProgressionTuningLoader.Parse(
                File.ReadAllText(Path.Combine(RepoTuningDir(), "progression.v3.json"))));
    }

    public void Dispose() => _testStore.Dispose();

    static readonly ProjectedLayerRow BaseRow = new(
        "resource.max.hp", FusionRpg.Core.Stats.Derived.DerivedModifierOp.Flat,
        new LayerValue.Fixed(500), "species-base:peashooter");
    static readonly ProjectedLayerRow DaveModRow = new(
        "combat.power.omni", FusionRpg.Core.Stats.Derived.DerivedModifierOp.Flat,
        new LayerValue.Fixed(40), "species-player:peashooter:fusion-pick");
    static readonly ProjectedLayerRow ZombossModRow = new(
        "combat.defense.omni", FusionRpg.Core.Stats.Derived.DerivedModifierOp.Flat,
        new LayerValue.Fixed(30), "species-player:peashooter:fusion-pick");

    static SpeciesLayerSource NewFakeSource(
        Func<string, string?>? resolveBoundInstanceId = null,
        Func<string, EmpireId?>? resolveSpecimenOwnerEmpire = null) => new(
        resolveSpeciesId: (_, typeId) => typeId == 42 ? SpeciesLookupResult.Hit("peashooter") : SpeciesLookupResult.NoSpecies,
        resolveBaseRows: id => id == "peashooter" ? new[] { BaseRow } : Array.Empty<ProjectedLayerRow>(),
        resolveModRows: (empire, id) => id != "peashooter" ? Array.Empty<ProjectedLayerRow>()
            : empire == EmpireId.Dave ? new[] { DaveModRow }
            : empire == EmpireId.Zomboss ? new[] { ZombossModRow }
            : Array.Empty<ProjectedLayerRow>(),
        resolveBoundInstanceId: resolveBoundInstanceId,
        resolveSpecimenOwnerEmpire: resolveSpecimenOwnerEmpire);

    [Fact]
    public void LawnGeneralPlant_getsBaseAndModOfDavesEmpire()
    {
        var rows = NewFakeSource().Resolve(new StatContext { Side = StatSide.Plant, TypeId = 42, EntityKey = "P1" });
        Assert.Contains(rows, r => r.SourceId == "species-base:peashooter");
        Assert.Contains(rows, r => r.SourceId == "species-player:peashooter:fusion-pick" && r.Channel == "combat.power.omni");
    }

    [Fact]
    public void LawnGeneralZombie_getsBaseAndModOfZombossEmpire()
    {
        var rows = NewFakeSource().Resolve(new StatContext { Side = StatSide.Zombie, TypeId = 42, EntityKey = "Z1" });
        Assert.Contains(rows, r => r.SourceId == "species-base:peashooter");
        Assert.Contains(rows, r => r.SourceId == "species-player:peashooter:fusion-pick" && r.Channel == "combat.defense.omni");
    }

    [Fact]
    public void BoundUnique_getsRowsOfItsOwnerEmpire_neverItsSide()
    {
        // A zombie-side, HUMAN-OWNED specimen: side says zombie, owner says Dave.
        var source = NewFakeSource(
            resolveBoundInstanceId: key => key == "Z1" ? "instance-abc" : null,
            resolveSpecimenOwnerEmpire: key => key == "Z1" ? EmpireId.Dave : null);
        var rows = source.Resolve(new StatContext { Side = StatSide.Zombie, TypeId = 42, EntityKey = "Z1" });

        Assert.Contains(rows, r => r.SourceId == "species-player:peashooter:fusion-pick" && r.Channel == "combat.power.omni");
        Assert.DoesNotContain(rows, r => r.Channel == "combat.defense.omni"); // Zomboss's own mod row must not appear
    }

    // ---- sheet ----------------------------------------------------------------------------------

    (UniqueActorDto Actor, EmpireRef Owner) MintAndFuse(long playerId, string speciesId, string pickChannel, long pickAmount)
    {
        var species = CreatureSpeciesCatalog.Get(speciesId);
        var (specimen, _) = _store.MintCreature(playerId, new CreatureMintSpec
        {
            SpeciesId = species.SpeciesId, Side = species.Side, GameTypeId = species.GameTypeId,
            Rarity = species.BaseRarity.ToId(), Variant = "normal",
            ElementPrimary = species.ElementPrimary.ToElementId(),
            ElementSecondary = species.ElementSecondary?.ToElementId(),
            TraitIds = new List<string> { species.TraitPool[0] }, Origin = "summon",
        });

        var coreAtomId = $"atom.{speciesId}-core.t1";
        Assert.True(_store.UpsertAtom(new AtomRow
        {
            AtomId = coreAtomId, KindId = "stat.derived", FamilyId = $"atom.{speciesId}-core", Tier = 1,
            Name = $"{speciesId} core", ParamsJson = """{"channel":"resource.max.stamina","op":"flat","amount":1}""",
        }).IsOk);
        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = $"species-passive.{speciesId}", Kind = ContainerKind.SpeciesPassive,
            Atoms = new[] { new ContainerAtomRow(1, coreAtomId) },
        }).IsOk);

        var pickAtomId = $"atom.{speciesId}-pick.t1";
        Assert.True(_store.UpsertAtom(new AtomRow
        {
            AtomId = pickAtomId, KindId = "stat.derived", FamilyId = $"atom.{speciesId}-pick", Tier = 1,
            Name = $"{speciesId} pick", ParamsJson = $$"""{"channel":"{{pickChannel}}","op":"flat","amount":{{pickAmount}}}""",
        }).IsOk);

        var effectInstanceId = Guid.NewGuid().ToString("N");
        using (var raw = SqliteConnectionFactory.Open(_store.HotPath))
        {
            using (var cmd = raw.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT INTO effect_instance
                      (instance_id, container_id, roll_seed, catalog_revision, created_utc, origin,
                       theta_content, content_scale_milli)
                    VALUES ($id, $c, 1, 0, $utc, 'test', 0, 0);
                    """;
                cmd.Parameters.AddWithValue("$id", effectInstanceId);
                cmd.Parameters.AddWithValue("$c", $"species-passive.{speciesId}");
                cmd.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("o"));
                cmd.ExecuteNonQuery();
            }
            using (var cmd = raw.CreateCommand())
            {
                cmd.CommandText =
                    "INSERT INTO effect_instance_atom (instance_id, seq, atom_id, values_json, power_json) " +
                    "VALUES ($id, 2, $atom, '{}', NULL);";
                cmd.Parameters.AddWithValue("$id", effectInstanceId);
                cmd.Parameters.AddWithValue("$atom", pickAtomId);
                cmd.ExecuteNonQuery();
            }
        }

        var owner = new EmpireRef(new SaveId(playerId), _store.HumanEmpireOf(playerId));
        Assert.True(_store.AppendSpeciesMod(owner, speciesId, SpeciesModMechanism.FusionPick,
            correlationId: effectInstanceId, instanceId: effectInstanceId, catalogRevision: 0));
        return (specimen.Actor, owner);
    }

    [Fact]
    public void Sheet_getsBaseAndModOfItsOwnerEmpire()
    {
        var player = _store.CreatePlayer("SheetOwner");
        var (actor, _) = MintAndFuse(player.Id, "peashooter", "combat.power.omni", 40);

        var (hub, ctx) = UniqueActorHubCompose.Build(_store, actor);
        var (_, bag) = hub.ResolveDerivedWithContributions(ctx);

        var maxHpSources = bag.ContributionsFor(FusionRpg.Core.Stats.Derived.DerivedStatChannels.ResourceMax("stamina")).Select(c => c.SourceId).ToList();
        var powerSources = bag.ContributionsFor(FusionRpg.Core.Stats.Derived.DerivedStatChannels.CombatPowerOmni).Select(c => c.SourceId).ToList();

        Assert.Contains("species-base:peashooter", maxHpSources);
        Assert.Contains("species-player:peashooter:fusion-pick", powerSources);
    }

    // ---- world-turn unique (RpgStore.WorldTurnHubInputsFor -- data-layer seam, called from Server-side too) --

    [Fact]
    public void WorldTurnUnique_getsBaseAndModOfItsOwnerEmpire()
    {
        var player = _store.CreatePlayer("WorldTurnOwner");
        var (actor, _) = MintAndFuse(player.Id, "peashooter", "combat.power.omni", 40);

        var member = new FusionRpg.Core.World.WorldEntityMember
        {
            SpeciesId = "peashooter", Level = 4, Hp = 100, InstanceId = actor.InstanceId,
        };
        var hub = _store.WorldTurnHubInputsFor(member, player.Id);

        Assert.NotNull(hub);
        Assert.NotNull(hub!.SpeciesLayers);
        Assert.Contains(hub.SpeciesLayers!, r => r.SourceId == "species-base:peashooter");
        Assert.Contains(hub.SpeciesLayers!, r => r.SourceId == "species-player:peashooter:fusion-pick");
    }

    // ---- web-squad unique (RpgStore.SpeciesLayersForSpecimen -- the SAME call WebMatchService.BuildSquad makes) --

    [Fact]
    public void WebSquadUnique_getsBaseAndModOfItsOwnerEmpire()
    {
        var player = _store.CreatePlayer("WebSquadOwner");
        var (_, owner) = MintAndFuse(player.Id, "peashooter", "combat.power.omni", 40);

        var rows = _store.SpeciesLayersForSpecimen(owner, "peashooter");

        Assert.Contains(rows, r => r.SourceId == "species-base:peashooter");
        Assert.Contains(rows, r => r.SourceId == "species-player:peashooter:fusion-pick");
    }

    // ---- cross-cutting: save isolation -----------------------------------------------------------

    [Fact]
    public void SaveAsLayer1bNeverReachesSaveB()
    {
        var a = _store.CreatePlayer("SaveA");
        var b = _store.CreatePlayer("SaveB");
        MintAndFuse(a.Id, "peashooter", "combat.power.omni", 40);
        MintAndFuse(b.Id, "wallnut", "combat.defense.omni", 30);

        var ownerA = new EmpireRef(new SaveId(a.Id), _store.HumanEmpireOf(a.Id));
        var ownerB = new EmpireRef(new SaveId(b.Id), _store.HumanEmpireOf(b.Id));

        var rowsAForWallnut = _store.SpeciesLayersForSpecimen(ownerA, "wallnut");
        var rowsBForPeashooter = _store.SpeciesLayersForSpecimen(ownerB, "peashooter");

        // Neither save's ledger leaks into a query against the OTHER save's owner for the OTHER
        // save's own fused species -- each only ever sees its own empire's ledger rows.
        Assert.DoesNotContain(rowsAForWallnut, r => r.SourceId.StartsWith("species-player:", StringComparison.Ordinal));
        Assert.DoesNotContain(rowsBForPeashooter, r => r.SourceId.StartsWith("species-player:", StringComparison.Ordinal));
    }

    static string RepoTuningDir() => Path.Combine(KeepverseRoots.Core(), "data", "tuning");

    static string LatestAptitudesPath()
    {
        var dir = RepoTuningDir();
        var best = Directory.EnumerateFiles(dir, "aptitudes.v*.json")
            .Select(Path.GetFileName)
            .Select(n => (Name: n!, Match: System.Text.RegularExpressions.Regex.Match(n!, @"^aptitudes\.v(\d+)\.json$")))
            .Where(x => x.Match.Success)
            .OrderByDescending(x => int.Parse(x.Match.Groups[1].Value))
            .First();
        return Path.Combine(dir, best.Name);
    }

    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
