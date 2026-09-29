using FusionRpg.Contracts;
using FusionRpg.Core.Aura;
using FusionRpg.Core.BuildPresets;
using FusionRpg.Core.Commanders;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Saves;
using Microsoft.Data.Sqlite;

namespace FusionRpg.Data;

/// <summary>One human empire's saved build preset header.</summary>
public sealed record RpgBuildPresetRow(
    string PresetId,
    long SaveId,
    string EmpireId,
    string Name,
    string CreatedUtc,
    long Revision);

/// <summary>One persisted reference row. The kind stays a string so future vocabulary remains readable.</summary>
public sealed record RpgBuildPresetPieceRow(
    string PresetId,
    string PieceKind,
    string TargetRef,
    long Ordinal,
    string RefId);

public enum BuildPresetPieceState
{
    Present,
    Missing,
}

/// <summary>One stored piece plus the result of checking the reference it names.</summary>
public sealed record BuildPresetPieceRead(
    BuildPresetPieceKind? Kind,
    string KindId,
    string TargetRef,
    long Ordinal,
    string RefId,
    BuildPresetPieceState State,
    string Reason);

/// <summary>A header and every stored piece; validation never removes a row.</summary>
public sealed record RpgBuildPresetRead(
    RpgBuildPresetRow Preset,
    IReadOnlyList<BuildPresetPieceRead> Pieces);

/// <summary>
/// A build-preset library row is human-empire state from its first build. The key stays
/// <c>(save_id, empire_id)</c>; widening the API to another controller is a separate reviewed change.
/// </summary>
public sealed class EmpireScopeNotWidened : InvalidOperationException
{
    public string Table { get; }

    public EmpireScopeNotWidened(string table, EmpireRef owner)
        : base($"{table} is human-empire scoped; empire '{owner.Empire.Value}' in save {owner.Save.Value} may not own a row")
    {
        Table = table;
    }
}

public sealed partial class RpgStore
{
    const string BuildPresetTable = "rpg_build_preset";

    void EnsureBuildPresetSchemaUnlocked(SqliteConnection db)
    {
        Exec(db, """
            CREATE TABLE IF NOT EXISTS rpg_build_preset (
              preset_id   TEXT    NOT NULL PRIMARY KEY,
              save_id     INTEGER NOT NULL,
              empire_id   TEXT    NOT NULL,
              name        TEXT    NOT NULL,
              created_utc TEXT    NOT NULL,
              revision    INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS ix_rpg_build_preset_empire
              ON rpg_build_preset(save_id, empire_id);

            CREATE TABLE IF NOT EXISTS rpg_build_preset_piece (
              preset_id  TEXT    NOT NULL,
              piece_kind TEXT    NOT NULL,
              target_ref TEXT    NOT NULL DEFAULT '',
              ordinal    INTEGER NOT NULL DEFAULT 0,
              ref_id     TEXT    NOT NULL,
              PRIMARY KEY (preset_id, piece_kind, target_ref, ordinal)
            );
            """);
    }

    static void RequireHumanBuildPresetOwnerUnlocked(SqliteConnection db, EmpireRef owner)
    {
        var human = HumanEmpireOfOrNull(db, owner.Save.Value);
        if (human is not null && string.Equals(human.Value.Value, owner.Empire.Value, StringComparison.Ordinal))
            return;

        throw new EmpireScopeNotWidened(BuildPresetTable, owner);
    }

