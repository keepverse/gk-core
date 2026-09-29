using FusionRpg.Contracts;
using FusionRpg.Core.Commanders;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Layers;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.World;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// `species-progression` SP1.2 (map C1, `layer-source-selector`) — the C1 regression. `RpgStore.
/// WorldTurns.cs:559-593`'s `HubInputsFor` provider composed `commander + species + specimen` for
/// EVERY district-assault member with an `InstanceId`. Every such member is a unique specimen, and
/// `decisions.md` "Creature progression source" is explicit: a unique specimen NEVER receives the
/// empire species fallback — it composes its own allocation (2a) only. This is asserted directly
/// against <see cref="RpgStore.WorldTurnHubInputsForUnlocked"/> (the extraction `CommitWorldTurn`'s
/// real delegate calls), not through a full turn-commit simulation of an actual district assault —
/// the same production code, independently testable, matching this repo's own established pattern for
/// an inline closure that needs a falsifier (e.g. `RpgStore.SpeciesMods.cs`'s `PickSourceAtomsUnlocked`).
/// </summary>
public class WorldTurnHubInputsForTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public WorldTurnHubInputsForTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        // WorldTurnHubInputsForUnlocked -> EffectiveSpeciesAllocationUnlocked reads AptitudeTuningHub.Tuning;
        // that hub is configured ONCE for the whole assembly by `ContractTuningTestBootstrap`, so this file
        // no longer carries a private copy of the call (test-substrate TVB-F16).
    }

    public void Dispose() => _testStore.Dispose();

    /// <summary>The same "latest published version, never a pinned literal" lookup the aptitudes path
    /// above uses — clamped to the one domain's own prefix so a publish cannot break this file.</summary>
    static string LatestTuningPath(string domain)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var tuningDir = Path.Combine(dir.FullName, "data", "tuning");
            if (Directory.Exists(tuningDir))
            {
                var best = Directory.GetFiles(tuningDir, domain + ".v*.json")
                    .Select(f => (Path: f, V: int.TryParse(
                        Path.GetFileNameWithoutExtension(f).Split(".v").Last(), out var v) ? v : -1))
                    .Where(x => x.V >= 0).OrderByDescending(x => x.V).FirstOrDefault();
                if (best.Path is not null) return best.Path;
            }
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"could not find data/tuning/{domain}.v*.json above " + AppContext.BaseDirectory);
    }

    /// <summary>Stamps a bare test specimen as Zomboss-owned — the STATE save-identity's real mint
    /// leaves on `rpg_unique_actors.empire_id`, and the owner input `ProgressionLayerSelector` reads.</summary>
    void StampZombossOwned(string instanceId)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE rpg_unique_actors SET empire_id = $e WHERE instance_id = $i;";
        cmd.Parameters.AddWithValue("$e", EmpireId.Zomboss.Value);
        cmd.Parameters.AddWithValue("$i", instanceId);
        Assert.Equal(1, cmd.ExecuteNonQuery());
    }

    FusionRpg.Core.Battle.BattleHubInputs? Invoke(WorldEntityMember member, long playerId)
    {
        using var raw = SqliteConnectionFactory.Open(_store.HotPath);
        return _store.WorldTurnHubInputsForUnlocked(raw, member, playerId);
    }

    [Fact]
    public void A_unique_specimen_with_a_levelled_species_composes_commander_and_its_own_allocation_only()
    {
        var player = _store.CreatePlayer("Owner");
        var actor = _store.CreateUniqueActor(player.Id, "plant", typeId: 0);

        // The species itself carries a real, nonzero CreatureType-scope allocation -- exactly the
        // fallback map C1 says a unique must never receive.
        _store.SaveAllocation(AllocationScope.CreatureType,
            SpeciesAllocation.ScopeKey(player.Id, EmpireId.Dave, "peashooter"),
            AptitudeAllocation.Single(AllocationScope.CreatureType, "Might", 500));

        // The specimen's OWN allocation, which SHOULD reach the compose.
        _store.SaveAllocation(AllocationScope.UniqueCreature, actor.InstanceId,
            AptitudeAllocation.Single(AllocationScope.UniqueCreature, "Vigor", 40));

        // The human commander's allocation, which SHOULD also reach the compose (the member fights
        // for the human empire).
        _store.SaveAllocation(AllocationScope.Commander, $"player:{player.Id}",
            AptitudeAllocation.Single(AllocationScope.Commander, "Onslaught", 10));

        var member = new WorldEntityMember
        {
            SpeciesId = "peashooter", Level = 4, Hp = 100, InstanceId = actor.InstanceId,
        };

        var hub = Invoke(member, player.Id);

        Assert.NotNull(hub);
        // The falsifier: today's code composes commander + species + specimen, so this reads 500
        // against unfixed code and must read 0 once the selector decides the unique gets no species term.
        Assert.Equal(0, hub!.Aptitude.TotalForScope(AllocationScope.CreatureType));
        Assert.Equal(40, hub.Aptitude.PointsAt(AllocationScope.UniqueCreature, "Vigor"));
        Assert.Equal(10, hub.Aptitude.PointsAt(AllocationScope.Commander, "Onslaught"));
    }

    [Fact]
    public void A_member_with_no_instanceId_composes_nothing_unchanged()
    {
        var player = _store.CreatePlayer("Owner");
        var member = new WorldEntityMember { SpeciesId = "peashooter", Level = 1, Hp = 100, InstanceId = null };

        var hub = Invoke(member, player.Id);

        Assert.Null(hub);
    }

    [Fact]
    public void A_specimen_with_no_empire_id_stamped_yet_falls_back_to_the_human_empire()
    {
        // CreateUniqueActor is a bare test seam (unlike the real MintCreatureUnlocked/CreateUniqueActor
        // production path, it stamps no empire_id) -- SpecimenOwnerEmpireUnlocked reads null for it, and
        // the provider's own fallback (HumanEmpireOf) is what this fact pins: a pre-R3-legacy row (or a
        // bare test row) still reads as the human empire, never silently dropping the commander term. A
        // REAL Zomboss-minted specimen's stamped empire_id is proven end to end by save-identity's own
        // AiEmpireSpecimenTests, not restated here.
        var player = _store.CreatePlayer("Owner");
        var actor = _store.CreateUniqueActor(player.Id, "zombie", typeId: 0);

        _store.SaveAllocation(AllocationScope.UniqueCreature, actor.InstanceId,
            AptitudeAllocation.Single(AllocationScope.UniqueCreature, "Vigor", 25));
        _store.SaveAllocation(AllocationScope.Commander, $"player:{player.Id}",
            AptitudeAllocation.Single(AllocationScope.Commander, "Onslaught", 999));

        var member = new WorldEntityMember
        {
            SpeciesId = "conezombie", Level = 1, Hp = 100, InstanceId = actor.InstanceId,
        };

        var hub = Invoke(member, player.Id);

        Assert.NotNull(hub);
        Assert.Equal(999, hub!.Aptitude.PointsAt(AllocationScope.Commander, "Onslaught"));
        Assert.Equal(25, hub.Aptitude.PointsAt(AllocationScope.UniqueCreature, "Vigor"));
    }

    // ---- species-progression step 6.2 (SP6.7) -- BattleHubInputs.SpeciesLayers -----------------

    /// <summary>Mints a REAL specimen (unlike CreateUniqueActor's bare test seam above, MintCreature
    /// populates the creature profile GetCreatureProfile reads) and fuses it for real, mirroring
    /// SpeciesLayerTransportTests.cs's own Fuse helper: a seq-1 core atom (1a) + a seq-2 rolled pick
    /// atom (1b), a real materialised effect_instance, and a real ledger row.</summary>
    string MintAndFuse(long playerId, string speciesId, string pickChannel, long pickAmount, out string instanceId)
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
        instanceId = specimen.Actor.InstanceId;

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

        var owner = new FusionRpg.Core.Saves.EmpireRef(
            new FusionRpg.Core.Saves.SaveId(playerId), _store.HumanEmpireOf(playerId));
        Assert.True(_store.AppendSpeciesMod(owner, speciesId, SpeciesModMechanism.FusionPick,
            correlationId: effectInstanceId, instanceId: effectInstanceId, catalogRevision: 0));
        return speciesId;
    }

    [Fact]
    public void A_world_turn_unique_carries_1a_and_1b_of_its_owner_empire()
    {
        var player = _store.CreatePlayer("Owner");
        var speciesId = MintAndFuse(player.Id, "peashooter", "combat.power.omni", 40, out var instanceId);

        var member = new WorldEntityMember { SpeciesId = speciesId, Level = 4, Hp = 100, InstanceId = instanceId };
        var hub = Invoke(member, player.Id);

        Assert.NotNull(hub);
        Assert.NotNull(hub!.SpeciesLayers);
        Assert.Contains(hub.SpeciesLayers!, r => r.SourceId == "species-base:peashooter" && r.Channel == "resource.max.stamina");
        Assert.Contains(hub.SpeciesLayers!, r => r.SourceId == "species-player:peashooter:fusion-pick" && r.Channel == "combat.power.omni");
    }

    [Fact]
    public void A_world_turn_unique_that_never_fused_carries_no_species_layers()
    {
        var player = _store.CreatePlayer("Owner");
        var actor = _store.CreateUniqueActor(player.Id, "plant", typeId: 0);
        var member = new WorldEntityMember { SpeciesId = "peashooter", Level = 1, Hp = 100, InstanceId = actor.InstanceId };

        var hub = Invoke(member, player.Id);

        Assert.NotNull(hub);
        Assert.Null(hub!.SpeciesLayers);
    }

    // ---- ai-empire-species EP4.18 (R23) — the empire-keyed commander pool on the siege seam --------

    [Fact]
    public void A_zomboss_owned_siege_member_carries_his_own_empires_commander_pool()
    {
        // Test 11's SIEGE leg. Before EP4.18 the commander term was gated to the human empire (the
        // selector), so a Zomboss-owned member composed no commander term at all; now it carries HIS
        // empire's pool, through the ONE empire-keyed read at this store's configured host Theta (EP4.16)
        // — and never the human's.
        AptitudePresetTuningHub.Configure(AptitudePresetTuningLoader.Parse(
            File.ReadAllText(LatestTuningPath("aptitude-presets"))));
        var player = _store.CreatePlayer("Owner");
        var actor = _store.CreateUniqueActor(player.Id, "zombie", typeId: 0);
        StampZombossOwned(actor.InstanceId);
        _store.SaveAllocation(AllocationScope.UniqueCreature, actor.InstanceId,
            AptitudeAllocation.Single(AllocationScope.UniqueCreature, "Vigor", 25));
        // The HUMAN's pool, distinctive: it must not appear on this member.
        _store.SaveAllocation(AllocationScope.Commander, $"player:{player.Id}",
            AptitudeAllocation.Single(AllocationScope.Commander, "Onslaught", 40));
        _store.ConfigureActorTheta((_, empire) => empire == EmpireId.Zomboss ? 100 : 0);

        var member = new WorldEntityMember
        {
            SpeciesId = "conezombie", Level = 1, Hp = 100, InstanceId = actor.InstanceId,
        };
        var hub = Invoke(member, player.Id);

        var expectedPool = _store.CommanderPoolFor(new FusionRpg.Core.Saves.EmpireRef(
            new FusionRpg.Core.Saves.SaveId(player.Id), EmpireId.Zomboss)).Allocation;
        Assert.NotNull(hub);
        Assert.True(expectedPool.TotalForScope(AllocationScope.Commander) > 0,
            "a non-zero Theta must price a real Zomboss pool, or this proves nothing");
        Assert.Equal(
            expectedPool.Entries.Select(e => (e.Scope, e.AptitudeId, e.Points)),
            hub!.Aptitude.Entries.Where(e => e.Scope == AllocationScope.Commander)
                .Select(e => (e.Scope, e.AptitudeId, e.Points)));
        // Never the human's pool: the member's Commander scope differs from the human allocation the
        // fixture seeded (whose own total is a single 40-point Onslaught).
        var humanPool = _store.LoadAllocation(AllocationScope.Commander, $"player:{player.Id}");
        Assert.NotEqual(
            humanPool.Entries.Select(e => (e.Scope, e.AptitudeId, e.Points)),
            hub.Aptitude.Entries.Where(e => e.Scope == AllocationScope.Commander)
                .Select(e => (e.Scope, e.AptitudeId, e.Points)));
        Assert.Equal(25, hub.Aptitude.PointsAt(AllocationScope.UniqueCreature, "Vigor")); // its own, still
    }

    [Fact]
    public void A_zomboss_owned_siege_member_with_no_theta_or_a_zero_budget_is_byte_identical_to_before_r23()
    {
        // Test 14's classification, at the seam: with no host Theta wired (every existing fixture and
        // tool), or with a zero budget (a never-credited commander), the pool read is empty and this
        // compose is byte-identical to the pre-R23 `Empty` — so no pin can move for those saves.
        AptitudePresetTuningHub.Configure(AptitudePresetTuningLoader.Parse(
            File.ReadAllText(LatestTuningPath("aptitude-presets"))));
        var player = _store.CreatePlayer("Owner");
        var actor = _store.CreateUniqueActor(player.Id, "zombie", typeId: 0);
        StampZombossOwned(actor.InstanceId);
        _store.SaveAllocation(AllocationScope.UniqueCreature, actor.InstanceId,
            AptitudeAllocation.Single(AllocationScope.UniqueCreature, "Vigor", 25));
        _store.SaveAllocation(AllocationScope.Commander, $"player:{player.Id}",
            AptitudeAllocation.Single(AllocationScope.Commander, "Onslaught", 40));
        var member = new WorldEntityMember
        {
            SpeciesId = "conezombie", Level = 1, Hp = 100, InstanceId = actor.InstanceId,
        };

        var unwired = Invoke(member, player.Id);           // no ConfigureActorTheta at all
        _store.ConfigureActorTheta((_, _) => 0);           // wired, but a zero budget
        var zeroBudget = Invoke(member, player.Id);

        Assert.NotNull(unwired);
        Assert.NotNull(zeroBudget);
        Assert.Equal(
            unwired!.Aptitude.Entries.Select(e => (e.Scope, e.AptitudeId, e.Points)),
            zeroBudget!.Aptitude.Entries.Select(e => (e.Scope, e.AptitudeId, e.Points)));
        Assert.Equal(0, unwired.Aptitude.TotalForScope(AllocationScope.Commander));
        Assert.Equal(25, unwired.Aptitude.PointsAt(AllocationScope.UniqueCreature, "Vigor"));
    }
}
