using FusionRpg.Contracts;
using FusionRpg.Core.Commanders;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Generation;
using FusionRpg.Core.Saves;
using FusionRpg.Core.Stats.Aptitudes;
using Microsoft.Data.Sqlite;
using FusionRpg.Core.Time;

namespace FusionRpg.Data;

/// <summary>
/// EP1.8 (spec-specimen-respec-price.md, R18, read in full this session) — the one gate for both
/// scopes a unique creature's points live in: <see cref="AllocationScope.UniqueCreature"/> (a
/// specimen's own points) and <see cref="AllocationScope.Commander"/> (the empire's side-wide pool,
/// priced too — R18's correction: "a commander is a unique creature, so a commander respec is paid").
///
/// <para><b>Style:</b> the same shape <c>RpgStore.SpeciesRespec.cs</c>'s <c>TryRespecSpeciesUnlocked</c>
/// already uses — free-vs-priced decided inside the same transaction as the debit and the write,
/// replay checked before pricing. <b>Never reuses that table</b> (spec's own callout): its key is
/// <c>(player_id, species_id)</c> and its row doubles as an "ever overridden" marker species semantics
/// need; a specimen or a commander pool needs neither, so a first allocation from
/// <see cref="AptitudeAllocation.Empty"/> is simply free (<see cref="RespecPolicy.IsRespec"/>), with no
/// row written at all.</para>
///
/// <para><b>Who pays</b> — born with `save-identity`'s <see cref="EmpireRef"/> signature (that module
/// landed first): every public method takes the caller's own <see cref="EmpireRef"/>, never a bare
/// player id. A specimen the caller's empire does not own (<see cref="RpgStore.OwnsSpecimenUnlocked"/>,
/// including a Zomboss specimen of the same save) and a non-human payer are both refused before any
/// read — souls are Tier B and an AI empire has no re-allocation surface, as for species.</para>
/// </summary>
public sealed record ReallocationOutcome(
    bool Ok, string Reason, bool Priced, long PriceAmount, long RespecCount, SoulBalanceDto? Balance);

public sealed partial class RpgStore
{
    void EnsureAllocationRespecSchemaUnlocked(SqliteConnection db) => Exec(db, """
        CREATE TABLE IF NOT EXISTS rpg_allocation_respec (
          scope           TEXT    NOT NULL,
          scope_key       TEXT    NOT NULL,
          count           INTEGER NOT NULL,
          last_respec_utc TEXT    NOT NULL,
          PRIMARY KEY (scope, scope_key)
        );
        """);

