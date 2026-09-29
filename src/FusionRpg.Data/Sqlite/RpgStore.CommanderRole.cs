using System.Globalization;
using FusionRpg.Contracts;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Commanders;
using FusionRpg.Core.Saves;
using Microsoft.Data.Sqlite;
using FusionRpg.Core.Time;

namespace FusionRpg.Data;

/// <summary>`commander-roster` EP3.1 — the commander role as a BINDING (spec-commander-roster.md "The
/// role is a binding", map decision P3, the owner's ruling *"a commander literally a unique demon, it
/// only carry more role"*).
///
/// <para>Grant inserts, revoke deletes, and the creature is unchanged either way: same
/// <c>rpg_unique_actors</c> row, same level, gear, allocation and phase. The creature is the noun;
/// commander is an adjective. Nothing here writes the actor, which is why the role can be added and
/// removed without a second progression system — R-C2's *"no special for commander, it basically a
/// unique unit"*.</para>
///
/// <para>Keyed <c>(save_id, empire_id, instance_id)</c>, born keyed because `save-identity`'s first
/// slice landed before this table existed (map S8) — so there is no migration and no re-key. The table
/// is created by <see cref="EnsureCommanderRoleSchemaUnlocked"/>, called from <c>Init</c> beside every
/// other store's own schema, which is this DAL's pattern for a NEW table (a migration is for re-keying
/// an existing one).</para>
///
/// <para><b>No roster size limit.</b> A roster is a population; a count limit would be a ceiling. If a
/// cap is ever wanted it is a soft, tunable one, and nobody has asked for one.</para></summary>
public sealed record CommanderRoleOutcome(bool Ok, string Reason)
{
    /// <summary>The role is held (idempotent: granting twice is one row, revoking a role the creature
    /// does not hold is a no-op).</summary>
    public static CommanderRoleOutcome Done { get; } = new(true, "");
}

public sealed partial class RpgStore : ICommanderRoleReader
{
    /// <summary>The refusal vocabulary, each named (spec-commander-roster.md "The role is a
    /// binding"). A caller switches on these strings; adding a refusal is a reviewed change here.</summary>
    public const string CommanderRoleRetired = "commander.role.retired";

    /// <summary>See <see cref="CommanderRoleRetired"/>.</summary>
    public const string CommanderRoleNotOwner = "commander.role.notOwner";

    /// <summary>See <see cref="CommanderRoleRetired"/>.</summary>
    public const string CommanderRoleUnknown = "commander.role.unknown";

    void EnsureCommanderRoleSchemaUnlocked(SqliteConnection db)
    {
        Exec(db, """
            CREATE TABLE IF NOT EXISTS rpg_commander_role (
              save_id     INTEGER NOT NULL,
              empire_id   TEXT    NOT NULL,
              instance_id TEXT    NOT NULL,
              granted_utc TEXT    NOT NULL,
              PRIMARY KEY (save_id, empire_id, instance_id)
            );
            """);
    }

