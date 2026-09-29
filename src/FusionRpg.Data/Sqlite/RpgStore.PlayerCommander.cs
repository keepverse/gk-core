using FusionRpg.Core.Commanders;
using Microsoft.Data.Sqlite;
using FusionRpg.Core.Time;

namespace FusionRpg.Data;

public sealed record PlayerCommanderRow(
    long PlayerId,
    string DefaultLawnCommanderId,
    string UpdatedUtc,
    long Revision);

public sealed partial class RpgStore
{
    void EnsurePlayerCommanderSchemaUnlocked(SqliteConnection db)
    {
        Exec(db, """
            CREATE TABLE IF NOT EXISTS rpg_player_commander (
              player_id INTEGER NOT NULL PRIMARY KEY,
              default_lawn_commander_id TEXT NOT NULL,
              updated_utc TEXT NOT NULL,
              revision INTEGER NOT NULL DEFAULT 0
            );
            """);
    }

    public string GetDefaultLawnCommanderId(long playerId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            var row = ReadPlayerCommanderUnlocked(db, playerId);
            if (row is null) return HumanDefaultCommanderStableId();
            if (!CommanderDirectoryHub.Current.TryResolve(row.DefaultLawnCommanderId, out _))
            {
                Console.WriteLine(
                    $"[commander] corrupt default for player {playerId}: {row.DefaultLawnCommanderId}");
                return HumanDefaultCommanderStableId();
            }

            return row.DefaultLawnCommanderId;
        }
    }

    public (bool Ok, string Reason) SetDefaultLawnCommanderId(long playerId, string commanderStableId)
    {
        if (string.IsNullOrWhiteSpace(commanderStableId)) return (false, "commander.missing");
        var stable = commanderStableId.Trim();
        if (!CommanderDirectoryHub.Current.TryResolve(stable, out var parsed))
            return (false, "commander.unknown");
        if (!PlayerEmpireCommanders.IsPlayerDefaultAllowed(CommanderDirectoryHub.Current, parsed))
            return (false, "commander.not-empire");

        lock (_gate)
        {
            using var db = OpenUnlocked();
            if (GetPlayerUnlocked(db, playerId) is null) return (false, "player.unknown");
            using var tx = db.BeginTransaction();
            var now = ServerClock.UtcNowDateTime.ToString("o");

            using (var cmd = db.CreateCommand())
            {
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
                cmd.Parameters.AddWithValue("$p", playerId);
                cmd.Parameters.AddWithValue("$c", stable);
                cmd.Parameters.AddWithValue("$t", now);
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
            return (true, "");
        }
    }

    PlayerCommanderRow? ReadPlayerCommanderUnlocked(SqliteConnection db, long playerId)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText =
            "SELECT player_id, default_lawn_commander_id, updated_utc, revision FROM rpg_player_commander WHERE player_id=$p;";
        cmd.Parameters.AddWithValue("$p", playerId);
        using var r = cmd.ExecuteReader();
        return r.Read()
            ? new PlayerCommanderRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3))
            : null;
    }

    /// <summary>The human empire's default commander stable id — authored data, through the configured
    /// directory. `save-identity` re-types this to the save's own human empire (SE4.35).</summary>
    static string HumanDefaultCommanderStableId() =>
        CommanderDirectoryHub.Current.DefaultFor(EmpireId.Dave).StableId;
}
