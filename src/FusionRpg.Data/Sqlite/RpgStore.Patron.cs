using FusionRpg.Contracts;
using FusionRpg.Core.Creatures.Patron;
using Microsoft.Data.Sqlite;
using FusionRpg.Core.Commanders;
using FusionRpg.Core.Saves;
using FusionRpg.Core.Time;

namespace FusionRpg.Data;

public sealed record PatronRow(long PlayerId, string InstanceId, string SetUtc, long Revision);

/// <summary>build-preset BP2.1 (spec-piece-appliers.md): the read-only answer <c>QuotePatron</c> gives —
/// what <c>SetPatron</c> would refuse or charge for the SAME target, without writing anything.
/// <c>Replay</c> mirrors <c>SetPatron</c>'s own free "re-designate the current patron" case.</summary>
public sealed record PatronQuote(bool Ok, string Reason, long Price, bool Replay);

public sealed partial class RpgStore
{
    /// <summary>
    /// Patron designation (spec-patron-creature.md): first set free, every change spends
    /// PatronPolicy.SwitchCostSouls — one transaction, refusals write nothing. Re-designating
    /// the CURRENT patron is a natural free replay; a correlation reused for a different target
    /// is a mismatch (the soul-ledger dedupe is the switch's replay anchor).
    /// </summary>
    public (bool Ok, string Reason, PatronRow? Patron) SetPatron(long playerId, string instanceId, string correlationId)
    {
        if (string.IsNullOrWhiteSpace(correlationId)) return (false, "correlation.missing", null);
        if (string.IsNullOrWhiteSpace(instanceId)) return (false, "specimen.missing", null);
        var id = instanceId.Trim();
        var corr = correlationId.Trim();

        lock (_gate)
        {
            using var db = OpenUnlocked();
            if (GetPlayerUnlocked(db, playerId) is null) return (false, "player.unknown", null);
            using var tx = db.BeginTransaction();
            var now = ServerClock.UtcNowDateTime.ToString("o");

            var refusal = PatronPreconditionsUnlocked(db, playerId, id, assumeBound: false);
            if (refusal is not null) return (false, refusal, null);

            var current = ReadPatronUnlocked(db, playerId);
            if (current != null && string.Equals(current.InstanceId, id, StringComparison.Ordinal))
            {
                tx.Commit();
                return (true, "replay", current);
            }

            if (current != null)
            {
                var balance = ReadSoulBalanceUnlocked(db, playerId);
                if (balance.Balance < PatronPolicy.SwitchCostSouls)
                    return (false, "souls.insufficient", null);
                if (!AppendSoulLedgerUnlocked(db, playerId, 0, -PatronPolicy.SwitchCostSouls,
                        Core.Creatures.SoulEarnPolicy.Reasons.Patron, "spend", corr, corr, now))
                    return (false, "correlation.mismatch", null); // corr already bought a different switch
            }

            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT INTO rpg_patron(player_id, instance_id, set_utc, revision)
                    VALUES($p, $i, $t, 1)
                    ON CONFLICT(player_id)
                    DO UPDATE SET instance_id = $i, set_utc = $t, revision = revision + 1;
                    """;
                cmd.Parameters.AddWithValue("$p", playerId);
                cmd.Parameters.AddWithValue("$i", id);
                cmd.Parameters.AddWithValue("$t", now);
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
            return (true, "", ReadPatronUnlocked(db, playerId));
        }
    }

    /// <summary>
    /// build-preset BP2.1 (spec-piece-appliers.md): the read-only half of <see cref="SetPatron"/>'s
    /// checks, extracted so the write and <see cref="QuotePatron"/> share one function — a preview
    /// can never disagree with what the write then refuses. <paramref name="assumeBound"/> lets a
    /// preset's patron piece be judged against a field piece that binds this SAME creature earlier
    /// in the same apply (<c>PlanState.WillBeBound</c>): the creature is not bound yet, but will be
    /// by the time this write actually runs, so the bound check alone is skipped — the deployable
    /// check still reads real loyalty (<c>ContractRow.Deployable</c> itself requires <c>Bound</c>,
    /// which would wrongly refuse an about-to-be-bound creature, so this reads
    /// <see cref="Core.Creatures.Contracts.ContractPolicy.IsDeployable"/> directly instead).
    /// </summary>
    internal string? PatronPreconditionsUnlocked(SqliteConnection db, long playerId, string instanceId, bool assumeBound)
    {
        var actor = ReadUniqueActorUnlocked(db, instanceId);
        var profile = actor == null ? null : ReadCreatureProfileUnlocked(db, instanceId);
        // save-identity SE4.24: the one ownership predicate — a Zomboss specimen of this save must
        // never be designatable as the human's patron (it replaces the old actor.PlayerId
        // comparison). build-preset BP2.1's extraction of this shared helper had used a bare
        // `PlayerId` equality, which silently dropped the SE4.24 EmpireRef ownership check
        // SetPatron's own pre-refactor body carried — restored here so QuotePatron (BP2.1's own
        // reason for this helper) gets the same real guard SetPatron always had, not a weaker one.
        if (actor is null
            || !OwnsSpecimenUnlocked(db, new EmpireRef(new SaveId(playerId), HumanEmpireOf(playerId)), actor.InstanceId)
            || profile is null
            || string.Equals(actor.Phase, UniqueActorPhases.Retired, StringComparison.Ordinal))
            return "specimen.missing";

        // A patron speaks for the summoner: it must be a creature that actually serves.
        var contract = ContractViewUnlocked(db, playerId, instanceId);
        if (!assumeBound && !contract.Bound) return "patron.unbound";
        if (!Core.Creatures.Contracts.ContractPolicy.IsDeployable(contract.Loyalty)) return "patron.insubordinate";

        return null;
    }

    /// <summary>
    /// build-preset BP2.1: what <see cref="SetPatron"/> would refuse or charge for this target,
    /// without writing anything — the same precondition and the same price rule
    /// (<c>PatronPolicy.SwitchCostSouls</c> on a switch, free on a first designation or a replay).
    /// </summary>
    public PatronQuote QuotePatron(long playerId, string instanceId, bool assumeBound)
    {
        var id = (instanceId ?? "").Trim();
        if (id.Length == 0) return new PatronQuote(false, "specimen.missing", 0, false);

        lock (_gate)
        {
            using var db = OpenUnlocked();
            if (GetPlayerUnlocked(db, playerId) is null) return new PatronQuote(false, "player.unknown", 0, false);

            var refusal = PatronPreconditionsUnlocked(db, playerId, id, assumeBound);
            if (refusal is not null) return new PatronQuote(false, refusal, 0, false);

            var current = ReadPatronUnlocked(db, playerId);
            if (current != null && string.Equals(current.InstanceId, id, StringComparison.Ordinal))
                return new PatronQuote(true, "replay", 0, true);

            var price = current != null ? PatronPolicy.SwitchCostSouls : 0;
            return new PatronQuote(true, "", price, false);
        }
    }

    public PatronRow? GetPatron(long playerId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            return ReadPatronUnlocked(db, playerId);
        }
    }

    PatronRow? ReadPatronUnlocked(SqliteConnection db, long playerId)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT player_id, instance_id, set_utc, revision FROM rpg_patron WHERE player_id=$p;";
        cmd.Parameters.AddWithValue("$p", playerId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? new PatronRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3)) : null;
    }

    internal bool IsPatronUnlocked(SqliteConnection db, long playerId, string instanceId)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM rpg_patron WHERE player_id=$p AND instance_id=$i LIMIT 1;";
        cmd.Parameters.AddWithValue("$p", playerId);
        cmd.Parameters.AddWithValue("$i", instanceId);
        return cmd.ExecuteScalar() != null;
    }

    /// <summary>PK point lookup — cheap enough for the per-kill earn hook (no scan, review-C1-safe).</summary>
    internal bool HasPatronUnlocked(SqliteConnection db, long playerId)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM rpg_patron WHERE player_id=$p LIMIT 1;";
        cmd.Parameters.AddWithValue("$p", playerId);
        return cmd.ExecuteScalar() != null;
    }
}