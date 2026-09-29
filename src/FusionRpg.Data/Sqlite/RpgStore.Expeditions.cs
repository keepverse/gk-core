using System.Text.Json;
using FusionRpg.Contracts;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Expeditions;
using FusionRpg.Core.Progression;
using FusionRpg.Core.Saves;
using Microsoft.Data.Sqlite;
using FusionRpg.Core.Time;

namespace FusionRpg.Data;

public sealed record ExpeditionRow(
    long Id, long PlayerId, string CorrelationId, string State, string TierId,
    string SquadJson, ulong Seed, string DispatchedUtc, string DueUtc, string? CollectedUtc)
{
    public List<string> SquadInstanceIds =>
        JsonSerializer.Deserialize<List<string>>(SquadJson) ?? new List<string>();
}

public sealed record CreatureMaterialRow(string MaterialId, long Qty);

public static class ExpeditionStates
{
    public const string Dispatched = "Dispatched";
    public const string Collected = "Collected";
    public const string Recalled = "Recalled";
}

/// <summary>Version gate for the durable collect response stored beside a terminal expedition.</summary>
public static class ExpeditionResultContract
{
    public const int Version = 1;
}

public sealed record ExpeditionBattleResult(
    int BattleIndex, bool Boss, string Outcome, long? RunId, string MatchKey,
    IReadOnlyList<TurnOrderEntry> TurnOrder);

public sealed record ExpeditionSpecimenXp(string InstanceId, long Xp);

/// <summary>
/// The exact reveal returned by a successful collect/recall. The store commits this document in the
/// same transaction as the state transition and every reward, so a retry after a lost response reads
/// bytes from the original settlement instead of resolving or paying again.
/// </summary>
public sealed record ExpeditionCollectResult(
    string State, int ElapsedTicks,
    IReadOnlyList<ExpeditionTickOutcome> Ticks,
    IReadOnlyList<ExpeditionBattleResult> Battles,
    long SoulsAwarded,
    IReadOnlyList<MaterialDrop> Materials,
    IReadOnlyList<CreatureSpecimenDto> WildJoins,
    IReadOnlyList<ExpeditionSpecimenXp> SpecimenXp);

public sealed record ExpeditionRewardCommitOutcome(
    bool Applied, string Reason, ExpeditionCollectResult? Result,
    IReadOnlyList<string> LeveledSpecimenIds);

public sealed partial class RpgStore
{
    /// <summary>
    /// Dispatch (spec-expeditions.md): validates tier/slots/ownership and the cross-mode
    /// soft-lock, seals the squad + seed, writes expedition + membership rows in one
    /// transaction. Correlation replay returns the stored expedition untouched.
    /// </summary>
    public (bool Ok, string Reason, ExpeditionRow? Expedition) DispatchExpedition(
        long playerId, string correlationId, string tierId, IReadOnlyList<string> squadInstanceIds,
        ulong seed, DateTimeOffset? utcNow = null) =>
        DispatchExpedition(playerId, correlationId, tierId, squadInstanceIds, seed, utcNow, null);

