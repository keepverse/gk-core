using FusionRpg.Core.Settings;
using Microsoft.Data.Sqlite;
using FusionRpg.Core.Time;

namespace FusionRpg.Data;

/// <summary>
/// <b>Per-player user settings</b> (`solid-remediation`, 2026-09-17).
///
/// <para><b>Deliberately not the existing <c>settings</c> table.</b> That one is singleton app state
/// keyed by a bare string — <c>stats</c>, <c>cheats</c>, <c>current_player_id</c> — with no player
/// column and no public accessor. A user preference belongs to a player, so it gets a table whose
/// primary key says so.</para>
///
/// <para>Values are stored as JSON text and the key must be one <see cref="UserSettingKeys"/> declares.
/// An unknown key is refused rather than stored: a settings table that accepts anything becomes a
/// junk drawer, and the registry is what lets a reader know the full set without grepping call
/// sites.</para>
/// </summary>
public sealed partial class RpgStore
{
    void EnsureUserSettingsSchemaUnlocked(SqliteConnection db)
    {
        Exec(db, """
            CREATE TABLE IF NOT EXISTS rpg_user_settings (
              player_id    INTEGER NOT NULL,
              key          TEXT    NOT NULL,
              value_json   TEXT    NOT NULL,
              updated_utc  TEXT    NOT NULL,
              PRIMARY KEY (player_id, key)
            );
            """);
    }

    /// <summary>Every setting the player has explicitly chosen. Absent keys are NOT filled in with
    /// defaults here — the caller merges, so "never chosen" stays distinguishable from "chosen to be
    /// the default value", which matters the day a default changes.</summary>
    public IReadOnlyDictionary<string, string> ListUserSettings(long playerId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            EnsureUserSettingsSchemaUnlocked(db);
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT key, value_json FROM rpg_user_settings WHERE player_id = $p ORDER BY key;";
            cmd.Parameters.AddWithValue("$p", playerId);
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            using var r = cmd.ExecuteReader();
            while (r.Read()) map[r.GetString(0)] = r.GetString(1);
            return map;
        }
    }

    /// <summary>The player's value for one setting, or its declared default when unset.</summary>
    public bool GetUserSettingBool(long playerId, string key)
    {
        if (!UserSettingKeys.IsKnown(key))
            throw new ArgumentException($"unknown user setting '{key}'", nameof(key));

        lock (_gate)
        {
            using var db = OpenUnlocked();
            EnsureUserSettingsSchemaUnlocked(db);
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT value_json FROM rpg_user_settings WHERE player_id = $p AND key = $k;";
            cmd.Parameters.AddWithValue("$p", playerId);
            cmd.Parameters.AddWithValue("$k", key);
            var raw = cmd.ExecuteScalar() as string;
            return UserSettingKeys.TryParseBool(key, raw, out var v) ? v : UserSettingKeys.DefaultBool(key);
        }
    }

    /// <summary>Upserts one setting. Refuses an unknown key, and refuses a value that does not fit the
    /// key's declared kind — storing an unparseable value would read back as the default forever, which
    /// looks exactly like the write never happened.</summary>
    public void SetUserSettingBool(long playerId, string key, bool value)
    {
        var def = UserSettingKeys.Find(key)
            ?? throw new ArgumentException($"unknown user setting '{key}'", nameof(key));
        if (def.Kind != UserSettingKind.Bool)
            throw new ArgumentException($"user setting '{key}' is {def.Kind}, not Bool", nameof(key));

        lock (_gate)
        {
            using var db = OpenUnlocked();
            EnsureUserSettingsSchemaUnlocked(db);
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO rpg_user_settings(player_id, key, value_json, updated_utc)
                VALUES($p, $k, $v, $t)
                ON CONFLICT(player_id, key) DO UPDATE SET
                  value_json = excluded.value_json, updated_utc = excluded.updated_utc;
                """;
            cmd.Parameters.AddWithValue("$p", playerId);
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", value ? "true" : "false");
            cmd.Parameters.AddWithValue("$t", ServerClock.UtcNowDateTime.ToString("o"));
            cmd.ExecuteNonQuery();
        }
    }
}