    public IReadOnlyList<RpgBuildPresetRow> ListBuildPresets(EmpireRef owner)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            EnsureBuildPresetSchemaUnlocked(db);
            RequireHumanBuildPresetOwnerUnlocked(db, owner);

            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT preset_id, save_id, empire_id, name, created_utc, revision
                FROM rpg_build_preset
                WHERE save_id = $save AND empire_id = $empire
                ORDER BY created_utc, preset_id;
                """;
            cmd.Parameters.AddWithValue("$save", owner.Save.Value);
            cmd.Parameters.AddWithValue("$empire", owner.Empire.Value);

            using var reader = cmd.ExecuteReader();
            var rows = new List<RpgBuildPresetRow>();
            while (reader.Read())
                rows.Add(ReadBuildPreset(reader));
            return rows;
        }
    }

    public RpgBuildPresetRow? GetBuildPreset(string presetId, EmpireRef owner)
    {
        if (string.IsNullOrWhiteSpace(presetId)) return null;
        lock (_gate)
        {
            using var db = OpenUnlocked();
            EnsureBuildPresetSchemaUnlocked(db);
            RequireHumanBuildPresetOwnerUnlocked(db, owner);
            return GetBuildPresetUnlocked(db, presetId, owner);
        }
    }

    RpgBuildPresetRow? GetBuildPresetUnlocked(SqliteConnection db, string presetId, EmpireRef owner)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT preset_id, save_id, empire_id, name, created_utc, revision
            FROM rpg_build_preset
            WHERE preset_id = $id AND save_id = $save AND empire_id = $empire;
            """;
        cmd.Parameters.AddWithValue("$id", presetId.Trim());
        cmd.Parameters.AddWithValue("$save", owner.Save.Value);
        cmd.Parameters.AddWithValue("$empire", owner.Empire.Value);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadBuildPreset(reader) : null;
    }

    static RpgBuildPresetRow ReadBuildPreset(SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetInt64(5));

    public IReadOnlyList<RpgBuildPresetPieceRow> GetBuildPresetPieceRows(string presetId, EmpireRef owner)
    {
        if (string.IsNullOrWhiteSpace(presetId)) return Array.Empty<RpgBuildPresetPieceRow>();
        lock (_gate)
        {
            using var db = OpenUnlocked();
            EnsureBuildPresetSchemaUnlocked(db);
            RequireHumanBuildPresetOwnerUnlocked(db, owner);
            return GetBuildPresetPieceRowsUnlocked(db, presetId.Trim(), owner);
        }
    }

    IReadOnlyList<RpgBuildPresetPieceRow> GetBuildPresetPieceRowsUnlocked(
        SqliteConnection db,
        string presetId,
        EmpireRef owner)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"""
            SELECT p.preset_id, p.piece_kind, p.target_ref, p.ordinal, p.ref_id
            FROM rpg_build_preset_piece p
            JOIN rpg_build_preset h ON h.preset_id = p.preset_id
            WHERE p.preset_id = $id AND h.save_id = $save AND h.empire_id = $empire
            ORDER BY
              CASE p.piece_kind
                WHEN 'patron' THEN 1
                WHEN 'field' THEN 2
                WHEN 'aptitudes' THEN 3
                WHEN 'gear' THEN 4
                WHEN 'skills' THEN 5
                ELSE 6
              END,
              p.target_ref,
              p.ordinal;
            """;
        cmd.Parameters.AddWithValue("$id", presetId);
        cmd.Parameters.AddWithValue("$save", owner.Save.Value);
        cmd.Parameters.AddWithValue("$empire", owner.Empire.Value);

        using var reader = cmd.ExecuteReader();
        var rows = new List<RpgBuildPresetPieceRow>();
        while (reader.Read())
            rows.Add(new RpgBuildPresetPieceRow(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt64(3), reader.GetString(4)));
        return rows;
    }

    public RpgBuildPresetRead? GetBuildPresetValidated(string presetId, EmpireRef owner)
    {
        if (string.IsNullOrWhiteSpace(presetId)) return null;
        lock (_gate)
        {
            using var db = OpenUnlocked();
            EnsureBuildPresetSchemaUnlocked(db);
            RequireHumanBuildPresetOwnerUnlocked(db, owner);

            var preset = GetBuildPresetUnlocked(db, presetId.Trim(), owner);
            if (preset is null) return null;

            var stored = GetBuildPresetPieceRowsUnlocked(db, preset.PresetId, owner);
            var pieces = stored.Select(piece =>
            {
                var parsed = BuildPresetPieceKinds.TryParse(piece.PieceKind, out var kind);
                var kindId = parsed ? BuildPresetPieceKinds.Id(kind) : BuildPresetPieceKinds.Unknown;
                var present = parsed && BuildPresetReferenceResolvesUnlocked(db, owner, kind, piece);
                return new BuildPresetPieceRead(
                    parsed ? kind : null,
                    kindId,
                    piece.TargetRef,
                    piece.Ordinal,
                    piece.RefId,
                    present ? BuildPresetPieceState.Present : BuildPresetPieceState.Missing,
                    present ? "" : $"build-preset.piece.missing:{kindId}");
            }).ToList();

            return new RpgBuildPresetRead(preset, pieces);
        }
    }

    static bool BuildPresetReferenceResolvesUnlocked(
        SqliteConnection db,
        EmpireRef owner,
        BuildPresetPieceKind kind,
        RpgBuildPresetPieceRow piece) => kind switch
        {
            BuildPresetPieceKind.Patron or BuildPresetPieceKind.Field =>
                BuildPresetSpecimenResolvesUnlocked(db, owner, piece.RefId),
            BuildPresetPieceKind.Aptitudes =>
                BuildPresetAptitudeResolvesUnlocked(db, owner, piece),
            BuildPresetPieceKind.Gear =>
                BuildPresetLoadoutResolvesUnlocked(db, owner, piece),
            BuildPresetPieceKind.Skills =>
                ActionExistsUnlocked(db, piece.RefId) || AuraContentCatalog.IsKnown(piece.RefId),
            _ => false,
        };

    static bool BuildPresetSpecimenResolvesUnlocked(SqliteConnection db, EmpireRef owner, string instanceId)
    {
        if (!OwnsSpecimenUnlocked(db, owner, instanceId)) return false;
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT phase FROM rpg_unique_actors WHERE instance_id = $id;";
        cmd.Parameters.AddWithValue("$id", instanceId.Trim());
        return cmd.ExecuteScalar() is string phase
            && !string.Equals(phase, UniqueActorPhases.Retired, StringComparison.Ordinal);
    }

    static bool BuildPresetAptitudeResolvesUnlocked(
        SqliteConnection db,
        EmpireRef owner,
        RpgBuildPresetPieceRow piece)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM rpg_aptitude_preset
            WHERE preset_id = $id AND player_id = $save;
            """;
        cmd.Parameters.AddWithValue("$id", piece.RefId);
        cmd.Parameters.AddWithValue("$save", owner.Save.Value);
        if (Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) == 0) return false;

        var split = piece.TargetRef.IndexOf(':');
        if (split <= 0) return false;
        var scope = piece.TargetRef[..split];
        var scopeKey = piece.TargetRef[(split + 1)..];
        return scope switch
        {
            "commander" => true,
            "unique" => OwnsSpecimenUnlocked(db, owner, scopeKey),
            "species" => CreatureSpeciesCatalog.All.Any(s => s.SpeciesId == scopeKey),
            _ => false,
        };
    }

    static bool BuildPresetLoadoutResolvesUnlocked(
        SqliteConnection db,
        EmpireRef owner,
        RpgBuildPresetPieceRow piece)
    {
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                SELECT COUNT(*) FROM rpg_item_loadout
                WHERE loadout_id = $id AND player_id = $player;
                """;
            cmd.Parameters.AddWithValue("$id", piece.RefId);
            cmd.Parameters.AddWithValue("$player", owner.Save.Value.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
            if (Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) == 0) return false;
        }

        if (CommanderDirectoryHub.IsConfigured)
        {
            var directory = CommanderDirectoryHub.Current;
            if (directory.TryResolve(piece.TargetRef, out var commander))
                return string.Equals(directory.EmpireOf(commander).Value, owner.Empire.Value, StringComparison.Ordinal);
        }

        return OwnsSpecimenUnlocked(db, owner, piece.TargetRef);
    }

    static bool ActionExistsUnlocked(SqliteConnection db, string actionId)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM rpg_action WHERE action_id = $id;";
        cmd.Parameters.AddWithValue("$id", actionId);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) > 0;
    }

    /// <summary>
    /// Creates or replaces one preset and all of its pieces atomically. Shape and owner refusals return
    /// their named reason before any library row changes; create alone is subject to the loaded soft max.
    /// </summary>
    public string SaveBuildPreset(
        EmpireRef owner,
        RpgBuildPresetRow preset,
        IReadOnlyList<BuildPresetPieceRow> pieces,
        bool isCreate)
    {
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(pieces);
        if (string.IsNullOrWhiteSpace(preset.PresetId))
            return "build-preset.id.missing";

        var shape = BuildPresetShape.Validate(preset.Name, pieces);
        if (!shape.Ok) return shape.Reason;
        if (preset.SaveId != owner.Save.Value ||
            !string.Equals(preset.EmpireId, owner.Empire.Value, StringComparison.Ordinal))
            return "build-preset.owner.mismatch";

        lock (_gate)
        {
            using var db = OpenUnlocked();
            EnsureBuildPresetSchemaUnlocked(db);
            RequireHumanBuildPresetOwnerUnlocked(db, owner);
            using var tx = db.BeginTransaction();

            var id = preset.PresetId.Trim();
            var existing = GetBuildPresetUnlocked(db, id, owner);
            var idExists = existing is not null || BuildPresetExistsAnyOwnerUnlocked(db, id);
            if (isCreate)
            {
                if (idExists)
                    return existing is not null ? "build-preset.id.exists" : "build-preset.owner.mismatch";
                if (CountBuildPresetsUnlocked(db, tx, owner) >= BuildPresetTuningHub.Tuning.SoftMaxBuildPresets)
                    return "build-preset.softMax";
            }
            else if (!idExists)
            {
                return "build-preset.notFound";
            }
            else if (existing is null)
            {
                return "build-preset.owner.mismatch";
            }
            if (isCreate)
            {
                ExecIn(db, tx, """
                    INSERT INTO rpg_build_preset
                      (preset_id, save_id, empire_id, name, created_utc, revision)
                    VALUES ($id, $save, $empire, $name, $utc, 1);
                    """,
                    ("$id", id), ("$save", owner.Save.Value), ("$empire", owner.Empire.Value),
                    ("$name", preset.Name.Trim()), ("$utc", preset.CreatedUtc));
            }
            else
            {
                ExecIn(db, tx, """
                    UPDATE rpg_build_preset
                    SET name = $name, revision = revision + 1
                    WHERE preset_id = $id AND save_id = $save AND empire_id = $empire;
                    """,
                    ("$id", id), ("$save", owner.Save.Value), ("$empire", owner.Empire.Value),
                    ("$name", preset.Name.Trim()));
            }

            ExecIn(db, tx, "DELETE FROM rpg_build_preset_piece WHERE preset_id = $id;", ("$id", id));
            foreach (var piece in pieces)
            {
                ExecIn(db, tx, """
                    INSERT INTO rpg_build_preset_piece
                      (preset_id, piece_kind, target_ref, ordinal, ref_id)
                    VALUES ($id, $kind, $target, $ordinal, $ref);
                    """,
                    ("$id", id), ("$kind", BuildPresetPieceKinds.Id(piece.Kind)),
                    ("$target", piece.TargetRef ?? ""), ("$ordinal", piece.Ordinal), ("$ref", piece.RefId));
            }

            tx.Commit();
            return "";
        }
    }

    static long CountBuildPresetsUnlocked(SqliteConnection db, SqliteTransaction tx, EmpireRef owner)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT COUNT(*) FROM rpg_build_preset
            WHERE save_id = $save AND empire_id = $empire;
            """;
        cmd.Parameters.AddWithValue("$save", owner.Save.Value);
        cmd.Parameters.AddWithValue("$empire", owner.Empire.Value);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }

    static bool BuildPresetExistsAnyOwnerUnlocked(SqliteConnection db, string presetId)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM rpg_build_preset WHERE preset_id = $id;";
        cmd.Parameters.AddWithValue("$id", presetId);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) > 0;
    }

    public bool DeleteBuildPreset(EmpireRef owner, string presetId)
    {
        if (string.IsNullOrWhiteSpace(presetId)) return false;
        lock (_gate)
        {
            using var db = OpenUnlocked();
            EnsureBuildPresetSchemaUnlocked(db);
            RequireHumanBuildPresetOwnerUnlocked(db, owner);
            using var tx = db.BeginTransaction();

            if (GetBuildPresetUnlocked(db, presetId.Trim(), owner) is null) return false;
            ExecIn(db, tx, "DELETE FROM rpg_build_preset_piece WHERE preset_id = $id;",
                ("$id", presetId.Trim()));
            ExecIn(db, tx, """
                DELETE FROM rpg_build_preset
                WHERE preset_id = $id AND save_id = $save AND empire_id = $empire;
                """,
                ("$id", presetId.Trim()), ("$save", owner.Save.Value), ("$empire", owner.Empire.Value));
            tx.Commit();
            return true;
        }
    }
}
