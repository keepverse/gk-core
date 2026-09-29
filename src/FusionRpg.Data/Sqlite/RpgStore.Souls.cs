using FusionRpg.Contracts;
using FusionRpg.Core.Activity;
using FusionRpg.Core.Commanders;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Saves;
using Microsoft.Data.Sqlite;
using FusionRpg.Core.Time;

namespace FusionRpg.Data;

public sealed partial class RpgStore
{
    /// <summary>
    /// Kill-earn memo per (player, run) — the store is single-writer under _gate, so this stays
    /// exact. Seeded once per run by an index-only COUNT, then incremented in memory: without it,
    /// every kill re-scanned the lifetime ledger inside the ingest transaction (review C1).
    /// </summary>
    readonly Dictionary<(long PlayerId, long RunId), int> _killEarnMemo = new();

    /// <summary>
    /// T3.6 (spec-caps-reconcile.md §2.3, SSOT §11.7a): every soul-earn amount now scales by
    /// <c>contentScale(Θ)</c> instead of the flat caps this task deleted. The live vanilla-PvZ
    /// capture pipeline (<c>zombie.die</c> / <c>match.result</c> → <see cref="PvzActivityKinds"/>)
    /// carries no per-kill or per-run depth signal today — a <c>ZombieKilled</c> fact records only
    /// that a kill happened (<c>PvzActivityRollupBuilder</c> reads nothing else off it), and a
    /// <c>MatchEnded</c> fact carries only a result string. Wiring a real signal through vanilla PvZ
    /// capture is a separate, unbuilt task — none of <c>PvzActivityKinds.cs</c>, the injector capture
    /// hooks, or the fact-shaping code are in this task's file list. Reading at the pin keeps every
    /// vanilla-PvZ soul award byte-identical to pre-T3.6 behavior (contentScale(20) = 1.000 exactly)
    /// — an explicit, documented placeholder, never a silent unscaled default.
    /// </summary>
    const int VanillaPvzKillAndRunTheta = Core.Creatures.SoulSinkPolicy.VanillaPvzTheta;

