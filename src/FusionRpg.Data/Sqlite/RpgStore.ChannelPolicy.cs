using FusionRpg.Core.Stats;
using Microsoft.Data.Sqlite;

namespace FusionRpg.Data;

/// <summary>
/// One channel's policy — the value, never the identity.
/// </summary>
/// <param name="Direction">0 higher-is-better, 1 lower-is-better.</param>
public sealed record ChannelPolicyRow(string ChannelId, int Direction);

/// <summary>
/// <c>effect_channel_policy</c> (E16) — direction, as rows.
///
/// <para><b>Values are data; channel identity stays code.</b> That is E1's own rule applied to
/// itself: changing direction on an existing channel is a value change with a live consumer
/// (<c>StatChannels.DirectionOf</c>), so it may be a row. Adding a <i>channel</i> needs a new composer
/// case and a new writer case — a new reader — so it stays code. A row that added a channel would be
/// accepted and then do nothing, which is the silent no-op this whole program exists to refuse.</para>
///
/// <para><b><c>default_value</c>, <c>cap_milli</c> and <c>compose_kind</c> retired</b>
/// (cap-consolidation, T1, 2026-08-24) — dead columns nothing ever read: a derived cap's one home is
/// <c>gk-core/data/tuning/derived-stats.v1.json</c>, and adding a channel needs code regardless, so a
/// per-channel default/compose-kind row could never do anything either. <c>direction</c> is the one
/// column with a live consumer and is all that remains.</para>
///
/// <para>It joins the content hash at registry <b>v4</b>, and the column retirement bumps it to
/// <b>v5</b> — a table-<i>shape</i> change, not a gameplay change; see
/// <see cref="Effects.Atoms.ContentHashRegistry"/>'s V5 doc comment for why the two are asserted
/// separately.</para>
/// </summary>
public sealed partial class RpgStore
{
    void EnsureChannelPolicySchemaUnlocked(SqliteConnection db)
    {
        Exec(db, """
            CREATE TABLE IF NOT EXISTS effect_channel_policy (
              channel_id    TEXT    NOT NULL PRIMARY KEY,
              direction     INTEGER NOT NULL DEFAULT 0
            );
            """);
    }

    /// <summary>Policy for every channel, falling back to the shipped defaults for any unwritten one.</summary>
    public IReadOnlyList<ChannelPolicyRow> GetChannelPolicies()
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText =
                "SELECT channel_id, direction " +
                "FROM effect_channel_policy ORDER BY channel_id;";
            using var r = cmd.ExecuteReader();

            var stored = new Dictionary<string, ChannelPolicyRow>(StringComparer.Ordinal);
            while (r.Read())
                stored[r.GetString(0)] = new ChannelPolicyRow(r.GetString(0), r.GetInt32(1));

            return StatChannels.All
                .Select(c => stored.TryGetValue(c, out var row) ? row : ShippedPolicy(c))
                .ToList();
        }
    }

    /// <summary>
    /// Write policy rows. <b>An unknown channel is refused</b>, because a row cannot add one: there
    /// would be no composer case and no writer case to read it, and the row would sit there looking
    /// like a feature.
    /// </summary>
    public (bool Ok, string Reason) UpsertChannelPolicies(IReadOnlyList<ChannelPolicyRow> rows)
    {
        var reason = ValidateChannelPolicyRows(rows);
        if (reason is not null) return (false, reason);

        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();

            var changed = 0;
            foreach (var row in rows) changed += UpsertChannelPolicyRowUnlocked(db, tx, row);

            // C4 (completeness-audit.md): bump only when something actually changed, matching the
            // import path's rule — a bump on a no-op call would make every connected E19 receiver
            // re-download the full push for content that did not move.
            if (changed > 0)
                ExecIn(db, tx, "UPDATE content_meta SET catalog_revision = catalog_revision + 1 WHERE id = 1;");

            tx.Commit();
            return (true, "");
        }
    }

    /// <summary>The check half, on a caller-owned row set — E14a (E22) validates before its own write.</summary>
    static string? ValidateChannelPolicyRows(IReadOnlyList<ChannelPolicyRow> rows)
    {
        foreach (var row in rows)
        {
            if (!Array.Exists(StatChannels.All, c => string.Equals(c, row.ChannelId, StringComparison.Ordinal)))
                return $"'{row.ChannelId}' is not a channel. This table holds a channel's VALUES; adding a "
                       + "channel needs a composer case and a writer case, so it is code (E1's code-or-data rule)";

            if (row.Direction is not (0 or 1))
                return $"{row.ChannelId}: direction {row.Direction} — 0 higher-is-better, 1 lower";
        }

        return null;
    }

    /// <summary>The write half, on a caller-owned transaction. Returns rows actually changed.</summary>
    static int UpsertChannelPolicyRowUnlocked(SqliteConnection db, SqliteTransaction tx, ChannelPolicyRow row)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO effect_channel_policy (channel_id, direction)
            VALUES ($id, $dir)
            ON CONFLICT(channel_id) DO UPDATE SET direction = excluded.direction
            WHERE effect_channel_policy.direction IS NOT excluded.direction;
            """;
        cmd.Parameters.AddWithValue("$id", row.ChannelId);
        cmd.Parameters.AddWithValue("$dir", row.Direction);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>What a channel does when nothing has been authored for it.</summary>
    public static ChannelPolicyRow ShippedPolicy(string channelId) => new(
        channelId, (int)StatChannels.DirectionOf(channelId));
}
