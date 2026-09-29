using FusionRpg.Core.Creatures.Layers;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Saves;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>species-progression step 6.2, Transport (SP6.3) — `RpgStore.SpeciesLayerTransport`, the
/// Data-layer read `AptitudeEndpoints.cs` wraps into the `speciesLayers` wire field. Same fixture
/// shape as `SpecimenMaterialisedRollTests`/`AptitudeEndpointsTests`' own `FuseASpecies` helper: a
/// `stat.derived` core atom at seq 1 (1a's own template row) and a rolled pick atom at seq 2 (strictly
/// after the template's own seq, matching the real Instantiator's numbering and
/// `SpeciesLayerProjector.ProjectPlayerMod`'s "1a never repeats 1a" filter).</summary>
public class SpeciesLayerTransportTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public SpeciesLayerTransportTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose() => _testStore.Dispose();

    (string SpeciesId, string InstanceId) Fuse(long saveId, string speciesId, string pickChannel, long pickAmount)
    {
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

        var instanceId = Guid.NewGuid().ToString("N");
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
                cmd.Parameters.AddWithValue("$id", instanceId);
                cmd.Parameters.AddWithValue("$c", $"species-passive.{speciesId}");
                cmd.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("o"));
                cmd.ExecuteNonQuery();
            }
            using (var cmd = raw.CreateCommand())
            {
                cmd.CommandText =
                    "INSERT INTO effect_instance_atom (instance_id, seq, atom_id, values_json, power_json) " +
                    "VALUES ($id, 2, $atom, '{}', NULL);";
                cmd.Parameters.AddWithValue("$id", instanceId);
                cmd.Parameters.AddWithValue("$atom", pickAtomId);
                cmd.ExecuteNonQuery();
            }
        }

        var owner = new EmpireRef(new SaveId(saveId), _store.HumanEmpireOf(saveId));
        Assert.True(_store.AppendSpeciesMod(owner, speciesId, SpeciesModMechanism.FusionPick,
            correlationId: instanceId, instanceId: instanceId, catalogRevision: 0));
        return (speciesId, instanceId);
    }

    [Fact]
    public void AFreshSave_hasEmptyBaseAndMod()
    {
        var player = _store.CreatePlayer("Fresh");
        var (baseRows, mod) = _store.SpeciesLayerTransport(player.Id);
        Assert.Empty(baseRows);
        Assert.Empty(mod);
    }

    [Fact]
    public void AfterAFusion_baseCarriesTheCoreRow_modCarriesThePickRow_keyedByTheRealEmpireId()
    {
        var player = _store.CreatePlayer("Fuser");
        Fuse(player.Id, "melon-pult", "resource.max.hp", 500);
        var humanEmpire = _store.HumanEmpireOf(player.Id).Value;

        var (baseRows, mod) = _store.SpeciesLayerTransport(player.Id);

        Assert.True(baseRows.ContainsKey("melon-pult"));
        Assert.Contains(baseRows["melon-pult"], r => r.Channel == "resource.max.stamina");
        Assert.DoesNotContain(baseRows["melon-pult"], r => r.Channel == "resource.max.hp");

        Assert.True(mod.ContainsKey(humanEmpire));
        Assert.Contains(mod[humanEmpire]["melon-pult"], r => r.Channel == "resource.max.hp" &&
            r.Value is LayerValue.Fixed f && f.Amount == 500);
    }

    [Fact]
    public void AnEmpireWithNoLedgerRows_isAbsentFromMod()
    {
        var player = _store.CreatePlayer("NoFusion");
        var (_, mod) = _store.SpeciesLayerTransport(player.Id);
        Assert.DoesNotContain("zomboss", mod.Keys);
        Assert.DoesNotContain(_store.HumanEmpireOf(player.Id).Value, mod.Keys);
    }

    [Fact]
    public void SaveIsolation_saveBNeverSeesSaveAsRows()
    {
        var a = _store.CreatePlayer("A");
        var b = _store.CreatePlayer("B");
        Fuse(a.Id, "melon-pult", "resource.max.hp", 500);
        Fuse(b.Id, "wallnut", "combat.defense.omni", 300);

        var (baseA, _) = _store.SpeciesLayerTransport(a.Id);
        var (baseB, _) = _store.SpeciesLayerTransport(b.Id);

        Assert.True(baseA.ContainsKey("melon-pult"));
        Assert.False(baseA.ContainsKey("wallnut"));
        Assert.True(baseB.ContainsKey("wallnut"));
        Assert.False(baseB.ContainsKey("melon-pult"));
    }

    [Fact]
    public void SourceIds_areGG49Attributable_baseIsSpeciesBase_modIsSpeciesPlayer()
    {
        var player = _store.CreatePlayer("Attribution");
        Fuse(player.Id, "melon-pult", "resource.max.hp", 500);
        var humanEmpire = _store.HumanEmpireOf(player.Id).Value;

        var (baseRows, mod) = _store.SpeciesLayerTransport(player.Id);

        Assert.All(baseRows["melon-pult"], r => Assert.StartsWith("species-base:", r.SourceId, StringComparison.Ordinal));
        Assert.All(mod[humanEmpire]["melon-pult"], r => Assert.StartsWith("species-player:", r.SourceId, StringComparison.Ordinal));
    }
}
