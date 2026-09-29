using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using FusionRpg.Contracts;
using Microsoft.Data.Sqlite;
using FusionRpg.Core.Time;

namespace FusionRpg.Data;

public sealed class AlmanacSeedDto
{
    public string Side { get; set; } = "";
    public int TypeId { get; set; }
    public string? TypeName { get; set; }
    public string? DisplayName { get; set; }
    public string? FlavorInfo { get; set; }
    public string? FlavorIntroduce { get; set; }
    public int? SunCost { get; set; }
    public double? CooldownSec { get; set; }
    public string CostStatus { get; set; } = "absent";
    public long? Hp { get; set; }
    public long? Attack { get; set; }
    public long? Armor { get; set; }
    public long? ArmorMax { get; set; }
    public bool StatsObserved { get; set; }
    public int ContractVersion { get; set; }
    public string RebuiltUtc { get; set; } = "";
    public AlmanacSeedEnrichmentDto? Enrichment { get; set; }
}

public sealed class SpawnBaselineDto
{
    public string Side { get; set; } = "";
    public int TypeId { get; set; }
    public string StatsJson { get; set; } = "";
    public string CapturedUtc { get; set; } = "";
}

public sealed class AlmanacSeedRebuildSummary
{
    public int Built { get; set; }
    public int PlantsBuilt { get; set; }
    public int ZombiesBuilt { get; set; }
    public int CostAbsent { get; set; }
    public int CostParsed { get; set; }
    public int CostUnparsed { get; set; }
    public int StatsObserved { get; set; }
    public int StatsUnobserved { get; set; }
    public int StaleRemoved { get; set; }
}

public sealed partial class RpgStore
{
    public const int AlmanacSeedContractVersion = 1;

    static readonly Regex SunCostRx = new(@"花费[:：]\s*<color=(?:red|#[0-9A-Fa-f]{6,8})>(\d+)</color>", RegexOptions.Compiled);
    static readonly Regex CooldownRx = new(@"冷却时间[:：]\s*<color=(?:red|#[0-9A-Fa-f]{6,8})>(\d+(?:\.\d+)?)秒</color>", RegexOptions.Compiled);
    static readonly Regex ColorTagRx = new(@"</?color[^>]*>", RegexOptions.Compiled);

    public AlmanacSeedRebuildSummary RebuildAlmanacSeed()
    {
        lock (_gate)
        {
            using var media = OpenMediaUnlocked();
            var dumps = new List<(string Side, int TypeId, string FieldsJson, string CapturedUtc)>();
            using (var cmd = media.CreateCommand())
            {
                cmd.CommandText = "SELECT side, type_id, fields_json, captured_utc FROM type_almanac_dump;";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    dumps.Add((r.GetString(0), r.GetInt32(1), r.GetString(2), r.GetString(3)));
            }

            using var hot = OpenUnlocked();
            using var tx = hot.BeginTransaction();
            var summary = new AlmanacSeedRebuildSummary();
            var nowUtc = ServerClock.UtcNowDateTime.ToString("o");
            var seen = new HashSet<(string Side, int TypeId)>();

            try
            {
                // One set-based query per side (not one query per type — was an N+1 loop over
                // ~900 types, each a full spawn_stats scan; confirmed live against a real 520MB
                // hot.sqlite this took 30s+ and never completed under a 30s client timeout).
                var baselines = LoadCombatBaselinesUnlocked(hot, tx);

                foreach (var d in dumps)
                {
                    seen.Add((d.Side, d.TypeId));
                    UpsertOneAlmanacSeedRowUnlocked(hot, tx, d.Side, d.TypeId, d.FieldsJson, d.CapturedUtc, nowUtc, baselines, summary);
                }

                summary.StaleRemoved = DeleteStaleAlmanacSeedRowsUnlocked(hot, tx, seen);

                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }

            summary.Built = summary.PlantsBuilt + summary.ZombiesBuilt;
            return summary;
        }
    }

