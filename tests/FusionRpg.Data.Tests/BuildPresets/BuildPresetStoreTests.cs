using FusionRpg.Core.Aura;
using FusionRpg.Core.BuildPresets;
using FusionRpg.Core.Commanders;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Items;
using FusionRpg.Core.Saves;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using Xunit;

namespace FusionRpg.Data.Tests.BuildPresets;

/// <summary>
/// build-preset BP1.11 — the two-table build-preset library over the in-memory store. These tests
/// exercise shape, identity, validation-on-read, soft refusal, and non-cascading deletion through
/// the same SQL store the Server routes call.
/// </summary>
[Trait("VerificationId", "data.build-preset")]
public sealed class BuildPresetStoreTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    readonly long _saveId;
    readonly EmpireRef _owner;
    readonly string _playerKey;

    public BuildPresetStoreTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _saveId = _store.GetCurrentPlayerId();
        _owner = new EmpireRef(new SaveId(_saveId), _store.HumanEmpireOf(_saveId));
        _playerKey = _saveId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        BuildPresetTuningHub.Configure(new BuildPresetTuning(1, 1, SoftMaxBuildPresets: 32));
        AptitudePresetTuningHub.Configure(new AptitudePresetTuning(
            1, 1, SoftMaxPresets: 32, DefaultRowAbsMax: 100,
            new AssignLadderTuning(new[] { AptitudeAutoAssignRules.Even })));
        CreatureSpeciesCatalog.ConfigureFromCompiledDefault();
    }

    public void Dispose() => _testStore.Dispose();

    [Fact]
    public void Save_reads_back_every_piece_in_ordinal_order_independent_of_request_order()
    {
        var patron = _store.CreateUniqueActor(_saveId, "plant", 1).InstanceId;
        SeedAptitudePreset("apt.commander");
        SeedItemLoadout("loadout.gear");
        var pieces = new[]
        {
            Piece(BuildPresetPieceKind.Skills, "player", "Might", 0),
            Piece(BuildPresetPieceKind.Patron, "", patron, 0),
            Piece(BuildPresetPieceKind.Aptitudes, "commander:", "apt.commander", 0),
            Piece(BuildPresetPieceKind.Field, "", patron, 0),
            Piece(BuildPresetPieceKind.Field, "", "field-b", 1),
            Piece(BuildPresetPieceKind.Gear, patron, "loadout.gear", 0),
        };

        Assert.Equal("", Save("bp-roundtrip", "Fire lean", pieces, isCreate: true));

        var read = _store.GetBuildPresetValidated("bp-roundtrip", _owner);
        Assert.NotNull(read);
        Assert.Equal(
            new[]
            {
                BuildPresetPieceKind.Patron,
                BuildPresetPieceKind.Field,
                BuildPresetPieceKind.Field,
                BuildPresetPieceKind.Aptitudes,
                BuildPresetPieceKind.Gear,
                BuildPresetPieceKind.Skills,
            },
            read!.Pieces.Where(p => p.Kind.HasValue).Select(p => p.Kind!.Value));
        Assert.Equal(new long[] { 0, 0, 1, 0, 0, 0 }, read.Pieces.Select(p => p.Ordinal));
        Assert.All(Enum.GetValues<BuildPresetPieceKind>(), kind =>
            Assert.Contains(read.Pieces, piece =>
                piece.Kind == kind && piece.State == BuildPresetPieceState.Present));
    }

    [Fact]
    public void A_future_stored_kind_reads_as_missing_without_dropping_the_row()
    {
        Assert.Equal("", Save("bp-future", "Future", new[]
        {
            Piece(BuildPresetPieceKind.Skills, "player", "Might", 0),
        }, isCreate: true));

        using (var db = SqliteConnectionFactory.Open(_store.HotPath))
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO rpg_build_preset_piece
                  (preset_id, piece_kind, target_ref, ordinal, ref_id)
                VALUES ('bp-future', 'future-kind', '', 0, 'future.ref');
                """;
            cmd.ExecuteNonQuery();
        }

        var read = _store.GetBuildPresetValidated("bp-future", _owner)!;
        Assert.Equal(2, read.Pieces.Count);
        var future = Assert.Single(read.Pieces, piece => piece.Kind is null);
        Assert.Equal(BuildPresetPieceKinds.Unknown, future.KindId);
        Assert.Equal(BuildPresetPieceState.Missing, future.State);
        Assert.Equal("build-preset.piece.missing:unknown", future.Reason);
    }

    [Fact]
    public void The_new_schema_and_rows_survive_a_store_reopen()
    {
        Assert.Equal("", Save("bp-reopen", "Reopen", new[]
        {
            Piece(BuildPresetPieceKind.Skills, "player", "Might", 0),
        }, isCreate: true));

        using var reopened = _testStore.Reopen();
        var read = reopened.GetBuildPresetValidated("bp-reopen", _owner);
        Assert.NotNull(read);
        Assert.Equal("Reopen", read!.Preset.Name);
        Assert.Equal("Might", Assert.Single(read.Pieces).RefId);
    }

    [Fact]
    public void Every_shape_refusal_is_named_and_writes_nothing()
    {
        var patron = _store.CreateUniqueActor(_saveId, "plant", 1).InstanceId;
        var cases = new (string Name, string Reason, BuildPresetPieceRow[] Pieces)[]
        {
            ("empty", "build-preset.empty", Array.Empty<BuildPresetPieceRow>()),
            ("patrons", "build-preset.patron.multiple", new[]
            {
                Piece(BuildPresetPieceKind.Patron, "", patron, 0),
                Piece(BuildPresetPieceKind.Patron, "", patron, 1),
            }),
            ("field", "build-preset.field.shape", new[]
            {
                Piece(BuildPresetPieceKind.Patron, "", patron, 0),
                Piece(BuildPresetPieceKind.Field, "", patron, 1),
            }),
            ("outside", "build-preset.patron.outside-field", new[]
            {
                Piece(BuildPresetPieceKind.Patron, "", patron, 0),
                Piece(BuildPresetPieceKind.Field, "", "other", 0),
            }),
            ("scope", "build-preset.aptitudes.scope", new[]
            {
                Piece(BuildPresetPieceKind.Aptitudes, "unknown:id", "apt", 0),
            }),
            ("skills", "build-preset.skills.shape", new[]
            {
                Piece(BuildPresetPieceKind.Skills, "player", "Might", 1),
            }),
        };

        foreach (var testCase in cases)
        {
            var reason = Save("bp-" + testCase.Name, testCase.Name, testCase.Pieces, isCreate: true);
            Assert.Equal(testCase.Reason, reason);
            Assert.Empty(_store.ListBuildPresets(_owner));
        }

        Assert.Equal("build-preset.name.missing", Save("bp-name", "  ", new[]
        {
            Piece(BuildPresetPieceKind.Skills, "player", "Might", 0),
        }, isCreate: true));
        Assert.Empty(_store.ListBuildPresets(_owner));
    }

    [Fact]
    public void Validate_on_read_keeps_every_row_and_names_each_missing_piece()
    {
        var patron = _store.CreateUniqueActor(_saveId, "plant", 1).InstanceId;
        var field = _store.CreateUniqueActor(_saveId, "plant", 2).InstanceId;
        SeedAptitudePreset("apt.present");
        SeedItemLoadout("loadout.present");
        var knownSpecies = CreatureSpeciesCatalog.All[0].SpeciesId;

        Assert.Equal("", Save("bp-validate", "Validate", new[]
        {
            Piece(BuildPresetPieceKind.Patron, "", patron, 0),
            Piece(BuildPresetPieceKind.Field, "", patron, 0),
            Piece(BuildPresetPieceKind.Aptitudes, "commander:", "apt.present", 0),
            Piece(BuildPresetPieceKind.Aptitudes, "unique:" + field, "apt.present", 0),
            Piece(BuildPresetPieceKind.Aptitudes, "species:" + knownSpecies, "apt.present", 0),
            Piece(BuildPresetPieceKind.Gear, field, "loadout.present", 0),
            Piece(BuildPresetPieceKind.Skills, "player", "skill.missing", 0),
        }, isCreate: true));

        Assert.True(_store.TryRetireUniqueActor(patron).Ok);
        Assert.True(_store.TryRetireUniqueActor(field).Ok);
        Assert.True(_store.DeleteAptitudePreset(_saveId, "apt.present"));
        Assert.True(_store.DeleteLoadout(_playerKey, "loadout.present"));

        var read = _store.GetBuildPresetValidated("bp-validate", _owner);
        Assert.NotNull(read);
        Assert.Equal(7, read!.Pieces.Count);
        Assert.All(read.Pieces, p => Assert.Equal(BuildPresetPieceState.Missing, p.State));
        Assert.All(read.Pieces, p => Assert.StartsWith("build-preset.piece.missing:", p.Reason));
    }

    [Fact]
    public void Deleting_a_build_preset_never_cascades_into_its_referenced_libraries()
    {
        SeedAptitudePreset("apt.shared");
        SeedItemLoadout("loadout.shared");
        Assert.Equal("", Save("bp-delete", "Delete", new[]
        {
            Piece(BuildPresetPieceKind.Aptitudes, "commander:", "apt.shared", 0),
            Piece(BuildPresetPieceKind.Gear, "commander:dave", "loadout.shared", 0),
        }, isCreate: true));

        Assert.True(_store.DeleteBuildPreset(_owner, "bp-delete"));

        Assert.Null(_store.GetBuildPreset("bp-delete", _owner));
        Assert.Empty(_store.GetBuildPresetPieceRows("bp-delete", _owner));
        Assert.NotNull(_store.GetAptitudePreset("apt.shared"));
        Assert.Contains(_store.ListLoadouts(_playerKey), l => l.LoadoutId == "loadout.shared");
    }

    [Fact]
    public void Create_refuses_at_the_loaded_soft_max_without_writing()
    {
        // The hub is process-global, so the lowered cap is restored on every path: leaving it at 1
        // would refuse creates in every later test that shares this assembly's process.
        var restore = BuildPresetTuningHub.Tuning;
        try
        {
            BuildPresetTuningHub.Configure(new BuildPresetTuning(1, 1, SoftMaxBuildPresets: 1));
            var pieces = new[] { Piece(BuildPresetPieceKind.Skills, "player", "Might", 0) };
            Assert.Equal("", Save("bp-one", "One", pieces, isCreate: true));

            Assert.Equal("build-preset.softMax", Save("bp-two", "Two", pieces, isCreate: true));
            Assert.Single(_store.ListBuildPresets(_owner));
        }
        finally
        {
            BuildPresetTuningHub.Configure(restore);
        }
    }

    [Fact]
    public void Another_save_cannot_update_or_delete_the_preset()
    {
        SeedAptitudePreset("apt.owner");
        Assert.Equal("", Save("bp-owned", "Owned", new[]
        {
            Piece(BuildPresetPieceKind.Aptitudes, "commander:", "apt.owner", 0),
        }, isCreate: true));

        var otherSave = _store.CreatePlayer("Other");
        var otherOwner = new EmpireRef(new SaveId(otherSave.Id), _store.HumanEmpireOf(otherSave.Id));
        Assert.Equal("build-preset.owner.mismatch", Save(
            "bp-owned", "Stolen", new[] { Piece(BuildPresetPieceKind.Skills, "player", "Might", 0) },
            isCreate: false, owner: otherOwner));
        Assert.False(_store.DeleteBuildPreset(otherOwner, "bp-owned"));
        Assert.Equal("Owned", _store.GetBuildPreset("bp-owned", _owner)!.Name);
        Assert.Single(_store.GetBuildPresetPieceRows("bp-owned", _owner));
    }

    [Fact]
    public void A_non_human_empire_refuses_before_any_write()
    {
        var ai = _store.EmpiresOf(_saveId).Single(e => e.Controller == EmpireController.Ai);
        var nonHuman = new EmpireRef(new SaveId(_saveId), ai.Empire);

        var error = Assert.Throws<EmpireScopeNotWidened>(() => Save(
            "bp-ai", "AI", new[] { Piece(BuildPresetPieceKind.Skills, "player", "Might", 0) },
            isCreate: true, owner: nonHuman));

        Assert.Equal("rpg_build_preset", error.Table);
        Assert.Empty(_store.ListBuildPresets(_owner));
    }

    [Fact]
    public void Updating_replaces_pieces_and_bumps_revision()
    {
        SeedAptitudePreset("apt.old");
        SeedAptitudePreset("apt.new");
        Assert.Equal("", Save("bp-replace", "Before", new[]
        {
            Piece(BuildPresetPieceKind.Aptitudes, "commander:", "apt.old", 0),
        }, isCreate: true));

        Assert.Equal("", Save("bp-replace", "After", new[]
        {
            Piece(BuildPresetPieceKind.Aptitudes, "commander:", "apt.new", 0),
        }, isCreate: false));
        var read = _store.GetBuildPreset("bp-replace", _owner)!;
        Assert.Equal("After", read.Name);
        Assert.Equal(2, read.Revision);
        Assert.Equal("apt.new", Assert.Single(_store.GetBuildPresetPieceRows("bp-replace", _owner)).RefId);
    }

    string Save(
        string id,
        string name,
        IReadOnlyList<BuildPresetPieceRow> pieces,
        bool isCreate,
        EmpireRef? owner = null)
    {
        var effectiveOwner = owner ?? _owner;
        var row = new RpgBuildPresetRow(
            id, effectiveOwner.Save.Value, effectiveOwner.Empire.Value, name, "2026-09-25T00:00:00Z", 0);
        return _store.SaveBuildPreset(effectiveOwner, row, pieces, isCreate);
    }

    static BuildPresetPieceRow Piece(BuildPresetPieceKind kind, string target, string reference, long ordinal) =>
        new(kind, target, ordinal, reference);

    void SeedAptitudePreset(string id)
    {
        var entries = AptitudeCatalog.All.Select((aptitude, index) =>
            new RpgAptitudePresetEntryRow(
                id, aptitude.Id, 83L + (index < 4 ? 1L : 0L), null, null, null, null)).ToList();
        Assert.Equal("", _store.SaveAptitudePreset(
            new RpgAptitudePresetRow(id, _saveId, id, RpgStore.AptitudePresetKindPlayer,
                "2026-09-25T00:00:00Z", 0), entries, isCreate: true));
    }

    void SeedItemLoadout(string id) =>
        _store.SaveLoadout(
            new RpgItemLoadoutRow(id, _playerKey, id, null, "2026-09-25T00:00:00Z", 0),
            new[] { new RpgItemLoadoutEntryRow(id, ItemRoles.Id(ItemRole.ArmamentPrimary), "stock", "item.test") });
}