    /// <summary>
    /// The internal seam exists only so Data.Tests can place two independent handles on the same
    /// side of the check-before-write boundary. Production callers use the overload above.
    /// </summary>
    internal (bool Ok, string Reason, ExpeditionRow? Expedition) DispatchExpedition(
        long playerId, string correlationId, string tierId, IReadOnlyList<string> squadInstanceIds,
        ulong seed, DateTimeOffset? utcNow, Action? beforeExclusiveWrite)
    {
        if (string.IsNullOrWhiteSpace(correlationId)) return (false, "correlation.missing", null);
        var corr = correlationId.Trim();
        if (!ExpeditionTierCatalog.IsKnown(tierId)) return (false, "tier.unknown", null);
        var tier = ExpeditionTierCatalog.Get(tierId);
        if (squadInstanceIds.Count == 0) return (false, "squad.empty", null);
        if (squadInstanceIds.Count > tier.SquadSlots) return (false, "squad.toolarge", null);
        if (squadInstanceIds.Distinct(StringComparer.Ordinal).Count() != squadInstanceIds.Count)
            return (false, "squad.duplicate", null);

        lock (_gate)
        {
            using var db = OpenUnlocked();
            if (GetPlayerUnlocked(db, playerId) is null) return (false, "player.unknown", null);
            if (!IsLiveSaveUnlocked(db, playerId)) return (false, "player.archived", null);

            var existing = ReadExpeditionByCorrelationUnlocked(db, playerId, corr);
            if (existing != null)
                return (true, "replay", existing);

            // save-identity SE4.24: the one ownership predicate, hoisted once for the whole squad loop.
            var owner = new EmpireRef(new SaveId(playerId), HumanEmpireOf(playerId));
            foreach (var id in squadInstanceIds)
            {
                // commander-roster EP3.10 (spec-legion-commander.md "The parent rule (R-C1)"): an
                // expedition seat is a child admission like the lawn and the seat, so it starts from
                // HOME or from a STATIONED legion and refuses a marching one — checked FIRST, so a
                // marching legion's commander is refused for that reason rather than for whatever else
                // happens to be wrong with the row.
                if (!ParentOfUnlocked(db, id).AdmitsChild)
                    return (false, "not-at-base", null);

                var actor = ReadUniqueActorUnlocked(db, id);
                if (actor is null || !OwnsSpecimenUnlocked(db, owner, actor.InstanceId) || ReadCreatureProfileUnlocked(db, id) is null)
                    return (false, "squad.unknown-specimen", null);
                if (!string.Equals(actor.Phase, UniqueActorPhases.Roster, StringComparison.Ordinal))
                    return (false, "specimen.deployed", null);
                if (HasActiveExpeditionMembershipUnlocked(db, id))
                    return (false, "specimen.on-expedition", null);
                // Contracts gate every path that fields a creature (spec-creature-contracts.md).
                var contract = ContractViewUnlocked(db, playerId, id);
                if (!contract.Bound) return (false, "specimen.unbound", null);
                if (!contract.Deployable) return (false, "specimen.insubordinate", null);
            }

            // Both independent handles have now completed every process-local admission read. The
            // partial unique index in the hot schema, not this callback or `_gate`, is the final arbiter.
            beforeExclusiveWrite?.Invoke();

            var now = utcNow ?? ServerClock.UtcNow;
            var dispatched = now.UtcDateTime.ToString("o");
            var due = now.AddMinutes(tier.DurationMinutes).UtcDateTime.ToString("o");
            long expeditionId;

            try
            {
                using var tx = db.BeginTransaction();
                using (var cmd = db.CreateCommand())
                {
                    cmd.CommandText = """
                        INSERT INTO rpg_expeditions(
                          player_id, correlation_id, state, tier_id, squad_json, seed,
                          dispatched_utc, due_utc)
                        VALUES($p,$c,$s,$tier,$squad,$seed,$dt,$due);
                        SELECT last_insert_rowid();
                        """;
                    cmd.Parameters.AddWithValue("$p", playerId);
                    cmd.Parameters.AddWithValue("$c", corr);
                    cmd.Parameters.AddWithValue("$s", ExpeditionStates.Dispatched);
                    cmd.Parameters.AddWithValue("$tier", tier.TierId);
                    cmd.Parameters.AddWithValue("$squad", JsonSerializer.Serialize(squadInstanceIds));
                    cmd.Parameters.AddWithValue("$seed", seed.ToString());
                    cmd.Parameters.AddWithValue("$dt", dispatched);
                    cmd.Parameters.AddWithValue("$due", due);
                    expeditionId = (long)(cmd.ExecuteScalar() ?? 0L);
                }

                foreach (var id in squadInstanceIds)
                {
                    using var cmd = db.CreateCommand();
                    cmd.CommandText = """
                        INSERT INTO rpg_expedition_members(expedition_id, instance_id, active)
                        VALUES($e,$i,1);
                        """;
                    cmd.Parameters.AddWithValue("$e", expeditionId);
                    cmd.Parameters.AddWithValue("$i", id);
                    cmd.ExecuteNonQuery();
                }

                tx.Commit();
            }
            catch (SqliteException ex) when (IsUniqueConstraint(ex, "rpg_expeditions.correlation_id"))
            {
                // A same-correlation request raced this handle through the pre-read. The unique
                // constraint chooses the winner; the loser reads and returns that exact row.
                var replay = ReadExpeditionByCorrelationUnlocked(db, playerId, corr);
                if (replay != null) return (true, "replay", replay);
                throw;
            }
            catch (SqliteException ex) when (IsUniqueConstraint(ex, "rpg_expedition_members.instance_id"))
            {
                return (false, "specimen.on-expedition", null);
            }

            return (true, "", ReadExpeditionByIdUnlocked(db, expeditionId));
        }
    }