    /// <summary>Grant the commander role to a specimen of <paramref name="empire"/>. Refuses with one
    /// of the three named reasons, and writes nothing when it does: an unknown instance, a creature the
    /// empire does not own (<see cref="OwnsSpecimenUnlocked"/>, the one ownership predicate), or a
    /// <see cref="UniqueActorPhases.Retired"/> specimen. Ownership is checked BEFORE the phase, so a
    /// non-owner learns nothing about a creature it does not own. A creature in any other phase may be
    /// granted the role — *where* it may then lead is the place's rule (`lawn-commander-seat`,
    /// `legion-commander`), never this table's.</summary>
    public CommanderRoleOutcome GrantCommanderRole(EmpireRef empire, string instanceId, DateTimeOffset? utcNow = null)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();
            var outcome = GrantCommanderRoleUnlocked(db, empire, instanceId, utcNow);
            if (outcome.Ok) tx.Commit();
            else tx.Rollback();
            return outcome;
        }
    }

    /// <summary>Same body on the caller's connection/transaction — the form a route or a larger
    /// transaction (a revoke that also resets a default) needs.</summary>
    internal CommanderRoleOutcome GrantCommanderRoleUnlocked(
        SqliteConnection db, EmpireRef empire, string instanceId, DateTimeOffset? utcNow = null)
    {
        var (known, owned, phase) = ReadCommanderRoleSubjectUnlocked(db, empire, instanceId);
        if (!known) return new CommanderRoleOutcome(false, CommanderRoleUnknown);
        if (!owned) return new CommanderRoleOutcome(false, CommanderRoleNotOwner);
        if (string.Equals(phase, UniqueActorPhases.Retired, StringComparison.Ordinal))
            return new CommanderRoleOutcome(false, CommanderRoleRetired);

        using var cmd = db.CreateCommand();
        // Idempotent by construction: the primary key is the binding, so a re-grant is one row.
        cmd.CommandText =
            "INSERT OR IGNORE INTO rpg_commander_role (save_id, empire_id, instance_id, granted_utc) " +
            "VALUES ($s, $e, $i, $t);";
        cmd.Parameters.AddWithValue("$s", empire.Save.Value);
        cmd.Parameters.AddWithValue("$e", empire.Empire.Value);
        cmd.Parameters.AddWithValue("$i", instanceId.Trim());
        cmd.Parameters.AddWithValue("$t", (utcNow ?? ServerClock.UtcNow).ToString("o"));
        cmd.ExecuteNonQuery();
        return CommanderRoleOutcome.Done;
    }

    /// <summary>Revoke the role. Refuses only for an unknown or unowned instance: REMOVING a binding is
    /// never blocked by the specimen's phase (a retired creature's role is data nobody can use, and
    /// leaving it behind would keep a dead instance in every roster read), and revoking a role the
    /// creature does not hold is a no-op rather than a fourth refusal.</summary>
    public CommanderRoleOutcome RevokeCommanderRole(EmpireRef empire, string instanceId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();
            var outcome = RevokeCommanderRoleUnlocked(db, empire, instanceId);
            if (outcome.Ok) tx.Commit();
            else tx.Rollback();
            return outcome;
        }
    }

    /// <inheritdoc cref="RevokeCommanderRole"/>
    internal CommanderRoleOutcome RevokeCommanderRoleUnlocked(SqliteConnection db, EmpireRef empire, string instanceId)
    {
        var (known, owned, _) = ReadCommanderRoleSubjectUnlocked(db, empire, instanceId);
        if (!known) return new CommanderRoleOutcome(false, CommanderRoleUnknown);
        if (!owned) return new CommanderRoleOutcome(false, CommanderRoleNotOwner);

        using var cmd = db.CreateCommand();
        cmd.CommandText =
            "DELETE FROM rpg_commander_role WHERE save_id=$s AND empire_id=$e AND instance_id=$i;";
        cmd.Parameters.AddWithValue("$s", empire.Save.Value);
        cmd.Parameters.AddWithValue("$e", empire.Empire.Value);
        cmd.Parameters.AddWithValue("$i", instanceId.Trim());
        cmd.ExecuteNonQuery();
        return CommanderRoleOutcome.Done;
    }

    /// <summary>Does this specimen hold the role for this empire? The read `commander-roster`'s
    /// directory source asks (`ICommanderDirectory.TryResolve`), and the one a roster read filters on.</summary>
    public bool HasCommanderRole(EmpireRef empire, string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) return false;
        lock (_gate)
        {
            using var db = OpenUnlocked();
            return HasCommanderRoleUnlocked(db, empire, instanceId);
        }
    }

    /// <inheritdoc cref="HasCommanderRole"/>
    internal static bool HasCommanderRoleUnlocked(SqliteConnection db, EmpireRef empire, string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) return false;
        using var cmd = db.CreateCommand();
        cmd.CommandText =
            "SELECT 1 FROM rpg_commander_role WHERE save_id=$s AND empire_id=$e AND instance_id=$i LIMIT 1;";
        cmd.Parameters.AddWithValue("$s", empire.Save.Value);
        cmd.Parameters.AddWithValue("$e", empire.Empire.Value);
        cmd.Parameters.AddWithValue("$i", instanceId.Trim());
        return cmd.ExecuteScalar() is not null;
    }

    /// <summary>The empire's role-holding specimen ids, ordinal by instance id — the roster's data rows.
    /// Enumeration only: resolution, display names and the default all stay on `ICommanderDirectory`
    /// (`commander-identity`), and `ICommanderRoster` (EP3.2) composes the authored defaults with this
    /// list rather than duplicating either.</summary>
    public IReadOnlyList<string> ListCommanderRoleInstanceIds(EmpireRef empire)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            return ListCommanderRoleInstanceIdsUnlocked(db, empire);
        }
    }

    /// <inheritdoc cref="ListCommanderRoleInstanceIds"/>
    internal static IReadOnlyList<string> ListCommanderRoleInstanceIdsUnlocked(SqliteConnection db, EmpireRef empire)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText =
            "SELECT instance_id FROM rpg_commander_role WHERE save_id=$s AND empire_id=$e " +
            "ORDER BY instance_id;";
        cmd.Parameters.AddWithValue("$s", empire.Save.Value);
        cmd.Parameters.AddWithValue("$e", empire.Empire.Value);
        var ids = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) ids.Add(r.GetString(0));
        return ids;
    }

    /// <summary>`(known, owned, phase)` for one instance, from the single row the checks need. Unknown
    /// (no row) and unowned are separate answers on purpose: the caller names them differently
    /// (<see cref="CommanderRoleUnknown"/> vs <see cref="CommanderRoleNotOwner"/>), and an unowned
    /// instance must not report its phase.</summary>
    static (bool Known, bool Owned, string Phase) ReadCommanderRoleSubjectUnlocked(
        SqliteConnection db, EmpireRef empire, string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) return (false, false, "");
        using var cmd = db.CreateCommand();
        cmd.CommandText =
            "SELECT player_id, empire_id, phase FROM rpg_unique_actors WHERE instance_id=$i;";
        cmd.Parameters.AddWithValue("$i", instanceId.Trim());
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return (false, false, "");
        if (r.IsDBNull(1)) return (true, false, "");
        var owned = r.GetInt64(0) == empire.Save.Value
            && string.Equals(r.GetString(1), empire.Empire.Value, StringComparison.Ordinal);
        return (true, owned, r.IsDBNull(2) ? "" : r.GetString(2));
    }

    /// <summary>Test-only seam (InternalsVisibleTo, this project's own convention): every raw row of
    /// every table that carries an `instance_id`, for one instance, as ordinal strings. It exists so a
    /// test can assert that a creature is **unchanged** by comparing the whole creature rather than the
    /// fields it remembered to check (spec-commander-roster.md testing 2: "the creature's row,
    /// allocation, gear and phase byte-identical"). Read-only — it opens no transaction and writes
    /// nothing — and no production path calls it.</summary>
    internal IReadOnlyList<string> SnapshotInstanceRowsForTests(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
            throw new ArgumentException("instanceId must not be empty", nameof(instanceId));

        lock (_gate)
        {
            using var db = OpenUnlocked();

            var tables = new List<string>();
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
                using var r = cmd.ExecuteReader();
                while (r.Read()) tables.Add(r.GetString(0));
            }

            var rows = new List<string>();
            foreach (var table in tables)
            {
                var columns = new List<string>();
                using (var cmd = db.CreateCommand())
                {
                    cmd.CommandText = $"PRAGMA table_info({table});";
                    using var r = cmd.ExecuteReader();
                    while (r.Read()) columns.Add(r.GetString(1));
                }
                if (!columns.Contains("instance_id")) continue;

                using var select = db.CreateCommand();
                select.CommandText =
                    $"SELECT {string.Join(", ", columns)} FROM {table} WHERE instance_id = $i ORDER BY rowid;";
                select.Parameters.AddWithValue("$i", instanceId.Trim());
                using var reader = select.ExecuteReader();
                while (reader.Read())
                {
                    var cells = new List<string>();
                    for (var i = 0; i < reader.FieldCount; i++)
                        cells.Add(reader.IsDBNull(i)
                            ? "∅"
                            : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? "");
                    rows.Add($"{table}:{string.Join("|", cells)}");
                }
            }
            return rows;
        }
    }

    // ── `ICommanderRoleReader` (commander-roster EP3.2) — the role rows the directory's source reads ──
    //
    // Three reads, no more (see the port's own doc): which empire an instance holds the role for, which
    // instances hold it for an empire, and what to call one. They live here rather than in Core because
    // `rpg_commander_role` is a Data table; the directory composes them and decides nothing itself.

    /// <summary>The empire this instance holds the commander role for, or null when it holds none.</summary>
    public EmpireRef? RoleEmpireOf(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) return null;
        lock (_gate)
        {
            using var db = OpenUnlocked();
            return RoleEmpireOfUnlocked(db, instanceId);
        }
    }

    /// <inheritdoc cref="RoleEmpireOf"/>
    internal static EmpireRef? RoleEmpireOfUnlocked(SqliteConnection db, string instanceId)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText =
            "SELECT save_id, empire_id FROM rpg_commander_role WHERE instance_id=$i LIMIT 1;";
        cmd.Parameters.AddWithValue("$i", instanceId.Trim());
        using var r = cmd.ExecuteReader();
        return r.Read()
            ? new EmpireRef(new SaveId(r.GetInt64(0)), new EmpireId(r.GetString(1)))
            : null;
    }

    /// <inheritdoc cref="ListCommanderRoleInstanceIds"/>
    public IReadOnlyList<string> RoleInstanceIds(EmpireRef empire) => ListCommanderRoleInstanceIds(empire);

    /// <summary>The creature's roster label: its nickname when set, else its species' display name —
    /// through <see cref="CreatureDisplayName"/>, the one declaring site the actor sheet also uses.</summary>
    public string? RoleHolderDisplayName(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) return null;
        lock (_gate)
        {
            using var db = OpenUnlocked();
            return RoleHolderDisplayNameUnlocked(db, instanceId);
        }
    }

    /// <inheritdoc cref="RoleHolderDisplayName"/>
    internal static string? RoleHolderDisplayNameUnlocked(SqliteConnection db, string instanceId)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT p.nickname, p.species_id FROM rpg_creature_profiles p " +
                          "WHERE p.instance_id = $i LIMIT 1;";
        cmd.Parameters.AddWithValue("$i", instanceId.Trim());
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var nickname = r.IsDBNull(0) ? null : r.GetString(0);
        var speciesId = r.IsDBNull(1) ? null : r.GetString(1);
        return CreatureDisplayName.For(nickname, speciesId);
    }

    /// <summary>Revoke, and when the specimen held the save's default lawn commander seat, reset the
    /// default to the empire's authored default <b>in the same transaction</b> — so the default can
    /// never point at a non-commander (spec-commander-roster.md "Routes"). A started match is untouched:
    /// its commander is the match snapshot, taken when the run began, and this only changes what the
    /// NEXT run defaults to.</summary>
    public CommanderRoleOutcome RevokeCommanderRoleAndResetDefault(EmpireRef empire, string instanceId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();
            var outcome = RevokeCommanderRoleUnlocked(db, empire, instanceId);
            if (!outcome.Ok)
            {
                tx.Rollback();
                return outcome;
            }

            var stableId = UniqueCommanderSource.UniquePrefix + instanceId.Trim();
            var row = ReadPlayerCommanderUnlocked(db, empire.Save.Value);
            if (string.Equals(row?.DefaultLawnCommanderId, stableId, StringComparison.Ordinal))
            {
                using var cmd = db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO rpg_player_commander(player_id, default_lawn_commander_id, updated_utc, revision)
                    VALUES($p, $c, $t, 1)
                    ON CONFLICT(player_id)
                    DO UPDATE SET
                      default_lawn_commander_id = $c,
                      updated_utc = $t,
                      revision = revision + 1;
                    """;
                cmd.Parameters.AddWithValue("$p", empire.Save.Value);
                cmd.Parameters.AddWithValue("$c", CommanderDirectoryHub.Current.DefaultFor(empire.Empire).StableId);
                cmd.Parameters.AddWithValue("$t", ServerClock.UtcNowDateTime.ToString("o"));
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
            return outcome;
        }
    }
}
