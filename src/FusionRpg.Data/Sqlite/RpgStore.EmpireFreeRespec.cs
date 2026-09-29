using FusionRpg.Core.Progression;
using FusionRpg.Core.Saves;
using Microsoft.Data.Sqlite;

namespace FusionRpg.Data;

/// <summary>
/// `respec-free-counter` (module 3, `empire-progression` Wave D; spec:
/// `docs/architecture/empire-progression/spec-respec-free-counter.md`, ruling R18) — the **stock** of
/// earned free empire respecs, and the only place an empire level's `FreeEmpireRespec` grant lands.
///
/// <para><b>Keyed for EVERY empire, not for the human one.</b> This is Empire-progression **X13** in
/// `solid-enforcement/spec-save-identity.md`'s consumer table: the free-respec stock ledger and the
/// empire-level row are the deliberate exception to Tier B's human-only rule, so Zomboss's empire
/// accrues on the same track (ruling R19) and only the SPEND stays human-only (EP4.9, where
/// `EmpireRef` refuses a non-human payer). The key is `(save_id, empire_id)` and nothing here reads
/// `HumanEmpireOf`.</para>
///
/// <para><b>The stock is `SUM(delta)`, never a stored running total.</b> A stored balance would be a
/// second source of truth for the same number and could disagree with its own ledger; the sum cannot.
/// Grants are positive and are never taken back (ruling: "a grant is never taken back"), so a negative
/// sum means the ledger is corrupt — the read derives that bound and THROWS rather than clamping to 0,
/// because a silently clamped stock would let a spend succeed past an empty ledger (AGENTS.md's
/// "absolute bounds are derived and throw" rule; the spend itself arrives with EP4.9).</para>
///
/// <para><b>Replay and idempotence are the primary key's job.</b> One row per
/// `(save, empire, level)` — `L{n}` in the spec's own words — so applying the same level's grant twice
/// is an `INSERT OR IGNORE` no-op, exactly like every other ledger in this store.</para>
/// </summary>
public sealed partial class RpgStore
{
    /// <summary>The one reason this ledger has today: an empire level's grant. Named for the faucet the
    /// registry row (`empire-resource-ssot.md` §3) already states, so a reader can join the two.</summary>
    public const string FreeRespecReasonEmpireLevel = "empire-level";

    void EnsureEmpireFreeRespecSchemaUnlocked(SqliteConnection db)
    {
        Exec(db, """
            CREATE TABLE IF NOT EXISTS rpg_empire_free_respec_ledger (
              save_id     INTEGER NOT NULL,
              empire_id   TEXT    NOT NULL,
              level       INTEGER NOT NULL,
              delta       INTEGER NOT NULL,
              reason      TEXT    NOT NULL,
              granted_utc TEXT    NOT NULL,
              PRIMARY KEY (save_id, empire_id, level)
            );
            """);
        // respec-free-counter EP4.9: a SPEND is a negative-delta row on this same ledger, and unlike a
        // grant it must be replayed safely. The primary key already separates rows by `level` (a grant
        // keys on the level that paid; a spend takes the next negative key), but a replay has to be
        // recognised by WHAT the caller asked for, not by the key it would mint — so a spend carries the
        // respec's own correlation id and this partial unique index refuses the replay. Grants leave the
        // column NULL: their primary key is already their guard.
        //
        // Additive migration (H2): the column and its index land here, in the same commit as the writer,
        // and before it — `EnsureEmpireFreeRespecSchemaUnlocked` runs from `Init`.
        EnsureColumn(db, "rpg_empire_free_respec_ledger", "dedupe_key", "TEXT");
        Exec(db, """
            CREATE UNIQUE INDEX IF NOT EXISTS ux_rpg_empire_free_respec_dedupe
              ON rpg_empire_free_respec_ledger(save_id, empire_id, dedupe_key)
              WHERE dedupe_key IS NOT NULL;
            """);
    }

    /// <summary>The reason a free-spend row carries, so the stock's history reads as a faucet and its
    /// sinks — the registry row (`empire-resource-ssot.md` §3) names the same pair.</summary>
    public const string FreeRespecReasonSpeciesRespec = "species-respec";

