using FusionRpg.Contracts;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Saves;
using FusionRpg.Core.Stats.Aptitudes;
using Microsoft.Data.Sqlite;

namespace FusionRpg.Data;

/// <summary>Player aptitude build-preset library header — mirrors <see cref="RpgItemLoadoutRow"/> discipline.</summary>
public sealed record RpgAptitudePresetRow(
    string PresetId,
    long PlayerId,
    string Name,
    string Kind,
    string CreatedUtc,
    long Revision);

/// <summary>One aptitude constraint row inside a preset (D13 axes optional — NULL means unset).</summary>
public sealed record RpgAptitudePresetEntryRow(
    string PresetId,
    string AptitudeId,
    long TargetPermille,
    long? MinAbs,
    long? MaxAbs,
    long? MinPermille,
    long? MaxPermille);

/// <summary>Active preset binding for one allocate scope.</summary>
public sealed record RpgAptitudePresetActiveRow(
    long PlayerId,
    string Scope,
    string ScopeKey,
    string PresetId);

/// <summary>Outcome of transactional Activate (AS-3.2) — no half-active / half-allocate.</summary>
public sealed record AptitudePresetActivateOutcome(
    bool Ok,
    string Reason,
    IReadOnlyDictionary<string, long> Shares,
    long Leftover,
    bool Priced,
    long PriceAmount,
    long RespecCount,
    SoulBalanceDto? Balance)
{
    /// <summary>`respec-free-counter` EP4.10 — what the species-scope respec actually paid with
    /// (`souls`/`freeRespec`, empty when nothing was charged), so the activate route reports the same
    /// shape the respec route does instead of re-deriving it. Additive init properties, as on
    /// <see cref="SpeciesRespecOutcome"/>.</summary>
    public string PaidWith { get; init; } = "";

    /// <summary>The empire's free-respec stock after the call, or the stock a refusal quotes. Read from
    /// the respec's own ledger, never predicted.</summary>
    public long FreeStock { get; init; }
}

/// <summary>
/// aptitude-sheet AS-3.1/AS-3.2 — named player-scoped aptitude build presets
/// (<c>rpg_aptitude_preset</c> / <c>_entry</c> / <c>_active</c>), mirroring item-loadout library
/// discipline. Soft max on create lives in <see cref="AptitudePresetTuningHub"/> (E8).
/// </summary>
public sealed partial class RpgStore
{
    public const string AptitudePresetKindPlayer = "player";
    public const string AptitudePresetKindSystemCopy = "systemCopy";

    void EnsureAptitudePresetSchemaUnlocked(SqliteConnection db)
    {
        Exec(db, """
            CREATE TABLE IF NOT EXISTS rpg_aptitude_preset (
              preset_id   TEXT    NOT NULL PRIMARY KEY,
              player_id   INTEGER NOT NULL,
              name        TEXT    NOT NULL,
              kind        TEXT    NOT NULL,
              created_utc TEXT    NOT NULL,
              revision    INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS ix_rpg_aptitude_preset_player
              ON rpg_aptitude_preset(player_id);

            CREATE TABLE IF NOT EXISTS rpg_aptitude_preset_entry (
              preset_id       TEXT    NOT NULL,
              aptitude_id     TEXT    NOT NULL,
              target_permille INTEGER NOT NULL,
              min_abs         INTEGER,
              max_abs         INTEGER,
              min_permille    INTEGER,
              max_permille    INTEGER,
              PRIMARY KEY (preset_id, aptitude_id)
            );

            CREATE TABLE IF NOT EXISTS rpg_aptitude_preset_active (
              player_id INTEGER NOT NULL,
              scope     TEXT    NOT NULL,
              scope_key TEXT    NOT NULL,
              preset_id TEXT    NOT NULL,
              PRIMARY KEY (player_id, scope, scope_key)
            );
            """);
    }