    (bool Exists, long Count, string LastRespecUtc) ReadAllocationRespecRowUnlocked(
        SqliteConnection db, AllocationScope scope, string scopeKey)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT count, last_respec_utc FROM rpg_allocation_respec WHERE scope=$sc AND scope_key=$sk;";
        cmd.Parameters.AddWithValue("$sc", ScopeToText(scope));
        cmd.Parameters.AddWithValue("$sk", scopeKey);
        using var r = cmd.ExecuteReader();
        return r.Read() ? (true, r.GetInt64(0), r.GetString(1)) : (false, 0L, ServerClock.UtcNow.ToString("o"));
    }

    /// <summary>Only <see cref="AllocationScope.UniqueCreature"/> or <see cref="AllocationScope.Commander"/>
    /// are legal here — species has its own gate with its own free rules (spec: "any other scope
    /// throws").</summary>
    static void RequireReallocationScope(AllocationScope scope)
    {
        if (scope != AllocationScope.UniqueCreature && scope != AllocationScope.Commander)
            throw new ArgumentOutOfRangeException(nameof(scope), scope,
                "RpgStore.AllocationRespec only prices Commander/UniqueCreature; species has its own gate");
    }

    /// <summary>Whether <paramref name="payer"/> owns the reallocation target. A specimen's ownership
    /// is the one save-identity predicate (<see cref="OwnsSpecimenUnlocked"/>); the commander pool has
    /// no specimen row to own, so its ownership is structural — the scope key is the payer's OWN
    /// commander scope key, "player:{save}" (<c>AptitudeEndpoints.ScopeKey</c>'s own format, mirrored
    /// here since Data may not reference Server).</summary>
    static bool OwnsReallocationTargetUnlocked(SqliteConnection db, EmpireRef payer, AllocationScope scope, string scopeKey) =>
        scope switch
        {
            AllocationScope.UniqueCreature => OwnsSpecimenUnlocked(db, payer, scopeKey),
            AllocationScope.Commander => string.Equals(
                scopeKey, $"player:{payer.Save.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "unreachable after RequireReallocationScope"),
        };

    static ReallocationOutcome Fail(string reason) => new(false, reason, false, 0, 0, null);

    /// <summary>The effective (decayed) respec count right now, for a preview before the player
    /// commits — the <see cref="AllocationScope.UniqueCreature"/>/<see cref="AllocationScope.Commander"/>
    /// twin of <see cref="GetSpeciesRespecCount"/>. Read-only; never decays the stored row.</summary>
    public long GetAllocationRespecCount(AllocationScope scope, string scopeKey, DateTimeOffset? utcNow = null)
    {
        RequireReallocationScope(scope);
        if (string.IsNullOrWhiteSpace(scopeKey))
            throw new ArgumentException("scopeKey must not be empty", nameof(scopeKey));

        lock (_gate)
        {
            using var db = OpenUnlocked();
            var (_, storedCount, lastUtc) = ReadAllocationRespecRowUnlocked(db, scope, scopeKey);
            return DecayedRespecCount(storedCount, DateTimeOffset.Parse(lastUtc), utcNow ?? ServerClock.UtcNow,
                SpeciesBuildTuningHub.Tuning.UniqueRespec.DecayDays);
        }
    }

    /// <summary>Read-only preview — the price and free stock a spend at this scope/target would face
    /// right now, without touching the counter or the ledger. <paramref name="isRespec"/> is
    /// <c>false</c> when <paramref name="proposed"/> only adds points (always free, never priced).
    /// Every quote here passes <c>freeStock: 0</c> (R18: only the species/empire respec draws on
    /// earned free respecs).</summary>
    public RespecQuote QuoteReallocation(
        EmpireRef payer, AllocationScope scope, string scopeKey, AptitudeAllocation proposed, out bool isRespec)
    {
        RequireReallocationScope(scope);
        if (proposed is null) throw new ArgumentNullException(nameof(proposed));
        if (string.IsNullOrWhiteSpace(scopeKey))
            throw new ArgumentException("scopeKey must not be empty", nameof(scopeKey));

        lock (_gate)
        {
            using var db = OpenUnlocked();
            var current = LoadAllocationUnlocked(db, scope, scopeKey);
            isRespec = RespecPolicy.IsRespec(scope, current, proposed);
            if (!isRespec) return new RespecQuote(new RespecPrice(RespecResource.Soul, 0), 0);

            var (_, storedCount, lastUtc) = ReadAllocationRespecRowUnlocked(db, scope, scopeKey);
            var effectiveCount = DecayedRespecCount(
                storedCount, DateTimeOffset.Parse(lastUtc), ServerClock.UtcNow,
                SpeciesBuildTuningHub.Tuning.UniqueRespec.DecayDays);
            return RespecPolicy.Quote(SpeciesBuildTuningHub.Tuning.UniqueRespec, effectiveCount, freeStock: 0);
        }
    }

    /// <summary>The gate's own connection + transaction wrapper (spec-specimen-respec-price.md
    /// "The gate: one store method for both scopes").</summary>
    public ReallocationOutcome TryReallocate(
        EmpireRef payer, AllocationScope scope, string scopeKey, AptitudeAllocation proposed,
        string? correlationId, DateTimeOffset? utcNow = null)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();
            var outcome = TryReallocateUnlocked(db, tx, payer, scope, scopeKey, proposed, correlationId, utcNow);
            if (outcome.Ok) tx.Commit();
            else tx.Rollback();
            return outcome;
        }
    }

    /// <summary>Same body as <see cref="TryReallocate"/> on the caller's own connection/transaction —
    /// preset activation (EP1.10) needs the respec price and the active-preset write in ONE
    /// transaction, the same shape <c>RpgStore.SpeciesRespec.cs</c>'s own Unlocked twin already gives
    /// AS-3.2's Activate.</summary>
    internal ReallocationOutcome TryReallocateUnlocked(
        SqliteConnection db, SqliteTransaction tx,
        EmpireRef payer, AllocationScope scope, string scopeKey, AptitudeAllocation proposed,
        string? correlationId, DateTimeOffset? utcNow = null)
    {
        RequireReallocationScope(scope);
        if (proposed is null) throw new ArgumentNullException(nameof(proposed));
        if (string.IsNullOrWhiteSpace(scopeKey))
            throw new ArgumentException("scopeKey must not be empty", nameof(scopeKey));

        // Non-human payer refused before any read (Tier B: souls, and an AI empire has no
        // re-allocation surface, as for species).
        if (payer.Empire != HumanEmpireOf(payer.Save.Value))
            return Fail("empire.notHuman");

        if (!OwnsReallocationTargetUnlocked(db, payer, scope, scopeKey))
            return Fail("respec.target.not-owned");

        var current = LoadAllocationUnlocked(db, scope, scopeKey);
        var isRespec = RespecPolicy.IsRespec(scope, current, proposed);

        if (!isRespec)
        {
            // Adding points is free (spec: "spending unspent points is not a respec") -- no counter,
            // no correlation id needed, nothing but the write itself.
            SaveAllocationUnlocked(db, tx, scope, scopeKey, proposed);
            return new ReallocationOutcome(true, "", false, 0, 0, ReadSoulBalanceUnlocked(db, payer.Save.Value));
        }

        if (string.IsNullOrWhiteSpace(correlationId))
            return Fail("correlation.missing");
        var corr = correlationId.Trim();

        var reason = scope == AllocationScope.Commander
            ? SoulEarnPolicy.Reasons.RespecCommander
            : SoulEarnPolicy.Reasons.RespecUnique;

        var (_, storedCount, lastUtc) = ReadAllocationRespecRowUnlocked(db, scope, scopeKey);
        var effectiveCount = DecayedRespecCount(
            storedCount, DateTimeOffset.Parse(lastUtc), utcNow ?? ServerClock.UtcNow,
            SpeciesBuildTuningHub.Tuning.UniqueRespec.DecayDays);

        // Replay check FIRST, before pricing off the (possibly already-advanced) counter -- same
        // reasoning as RpgStore.SpeciesRespec.cs: this price is a function of the counter the very
        // same call increments, so a stale recompute-and-compare would reject a legitimate replay.
        using (var check = db.CreateCommand())
        {
            check.Transaction = tx;
            check.CommandText = "SELECT delta FROM rpg_soul_ledger WHERE player_id=$p AND reason=$r AND dedupe_key=$dk;";
            check.Parameters.AddWithValue("$p", payer.Save.Value);
            check.Parameters.AddWithValue("$r", reason);
            check.Parameters.AddWithValue("$dk", corr);
            if (check.ExecuteScalar() is long storedDelta)
                return new ReallocationOutcome(true, "replay", true, -storedDelta, effectiveCount,
                    ReadSoulBalanceUnlocked(db, payer.Save.Value));
        }

        var price = RespecPolicy.PriceOf(SpeciesBuildTuningHub.Tuning.UniqueRespec, effectiveCount);
        var balance = ReadSoulBalanceUnlocked(db, payer.Save.Value);
        if (balance.Balance < price.Amount)
            return new ReallocationOutcome(false, "souls.insufficient", true, price.Amount, effectiveCount, balance);

        var nowText = (utcNow ?? ServerClock.UtcNow).ToString("o");
        AppendSoulLedgerUnlocked(db, payer.Save.Value, 0, -price.Amount, reason, "spend", corr, corr, nowText);

        var newCount = effectiveCount + 1;
        using (var cmd = db.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO rpg_allocation_respec(scope, scope_key, count, last_respec_utc)
                VALUES ($sc, $sk, $c, $t)
                ON CONFLICT(scope, scope_key)
                DO UPDATE SET count = $c, last_respec_utc = $t;
                """;
            cmd.Parameters.AddWithValue("$sc", ScopeToText(scope));
            cmd.Parameters.AddWithValue("$sk", scopeKey);
            cmd.Parameters.AddWithValue("$c", newCount);
            cmd.Parameters.AddWithValue("$t", nowText);
            cmd.ExecuteNonQuery();
        }

        SaveAllocationUnlocked(db, tx, scope, scopeKey, proposed);
        return new ReallocationOutcome(true, "", true, price.Amount, newCount, ReadSoulBalanceUnlocked(db, payer.Save.Value));
    }
}
