using FusionRpg.Core.Stats.Derived;
using Microsoft.Data.Sqlite;

namespace FusionRpg.Data;

/// <summary>
/// <c>rpg_run_pool</c> (spec-action-costs.md §9, T18): the persisted half of the resource reader —
/// what an actor's six pools held the last time a run touched rest or an encounter boundary.
///
/// <para><b>No run row means a run of one</b> — the skirmish case, and the current default for every
/// caller, since nothing dispatches a multi-battle run yet. <see cref="LoadRunPools"/> returning
/// <c>null</c> is not an error; it is the caller's cue to start the actor at
/// <c>ActorResourcePools.CreateFull</c> instead, exactly as if no persistence existed at all.</para>
///
/// <para><b><c>hp</c> is included</b> — spec §9 says so explicitly, and this table treats all six
/// <see cref="DerivedStatChannels.ResourceIds"/> uniformly; nothing here special-cases <c>hp</c> the
/// way exhaustion (T16) and death (the turn FSM's own <c>Downed</c> state) do.</para>
///
/// <para><b>Cooldowns are not here on purpose.</b> <c>CooldownLedger</c>/<c>ActionRunner</c> are
/// per-battle, in-memory-only objects with no save path anywhere in this store — "cooldowns do not
/// cross a battle boundary" (§9) is not a rule this table enforces, it is what NOT having a table
/// for them already guarantees.</para>
/// </summary>
public sealed partial class RpgStore
{
    void EnsureRunPoolSchemaUnlocked(SqliteConnection db)
    {
        Exec(db, """
            CREATE TABLE IF NOT EXISTS rpg_run_pool (
              run_id TEXT NOT NULL,
              actor_key TEXT NOT NULL,
              resource_id TEXT NOT NULL,
              stored_value INTEGER NOT NULL,
              PRIMARY KEY (run_id, actor_key, resource_id)
            );
            """);
    }

    /// <summary>Writes every one of the six resolved values for one actor in one run — always the
    /// full closed set, never a partial update, so a caller can never leave a stale fifth pool behind
    /// after only meaning to persist a sixth.</summary>
    public void SaveRunPools(string runId, string actorKey, IReadOnlyDictionary<string, long> values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorKey);
        if (values.Count != DerivedStatChannels.ResourceIds.Count)
            throw new ArgumentException(
                $"expected all {DerivedStatChannels.ResourceIds.Count} resource ids, got {values.Count}",
                nameof(values));
        foreach (var id in DerivedStatChannels.ResourceIds)
            if (!values.ContainsKey(id))
                throw new ArgumentException($"missing resource id '{id}'", nameof(values));

        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();
            foreach (var (resourceId, storedValue) in values)
            {
                using var cmd = db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO rpg_run_pool (run_id, actor_key, resource_id, stored_value)
                    VALUES ($run, $actor, $resource, $value)
                    ON CONFLICT(run_id, actor_key, resource_id) DO UPDATE SET
                      stored_value = excluded.stored_value;
                    """;
                cmd.Parameters.AddWithValue("$run", runId);
                cmd.Parameters.AddWithValue("$actor", actorKey);
                cmd.Parameters.AddWithValue("$resource", resourceId);
                cmd.Parameters.AddWithValue("$value", storedValue);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>Null when no row exists for this (run, actor) — "no run row means a run of one", the
    /// caller's cue to use <c>ActorResourcePools.CreateFull</c> instead. Never a partial dictionary:
    /// a run row is written for all six ids at once (<see cref="SaveRunPools"/>), so anything less
    /// than six here means the row was tampered with outside this store.</summary>
    public IReadOnlyDictionary<string, long>? LoadRunPools(string runId, string actorKey)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT resource_id, stored_value FROM rpg_run_pool
                WHERE run_id = $run AND actor_key = $actor;
                """;
            cmd.Parameters.AddWithValue("$run", runId);
            cmd.Parameters.AddWithValue("$actor", actorKey);
            using var r = cmd.ExecuteReader();

            var result = new Dictionary<string, long>(StringComparer.Ordinal);
            while (r.Read())
                result[r.GetString(0)] = r.GetInt64(1);
            return result.Count == 0 ? null : result;
        }
    }

    /// <summary>Rest: return to base. Deletes the persisted row so the next <see cref="LoadRunPools"/>
    /// misses and the actor's next <c>ActorResourcePools.CreateFull</c> starts every pool at max —
    /// "refill at rest" (§9), expressed as "there is nothing left to load" rather than as six writes
    /// of a max value this store would have to re-derive from a derived snapshot it does not hold.</summary>
    public void DeleteRunPools(string runId, string actorKey)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "DELETE FROM rpg_run_pool WHERE run_id = $run AND actor_key = $actor;";
            cmd.Parameters.AddWithValue("$run", runId);
            cmd.Parameters.AddWithValue("$actor", actorKey);
            cmd.ExecuteNonQuery();
        }
    }
}