    public long CountAptitudePresets(long playerId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM rpg_aptitude_preset WHERE player_id = $p;";
            cmd.Parameters.AddWithValue("$p", playerId);
            return (long)(cmd.ExecuteScalar() ?? 0L);
        }
    }

    public IReadOnlyList<RpgAptitudePresetRow> ListAptitudePresets(long playerId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT preset_id, player_id, name, kind, created_utc, revision
                FROM rpg_aptitude_preset WHERE player_id = $p
                ORDER BY created_utc, preset_id;
                """;
            cmd.Parameters.AddWithValue("$p", playerId);
            using var r = cmd.ExecuteReader();
            var list = new List<RpgAptitudePresetRow>();
            while (r.Read())
                list.Add(new RpgAptitudePresetRow(
                    r.GetString(0), r.GetInt64(1), r.GetString(2), r.GetString(3),
                    r.GetString(4), r.GetInt64(5)));
            return list;
        }
    }

    public RpgAptitudePresetRow? GetAptitudePreset(string presetId)
    {
        if (string.IsNullOrWhiteSpace(presetId)) return null;
        lock (_gate)
        {
            using var db = OpenUnlocked();
            return GetAptitudePresetUnlocked(db, presetId);
        }
    }

    RpgAptitudePresetRow? GetAptitudePresetUnlocked(SqliteConnection db, string presetId)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT preset_id, player_id, name, kind, created_utc, revision
            FROM rpg_aptitude_preset WHERE preset_id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", presetId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new RpgAptitudePresetRow(
            r.GetString(0), r.GetInt64(1), r.GetString(2), r.GetString(3),
            r.GetString(4), r.GetInt64(5));
    }

    public IReadOnlyList<RpgAptitudePresetEntryRow> GetAptitudePresetEntries(string presetId)
    {
        if (string.IsNullOrWhiteSpace(presetId))
            throw new ArgumentException("presetId must not be empty", nameof(presetId));
        lock (_gate)
        {
            using var db = OpenUnlocked();
            return GetAptitudePresetEntriesUnlocked(db, presetId);
        }
    }

    IReadOnlyList<RpgAptitudePresetEntryRow> GetAptitudePresetEntriesUnlocked(
        SqliteConnection db, string presetId)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT preset_id, aptitude_id, target_permille, min_abs, max_abs, min_permille, max_permille
            FROM rpg_aptitude_preset_entry WHERE preset_id = $id
            ORDER BY aptitude_id;
            """;
        cmd.Parameters.AddWithValue("$id", presetId);
        using var r = cmd.ExecuteReader();
        var list = new List<RpgAptitudePresetEntryRow>();
        while (r.Read())
            list.Add(new RpgAptitudePresetEntryRow(
                r.GetString(0), r.GetString(1), r.GetInt64(2),
                r.IsDBNull(3) ? null : r.GetInt64(3),
                r.IsDBNull(4) ? null : r.GetInt64(4),
                r.IsDBNull(5) ? null : r.GetInt64(5),
                r.IsDBNull(6) ? null : r.GetInt64(6)));
        return list;
    }

    /// <summary>Create or update a preset. Caller must have already validated E5 sum-1000 and soft max
    /// (create only). Returns a named reason on ownership / kind refusal; empty string on success.</summary>
    public string SaveAptitudePreset(
        RpgAptitudePresetRow preset, IReadOnlyList<RpgAptitudePresetEntryRow> entries, bool isCreate)
    {
        if (preset is null) throw new ArgumentNullException(nameof(preset));
        if (entries is null) throw new ArgumentNullException(nameof(entries));
        if (string.IsNullOrWhiteSpace(preset.PresetId))
            return "presets.id.missing";
        if (string.IsNullOrWhiteSpace(preset.Name))
            return "presets.name.missing";
        if (preset.Kind is not (AptitudePresetKindPlayer or AptitudePresetKindSystemCopy))
            return "presets.kind.unknown";

        var specs = entries.Select(e => new AptitudePresetRowSpec(
            e.AptitudeId, e.TargetPermille, e.MinAbs, e.MaxAbs, e.MinPermille, e.MaxPermille)).ToList();
        var sumCheck = AptitudePresetMaterialize.ValidateTargetPermilleSum(specs);
        if (!sumCheck.Ok) return sumCheck.Reason;

        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();

            var existing = GetAptitudePresetUnlocked(db, preset.PresetId);
            if (isCreate)
            {
                if (existing is not null) return "presets.id.exists";
                var softMax = AptitudePresetTuningHub.Tuning.SoftMaxPresets;
                using (var countCmd = db.CreateCommand())
                {
                    countCmd.Transaction = tx;
                    countCmd.CommandText = "SELECT COUNT(*) FROM rpg_aptitude_preset WHERE player_id = $p;";
                    countCmd.Parameters.AddWithValue("$p", preset.PlayerId);
                    var count = (long)(countCmd.ExecuteScalar() ?? 0L);
                    if (count >= softMax)
                        return "presets.softMax";
                }
            }
            else
            {
                if (existing is null) return "presets.notFound";
                if (existing.PlayerId != preset.PlayerId) return "presets.owner.mismatch";
            }

            ExecIn(db, tx, """
                INSERT INTO rpg_aptitude_preset (preset_id, player_id, name, kind, created_utc, revision)
                VALUES ($id, $player, $name, $kind, $utc, 1)
                ON CONFLICT(preset_id) DO UPDATE SET
                  name = excluded.name, kind = excluded.kind,
                  revision = rpg_aptitude_preset.revision + 1;
                """,
                ("$id", preset.PresetId), ("$player", preset.PlayerId), ("$name", preset.Name),
                ("$kind", preset.Kind), ("$utc", preset.CreatedUtc));

            ExecIn(db, tx, "DELETE FROM rpg_aptitude_preset_entry WHERE preset_id = $id;",
                ("$id", preset.PresetId));
            foreach (var e in entries)
            {
                ExecIn(db, tx, """
                    INSERT INTO rpg_aptitude_preset_entry
                      (preset_id, aptitude_id, target_permille, min_abs, max_abs, min_permille, max_permille)
                    VALUES ($id, $apt, $tp, $mina, $maxa, $minp, $maxp);
                    """,
                    ("$id", preset.PresetId), ("$apt", e.AptitudeId), ("$tp", e.TargetPermille),
                    ("$mina", (object?)e.MinAbs ?? DBNull.Value),
                    ("$maxa", (object?)e.MaxAbs ?? DBNull.Value),
                    ("$minp", (object?)e.MinPermille ?? DBNull.Value),
                    ("$maxp", (object?)e.MaxPermille ?? DBNull.Value));
            }

            tx.Commit();
            return "";
        }
    }

    /// <summary>Delete a preset and clear any active bindings that pointed at it.</summary>
    public bool DeleteAptitudePreset(long playerId, string presetId)
    {
        if (string.IsNullOrWhiteSpace(presetId)) return false;
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();
            var existing = GetAptitudePresetUnlocked(db, presetId);
            if (existing is null || existing.PlayerId != playerId) return false;

            ExecIn(db, tx, "DELETE FROM rpg_aptitude_preset_active WHERE preset_id = $id;",
                ("$id", presetId));
            ExecIn(db, tx, "DELETE FROM rpg_aptitude_preset_entry WHERE preset_id = $id;",
                ("$id", presetId));
            ExecIn(db, tx, "DELETE FROM rpg_aptitude_preset WHERE preset_id = $id AND player_id = $p;",
                ("$id", presetId), ("$p", playerId));
            tx.Commit();
            return true;
        }
    }

    public RpgAptitudePresetActiveRow? GetAptitudePresetActive(long playerId, string scope, string scopeKey)
    {
        if (string.IsNullOrWhiteSpace(scope)) throw new ArgumentException("scope must not be empty", nameof(scope));
        scopeKey ??= "";
        lock (_gate)
        {
            using var db = OpenUnlocked();
            return GetAptitudePresetActiveUnlocked(db, playerId, scope, scopeKey);
        }
    }

    RpgAptitudePresetActiveRow? GetAptitudePresetActiveUnlocked(
        SqliteConnection db, long playerId, string scope, string scopeKey)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT player_id, scope, scope_key, preset_id FROM rpg_aptitude_preset_active
            WHERE player_id = $p AND scope = $s AND scope_key = $k;
            """;
        cmd.Parameters.AddWithValue("$p", playerId);
        cmd.Parameters.AddWithValue("$s", scope);
        cmd.Parameters.AddWithValue("$k", scopeKey);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new RpgAptitudePresetActiveRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3));
    }

    public string SetAptitudePresetActive(long playerId, string scope, string scopeKey, string presetId)
    {
        if (string.IsNullOrWhiteSpace(scope)) return "presets.scope.missing";
        if (string.IsNullOrWhiteSpace(presetId)) return "presets.id.missing";
        scopeKey ??= "";

        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();
            var preset = GetAptitudePresetUnlocked(db, presetId);
            if (preset is null) return "presets.notFound";
            if (preset.PlayerId != playerId) return "presets.owner.mismatch";
            SetAptitudePresetActiveUnlocked(db, tx, playerId, scope, scopeKey, presetId);
            tx.Commit();
            return "";
        }
    }

    void SetAptitudePresetActiveUnlocked(
        SqliteConnection db, SqliteTransaction tx, long playerId, string scope, string scopeKey, string presetId)
    {
        ExecIn(db, tx, """
            INSERT INTO rpg_aptitude_preset_active (player_id, scope, scope_key, preset_id)
            VALUES ($p, $s, $k, $id)
            ON CONFLICT(player_id, scope, scope_key) DO UPDATE SET preset_id = excluded.preset_id;
            """,
            ("$p", playerId), ("$s", scope), ("$k", scopeKey), ("$id", presetId));
    }

    /// <summary>
    /// AS-3.2 transactional Activate: materialize already done by caller; this writes active +
    /// allocation/respec in ONE transaction so neither half applies alone.
    /// </summary>
    public AptitudePresetActivateOutcome TryActivateAptitudePreset(
        long playerId,
        string presetId,
        string scope,
        string scopeKey,
        AptitudeAllocation allocation,
        IReadOnlyDictionary<string, long> shares,
        long leftover,
        string? correlationId = null,
        DateTimeOffset? utcNow = null,
        RespecPayment? payWith = null)
    {
        if (string.IsNullOrWhiteSpace(presetId))
            return FailActivate("presets.id.missing", shares, leftover);
        if (string.IsNullOrWhiteSpace(scope))
            return FailActivate("presets.scope.missing", shares, leftover);
        if (allocation is null) throw new ArgumentNullException(nameof(allocation));
        scopeKey ??= "";

        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();

            var preset = GetAptitudePresetUnlocked(db, presetId);
            if (preset is null)
                return FailActivate("presets.notFound", shares, leftover);
            if (preset.PlayerId != playerId)
                return FailActivate("presets.owner.mismatch", shares, leftover);

            SetAptitudePresetActiveUnlocked(db, tx, playerId, scope, scopeKey, presetId);

            switch (scope)
            {
                case "commander":
                {
                    // EP1.10 (spec-specimen-respec-price.md): activating over the commander pool goes
                    // through the SAME priced-or-not gate the by-hand route uses, so an activation that
                    // takes points back charges exactly what `/allocate` would for the same change.
                    var payer = new EmpireRef(new SaveId(playerId), HumanEmpireOf(playerId));
                    var outcome = TryReallocateUnlocked(db, tx, payer, AllocationScope.Commander,
                        AptitudeEndpointsScopeKey(playerId), allocation, correlationId, utcNow);
                    if (!outcome.Ok)
                    {
                        tx.Rollback();
                        return new AptitudePresetActivateOutcome(
                            false, outcome.Reason, shares, leftover,
                            outcome.Priced, outcome.PriceAmount, outcome.RespecCount, outcome.Balance);
                    }
                    tx.Commit();
                    return new AptitudePresetActivateOutcome(
                        true, outcome.Reason, shares, leftover,
                        outcome.Priced, outcome.PriceAmount, outcome.RespecCount, outcome.Balance);
                }

                case "unique":
                {
                    if (string.IsNullOrWhiteSpace(scopeKey))
                    {
                        tx.Rollback();
                        return FailActivate("presets.scopeKey.missing", shares, leftover);
                    }
                    // Same gate as the commander branch above; ownership of `scopeKey` (the specimen)
                    // is TryReallocateUnlocked's own check (respec.target.not-owned), not re-derived here.
                    var payer = new EmpireRef(new SaveId(playerId), HumanEmpireOf(playerId));
                    var outcome = TryReallocateUnlocked(db, tx, payer, AllocationScope.UniqueCreature,
                        scopeKey, allocation, correlationId, utcNow);
                    if (!outcome.Ok)
                    {
                        tx.Rollback();
                        return new AptitudePresetActivateOutcome(
                            false, outcome.Reason, shares, leftover,
                            outcome.Priced, outcome.PriceAmount, outcome.RespecCount, outcome.Balance);
                    }
                    tx.Commit();
                    return new AptitudePresetActivateOutcome(
                        true, outcome.Reason, shares, leftover,
                        outcome.Priced, outcome.PriceAmount, outcome.RespecCount, outcome.Balance);
                }

                case "species":
                {
                    if (string.IsNullOrWhiteSpace(scopeKey))
                    {
                        tx.Rollback();
                        return FailActivate("presets.scopeKey.missing", shares, leftover);
                    }
                    if (string.IsNullOrWhiteSpace(correlationId))
                    {
                        tx.Rollback();
                        return FailActivate("correlation.missing", shares, leftover);
                    }
                    if (!CreatureSpeciesCatalog.IsKnown(scopeKey))
                    {
                        tx.Rollback();
                        return FailActivate("species.unknown", shares, leftover);
                    }

                    var respec = TryRespecSpeciesUnlocked(
                        db, tx, playerId, scopeKey, allocation, correlationId!, utcNow, payWith);
                    if (!respec.Ok)
                    {
                        tx.Rollback();
                        return new AptitudePresetActivateOutcome(
                            false, respec.Reason, shares, leftover,
                            respec.Priced, respec.PriceAmount, respec.RespecCount, respec.Balance)
                            with { PaidWith = respec.PaidWith, FreeStock = respec.FreeStock };
                    }
                    tx.Commit();
                    // respec-free-counter EP4.10: the SUCCESS path reports what the respec actually paid -
                    // the same `PaidWith`/`FreeStock` the respec route returns - so an activation body and a
                    // by-hand respec body agree about the payment. (A test caught this: without it the
                    // activate response said nothing was paid even when a free respec had been spent.)
                    return new AptitudePresetActivateOutcome(
                        true, respec.Reason, shares, leftover,
                        respec.Priced, respec.PriceAmount, respec.RespecCount, respec.Balance)
                        with { PaidWith = respec.PaidWith, FreeStock = respec.FreeStock };
                }

                default:
                    tx.Rollback();
                    return FailActivate("presets.scope.unknown", shares, leftover);
            }
        }
    }

    static string AptitudeEndpointsScopeKey(long playerId) => $"player:{playerId}";

    static AptitudePresetActivateOutcome FailActivate(
        string reason, IReadOnlyDictionary<string, long> shares, long leftover) =>
        new(false, reason, shares, leftover, false, 0, 0, null);

    public static IReadOnlyList<AptitudePresetRowSpec> ToRowSpecs(
        IReadOnlyList<RpgAptitudePresetEntryRow> entries) =>
        entries.Select(e => new AptitudePresetRowSpec(
            e.AptitudeId, e.TargetPermille, e.MinAbs, e.MaxAbs, e.MinPermille, e.MaxPermille)).ToList();
}