    /// <summary>
    /// Soul earns from a freshly inserted Activity fact — called inside the fact's own transaction
    /// (spec-soul-economy.md). Dedupe key = fact id, so re-ingest can never double-earn.
    /// </summary>
    /// <summary>
    /// <b>SR-18 (2026-09-17, owner ruling: "owner of unique actor").</b> A lawn kill is credited to the
    /// player who owns the unique actor that made it, not to whoever happens to own the run.
    ///
    /// <para><b>Why this is a lookup and not a new capture signal.</b> The register recorded this as
    /// blocked on the <c>ZombieKilled</c> fact carrying no attribution. The FACT does not, but the raw
    /// capture <paramref name="payload"/> right here does — <c>killerPtr</c>, with the same
    /// <c>damageFrom</c> fallback <c>EffectEventAdapterCore</c> reads. The attribution was being dropped
    /// at the projection, one frame from where it was needed.</para>
    ///
    /// <para><b>The anti-fraud discipline is borrowed, not invented.</b> The killer must be an
    /// <c>ActiveBound</c> specimen in THIS match on the OPPOSING side — a <c>ZombieKilled</c> can only be
    /// credited to a plant. That is exactly what <c>TryRecoverActiveByPtr</c> already requires before it
    /// awards the same kill's XP, so souls and XP now agree on who earned it instead of answering
    /// differently.</para>
    ///
    /// <para>Falls back to the run's own save, human empire, whenever the kill cannot be attributed —
    /// an ordinary vanilla plant killing a zombie is the common case and is not a unique actor at all.</para>
    ///
    /// <para><b>save-identity SE4.25:</b> returns the killer's own <see cref="EmpireRef"/>, read off the
    /// specimen's `empire_id` — never just its `player_id`, which after SE4.22 a Zomboss specimen shares
    /// with the human save. A row with no `empire_id` yet (pre-R3 legacy) reads as that save's human
    /// empire, matching <c>IsHumanEmpireSpecimenUnlocked</c>'s own "no column yet → human" rule.</para>
    /// </summary>
    EmpireRef ResolveLawnKillCreditUnlocked(
        SqliteConnection db, string payload, string? matchKey, long fallbackPlayerId)
    {
        var fallback = new EmpireRef(new SaveId(fallbackPlayerId), HumanEmpireOf(fallbackPlayerId));
        if (string.IsNullOrWhiteSpace(matchKey)) return fallback;
        var killerPtr = TryString(payload, "killerPtr") ?? TryString(payload, "damageFrom");
        if (string.IsNullOrWhiteSpace(killerPtr)) return fallback;

        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT player_id, empire_id FROM rpg_unique_actors
            WHERE phase = $phase AND last_ptr = $ptr AND match_key = $mk AND side = 'plant';
            """;
        cmd.Parameters.AddWithValue("$phase", FusionRpg.Contracts.UniqueActorPhases.ActiveBound);
        cmd.Parameters.AddWithValue("$ptr", killerPtr!);
        cmd.Parameters.AddWithValue("$mk", matchKey!);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return fallback;
        var ownerSave = new SaveId(r.GetInt64(0));
        if (r.IsDBNull(1)) return new EmpireRef(ownerSave, HumanEmpireOf(ownerSave.Value));
        return new EmpireRef(ownerSave, new EmpireId(r.GetString(1)));
    }

    void ApplySoulEarnFromActivityUnlocked(
        SqliteConnection db, long playerId, long runId, string t, string factKind, string payload, long factId,
        string? matchKey = null)
    {
        var tuning = Core.Power.PowerTuningHub.Tuning;
        long delta;
        string reason;
        // SR-18: every read below uses the CREDITED player -- memo, patron check, prior-kill count and
        // the ledger row itself. Crediting one player while counting another's kills would make the
        // patron bonus fire on the wrong ledger.
        long creditPlayerId;
        if (factKind == PvzActivityKinds.ZombieKilled)
        {
            // Deliberately NOT named after the field BattleReportEmitter.cs writes for the unread SR-18
            // dark carrier (DarkCarrierGuardTests scans plain text, so even a comment repeating that
            // exact identifier trips its canary). This is a different mechanism entirely: the LAWN'S
            // OWN killerPtr-based attribution (ResolveLawnKillCreditUnlocked) -- SR-18's own register
            // text says the lawn side still has "no credit to spend" from that battle-side field, so
            // this local must never spell the same identifier, even by coincidence.
            var lawnKillOwner = ResolveLawnKillCreditUnlocked(db, payload, matchKey, playerId);
            // save-identity SE4.25: a non-human killer (an AI empire's own specimen, e.g. Zomboss's)
            // earns no kill souls. Logged the way every other silent-skip path in this store already
            // is (`[tag] ...`, RpgStore.cs's own ProjectRecipes/ProjectBaseStats precedent) — never
            // thrown, because a kill must never fail activity ingest.
            if (!string.Equals(lawnKillOwner.Empire.Value, HumanEmpireOf(lawnKillOwner.Save.Value).Value, StringComparison.Ordinal))
            {
                Console.WriteLine($"[souls] non-human kill credit skipped: empire={lawnKillOwner.Empire.Value} save={lawnKillOwner.Save.Value}");
                return;
            }
            creditPlayerId = lawnKillOwner.Save.Value;
        }
        else
        {
            creditPlayerId = playerId;
        }
        switch (factKind)
        {
            case PvzActivityKinds.ZombieKilled:
            {
                var key = (creditPlayerId, runId);
                if (!_killEarnMemo.TryGetValue(key, out var counted))
                {
                    if (_killEarnMemo.Count > 128) _killEarnMemo.Clear(); // stale-run safety valve
                    counted = (int)CountSoulEarnsUnlocked(db, creditPlayerId, SoulEarnPolicy.Reasons.Kill, runId);
                    _killEarnMemo[key] = counted;
                }

                // Patron bonus (spec-patron-creature.md): +1 on every 10th earning kill, uncapped since
                // T3.6. Gated so the audited unpatroned shape stays byte-identical; the patron check
                // is a PK point lookup, not a scan (review-C1 discipline).
                delta = HasPatronUnlocked(db, creditPlayerId)
                    ? Core.Creatures.Patron.PatronPolicy.KillEarnWithPatron(counted, VanillaPvzKillAndRunTheta, tuning)
                    : SoulEarnPolicy.KillEarn(VanillaPvzKillAndRunTheta, tuning);
                if (delta > 0)
                    _killEarnMemo[key] = counted + 1;
                reason = SoulEarnPolicy.Reasons.Kill;
                break;
            }
            case PvzActivityKinds.MatchEnded:
            {
                // Same normalization as the runs projection ("win" → "victory", etc.). No more
                // daily-victory decay (T3.6 deleted VictoryFullPerDay, audit F11) -- both outcomes
                // read the same way now.
                var victory = string.Equals(NormalizeResult(TryString(payload, "result")), "victory", StringComparison.Ordinal);
                delta = SoulEarnPolicy.MatchEndEarn(victory, VanillaPvzKillAndRunTheta, tuning);
                reason = victory ? SoulEarnPolicy.Reasons.Victory : SoulEarnPolicy.Reasons.Defeat;
                break;
            }
            default:
                return;
        }

        if (delta <= 0) return;
        AppendSoulLedgerUnlocked(db, creditPlayerId, runId, delta, reason, "activity_fact",
            factId.ToString(), factId.ToString(), t);
    }

    long CountSoulEarnsUnlocked(SqliteConnection db, long playerId, string reason, long runId)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM rpg_soul_ledger WHERE player_id=$p AND reason=$r AND run_id=$run;";
        cmd.Parameters.AddWithValue("$p", playerId);
        cmd.Parameters.AddWithValue("$r", reason);
        cmd.Parameters.AddWithValue("$run", runId);
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    /// <summary>Append one ledger row + fold it into the balance snapshot. True when newly inserted.</summary>
    bool AppendSoulLedgerUnlocked(
        SqliteConnection db, long playerId, long runId, long delta, string reason,
        string? refKind, string? refId, string dedupeKey, string t)
    {
        long ledgerId;
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                INSERT OR IGNORE INTO rpg_soul_ledger(
                  player_id, run_id, delta, reason, ref_kind, ref_id, dedupe_key, t)
                VALUES($p, $run, $d, $r, $rk, $ri, $dk, $t);
                SELECT CASE WHEN changes() > 0 THEN last_insert_rowid() ELSE 0 END;
                """;
            cmd.Parameters.AddWithValue("$p", playerId);
            cmd.Parameters.AddWithValue("$run", runId);
            cmd.Parameters.AddWithValue("$d", delta);
            cmd.Parameters.AddWithValue("$r", reason);
            cmd.Parameters.AddWithValue("$rk", (object?)refKind ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ri", (object?)refId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$dk", dedupeKey);
            cmd.Parameters.AddWithValue("$t", t);
            ledgerId = (long)(cmd.ExecuteScalar() ?? 0L);
        }