    public List<ExpeditionRow> ListExpeditions(long playerId, int limit = 50)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = SelectExpedition + " WHERE player_id=$p ORDER BY id DESC LIMIT $l;";
            cmd.Parameters.AddWithValue("$p", playerId);
            cmd.Parameters.AddWithValue("$l", limit);
            using var r = cmd.ExecuteReader();
            var list = new List<ExpeditionRow>();
            while (r.Read())
                list.Add(MapExpedition(r));
            return list;
        }
    }

    public ExpeditionRow? TryGetExpedition(long expeditionId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            return ReadExpeditionByIdUnlocked(db, expeditionId);
        }
    }

    public bool HasActiveExpeditionMembership(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) return false;
        lock (_gate)
        {
            using var db = OpenUnlocked();
            return HasActiveExpeditionMembershipUnlocked(db, instanceId.Trim());
        }
    }

    /// <summary>Collect/recall terminal transition: state + collected stamp + lock release.</summary>
    public bool TryCloseExpedition(long expeditionId, string state, DateTimeOffset? utcNow = null)
    {
        if (state is not (ExpeditionStates.Collected or ExpeditionStates.Recalled))
            throw new ArgumentException($"Invalid terminal expedition state '{state}'.");
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();
            var closed = CloseExpeditionUnlocked(db, expeditionId, state, utcNow ?? ServerClock.UtcNow);
            tx.Commit();
            return closed;
        }
    }

    internal bool CloseExpeditionUnlocked(SqliteConnection db, long expeditionId, string state, DateTimeOffset utcNow)
    {
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE rpg_expeditions SET state=$s, collected_utc=$t
                WHERE id=$id AND state=$open;
                """;
            cmd.Parameters.AddWithValue("$s", state);
            cmd.Parameters.AddWithValue("$t", utcNow.UtcDateTime.ToString("o"));
            cmd.Parameters.AddWithValue("$id", expeditionId);
            cmd.Parameters.AddWithValue("$open", ExpeditionStates.Dispatched);
            if (cmd.ExecuteNonQuery() == 0)
                return false;
        }

        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "UPDATE rpg_expedition_members SET active=0 WHERE expedition_id=$e;";
            cmd.Parameters.AddWithValue("$e", expeditionId);
            cmd.ExecuteNonQuery();
        }

        return true;
    }

    /// <summary>
    /// `ForceExpeditionDue` is RETIRED (RS3 increment 5b, owner rulings B3 (a) and RS-F16): it rewrote
    /// `due_utc`, a store bypass that made the row lie about its own history. The clock seam replaces it
    /// without one — `ExpeditionService.CollectAsync` compares the seam's clock against the stored
    /// `due_utc`, so a caller that wants an expedition due moves the CLOCK (the host's `FUSIONRPG_CLOCK_OFFSET`
    /// / a scenario's `clock.set` step), and a test that wants one due dispatches it with a past `utcNow`.
    /// No store method replaces this one.
    /// </summary>
    public void AddCreatureMaterials(long playerId, IReadOnlyList<(string MaterialId, long Qty)> drops)
    {
        if (drops.Count == 0) return;
        foreach (var (materialId, qty) in drops)
        {
            if (!CreatureMaterialCatalog.IsKnown(materialId))
                throw new ArgumentException($"Unknown creature material id '{materialId}'.");
            if (qty <= 0)
                throw new ArgumentException($"Material qty must be positive ({materialId}).");
        }

        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();
            AddCreatureMaterialsUnlocked(db, playerId, drops);
            tx.Commit();
        }
    }

    internal void AddCreatureMaterialsUnlocked(
        SqliteConnection db, long playerId, IReadOnlyList<(string MaterialId, long Qty)> drops)
    {
        var now = ServerClock.UtcNowDateTime.ToString("o");
        foreach (var (materialId, qty) in drops)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO rpg_creature_materials(player_id, material_id, qty, updated_utc)
                VALUES($p,$m,$q,$t)
                ON CONFLICT(player_id, material_id)
                DO UPDATE SET qty = qty + $q, updated_utc = $t;
                """;
            cmd.Parameters.AddWithValue("$p", playerId);
            cmd.Parameters.AddWithValue("$m", materialId);
            cmd.Parameters.AddWithValue("$q", qty);
            cmd.Parameters.AddWithValue("$t", now);
            cmd.ExecuteNonQuery();
        }
    }

    public List<CreatureMaterialRow> ListCreatureMaterials(long playerId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT material_id, qty FROM rpg_creature_materials
                WHERE player_id=$p AND qty > 0 ORDER BY material_id;
                """;
            cmd.Parameters.AddWithValue("$p", playerId);
            using var r = cmd.ExecuteReader();
            var list = new List<CreatureMaterialRow>();
            while (r.Read())
                list.Add(new CreatureMaterialRow(r.GetString(0), r.GetInt64(1)));
            return list;
        }
    }

    public sealed record ExpeditionRewardApply(
        long EventSouls,
        IReadOnlyList<(string MaterialId, long Qty)> Materials,
        IReadOnlyList<(string InstanceId, long Xp)> SpecimenXp,
        IReadOnlyList<CreatureMintSpec> WildMints);

    /// <summary>
    /// Exactly-once reward application: ONE transaction gated on the Dispatched→terminal state
    /// transition. If the expedition is already closed, nothing applies and nothing is written.
    /// This compatibility overload keeps direct store callers unchanged; the expedition route uses
    /// <see cref="CommitExpeditionRewardsAndResult"/> so its reveal is durable in the same transaction.
    /// </summary>
    public (bool Applied, string Reason, List<CreatureSpecimenDto> Minted, IReadOnlyList<string> LeveledSpecimenIds) ApplyExpeditionRewards(
        long expeditionId, long playerId, string state, ExpeditionRewardApply rewards,
        DateTimeOffset? utcNow = null)
    {
        var applied = ApplyExpeditionRewardsCore(
            expeditionId, playerId, state, rewards, durableResult: null,
            utcNow, beforeCommit: null);
        return (applied.Applied, applied.Reason, applied.Minted, applied.LeveledSpecimenIds);
    }

    public ExpeditionRewardCommitOutcome CommitExpeditionRewardsAndResult(
        long expeditionId, long playerId, string state, ExpeditionRewardApply rewards,
        ExpeditionCollectResult result, DateTimeOffset? utcNow = null) =>
        CommitExpeditionRewardsAndResult(
            expeditionId, playerId, state, rewards, result, utcNow, beforeCommit: null);

    /// <summary>Independent-handle test seam; production uses the public overload above.</summary>
    internal ExpeditionRewardCommitOutcome CommitExpeditionRewardsAndResult(
        long expeditionId, long playerId, string state, ExpeditionRewardApply rewards,
        ExpeditionCollectResult result, DateTimeOffset? utcNow,
        Action? beforeCommit)
    {
        var applied = ApplyExpeditionRewardsCore(
            expeditionId, playerId, state, rewards, result,
            utcNow, beforeCommit);
        return new ExpeditionRewardCommitOutcome(
            applied.Applied, applied.Reason, applied.Result, applied.LeveledSpecimenIds);
    }

    (bool Applied, string Reason, ExpeditionCollectResult? Result,
        List<CreatureSpecimenDto> Minted, IReadOnlyList<string> LeveledSpecimenIds)
        ApplyExpeditionRewardsCore(
            long expeditionId, long playerId, string state, ExpeditionRewardApply rewards,
            ExpeditionCollectResult? durableResult, DateTimeOffset? utcNow,
            Action? beforeCommit)
    {
        if (state is not (ExpeditionStates.Collected or ExpeditionStates.Recalled))
            throw new ArgumentException($"Invalid terminal expedition state '{state}'.");
        foreach (var (materialId, qty) in rewards.Materials)
        {
            if (!CreatureMaterialCatalog.IsKnown(materialId))
                throw new ArgumentException($"Unknown creature material id '{materialId}'.");
            if (qty <= 0)
                throw new ArgumentException($"Material qty must be positive ({materialId}).");
        }
        if (durableResult is not null)
        {
            if (!string.Equals(durableResult.State, state, StringComparison.Ordinal))
                throw new ArgumentException("Durable result state must match the terminal expedition state.");
            if (durableResult.SoulsAwarded != rewards.EventSouls)
                throw new ArgumentException("Durable result SoulsAwarded must match the reward manifest.");
            if (durableResult.WildJoins.Count != 0)
                throw new ArgumentException("Wild joins are generated inside the reward transaction.");
        }

        beforeCommit?.Invoke();
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();
            var now = utcNow ?? ServerClock.UtcNow;

            if (!CloseExpeditionUnlocked(db, expeditionId, state, now))
            {
                var replay = durableResult is null
                    ? null
                    : ReadExpeditionCollectResultUnlocked(db, expeditionId);
                return replay is null
                    ? (false, "expedition.closed", (ExpeditionCollectResult?)null,
                        new List<CreatureSpecimenDto>(), (IReadOnlyList<string>)Array.Empty<string>())
                    : (false, "expedition.replay", replay, replay.WildJoins.ToList(),
                        (IReadOnlyList<string>)Array.Empty<string>());
            }

            if (rewards.EventSouls > 0)
            {
                // One policy with AwardSouls (spec-caps-reconcile.md §2.1, §11.2a) — this path used to
                // silently clamp via Math.Min while AwardSouls threw; now both throw on the same
                // dynamic headroom rather than the reward being quietly shorted.
                GuardSoulAwardOrThrow(ReadSoulBalanceUnlocked(db, playerId).Balance, rewards.EventSouls);
                AppendSoulLedgerUnlocked(db, playerId, 0,
                    rewards.EventSouls,
                    Core.Creatures.SoulEarnPolicy.Reasons.Expedition, null, null,
                    "exp:" + expeditionId, now.UtcDateTime.ToString("o"));
            }

            if (rewards.Materials.Count > 0)
                AddCreatureMaterialsUnlocked(db, playerId, rewards.Materials);

            // EP1.15 (spec-default-build.md, cache trigger T3): the specimens THIS collect itself
            // leveled -- the caller broadcasts AptitudesUpdated(unique, instanceId) for exactly these.
            var leveledSpecimenIds = new List<string>();
            foreach (var (instanceId, xp) in rewards.SpecimenXp)
            {
                if (xp > 0)
                {
                    var (xpOk, _, xpActor, xpLevelsGained) = AwardUniqueActorXpUnlocked(db, instanceId, xp, tx);
                    // A21 (spec-action-instance-and-grant.md §4): the expedition reward apply is the
                    // SECOND of AwardUniqueActorXpUnlocked's two production callers this module wires,
                    // same shape as AwardUniqueActorXp above.
                    if (xpOk && xpLevelsGained > 0 && xpActor is not null)
                    {
                        TryRollActionUnlocks(db, instanceId, xpActor.TypeId, xpLevelsGained, tx);
                        leveledSpecimenIds.Add(instanceId);
                    }

                    // Dedicated specimen progression is intentionally isolated from the empire
                    // species row. Expedition rewards level the specimen only; species progression
                    // is awarded by its own general-spawn activity projector.
                }
            }

            var minted = new List<CreatureSpecimenDto>();
            foreach (var spec in rewards.WildMints)
            {
                var stamp = now.UtcDateTime.ToString("o");
                minted.Add(MintCreatureUnlocked(db, playerId, spec, stamp, out var speciesNewlyDiscovered));
                // One discovery policy across acquisition paths (2026-08-21 review S5): a species
                // first met on an expedition pays the same bonus a summon would; the shared
                // `species:{id}` dedupe keeps it once-ever no matter which path lands first.
                if (speciesNewlyDiscovered
                    && Core.Creatures.CreatureRarityIds.TryParse(spec.Rarity, out var mintRarity))
                {
                    var reward = Core.Creatures.SoulEarnPolicy.DiscoveryDelta(mintRarity);
                    if (reward > 0)
                        AppendSoulLedgerUnlocked(db, playerId, 0, reward,
                            Core.Creatures.SoulEarnPolicy.Reasons.Discovery,
                            "species", spec.SpeciesId, "species:" + spec.SpeciesId, stamp);
                }
            }

            ExpeditionCollectResult? committedResult = null;
            if (durableResult is not null)
            {
                committedResult = durableResult with { WildJoins = minted.ToList() };
                WriteExpeditionCollectResultUnlocked(
                    db, expeditionId, ExpeditionResultContract.Version, committedResult);
            }

            tx.Commit();
            return (true, "", committedResult, minted, leveledSpecimenIds);
        }
    }

    public ExpeditionCollectResult? TryGetExpeditionCollectResult(long expeditionId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            return ReadExpeditionCollectResultUnlocked(db, expeditionId);
        }
    }

    /// <summary>`species-build` T1.4 — the direct `instance_id -> species_id` link
    /// (`rpg_creature_profiles`, set once at mint and never renamed), used instead of reconstructing the
    /// species from `rpg_unique_actors.type_id` (which stores the PvZ `GameTypeId`, not
    /// `CreatureTypeId`, and could collide across sides) — this FK has no such ambiguity.</summary>
    static string? ReadSpeciesIdForInstanceUnlocked(SqliteConnection db, string instanceId)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT species_id FROM rpg_creature_profiles WHERE instance_id=$id;";
        cmd.Parameters.AddWithValue("$id", instanceId);
        return cmd.ExecuteScalar() as string;
    }

    internal bool HasActiveExpeditionMembershipUnlocked(SqliteConnection db, string instanceId)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT 1 FROM rpg_expedition_members WHERE instance_id=$i AND active=1 LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$i", instanceId);
        return cmd.ExecuteScalar() != null;
    }

    static bool IsUniqueConstraint(SqliteException ex, string column) =>
        ex.SqliteErrorCode == 19
        && ex.Message.Contains($"UNIQUE constraint failed: {column}", StringComparison.OrdinalIgnoreCase);

    static void WriteExpeditionCollectResultUnlocked(
        SqliteConnection db, long expeditionId, int schemaVersion, ExpeditionCollectResult result)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            UPDATE rpg_expeditions
               SET result_schema_version=$version, result_json=$result
             WHERE id=$id;
            """;
        cmd.Parameters.AddWithValue("$version", schemaVersion);
        cmd.Parameters.AddWithValue("$result", JsonSerializer.Serialize(result, Json));
        cmd.Parameters.AddWithValue("$id", expeditionId);
        if (cmd.ExecuteNonQuery() != 1)
            throw new InvalidOperationException($"Expedition {expeditionId} disappeared before its result was stored.");
    }

    static ExpeditionCollectResult? ReadExpeditionCollectResultUnlocked(
        SqliteConnection db, long expeditionId)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT result_schema_version, result_json
              FROM rpg_expeditions WHERE id=$id;
            """;
        cmd.Parameters.AddWithValue("$id", expeditionId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read() || (reader.IsDBNull(0) && reader.IsDBNull(1))) return null;
        if (reader.IsDBNull(0) || reader.IsDBNull(1))
            throw new InvalidDataException($"Expedition {expeditionId} has a partial durable result.");

        var version = reader.GetInt32(0);
        if (version != ExpeditionResultContract.Version)
            throw new InvalidDataException(
                $"Expedition {expeditionId} durable result schema {version} is unsupported; expected {ExpeditionResultContract.Version}.");

        ExpeditionCollectResult? result;
        try
        {
            result = JsonSerializer.Deserialize<ExpeditionCollectResult>(reader.GetString(1), Json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Expedition {expeditionId} durable result JSON is invalid.", ex);
        }
        if (result is null)
            throw new InvalidDataException($"Expedition {expeditionId} durable result JSON is null.");
        if (result.State is not (ExpeditionStates.Collected or ExpeditionStates.Recalled)
            || result.ElapsedTicks < 0
            || result.SoulsAwarded < 0
            || result.Ticks is null || result.Ticks.Any(tick => tick is null)
            || result.Battles is null
            || result.Battles.Any(battle => battle is null || battle.TurnOrder is null)
            || result.Materials is null
            || result.Materials.Any(material => material is null || string.IsNullOrWhiteSpace(material.MaterialId))
            || result.WildJoins is null || result.WildJoins.Any(join => join is null)
            || result.SpecimenXp is null
            || result.SpecimenXp.Any(xp => xp is null || string.IsNullOrWhiteSpace(xp.InstanceId)))
            throw new InvalidDataException($"Expedition {expeditionId} durable result JSON failed its shape contract.");

        return result;
    }

    const string SelectExpedition = """
        SELECT id, player_id, correlation_id, state, tier_id, squad_json, seed,
               dispatched_utc, due_utc, collected_utc
        FROM rpg_expeditions
        """;

    ExpeditionRow? ReadExpeditionByIdUnlocked(SqliteConnection db, long id)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = SelectExpedition + " WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? MapExpedition(r) : null;
    }

    ExpeditionRow? ReadExpeditionByCorrelationUnlocked(SqliteConnection db, long playerId, string correlationId)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = SelectExpedition + " WHERE player_id=$p AND correlation_id=$c;";
        cmd.Parameters.AddWithValue("$p", playerId);
        cmd.Parameters.AddWithValue("$c", correlationId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? MapExpedition(r) : null;
    }

    static ExpeditionRow MapExpedition(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetString(4),
        r.GetString(5),
        ParseSeed(Convert.ToString(r.GetValue(6), System.Globalization.CultureInfo.InvariantCulture)),
        r.GetString(7), r.GetString(8),
        r.IsDBNull(9) ? null : r.GetString(9));
}
