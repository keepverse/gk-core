using System.Text.Json;
using FusionRpg.Core.Effects.Atoms;
using Microsoft.Data.Sqlite;

namespace FusionRpg.Data;

/// <summary>
/// The <c>effect_curve</c> table (spec-value-spec-and-curve.md, E2).
///
/// <para>It lives here rather than in Core for one reason: <c>guard-dal.ps1</c> forbids SQL outside
/// this project. The table had no owner before the 2026-08-22 audit — E4 validates <c>curveId</c>
/// and E8 hashes the rows, so three modules depended on a table nobody created.</para>
///
/// <para>Points are stored as an ordered JSON array of <c>(x, multiplierMilli)</c>, integer
/// per-mille. No formula strings, ever: a formula is a language, and a language is a parser, a
/// sandbox, and a security surface.</para>
/// </summary>
public sealed partial class RpgStore
{
    static readonly JsonSerializerOptions CurveJson = new() { WriteIndented = false };

    /// <summary>Called from EnsureHotSchema so a fresh database has the table.</summary>
    void EnsureCurveSchemaUnlocked(SqliteConnection db)
    {
        Exec(db, """
            CREATE TABLE IF NOT EXISTS effect_curve (
              curve_id TEXT NOT NULL PRIMARY KEY,
              input TEXT NOT NULL,
              points_json TEXT NOT NULL,
              revision INTEGER NOT NULL DEFAULT 0
            );
            """);
    }

    /// <summary>
    /// Insert or replace a curve. Validation runs in Core (<see cref="CurveTable.TryCreate"/>) and
    /// again here, because a row that reaches the table unvalidated becomes a hot-path failure much
    /// later, where it is far more expensive to diagnose.
    /// </summary>
    public (bool Ok, string Reason) UpsertCurve(
        string curveId, CurveInput input, IReadOnlyList<CurvePoint> points)
    {
        var check = CurveTable.TryCreate(curveId, input, points, out _);
        if (!check.IsOk) return (false, check.ToString());

        lock (_gate)
        {
            using var db = OpenUnlocked();
            WriteCurveUnlocked(db, null, curveId, input, points);
            return (true, "");
        }
    }

    /// <summary>
    /// The curve write, on a caller-owned connection and optional transaction. E14a imports curves,
    /// atoms and containers in one transaction — an atom that scales through a curve is only valid
    /// once that curve exists, so the two cannot commit separately.
    /// </summary>
    /// <returns>How many rows the write changed — 0 when the row was already identical.</returns>
    int WriteCurveUnlocked(
        SqliteConnection db, SqliteTransaction? tx,
        string curveId, CurveInput input, IReadOnlyList<CurvePoint> points)
    {
        using var cmd = db.CreateCommand();
        if (tx is not null) cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO effect_curve (curve_id, input, points_json, revision)
            VALUES ($id, $in, $pts, 1)
            -- Skipped when nothing differs: `revision` is a hashed column, so bumping it on an
            -- identical re-import would make a repeat import look like a content edit (E14a).
            ON CONFLICT(curve_id) DO UPDATE SET
              input = excluded.input,
              points_json = excluded.points_json,
              revision = effect_curve.revision + 1
            WHERE effect_curve.input IS NOT excluded.input
               OR effect_curve.points_json IS NOT excluded.points_json;
            """;
        cmd.Parameters.AddWithValue("$id", curveId);
        cmd.Parameters.AddWithValue("$in", input.ToString().ToLowerInvariant());
        cmd.Parameters.AddWithValue("$pts", SerializePoints(points));
        return cmd.ExecuteNonQuery();
    }

    /// <summary>One curve, or null. Rows that fail validation on read are treated as absent.</summary>
    public CurveTable? GetCurve(string curveId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText =
                "SELECT curve_id, input, points_json FROM effect_curve WHERE curve_id = $id;";
            cmd.Parameters.AddWithValue("$id", curveId);
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadCurve(r) : null;
        }
    }

    /// <summary>
    /// Every curve, in stable id order. E8 hashes this table, so the order a load returns must not
    /// depend on insertion order.
    /// </summary>
    public IReadOnlyList<CurveTable> ListCurves()
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText =
                "SELECT curve_id, input, points_json FROM effect_curve ORDER BY curve_id;";
            using var r = cmd.ExecuteReader();

            var list = new List<CurveTable>();
            while (r.Read())
            {
                if (ReadCurve(r) is { } curve) list.Add(curve);
            }
            return list;
        }
    }

    /// <summary>Current revision, or 0 when absent. E8 reads it; E4 reproduces against it.</summary>
    public long GetCurveRevision(string curveId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT revision FROM effect_curve WHERE curve_id = $id;";
            cmd.Parameters.AddWithValue("$id", curveId);
            var v = cmd.ExecuteScalar();
            return v is null or DBNull ? 0 : Convert.ToInt64(v);
        }
    }

    static string SerializePoints(IReadOnlyList<CurvePoint> points)
    {
        var pairs = new int[points.Count][];
        for (var i = 0; i < points.Count; i++)
            pairs[i] = new[] { points[i].X, points[i].MultiplierMilli };
        return JsonSerializer.Serialize(pairs, CurveJson);
    }

    static CurveTable? ReadCurve(SqliteDataReader r)
    {
        var id = r.GetString(0);
        if (!Enum.TryParse<CurveInput>(r.GetString(1), ignoreCase: true, out var input))
            return null;

        var pairs = JsonSerializer.Deserialize<int[][]>(r.GetString(2));
        if (pairs is null) return null;

        var points = new List<CurvePoint>(pairs.Length);
        foreach (var p in pairs)
        {
            if (p.Length != 2) return null;
            points.Add(new CurvePoint(p[0], p[1]));
        }

        return CurveTable.TryCreate(id, input, points, out var curve).IsOk ? curve : null;
    }
}