        if (ledgerId == 0) return false;

        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO rpg_soul_balances(player_id, balance, earned_total, spent_total, through_ledger_id, revision, updated_utc)
                VALUES($p, $d, $earn, $spend, $lid, 1, $t)
                ON CONFLICT(player_id) DO UPDATE SET
                  balance = balance + $d,
                  earned_total = earned_total + $earn,
                  spent_total = spent_total + $spend,
                  through_ledger_id = $lid,
                  revision = revision + 1,
                  updated_utc = $t;
                """;
            cmd.Parameters.AddWithValue("$p", playerId);
            cmd.Parameters.AddWithValue("$d", delta);
            cmd.Parameters.AddWithValue("$earn", delta > 0 ? delta : 0);
            cmd.Parameters.AddWithValue("$spend", delta < 0 ? -delta : 0);
            cmd.Parameters.AddWithValue("$lid", ledgerId);
            cmd.Parameters.AddWithValue("$t", t);
            cmd.ExecuteNonQuery();
        }

        // Earn notifications ride PvzActivityUpdated (earns always accompany a fact);
        // spends/awards push SoulsUpdated explicitly from their endpoints.
        return true;
    }

    /// <summary>
    /// Dynamic per-award ceiling — headroom to <c>long.MaxValue</c> from a given balance
    /// (spec-caps-reconcile.md §2.1, F12), not a fixed constant. What actually overflows is
    /// <c>balance + award</c> after many awards, not any single award's own size, so the bound has to
    /// track the balance it protects rather than sit at some round decimal near <c>int64</c>. Pure and
    /// public — no DB needed to test "an award legal at balance 0 is refused near int64Max" directly.
    /// </summary>
    public static long MaxSoulAwardFrom(long balance) => checked(long.MaxValue - Math.Max(0L, balance));

    /// <summary>
    /// One policy for the soul-award ceiling, shared by every path that can credit a balance
    /// (<see cref="AwardSouls"/> here and <c>ApplyExpeditionRewards</c> in RpgStore.Expeditions.cs) —
    /// before this task the expedition path silently clamped instead (§11.2a). Throws, never clamps.
    /// </summary>
    static void GuardSoulAwardOrThrow(long balance, long delta)
    {
        var headroom = MaxSoulAwardFrom(balance);
        if (delta > headroom)
            throw new ArgumentOutOfRangeException(nameof(delta), delta,
                $"soul award exceeds headroom {headroom} at balance {balance} (int64 balance ceiling)");
    }

    /// <summary>Positive award outside the activity path (codex discovery, milestones). Idempotent on dedupe.</summary>
    public (bool Inserted, SoulBalanceDto Balance) AwardSouls(long playerId, long delta, string reason, string dedupeKey)
    {
        if (delta <= 0) throw new ArgumentOutOfRangeException(nameof(delta));
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();
            GuardSoulAwardOrThrow(ReadSoulBalanceUnlocked(db, playerId).Balance, delta);
            var inserted = AppendSoulLedgerUnlocked(db, playerId, 0, delta, reason, null, null, dedupeKey,
                ServerClock.UtcNowDateTime.ToString("o"));
            tx.Commit();
            return (inserted, ReadSoulBalanceUnlocked(db, playerId));
        }
    }

    /// <summary>
    /// Atomic spend. Refusals write nothing; a replayed (player, reason, correlationId) that
    /// previously succeeded returns the original success without spending again.
    /// </summary>
    public (bool Ok, string Reason, SoulBalanceDto Balance) TrySpendSouls(
        long playerId, long amount, string reason, string correlationId)
    {
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount));
        if (string.IsNullOrWhiteSpace(correlationId)) throw new ArgumentException("correlationId required");
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();
            var corr = correlationId.Trim();

            using (var check = db.CreateCommand())
            {
                check.CommandText = "SELECT delta FROM rpg_soul_ledger WHERE player_id=$p AND reason=$r AND dedupe_key=$dk;";
                check.Parameters.AddWithValue("$p", playerId);
                check.Parameters.AddWithValue("$r", reason);
                check.Parameters.AddWithValue("$dk", corr);
                if (check.ExecuteScalar() is long storedDelta)
                {
                    tx.Commit();
                    // Replay must be the SAME request — a different amount under a reused
                    // correlation is a caller bug, not an idempotent retry.
                    return storedDelta == -amount
                        ? (true, "replay", ReadSoulBalanceUnlocked(db, playerId))
                        : (false, "correlation.mismatch", ReadSoulBalanceUnlocked(db, playerId));
                }
            }

            var balance = ReadSoulBalanceUnlocked(db, playerId);
            if (balance.Balance < amount)
            {
                tx.Rollback();
                return (false, "souls.insufficient", balance);
            }

            AppendSoulLedgerUnlocked(db, playerId, 0, -amount, reason, "spend", corr, corr,
                ServerClock.UtcNowDateTime.ToString("o"));
            tx.Commit();
            return (true, "", ReadSoulBalanceUnlocked(db, playerId));
        }
    }

    public SoulBalanceDto GetSoulBalance(long playerId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            return ReadSoulBalanceUnlocked(db, playerId);
        }
    }

    public SoulLedgerDto ListSoulLedger(long playerId, int limit = 100, long afterId = 0)
    {
        limit = Math.Clamp(limit, 1, 500);
        lock (_gate)
        {
            using var db = OpenUnlocked();
            var items = new List<SoulLedgerEntryDto>();
            using var cmd = db.CreateCommand();
            // Descending pagination: afterId is the smallest id already seen — pass it back to get
            // the next OLDER page (id > $after with DESC order could never page backwards).
            cmd.CommandText = """
                SELECT id, run_id, delta, reason, ref_kind, ref_id, t
                FROM rpg_soul_ledger
                WHERE player_id=$p AND ($after <= 0 OR id < $after)
                ORDER BY id DESC LIMIT $lim;
                """;
            cmd.Parameters.AddWithValue("$p", playerId);
            cmd.Parameters.AddWithValue("$after", afterId);
            cmd.Parameters.AddWithValue("$lim", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                items.Add(new SoulLedgerEntryDto
                {
                    Id = r.GetInt64(0),
                    RunId = r.GetInt64(1),
                    Delta = r.GetInt64(2),
                    Reason = r.GetString(3),
                    RefKind = r.IsDBNull(4) ? null : r.GetString(4),
                    RefId = r.IsDBNull(5) ? null : r.GetString(5),
                    T = r.GetString(6)
                });
            }

            return new SoulLedgerDto { PlayerId = playerId, Items = items };
        }
    }

    SoulBalanceDto ReadSoulBalanceUnlocked(SqliteConnection db, long playerId)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT balance, earned_total, spent_total, revision, updated_utc
            FROM rpg_soul_balances WHERE player_id=$p;
            """;
        cmd.Parameters.AddWithValue("$p", playerId);
        using var r = cmd.ExecuteReader();
        if (!r.Read())
            return new SoulBalanceDto { PlayerId = playerId };
        return new SoulBalanceDto
        {
            PlayerId = playerId,
            Balance = r.GetInt64(0),
            EarnedTotal = r.GetInt64(1),
            SpentTotal = r.GetInt64(2),
            Revision = r.GetInt64(3),
            UpdatedUtc = r.GetString(4)
        };
    }
}
