using FusionRpg.Core.Effects.Atoms;
using Microsoft.Data.Sqlite;

namespace FusionRpg.Data;

/// <summary>
/// Reads the covered content tables and hands the rows to <see cref="ContentHash"/>
/// (spec-content-hash.md, E8). The algorithm lives in Core; only the reading lives here.
///
/// <para><b>Not cached.</b> Caching on <c>catalog_revision</c> looks obvious and is wrong: the
/// revision is bumped explicitly (once per import transaction), and a direct upsert changes content
/// without touching it — a cache keyed on it would serve a stale hash for exactly the hand edit this
/// module exists to make visible.</para>
/// </summary>
public sealed partial class RpgStore
{
    /// <summary>
    /// Hash the covered tables at the given registry version. One connection for the whole sweep —
    /// a per-table connection would be a connection storm over a set that only grows.
    /// </summary>
    public ContentHashStamp ComputeContentHash(int schemaVersion = ContentHashRegistry.CurrentSchemaVersion)
    {
        var tables = ContentHashRegistry.For(schemaVersion);

        lock (_gate)
        {
            using var db = OpenUnlocked();

            var perTable = new Dictionary<string, string>(StringComparer.Ordinal);
            var inOrder = new List<byte[]>(tables.Count);

            foreach (var table in tables)
            {
                var digest = TableDigestUnlocked(db, table);
                inOrder.Add(digest);
                perTable[table.TableName] = ContentHash.Hex(digest);
            }

            return new ContentHashStamp(
                schemaVersion, ContentHash.Hex(ContentHash.Combine(inOrder)), perTable);
        }
    }

    static byte[] TableDigestUnlocked(SqliteConnection db, ContentHashTable table)
    {
        // A covered table that does not exist is a broken build, not an empty catalog: reporting it
        // as SHA256("") would produce a plausible-looking hash for content nobody can account for.
        using (var probe = db.CreateCommand())
        {
            probe.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$n;";
            probe.Parameters.AddWithValue("$n", table.TableName);
            if (probe.ExecuteScalar() is null)
                throw new InvalidOperationException(
                    $"content hash covers '{table.TableName}' but the table does not exist");
        }

        var columns = table.Columns;
        using var cmd = db.CreateCommand();
        cmd.CommandText =
            $"SELECT {string.Join(", ", columns.Select(c => c.Name))} FROM {table.TableName};";

        var rows = new List<byte[]>();
        var values = new object?[columns.Count];
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            for (var i = 0; i < columns.Count; i++)
                values[i] = r.IsDBNull(i) ? null : r.GetValue(i);
            rows.Add(ContentHash.RowDigest(columns, values));
        }

        return ContentHash.TableDigest(rows);
    }
}