    /// <summary>Whether this empire already paid for the respec named by <paramref name="correlationId"/>.
    /// The spend ledger's own replay guard: the correlation is the caller's promise that a repeat means
    /// "the same request" (`TryRespecSpecies`'s soul-ledger check makes the identical promise one ledger
    /// over).</summary>
    internal bool FreeRespecSpentForCorrelationUnlocked(SqliteConnection db, EmpireRef owner, string correlationId)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT 1 FROM rpg_empire_free_respec_ledger
            WHERE save_id = $s AND empire_id = $e AND dedupe_key = $d LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$s", owner.Save.Value);
        cmd.Parameters.AddWithValue("$e", owner.Empire.Value);
        cmd.Parameters.AddWithValue("$d", correlationId);
        return cmd.ExecuteScalar() is not null;
    }

    /// <summary>
    /// Spends ONE free empire respec for the respec named by <paramref name="correlationId"/>, in the
    /// caller's transaction — the spec's own "the spend and the override are one transaction", so a
    /// failed respec never costs the stock. Refuses (returns false) when the stock is empty; returns true
    /// for a replay without writing, which is what makes a repeated request return its original payment.
    ///
    /// <para>The row's key is the next NEGATIVE integer in this empire's ledger: grants key on the level
    /// that paid (`2, 3, ...`), spends take `-1, -2, ...`, and the primary key keeps them distinct while
    /// `SUM(delta)` reads both as one stock.</para>
    /// </summary>
    internal bool TrySpendFreeRespecUnlocked(
        SqliteConnection db, EmpireRef owner, string correlationId, string t, out long stockAfter)
    {
        if (FreeRespecSpentForCorrelationUnlocked(db, owner, correlationId))
        {
            stockAfter = FreeRespecStockUnlocked(db, owner);
            return true;   // replay: the original payment stands, nothing is written
        }

        var stock = FreeRespecStockUnlocked(db, owner);
        if (stock < 1)
        {
            stockAfter = stock;
            return false;
        }

        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO rpg_empire_free_respec_ledger(save_id, empire_id, level, delta, reason, granted_utc, dedupe_key)
            VALUES ($s, $e,
                    (SELECT COALESCE(MIN(level), 0) - 1 FROM rpg_empire_free_respec_ledger
                     WHERE save_id = $s AND empire_id = $e),
                    -1, $r, $t, $d);
            """;
        cmd.Parameters.AddWithValue("$s", owner.Save.Value);
        cmd.Parameters.AddWithValue("$e", owner.Empire.Value);
        cmd.Parameters.AddWithValue("$r", FreeRespecReasonSpeciesRespec);
        cmd.Parameters.AddWithValue("$t", t);
        cmd.Parameters.AddWithValue("$d", correlationId);
        cmd.ExecuteNonQuery();

        stockAfter = FreeRespecStockUnlocked(db, owner);
        return true;
    }

    /// <summary>
    /// Applies every grant the crossing levels carry, in the CALLER's transaction — the spec's own
    /// "applied in the level-change transaction", so a rolled-back level change takes its grants with
    /// it and a level can never be paid without its stock row. `level` is the level the GRANT is for
    /// (`L{levelAfter}`), which is also the primary key's third column: a replayed level-up is ignored
    /// by the database rather than by a rule someone has to remember.
    ///
    /// <para>A grant list of zero (the published `freeRespecsPerEmpireLevel = 0`, or every level below
    /// the one that pays) writes nothing at all — no row, no zero-delta row.</para>
    /// </summary>
    internal void ApplyEmpireLevelGrantsUnlocked(
        SqliteConnection db, EmpireRef owner, IReadOnlyList<EmpireLevelUpEvent> events, string t)
    {
        foreach (var levelUp in events)
        {
            foreach (var grant in levelUp.Grants)
            {
                if (grant.Kind != EmpireLevelGrantKind.FreeEmpireRespec) continue;
                if (grant.Amount == 0) continue;
                using var cmd = db.CreateCommand();
                cmd.CommandText = """
                    INSERT OR IGNORE INTO rpg_empire_free_respec_ledger
                        (save_id, empire_id, level, delta, reason, granted_utc)
                    VALUES ($s, $e, $l, $d, $r, $t);
                    """;
                cmd.Parameters.AddWithValue("$s", owner.Save.Value);
                cmd.Parameters.AddWithValue("$e", owner.Empire.Value);
                cmd.Parameters.AddWithValue("$l", levelUp.LevelAfter);
                cmd.Parameters.AddWithValue("$d", grant.Amount);
                cmd.Parameters.AddWithValue("$r", FreeRespecReasonEmpireLevel);
                cmd.Parameters.AddWithValue("$t", t);
                cmd.ExecuteNonQuery();
            }
        }
    }

    /// <summary>The stock for one empire: `SUM(delta)` over its own rows. Throws
    /// <see cref="InvalidOperationException"/> when the sum is negative — see the class doc; a stock is
    /// a derived quantity, so the bound is derived and reported rather than clamped.</summary>
    internal long FreeRespecStockUnlocked(SqliteConnection db, EmpireRef owner)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT COALESCE(SUM(delta), 0) FROM rpg_empire_free_respec_ledger
            WHERE save_id = $s AND empire_id = $e;
            """;
        cmd.Parameters.AddWithValue("$s", owner.Save.Value);
        cmd.Parameters.AddWithValue("$e", owner.Empire.Value);
        var sum = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
        if (sum < 0)
            throw new InvalidOperationException(
                $"rpg_empire_free_respec_ledger sums to {sum} for save {owner.Save.Value} empire " +
                $"'{owner.Empire.Value}' — a grant is never taken back, so a negative stock is a corrupt ledger, " +
                "not a balance to clamp");
        return sum;
    }

    /// <summary>The stock for one empire, for a caller that holds no connection (the level read EP4.7
    /// renders, and the respec path EP4.9 spends). A save whose empires were never seeded throws from
    /// its own caller rather than guessing an empire here.</summary>
    public long FreeRespecStock(EmpireRef owner)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            return FreeRespecStockUnlocked(db, owner);
        }
    }
}
