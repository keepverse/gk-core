using FusionRpg.Contracts;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Eligibility;
using FusionRpg.Core.Actions.Rungs;
using FusionRpg.Core.Actions.Unlock;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.World;
using FusionRpg.Data;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FusionRpg.Server.Tests;

/// <summary>
/// `species-progression` SP1.4/SP1.5 (`layer-source-selector`, four-path parity) — for one specimen,
/// the aptitude input the lawn (a Bound ctx, `SpeciesAllocationSource.Resolve`, SP1.3), the web squad
/// (`WebMatchService.BuildSquad`), the sheet (`UniqueActorHubCompose.ResolveAptitudeAllocation`) and
/// the world-turn provider (`RpgStore.WorldTurnHubInputsForUnlocked`, SP1.2) must all be equal by
/// scope and points — for both a plant-side and a zombie-side human-owned unique. Bootstrap mirrors
/// `EquippedHubParityTests`'/`ProjectStandingTests`' own established shape.
/// </summary>
public class ProgressionLayerParityTests : IDisposable
{
    readonly RpgStore _store;
    readonly WebMatchService _service;

    static ProgressionLayerParityTests()
    {
        UnlockTuningPolicy.Configure(new UnlockTuning(
            P1Milli: 1000, DeltaMilli: 1000, FloorMilli: 1000, HeldCap: 10, RungCap: 10, DiscardTaxCoeffMilli: 100));
        ActionFamilyMapPolicy.Configure(new Dictionary<string, IReadOnlyList<string>>());
    }

    public ProgressionLayerParityTests()
    {
        var tuningDir = Path.Combine(FindRepoRoot(), "data", "tuning");
        string Read(string name) => File.ReadAllText(Path.Combine(tuningDir, name));
        AptitudeTuningHub.Configure(AptitudeTuningLoader.Parse(LatestAptitudesText(tuningDir)));
        PowerTuningHub.Configure(PowerTuningLoader.Parse(Read("power-scale.v2.json")));
        SummoningTuningHub.Configure(SummoningTuningLoader.Parse(Read("summoning.v1.json")));
        FusionRpg.Core.Creatures.Contracts.ContractPolicy.Configure(
            FusionRpg.Core.Creatures.Contracts.ContractTuningLoader.Parse(Read("contracts.v1.json")));
        SoulEarnPolicy.Configure(SoulEarnTuningLoader.Parse(Read("souls.v1.json")));
        FusionRpg.Core.Creatures.Fusion.StarPolicy.Configure(
            FusionRpg.Core.Creatures.Fusion.FusionTuningLoader.Parse(Read("fusion.v2.json")));
        FusionRpg.Core.Progression.ProgressionTuningHub.Configure(
            FusionRpg.Core.Progression.ProgressionTuningLoader.Parse(Read("progression.v3.json")));
        FusionRpg.Core.Battle.BattleTuningHub.Configure(
            FusionRpg.Core.Battle.BattleTuningLoader.Parse(Read("battle.v5.json")));
        FusionRpg.Core.Battle.BattleRuleset.ConfigureResources(
            FusionRpg.Core.Battle.BattleResourceTuningLoader.Parse(Read("battle-resources.v1.json")));
        FusionRpg.Core.Actions.ActionTimingPolicy.Configure(
            FusionRpg.Core.Actions.ActionTimingTuningLoader.Parse(Read("action-timing.v1.json")));
        FusionRpg.Core.Stats.Derived.StatsTuningHub.Configure(
            FusionRpg.Core.Stats.Derived.StatsTuningLoader.Parse(Read("stats.v1.json")));

        _store = RpgStore.InMemory();
        _store.Init();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSignalR();
        var provider = services.BuildServiceProvider();
        var hub = provider.GetRequiredService<IHubContext<RpgHub>>();
        _service = new WebMatchService(_store, hub);
    }

    static string LatestAptitudesText(string tuningDir)
    {
        var file = Directory.GetFiles(tuningDir, "aptitudes.v*.json")
            .OrderByDescending(f => f, StringComparer.Ordinal).First();
        return File.ReadAllText(file);
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Core"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("could not locate repo root above " + AppContext.BaseDirectory);
    }

    public void Dispose() => _store.Dispose();

    static readonly CreatureSpeciesDef PlantSpecies = CreatureSpeciesCatalog.All.First(
        s => s.Side == "plant" && s.DeployMode != CreatureDeployMode.HypnoAlly);
    static readonly CreatureSpeciesDef ZombieSpecies = CreatureSpeciesCatalog.All.First(
        s => s.Side == "zombie" && s.DeployMode != CreatureDeployMode.HypnoAlly);

