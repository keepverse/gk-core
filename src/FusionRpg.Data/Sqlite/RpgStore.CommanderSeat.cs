using FusionRpg.Contracts;
using FusionRpg.Core.Commanders;
using FusionRpg.Core.Saves;
using Microsoft.Data.Sqlite;
using FusionRpg.Core.Time;

namespace FusionRpg.Data;

/// <summary>The outcome of one seating attempt: where it landed, and — when it was refused — the named
/// reason. A refusal is RECORDED and never gates the run (spec-lawn-commander-seat.md: "a refused seat is
/// recorded on the run, never a gate"); the run proceeds, the injector's frozen aura still applies, and
/// no duration XP is paid for a seat that never happened.</summary>
public sealed record CommanderSeatOutcome(bool Seated, string Reason, string? InstanceId, long? PlayerId)
{
    public static CommanderSeatOutcome Refused(string reason, string? instanceId = null) =>
        new(false, reason, instanceId, null);
}

public sealed partial class RpgStore
{
    /// <summary>The seat refusals, each named (spec-lawn-commander-seat.md "Who is seated, and when").</summary>
    public const string SeatNotCommander = "seat.notCommander";

    /// <inheritdoc cref="SeatNotCommander"/>
    public const string SeatNotAtBase = "seat.notAtBase";

    /// <inheritdoc cref="SeatNotCommander"/>
    public const string SeatIsPatron = "seat.isPatron";

