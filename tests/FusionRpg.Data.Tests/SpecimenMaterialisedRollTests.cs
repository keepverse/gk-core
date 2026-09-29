using FusionRpg.Contracts;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Saves;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// WAVE F2.2 (creature-standalone, 2026-09-07) named the gap; `species-progression` SP0.5 restates it
/// against the ledger. Two distinct questions share the same underlying species-passive roll, and they
/// must not answer alike:
///
/// <list type="bullet">
/// <item><b><see cref="RpgStore.GetSpecimenLedgerRoll"/></b> (the sheet-compose seam,
/// <c>UniqueActorHubCompose</c>) — a specimen's OWN empire's ledger instance for its species, and
/// NOTHING else. A specimen whose species has no <c>rpg_player_species_mod</c> row (a non-fuser)
/// composes no per-save roll at all (spec-species-mod-ledger.md ruling behaviour 1).</item>
/// <item><b><see cref="RpgStore.PickSourceAtoms"/></b> (the fusion pick-source seam) — the empire's
/// ledger instance if it has one, <i>or else</i> the delayed <c>SpeciesRollPreview</c>. A preview is
/// legitimate here because nothing has happened yet; the caller is asking what COULD be picked, not
/// what already is.</item>
/// </list>
/// </summary>
public class SpecimenMaterialisedRollTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public SpecimenMaterialisedRollTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    const int PinTheta = 20;

    void SeedSpecies(string speciesId, int amount)
    {
        var atomId = $"atom.{speciesId}-vitality.t1";
        Assert.True(_store.UpsertAtom(new AtomRow
        {
            AtomId = atomId, KindId = "stat.modify", FamilyId = $"atom.{speciesId}-vitality", Tier = 1,
            Name = $"{speciesId} Vitality", ParamsJson = $$"""{"channel":"maxHp","op":"flat","amount":{{amount}}}""",
        }).IsOk);

        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = $"species-passive.{speciesId}", Kind = ContainerKind.SpeciesPassive,
            Atoms = new[] { new ContainerAtomRow(1, atomId) },
        }).IsOk);
    }

    (long PlayerId, string SpecimenId) MintSpecimen(string speciesId, int typeId)
    {
        var player = _store.CreatePlayer("Owner");
        var (specimen, _) = _store.MintCreature(player.Id, new CreatureMintSpec
        {
            SpeciesId = speciesId, Side = "plant", GameTypeId = typeId, Rarity = "sprout",
            Variant = "normal", ElementPrimary = "earth", TraitIds = new List<string>(), Origin = "test",
        });
        return (player.Id, specimen.Actor.InstanceId);
    }

    /// <summary>Test-only bridge to a REAL, persisted `effect_instance`, mirroring the exact
    /// two-INSERT shape `ExecuteFusion` itself writes (`RpgStore.Fusion.cs`) — SP0.6 retired the only
    /// OTHER thing that used to persist one for a test this narrowly scoped
    /// (`MaterialisePlayerSpecies`), so this file writes its own fixture instance directly rather than
    /// standing up a full fusion recipe (`FusionInheritancePicksTests`' own, separate scope). One fixed
    /// atom only — this file's own <see cref="SeedSpecies"/> shape is never pooled.</summary>
    string InsertRealInstance(string speciesId, string atomId)
    {
        var instanceId = Guid.NewGuid().ToString("N");
        using var raw = SqliteConnectionFactory.Open(_store.HotPath);
        using (var cmd = raw.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO effect_instance
                  (instance_id, container_id, roll_seed, catalog_revision, created_utc, origin,
                   theta_content, content_scale_milli)
                VALUES ($id, $c, 1, 0, $utc, 'drop', $theta, 0);
                """;
            cmd.Parameters.AddWithValue("$id", instanceId);
            cmd.Parameters.AddWithValue("$c", $"species-passive.{speciesId}");
            cmd.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$theta", PinTheta);
            cmd.ExecuteNonQuery();
        }
        using (var cmd = raw.CreateCommand())
        {
            cmd.CommandText =
                "INSERT INTO effect_instance_atom (instance_id, seq, atom_id, values_json, power_json) " +
                "VALUES ($id, 1, $atom, '{}', NULL);";
            cmd.Parameters.AddWithValue("$id", instanceId);
            cmd.Parameters.AddWithValue("$atom", atomId);
            cmd.ExecuteNonQuery();
        }
        return instanceId;
    }

    [Fact]
    public void A_real_specimens_own_instanceId_resolves_to_its_empires_ledger_instance()
    {
        SeedSpecies("peashooter", 42);
        var (playerId, specimenId) = MintSpecimen("peashooter", 0);
        var instanceId = InsertRealInstance("peashooter", "atom.peashooter-vitality.t1");
        var owner = new EmpireRef(new SaveId(playerId), _store.HumanEmpireOf(playerId));
        Assert.True(_store.AppendSpeciesMod(owner, "peashooter",
            FusionRpg.Core.Creatures.Layers.SpeciesModMechanism.FusionPick,
            correlationId: instanceId, instanceId: instanceId, catalogRevision: 0));

        var roll = _store.GetSpecimenLedgerRoll(specimenId);

        Assert.NotNull(roll);
        Assert.Equal("peashooter", roll!.SpeciesId);
        var expected = _store.GetInstance(instanceId)!;
        Assert.Equal(expected.ContentFingerprint(), roll.Instance.ContentFingerprint());
        // Not the species' generic pool — a real, ledgered roll's own atoms.
        Assert.Contains(roll.Instance.Atoms, a => a.AtomId == "atom.peashooter-vitality.t1");
    }

    [Fact]
    public void A_specimen_whose_species_has_no_ledger_row_composes_nothing_ruling_behaviour_1()
    {
        SeedSpecies("peashooter", 10);
        var (_, specimenId) = MintSpecimen("peashooter", 0);
        // A non-fuser: deliberately no AppendSpeciesMod call for this species.

        var roll = _store.GetSpecimenLedgerRoll(specimenId);

        Assert.Null(roll);
    }

    [Fact]
    public void An_unknown_instanceId_returns_null_not_a_crash()
    {
        var roll = _store.GetSpecimenLedgerRoll("not-a-real-instance-id");

        Assert.Null(roll);
    }

    [Fact]
    public void A_bare_unique_actor_with_no_creature_profile_returns_null_not_a_crash()
    {
        var player = _store.CreatePlayer("Owner");
        var actor = _store.CreateUniqueActor(player.Id, "plant", typeId: 1);

        var roll = _store.GetSpecimenLedgerRoll(actor.InstanceId);

        Assert.Null(roll);
    }

    [Fact]
    public void A_specimens_pickable_atoms_are_its_empires_ledger_instance_or_else_the_preview()
    {
        // The OTHER seam (PickSourceAtoms) sits beside GetSpecimenLedgerRoll on purpose: it answers "what
        // could be picked", so it previews when there is no ledger row yet — the exact case the sheet
        // join above must NOT compose.
        SeedSpecies("peashooter", 7);
        var (playerId, specimenId) = MintSpecimen("peashooter", 0);
        Assert.Null(_store.GetSpecimenLedgerRoll(specimenId));

        var previewed = _store.PickSourceAtoms(playerId, "peashooter");
        Assert.Contains(previewed, a => a.AtomId == "atom.peashooter-vitality.t1");

        var instanceId = InsertRealInstance("peashooter", "atom.peashooter-vitality.t1");
        var owner = new EmpireRef(new SaveId(playerId), _store.HumanEmpireOf(playerId));
        Assert.True(_store.AppendSpeciesMod(owner, "peashooter",
            FusionRpg.Core.Creatures.Layers.SpeciesModMechanism.FusionPick,
            correlationId: instanceId, instanceId: instanceId, catalogRevision: 0));

        var ledgered = _store.PickSourceAtoms(playerId, "peashooter");
        Assert.Equal(_store.GetInstance(instanceId)!.Atoms.Select(a => a.AtomId).ToHashSet(StringComparer.Ordinal),
            ledgered.Select(a => a.AtomId).ToHashSet(StringComparer.Ordinal));
        // Now that a ledger row exists, the sheet join composes it too.
        Assert.NotNull(_store.GetSpecimenLedgerRoll(specimenId));
    }
}