    (long PlayerId, string InstanceId) Mint(CreatureSpeciesDef species, string suffix)
    {
        var playerId = _store.CreatePlayer("parity-" + suffix).Id;
        var (specimen, _) = _store.MintCreature(playerId, new CreatureMintSpec
        {
            SpeciesId = species.SpeciesId,
            Side = species.Side,
            GameTypeId = species.GameTypeId,
            Rarity = species.BaseRarity.ToId(),
            Variant = "normal",
            ElementPrimary = species.ElementPrimary.ToElementId(),
            ElementSecondary = species.ElementSecondary?.ToElementId(),
            TraitIds = new List<string> { species.TraitPool[0] },
            Origin = "test",
        });
        return (playerId, specimen.Actor.InstanceId);
    }

    AptitudeAllocation LawnPath(long playerId, string instanceId, StatSide side)
    {
        var ptr = "ptr-" + instanceId;
        var source = new SpeciesAllocationSource(
            resolveSpeciesId: (_, _) => throw new InvalidOperationException("must not be called for a Bound ctx"),
            resolveSpeciesAllocation: (_, _) => throw new InvalidOperationException("must not be called for a Bound ctx"),
            // EP4.18 (R23): the production delegate is EMPIRE-keyed now, so this fixture is too — for a
            // human-owned specimen it reads exactly the row the old human-keyed form read.
            resolveCommanderAllocation: empire => _store.CommanderPoolFor(
                new FusionRpg.Core.Saves.EmpireRef(new FusionRpg.Core.Saves.SaveId(playerId), empire)).Allocation,
            reportUnconfigured: _ => Assert.Fail("must not report for a Bound ctx"),
            resolveBoundInstanceId: entityKey => entityKey == ptr ? instanceId : null,
            resolveUniqueAllocation: id => _store.LoadAllocation(AllocationScope.UniqueCreature, id),
            resolveSpecimenOwnerEmpire: entityKey =>
                entityKey == ptr ? _store.SpecimenOwnerEmpire(instanceId)?.Empire : null);
        var ctx = new StatContext { Side = side, TypeId = 0, EntityKey = ptr, PlayerId = playerId };
        return source.Resolve(ctx);
    }

    AptitudeAllocation WebSquadPath(long playerId, string instanceId)
    {
        var (ok, reason, squad, _) = _service.BuildSquad(playerId, new[] { instanceId });
        Assert.True(ok, reason);
        var entry = Assert.Single(squad!);
        return entry.HubInputs!.Aptitude;
    }

    AptitudeAllocation SheetPath(string instanceId, long playerId) =>
        UniqueActorHubCompose.ResolveAptitudeAllocation(_store, instanceId, playerId);

    /// <summary>SP1.5's fourth leg — `WorldTurnHubInputsFor`, the locked public form of the SAME
    /// extraction `RpgStore.WorldTurns.cs`'s real `CommitWorldTurn` delegate calls, not a
    /// re-implementation.</summary>
    AptitudeAllocation WorldTurnPath(long playerId, string instanceId, string speciesId)
    {
        var member = new WorldEntityMember { SpeciesId = speciesId, Level = 1, Hp = 100, InstanceId = instanceId };
        var hub = _store.WorldTurnHubInputsFor(member, playerId);
        Assert.NotNull(hub);
        return hub!.Aptitude;
    }

    [Fact]
    public void Plant_side_human_owned_unique_gets_equal_aptitude_across_all_four_paths()
    {
        var (playerId, instanceId) = Mint(PlantSpecies, "plant");
        _store.SaveAllocation(AllocationScope.Commander, AptitudeEndpoints.ScopeKey(playerId),
            AptitudeAllocation.Single(AllocationScope.Commander, "Might", 40));
        _store.SaveAllocation(AllocationScope.UniqueCreature, instanceId,
            AptitudeAllocation.Single(AllocationScope.UniqueCreature, "Vigor", 25));

        var lawn = LawnPath(playerId, instanceId, StatSide.Plant);
        var webSquad = WebSquadPath(playerId, instanceId);
        var sheet = SheetPath(instanceId, playerId);
        var worldTurn = WorldTurnPath(playerId, instanceId, PlantSpecies.SpeciesId);

        Assert.Equal(lawn.Entries, webSquad.Entries);
        Assert.Equal(lawn.Entries, sheet.Entries);
        Assert.Equal(lawn.Entries, worldTurn.Entries);
        Assert.Equal(40, lawn.PointsAt(AllocationScope.Commander, "Might"));
        Assert.Equal(25, lawn.PointsAt(AllocationScope.UniqueCreature, "Vigor"));
    }