    /// <summary>Seat the run's leading creature commander — the MatchStarted drain's own call.
    ///
    /// <para>Seating is ONE phase change plus a session row: `Roster → ActiveBound` with the run's
    /// match key, skipping `Deploying` because there is no engine spawn to wait for. That buys every
    /// property R-C1 wants from machinery that already ships — delve/expedition/lawn admission refuse a
    /// non-`Roster` specimen, `TryRecoverActiveByMatchKey` already pays duration XP and recovers it at
    /// run end, and kill credit needs a proven attacker ptr the seat does not have. The seat carries no
    /// tile and no ptr, so it can never die on the board either.</para></summary>
    public CommanderSeatOutcome SeatLeadingCommander(string leadingCommanderId, string matchKey)
    {
        if (string.IsNullOrWhiteSpace(matchKey)) return CommanderSeatOutcome.Refused(SeatNotCommander);
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();
            var outcome = SeatLeadingCommanderUnlocked(db, tx, leadingCommanderId, matchKey);
            if (outcome.Seated) tx.Commit();
            else tx.Rollback();
            return outcome;
        }
    }

    /// <inheritdoc cref="SeatLeadingCommander"/>
    internal CommanderSeatOutcome SeatLeadingCommanderUnlocked(
        SqliteConnection db, SqliteTransaction? tx, string? leadingCommanderId, string matchKey)
    {
        // The authored rows are not creatures: only `commander:unique:{instanceId}` can be seated.
        if (string.IsNullOrWhiteSpace(leadingCommanderId)
            || !leadingCommanderId.StartsWith(UniqueCommanderSource.UniquePrefix, StringComparison.Ordinal))
            return CommanderSeatOutcome.Refused(SeatNotCommander, leadingCommanderId);

        var instanceId = leadingCommanderId[UniqueCommanderSource.UniquePrefix.Length..];
        if (instanceId.Length == 0) return CommanderSeatOutcome.Refused(SeatNotCommander, leadingCommanderId);

        var actor = ReadUniqueActorUnlocked(db, instanceId, tx);
        if (actor is null) return CommanderSeatOutcome.Refused(SeatNotCommander, instanceId);

        // The role, for the SAVE the specimen belongs to (today the player id IS the save, R17) and the
        // empire the directory names — never a guessed empire.
        if (!CommanderDirectoryHub.IsConfigured
            || !CommanderDirectoryHub.Current.TryResolve(leadingCommanderId, out var commander))
            return CommanderSeatOutcome.Refused(SeatNotCommander, instanceId);

        var empire = new EmpireRef(
            new SaveId(actor.PlayerId), CommanderDirectoryHub.Current.EmpireOf(commander));
        if (!HasCommanderRoleUnlocked(db, empire, instanceId))
            return CommanderSeatOutcome.Refused(SeatNotCommander, instanceId);

        // The parent rule (EP3.9): a seat is a child admission too, so it starts from home or from a
        // STATIONED legion and refuses a marching one — the specimen is otherwise exactly the "at base"
        // the deployment tree means.
        if (!ParentOfUnlocked(db, instanceId, tx).AdmitsChild)
            return CommanderSeatOutcome.Refused(SeatNotAtBase, instanceId);

        // At base: `Roster`, or (after legion-commander) a stationed legion member. Anything already
        // out on the lawn, in a delve or on an expedition is `ActiveBound`/`Deploying`/`Recovering`.
        if (!string.Equals(actor.Phase, UniqueActorPhases.Roster, StringComparison.Ordinal))
            return CommanderSeatOutcome.Refused(SeatNotAtBase, instanceId);

        // One creature does not run two aura economies at once — the same reasoning that refuses the
        // Patron a lawn deploy.
        if (IsPatronUnlocked(db, actor.PlayerId, instanceId))
            return CommanderSeatOutcome.Refused(SeatIsPatron, instanceId);

        var now = ServerClock.UtcNow.ToString("o");
        using (var cmd = db.CreateCommand())
        {
            if (tx is not null) cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE rpg_unique_actors SET
                  phase = $phase,
                  match_key = $mk,
                  last_ptr = NULL,
                  revision = revision + 1,
                  updated_utc = $now
                WHERE instance_id = $id;
                """;
            cmd.Parameters.AddWithValue("$phase", UniqueActorPhases.ActiveBound);
            cmd.Parameters.AddWithValue("$mk", matchKey);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.Parameters.AddWithValue("$id", instanceId);
            cmd.ExecuteNonQuery();
        }

        using (var cmd = db.CreateCommand())
        {
            if (tx is not null) cmd.Transaction = tx;
            // No ptr and no correlation id: a seat has no engine spawn, so it can never be matched to a
            // board casualty or credited a kill. `bound_active_ms` starts at zero and the run-end
            // recovery writes the duration receipt exactly once (its own receipt table is idempotent).
            cmd.CommandText = """
                INSERT INTO rpg_unique_lawn_sessions(instance_id, player_id, match_key, bound_utc, correlation_id, ptr, bound_active_ms, seat)
                VALUES($id, $p, $mk, $t, NULL, NULL, 0, $seat)
                ON CONFLICT(instance_id) DO UPDATE SET
                  player_id=$p, match_key=$mk, bound_utc=$t, correlation_id=NULL, ptr=NULL,
                  bound_active_ms=0, seat=$seat;
                """;
            cmd.Parameters.AddWithValue("$id", instanceId);
            cmd.Parameters.AddWithValue("$p", actor.PlayerId);
            cmd.Parameters.AddWithValue("$mk", matchKey);
            cmd.Parameters.AddWithValue("$t", now);
            cmd.Parameters.AddWithValue("$seat", UniqueLawnSeats.Commander);
            cmd.ExecuteNonQuery();
        }

        return new CommanderSeatOutcome(true, "", instanceId, actor.PlayerId);
    }

    /// <summary>Is this specimen holding a run's COMMANDER seat right now? `TryBeginUniqueDeploy`'s
    /// canary reads it (EP3.6) and it is the same fact the roster reads: a board seat is not a commander
    /// seat, so an ordinary bound actor is never refused by this.</summary>
    public bool IsSeatedCommander(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) return false;
        lock (_gate)
        {
            using var db = OpenUnlocked();
            return IsSeatedCommanderUnlocked(db, instanceId);
        }
    }

    /// <inheritdoc cref="IsSeatedCommander"/>
    internal static bool IsSeatedCommanderUnlocked(SqliteConnection db, string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) return false;
        using var cmd = db.CreateCommand();
        cmd.CommandText =
            "SELECT 1 FROM rpg_unique_lawn_sessions WHERE instance_id=$i AND seat=$seat LIMIT 1;";
        cmd.Parameters.AddWithValue("$i", instanceId.Trim());
        cmd.Parameters.AddWithValue("$seat", UniqueLawnSeats.Commander);
        return cmd.ExecuteScalar() is not null;
    }
}