    /// <summary>
    /// `creature-seed` module 13 (`catalog-runtime`) precondition: a fresh database's `almanac_seed`
    /// table is empty until a player has manually browsed hundreds of live in-game almanac entries
    /// (`RebuildAlmanacSeed`'s own source, `type_almanac_dump`, only grows from that live capture) —
    /// which is why `species-import`'s own name resolution (`GetAlmanacSeed(side, gameTypeId)`)
    /// silently fell back to a placeholder ("Creature 918") for every species on a database that has
    /// never had that manual browsing happen. The committed corpus dump
    /// (`gk-data/packs/fusion/data/seed/creatures/_dump/almanac/{plant,zombie}.json`, `corpus-dump`/module 1) already carries
    /// the SAME final, already-parsed shape `almanac_seed` stores — it is not raw capture text needing
    /// <see cref="RebuildAlmanacSeed"/>'s regex parsing, it is that parsing's own OUTPUT, captured
    /// once elsewhere and committed. This is the direct, one-transaction bulk load of that committed
    /// snapshot, bypassing the raw <c>type_almanac_dump</c>/regex layer entirely because there is
    /// nothing left to parse.
    /// </summary>
    public int UpsertAlmanacSeedBulk(IReadOnlyList<AlmanacSeedDto> rows)
    {
        if (rows is null) throw new ArgumentNullException(nameof(rows));
        var nowUtc = ServerClock.UtcNowDateTime.ToString("o");

        lock (_gate)
        {
            using var hot = OpenUnlocked();
            using var tx = hot.BeginTransaction();
            var written = 0;
            foreach (var r in rows)
            {
                using var cmd = hot.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO almanac_seed(
                      side, type_id, type_name, display_name, flavor_info, flavor_introduce,
                      sun_cost, cooldown_sec, cost_status, hp, attack, armor, armor_max,
                      stats_observed, stats_sample_utc, almanac_captured_utc, contract_version, rebuilt_utc)
                    VALUES(
                      $side,$type,$tn,$dn,$fi,$fintro,
                      $sc,$cd,$cs,$hp,$atk,$arm,$armMax,
                      $so,$ssu,$acu,$cv,$ru)
                    ON CONFLICT(side, type_id) DO UPDATE SET
                      type_name=excluded.type_name, display_name=excluded.display_name,
                      flavor_info=excluded.flavor_info, flavor_introduce=excluded.flavor_introduce,
                      sun_cost=excluded.sun_cost, cooldown_sec=excluded.cooldown_sec, cost_status=excluded.cost_status,
                      hp=excluded.hp, attack=excluded.attack, armor=excluded.armor, armor_max=excluded.armor_max,
                      stats_observed=excluded.stats_observed, stats_sample_utc=excluded.stats_sample_utc,
                      almanac_captured_utc=excluded.almanac_captured_utc,
                      contract_version=excluded.contract_version, rebuilt_utc=excluded.rebuilt_utc;
                    """;
                cmd.Parameters.AddWithValue("$side", r.Side);
                cmd.Parameters.AddWithValue("$type", r.TypeId);
                cmd.Parameters.AddWithValue("$tn", (object?)r.TypeName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$dn", (object?)r.DisplayName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$fi", (object?)r.FlavorInfo ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$fintro", (object?)r.FlavorIntroduce ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$sc", (object?)r.SunCost ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$cd", (object?)r.CooldownSec ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$cs", r.CostStatus);
                cmd.Parameters.AddWithValue("$hp", (object?)r.Hp ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$atk", (object?)r.Attack ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$arm", (object?)r.Armor ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$armMax", (object?)r.ArmorMax ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$so", r.StatsObserved ? 1 : 0);
                // The dump's own `rebuiltUtc` is the sample stamp here — there is no separate
                // spawn_stats row to point at, since this bulk load skips that table entirely.
                cmd.Parameters.AddWithValue("$ssu", r.StatsObserved ? (object)r.RebuiltUtc : DBNull.Value);
                cmd.Parameters.AddWithValue("$acu", (object?)r.RebuiltUtc ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$cv", r.ContractVersion > 0 ? r.ContractVersion : AlmanacSeedContractVersion);
                cmd.Parameters.AddWithValue("$ru", nowUtc);
                cmd.ExecuteNonQuery();
                written++;
            }
            tx.Commit();
            return written;
        }
    }

    void UpsertOneAlmanacSeedRowUnlocked(
        SqliteConnection hot, SqliteTransaction tx,
        string side, int typeId, string fieldsJson, string almanacCapturedUtc, string nowUtc,
        Dictionary<(string Side, int TypeId), (string StatsJson, string CapturedUtc)> baselines,
        AlmanacSeedRebuildSummary summary)
    {
        var rawFields = JsonSerializer.Deserialize<Dictionary<string, string?>>(fieldsJson, AlmanacJson)
                        ?? throw new InvalidOperationException($"almanac dump {side}/{typeId}: fields_json did not deserialize to an object");
        var fields = ToIgnoreCaseFields(rawFields);

        static string? F(Dictionary<string, string?> map, string key)
        {
            if (!map.TryGetValue(key, out var v) || string.IsNullOrWhiteSpace(v)) return null;
            return v.Trim();
        }

        var displayName = F(fields, "name") ?? F(fields, "displayName");
        var typeName = F(fields, "enumName");
        var flavorInfo = StripColorMarkup(F(fields, "info"));
        var flavorIntroduce = side == "zombie" ? StripColorMarkup(F(fields, "introduce")) : null;
        var costText = F(fields, "cost");

        string costStatus;
        int? sunCost = null;
        double? cooldownSec = null;
        if (string.IsNullOrWhiteSpace(costText))
        {
            costStatus = "absent";
            summary.CostAbsent++;
        }
        else
        {
            var costMatch = SunCostRx.Match(costText);
            var cooldownMatch = CooldownRx.Match(costText);
            if (costMatch.Success && cooldownMatch.Success
                && int.TryParse(costMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedCost)
                && double.TryParse(cooldownMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedCooldown))
            {
                costStatus = "parsed";
                sunCost = parsedCost;
                cooldownSec = parsedCooldown;
                summary.CostParsed++;
            }
            else
            {
                costStatus = "unparsed";
                summary.CostUnparsed++;
            }
        }

        var (hp, attack, armor, armorMax, statsObserved, statsSampleUtc) = ResolveCombatBaseline(side, typeId, baselines);
        if (statsObserved) summary.StatsObserved++; else summary.StatsUnobserved++;
        if (side == "plant") summary.PlantsBuilt++; else if (side == "zombie") summary.ZombiesBuilt++;

        using var cmd = hot.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO almanac_seed(
              side, type_id, type_name, display_name, flavor_info, flavor_introduce,
              sun_cost, cooldown_sec, cost_status, hp, attack, armor, armor_max,
              stats_observed, stats_sample_utc, almanac_captured_utc, contract_version, rebuilt_utc)
            VALUES(
              $side,$type,$tn,$dn,$fi,$fintro,
              $sc,$cd,$cs,$hp,$atk,$arm,$armMax,
              $so,$ssu,$acu,$cv,$ru)
            ON CONFLICT(side, type_id) DO UPDATE SET
              type_name=excluded.type_name, display_name=excluded.display_name,
              flavor_info=excluded.flavor_info, flavor_introduce=excluded.flavor_introduce,
              sun_cost=excluded.sun_cost, cooldown_sec=excluded.cooldown_sec, cost_status=excluded.cost_status,
              hp=excluded.hp, attack=excluded.attack, armor=excluded.armor, armor_max=excluded.armor_max,
              stats_observed=excluded.stats_observed, stats_sample_utc=excluded.stats_sample_utc,
              almanac_captured_utc=excluded.almanac_captured_utc,
              contract_version=excluded.contract_version, rebuilt_utc=excluded.rebuilt_utc;
            """;
        cmd.Parameters.AddWithValue("$side", side);
        cmd.Parameters.AddWithValue("$type", typeId);
        cmd.Parameters.AddWithValue("$tn", (object?)typeName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$dn", (object?)displayName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fi", (object?)flavorInfo ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fintro", (object?)flavorIntroduce ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sc", (object?)sunCost ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cd", (object?)cooldownSec ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cs", costStatus);
        cmd.Parameters.AddWithValue("$hp", (object?)hp ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$atk", (object?)attack ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$arm", (object?)armor ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$armMax", (object?)armorMax ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$so", statsObserved ? 1 : 0);
        cmd.Parameters.AddWithValue("$ssu", (object?)statsSampleUtc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$acu", almanacCapturedUtc);
        cmd.Parameters.AddWithValue("$cv", AlmanacSeedContractVersion);
        cmd.Parameters.AddWithValue("$ru", nowUtc);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Loads the earliest baseline spawn_stats sample per (side, type) in exactly two queries —
    /// not one query per type. Each query uses ROW_NUMBER() OVER (PARTITION BY type ORDER BY
    /// captured_utc) so SQLite does one indexed pass per side instead of one lookup per type
    /// (the previous per-type loop was an N+1 pattern: ~900 separate queries, each a full table
    /// scan without a matching index — confirmed live to take 30s+ against a real ~38k-row
    /// spawn_stats table before this rewrite and the ix_spawn_stats_side_type_source index).
    /// </summary>
    static Dictionary<(string Side, int TypeId), (string StatsJson, string CapturedUtc)> LoadCombatBaselinesUnlocked(
        SqliteConnection hot, SqliteTransaction? tx)
    {
        var result = new Dictionary<(string, int), (string, string)>();

        void LoadSide(string side, string sourceFilterSql, Action<SqliteCommand> bindSources)
        {
            using var cmd = hot.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"""
                SELECT type, stats_json, captured_utc FROM (
                  SELECT type, stats_json, captured_utc,
                         ROW_NUMBER() OVER (PARTITION BY type ORDER BY captured_utc ASC) AS rn
                  FROM spawn_stats
                  WHERE side=$side AND {sourceFilterSql}
                )
                WHERE rn = 1;
                """;
            cmd.Parameters.AddWithValue("$side", side);
            bindSources(cmd);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                result[(side, r.GetInt32(0))] = (r.GetString(1), r.GetString(2));
        }

        LoadSide("plant", "source=$s1", cmd => cmd.Parameters.AddWithValue("$s1", "start"));
        LoadSide("zombie", "source IN ($s1,$s2)", cmd =>
        {
            cmd.Parameters.AddWithValue("$s1", "start");
            cmd.Parameters.AddWithValue("$s2", "initHealth");
        });

        // The game's own static table wins over a spawn sample, and overwrites it where both exist.
        // A spawn sample is one observation of one entity in one run, and it can only exist for a type
        // the player happened to spawn; the static table is the definition, and it covers every type.
        // Reading the same `*Base` keys means `ResolveCombatBaseline` needs no knowledge of which
        // source it got. The spawn path stays as the fallback so a database captured before the
        // base-stat sweep existed keeps working unchanged.
        foreach (var (key, value) in LoadTypeBaseStatsUnlocked(hot, tx))
            result[key] = value;

        return result;
    }

    static (long? Hp, long? Attack, long? Armor, long? ArmorMax, bool Observed, string? SampleUtc) ResolveCombatBaseline(
        string side, int typeId, Dictionary<(string Side, int TypeId), (string StatsJson, string CapturedUtc)> baselines)
    {
        if (!baselines.TryGetValue((side, typeId), out var baseline))
            return (null, null, null, null, false, null);

        var hp = TryInt(baseline.StatsJson, "hpBase");
        var attack = TryInt(baseline.StatsJson, "attackBase");
        long? armor = null, armorMax = null;
        if (side == "zombie")
        {
            armor = TryInt(baseline.StatsJson, "armorBase");
            armorMax = TryInt(baseline.StatsJson, "armorMaxBase");
        }
        return (hp, attack, armor, armorMax, true, baseline.CapturedUtc);
    }

    static int DeleteStaleAlmanacSeedRowsUnlocked(SqliteConnection hot, SqliteTransaction tx, HashSet<(string Side, int TypeId)> keep)
    {
        var toDelete = new List<(string Side, int TypeId)>();
        using (var cmd = hot.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT side, type_id FROM almanac_seed;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var key = (r.GetString(0), r.GetInt32(1));
                if (!keep.Contains(key)) toDelete.Add(key);
            }
        }
        foreach (var (side, typeId) in toDelete)
        {
            using var cmd = hot.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM almanac_seed WHERE side=$s AND type_id=$t;";
            cmd.Parameters.AddWithValue("$s", side);
            cmd.Parameters.AddWithValue("$t", typeId);
            cmd.ExecuteNonQuery();
        }
        return toDelete.Count;
    }

    static string? StripColorMarkup(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return ColorTagRx.Replace(text, "");
    }

    public AlmanacSeedDto? GetAlmanacSeed(string side, int typeId)
    {
        side = NormSide(side);
        lock (_gate)
        {
            using var hot = OpenUnlocked();
            return ReadAlmanacSeedUnlocked(hot, side, typeId);
        }
    }

    /// <summary>
    /// Public, non-transactional twin of <see cref="LoadCombatBaselinesUnlocked"/> — same query
    /// (earliest observed spawn_stats sample per side/type), exposed for `creature-seed`'s
    /// `corpus-dump` module so the raw baseline can be dumped as its own file (spec-corpus-dump.md
    /// §1/§4) without a raw <c>SqliteCommand</c> ever appearing under `tools/`.
    /// </summary>
    public List<SpawnBaselineDto> ListSpawnBaselines()
    {
        lock (_gate)
        {
            using var hot = OpenUnlocked();
            var raw = LoadCombatBaselinesUnlocked(hot, null);
            return raw
                .Select(kv => new SpawnBaselineDto
                {
                    Side = kv.Key.Side,
                    TypeId = kv.Key.TypeId,
                    StatsJson = kv.Value.StatsJson,
                    CapturedUtc = kv.Value.CapturedUtc
                })
                .OrderBy(d => d.Side, StringComparer.Ordinal)
                .ThenBy(d => d.TypeId)
                .ToList();
        }
    }

    public List<AlmanacSeedDto> ListAlmanacSeed(string? side = null)
    {
        lock (_gate)
        {
            using var hot = OpenUnlocked();
            using var cmd = hot.CreateCommand();
            if (string.IsNullOrWhiteSpace(side))
                cmd.CommandText = "SELECT side, type_id FROM almanac_seed ORDER BY side, type_id;";
            else
            {
                cmd.CommandText = "SELECT side, type_id FROM almanac_seed WHERE side=$s ORDER BY type_id;";
                cmd.Parameters.AddWithValue("$s", NormSide(side));
            }
            var keys = new List<(string Side, int TypeId)>();
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                    keys.Add((r.GetString(0), r.GetInt32(1)));
            }
            return keys.Select(k => ReadAlmanacSeedUnlocked(hot, k.Side, k.TypeId)!).Where(x => x != null).ToList()!;
        }
    }

    AlmanacSeedDto? ReadAlmanacSeedUnlocked(SqliteConnection hot, string side, int typeId)
    {
        using var cmd = hot.CreateCommand();
        cmd.CommandText = """
            SELECT type_name, display_name, flavor_info, flavor_introduce, sun_cost, cooldown_sec,
                   cost_status, hp, attack, armor, armor_max, stats_observed, contract_version, rebuilt_utc
            FROM almanac_seed WHERE side=$s AND type_id=$t;
            """;
        cmd.Parameters.AddWithValue("$s", side);
        cmd.Parameters.AddWithValue("$t", typeId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;

        var dto = new AlmanacSeedDto
        {
            Side = side,
            TypeId = typeId,
            TypeName = r.IsDBNull(0) ? null : r.GetString(0),
            DisplayName = r.IsDBNull(1) ? null : r.GetString(1),
            FlavorInfo = r.IsDBNull(2) ? null : r.GetString(2),
            FlavorIntroduce = r.IsDBNull(3) ? null : r.GetString(3),
            SunCost = r.IsDBNull(4) ? null : r.GetInt32(4),
            CooldownSec = r.IsDBNull(5) ? null : r.GetDouble(5),
            CostStatus = r.GetString(6),
            Hp = r.IsDBNull(7) ? null : r.GetInt64(7),
            Attack = r.IsDBNull(8) ? null : r.GetInt64(8),
            Armor = r.IsDBNull(9) ? null : r.GetInt64(9),
            ArmorMax = r.IsDBNull(10) ? null : r.GetInt64(10),
            StatsObserved = r.GetInt32(11) != 0,
            ContractVersion = r.GetInt32(12),
            RebuiltUtc = r.GetString(13)
        };
        r.Close();

        // Naming falls back to the live `types` row on read — types is the naming SSOT,
        // this table's type_name/display_name are only a rebuild-time snapshot.
        using (var tcmd = hot.CreateCommand())
        {
            tcmd.CommandText = "SELECT type_name, display_name FROM types WHERE game=$g AND side=$s AND type=$t;";
            tcmd.Parameters.AddWithValue("$g", RpgConstants.GameId);
            tcmd.Parameters.AddWithValue("$s", side);
            tcmd.Parameters.AddWithValue("$t", typeId);
            using var tr = tcmd.ExecuteReader();
            if (tr.Read())
            {
                // `types` is the naming SSOT (data-architecture.md §3) — a correction landing there
                // must be visible on the next read, not just when the rebuild-time snapshot is empty.
                var liveTypeName = tr.IsDBNull(0) ? null : tr.GetString(0);
                var liveDisplayName = tr.IsDBNull(1) ? null : tr.GetString(1);
                if (!string.IsNullOrWhiteSpace(liveTypeName)) dto.TypeName = liveTypeName;
                if (!string.IsNullOrWhiteSpace(liveDisplayName)) dto.DisplayName = liveDisplayName;
            }
        }

        dto.Enrichment = ReadAlmanacSeedEnrichmentUnlocked(hot, side, typeId);
        return dto;
    }
}