    [Fact]
    public void Zombie_side_human_owned_unique_gets_equal_aptitude_across_all_four_paths()
    {
        var (playerId, instanceId) = Mint(ZombieSpecies, "zombie");
        _store.SaveAllocation(AllocationScope.Commander, AptitudeEndpoints.ScopeKey(playerId),
            AptitudeAllocation.Single(AllocationScope.Commander, "Might", 40));
        _store.SaveAllocation(AllocationScope.UniqueCreature, instanceId,
            AptitudeAllocation.Single(AllocationScope.UniqueCreature, "Vigor", 25));

        var lawn = LawnPath(playerId, instanceId, StatSide.Zombie);
        var webSquad = WebSquadPath(playerId, instanceId);
        var sheet = SheetPath(instanceId, playerId);
        var worldTurn = WorldTurnPath(playerId, instanceId, ZombieSpecies.SpeciesId);

        Assert.Equal(lawn.Entries, webSquad.Entries);
        Assert.Equal(lawn.Entries, sheet.Entries);
        Assert.Equal(lawn.Entries, worldTurn.Entries);
        // The named behaviour change (SP1.2/SP1.3/SP1.4): a zombie-side, human-owned unique STILL
        // carries the human commander term on every path -- ownership decides, never the board side.
        Assert.Equal(40, lawn.PointsAt(AllocationScope.Commander, "Might"));
        Assert.Equal(25, lawn.PointsAt(AllocationScope.UniqueCreature, "Vigor"));
    }

    [Fact]
    public void A_zomboss_owned_unique_carries_his_pool_on_all_four_paths_and_never_the_players()
    {
        // ai-empire-species EP4.18 (R23, R4 mirrored) — test 11's remaining legs, on the SAME parity
        // fixture SP1.4/SP1.5 built: the lawn seam (`SpeciesAllocationSource`), the web squad, the sheet
        // and the world-turn provider must agree, and for a ZOMBOSS-owned specimen they must agree on HIS
        // empire's pool rather than the human player's.
        AptitudePresetTuningHub.Configure(AptitudePresetTuningLoader.Parse(
            File.ReadAllText(Path.Combine(FindRepoRoot(), "data", "tuning", "aptitude-presets.v2.json"))));
        var (playerId, instanceId) = Mint(ZombieSpecies, "zomboss-owned");
        using (var db = FusionRpg.Data.Sqlite.SqliteConnectionFactory.Open(_store.HotPath))
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "UPDATE rpg_unique_actors SET empire_id = $e WHERE instance_id = $i;";
            cmd.Parameters.AddWithValue("$e", FusionRpg.Core.Commanders.EmpireId.Zomboss.Value);
            cmd.Parameters.AddWithValue("$i", instanceId);
            Assert.Equal(1, cmd.ExecuteNonQuery());
        }

        // The HUMAN's pool, distinctive, plus his own specimen allocation.
        _store.SaveAllocation(AllocationScope.Commander, AptitudeEndpoints.ScopeKey(playerId),
            AptitudeAllocation.Single(AllocationScope.Commander, "Onslaught", 40));
        _store.SaveAllocation(AllocationScope.UniqueCreature, instanceId,
            AptitudeAllocation.Single(AllocationScope.UniqueCreature, "Vigor", 25));
        // The host's one empire-keyed Theta read (EP4.16's ActorIndexFor, wired at the composition root).
        _store.ConfigureActorTheta((_, empire) =>
            empire == FusionRpg.Core.Commanders.EmpireId.Zomboss ? 100 : 0);

        var expectedPool = _store.CommanderPoolFor(new FusionRpg.Core.Saves.EmpireRef(
            new FusionRpg.Core.Saves.SaveId(playerId), FusionRpg.Core.Commanders.EmpireId.Zomboss)).Allocation;
        Assert.True(expectedPool.TotalForScope(AllocationScope.Commander) > 0,
            "a non-zero Theta must price a real Zomboss pool, or this proves nothing");

        var lawn = LawnPath(playerId, instanceId, StatSide.Zombie);
        var webSquad = WebSquadPath(playerId, instanceId);
        var sheet = SheetPath(instanceId, playerId);
        var worldTurn = WorldTurnPath(playerId, instanceId, ZombieSpecies.SpeciesId);

        Assert.Equal(lawn.Entries, webSquad.Entries);
        Assert.Equal(lawn.Entries, sheet.Entries);
        Assert.Equal(lawn.Entries, worldTurn.Entries);
        Assert.Equal(
            expectedPool.Entries.Select(e => (e.Scope, e.AptitudeId, e.Points)),
            lawn.Entries.Where(e => e.Scope == AllocationScope.Commander)
                .Select(e => (e.Scope, e.AptitudeId, e.Points)));
        Assert.Equal(25, lawn.PointsAt(AllocationScope.UniqueCreature, "Vigor"));
    }
}
