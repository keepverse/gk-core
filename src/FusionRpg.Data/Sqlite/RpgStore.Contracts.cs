using FusionRpg.Contracts;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Contracts;
using FusionRpg.Core.Saves;
using Microsoft.Data.Sqlite;
using FusionRpg.Core.Time;

namespace FusionRpg.Data;

/// <summary>One creature's contract. Rank is NOT stored — it is derived from <see cref="Loyalty"/>.</summary>
public sealed record ContractRow(
    string InstanceId,
    long PlayerId,
    bool Bound,
    int Loyalty,
    CreaturePersonality Personality,
    string? BoundUtc,
    string? ReleasedUtc,
    string GainDay,
    int GainToday,
    long Revision,
    bool Warden = false)
{
    public LoyaltyRank Rank => ContractPolicy.RankFor(Loyalty);
    public bool Deployable => Bound && ContractPolicy.IsDeployable(Loyalty);
}

/// <summary>build-preset BP2.2 (spec-piece-appliers.md): the read-only answer <c>QuoteBind</c>/
/// <c>QuoteRelease</c> give — what <c>BindContract</c>/<c>ReleaseContract</c> would refuse or charge
/// for the SAME target, without writing anything. <c>Replay</c> mirrors each write's own free
/// "nothing changed" case (already bound / already unbound).</summary>
public sealed record ContractQuote(bool Ok, string Reason, long Price, bool Replay);

/// <summary>Per-player capacity and the settlement stamp.</summary>
public sealed record ContractStateRow(
    long PlayerId, int PurchasedSlots, string LastSettledUtc, string? MigratedUtc, long Revision)
{
    public int Capacity => ContractPolicy.Capacity(PurchasedSlots);
}

public sealed partial class RpgStore
{
    /// <summary>
    /// Contracts (spec-creature-contracts.md): a binding slot plus a loyalty record. Owning is
    /// unlimited; FIELDING is finite. Unbound creatures are free and frozen — no tribute, no decay,
    /// loyalty preserved — so a benched hoard costs nothing and a returning player never finds
    /// a dead army, only a poorer contracted one.
    /// </summary>
    public bool EnsureContractsMigrated(long playerId, DateTimeOffset? utcNow = null)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            if (GetPlayerUnlocked(db, playerId) is null) return false;
            using var tx = db.BeginTransaction();
            var did = EnsureContractsMigratedUnlocked(db, playerId, (utcNow ?? ServerClock.UtcNow));
            tx.Commit();
            return did;
        }
    }

    /// <summary>
    /// One-shot auto-bind of a roster that predates contracts: best-first (rarity → star → level →
    /// oldest) until base capacity is full, free of charge. The overflow stays owned and unbound.
    /// Returns true only on the run that actually migrated.
    /// </summary>
    internal bool EnsureContractsMigratedUnlocked(SqliteConnection db, long playerId, DateTimeOffset now)
    {
        var state = ReadContractStateUnlocked(db, playerId);
        if (state?.MigratedUtc != null) return false;

        var stamp = now.UtcDateTime.ToString("o");
        UpsertContractStateUnlocked(db, playerId, state?.PurchasedSlots ?? 0, stamp, stamp);

        var capacity = ContractPolicy.Capacity(state?.PurchasedSlots ?? 0);
        var bound = CountBoundContractsUnlocked(db, playerId);
        foreach (var candidate in ReadMigrationCandidatesUnlocked(db, playerId))
        {
            if (bound >= capacity) break;
            if (ReadContractUnlocked(db, candidate.InstanceId) != null) continue;
            BindContractRowUnlocked(db, playerId, candidate.InstanceId, stamp);
            bound++;
        }

        return true;
    }

    /// <summary>
    /// Mint-time binding: a new creature takes a free slot automatically (free — this is not churn),
    /// and simply arrives unbound when capacity is full. Without this, every pull, fusion output,
    /// and wild-join would land unusable behind a button press nobody would enjoy.
    /// </summary>
    internal void AutoBindNewSpecimenUnlocked(SqliteConnection db, long playerId, string instanceId, string now)
    {
        var state = ReadContractStateUnlocked(db, playerId);
        if (state is null)
        {
            // First contact with contracts also migrates whatever the player already owns.
            EnsureContractsMigratedUnlocked(db, playerId, DateTimeOffset.Parse(
                now, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind));
            state = ReadContractStateUnlocked(db, playerId);
        }

        if (ReadContractUnlocked(db, instanceId) != null) return;
        // Capacity full: write nothing. A missing row IS the unbound state (see ContractViewUnlocked) —
        // one representation, so no reader can disagree with another about what "unbound" means.
        if (CountBoundContractsUnlocked(db, playerId) >= ContractPolicy.Capacity(state?.PurchasedSlots ?? 0))
            return;

        BindContractRowUnlocked(db, playerId, instanceId, now);
    }

    /// <summary>
    /// Settles every whole UTC day since the last stamp, clamped to <see cref="ContractPolicy.MaxSettleDays"/>.
    /// Each day either charges the full tribute (one dedupe-keyed ledger row, so replays and crashes
    /// cost nothing extra) or — when the balance cannot cover it — decays every bound creature instead.
    /// All-or-nothing per day: you either paid the tribute or you did not.
    /// </summary>
    public (int DaysSettled, long SoulsPaid, int CreaturesDecayed) SettleContracts(
        long playerId, DateTimeOffset? utcNow = null)
    {
        var now = utcNow ?? ServerClock.UtcNow;
        lock (_gate)
        {
            using var db = OpenUnlocked();
            if (GetPlayerUnlocked(db, playerId) is null) return (0, 0, 0);
            using var tx = db.BeginTransaction();
            var result = SettleContractsUnlocked(db, playerId, now);
            tx.Commit();
            return result;
        }
    }

    internal (int DaysSettled, long SoulsPaid, int CreaturesDecayed) SettleContractsUnlocked(
        SqliteConnection db, long playerId, DateTimeOffset now)
    {
        EnsureContractsMigratedUnlocked(db, playerId, now);
        var state = ReadContractStateUnlocked(db, playerId);
        if (state is null) return (0, 0, 0);

        // Invariant culture on purpose: these stamps are round-trip "o" strings, and a non-Gregorian
        // ambient culture would parse them into a different date.
        var lastSettled = DateTimeOffset.Parse(state.LastSettledUtc,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind);
        var elapsed = ContractPolicy.ElapsedDays(lastSettled, now);
        if (elapsed == 0) return (0, 0, 0);

        var stamp = now.UtcDateTime.ToString("o");
        var rarities = ReadContractRaritiesUnlocked(db, playerId);
        var loyalty = new Dictionary<string, int>(StringComparer.Ordinal);
        var bound = ListContractsUnlocked(db, playerId).Where(c => c.Bound).ToList();
        foreach (var c in bound) loyalty[c.InstanceId] = c.Loyalty;

        long paid = 0;
        var decayed = new HashSet<string>(StringComparer.Ordinal);
        var firstDay = lastSettled.UtcDateTime.Date;
        var due = bound.Sum(c => (long)ContractPolicy.UpkeepPerDay(
            rarities.TryGetValue(c.InstanceId, out var r) ? r : CreatureRarity.Chaff, c.Personality));
        for (var d = 1; d <= elapsed && bound.Count > 0; d++)
        {
            var day = firstDay.AddDays(d).ToString("yyyy-MM-dd");
            if (ReadSoulBalanceUnlocked(db, playerId).Balance >= due)
            {
                // A false append means this day is ALREADY paid for (a dedupe key survives). That is
                // a settled day, not an unpaid one — decaying here would punish a player who paid.
                if (AppendSoulLedgerUnlocked(db, playerId, 0, -due, SoulEarnPolicy.Reasons.Upkeep,
                        "contract", day, $"upkeep:{playerId}:{day}", stamp))
                    paid += due;
                continue;
            }

            // Tribute unpaid: faith erodes. Nothing is owed forward — a debt ledger would let a
            // returning player be bankrupted by days they never played.
            foreach (var c in bound)
            {
                var before = loyalty[c.InstanceId];
                var after = ContractPolicy.ApplyDecay(before, c.Personality);
                if (after == before) continue;   // already resting on the floor
                loyalty[c.InstanceId] = after;
                decayed.Add(c.InstanceId);
            }
        }

        foreach (var c in bound)
            if (loyalty[c.InstanceId] != c.Loyalty)
                WriteLoyaltyUnlocked(db, c.InstanceId, loyalty[c.InstanceId], c.GainDay, c.GainToday);

        // Stamp today's boundary, not lastSettled+elapsed: when the clamp bit, the skipped days are
        // forgiven rather than queued for the next call.
        UpsertContractStateUnlocked(db, playerId, state.PurchasedSlots,
            now.UtcDateTime.Date.ToString("o"), state.MigratedUtc);
        return (elapsed, paid, decayed.Count);
    }

    internal void WriteLoyaltyUnlocked(SqliteConnection db, string instanceId, int loyalty, string gainDay, int gainToday)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            UPDATE rpg_creature_contracts
               SET loyalty = $l, gain_day = $gd, gain_today = $gt, revision = revision + 1
             WHERE instance_id = $i;
            """;
        cmd.Parameters.AddWithValue("$i", instanceId);
        cmd.Parameters.AddWithValue("$l", loyalty);
        cmd.Parameters.AddWithValue("$gd", gainDay);
        cmd.Parameters.AddWithValue("$gt", gainToday);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Rarity per contracted specimen — upkeep is rarity-scaled, so the bill needs the profiles.</summary>
    Dictionary<string, CreatureRarity> ReadContractRaritiesUnlocked(SqliteConnection db, long playerId)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT c.instance_id, p.rarity
            FROM rpg_creature_contracts c
            JOIN rpg_creature_profiles p ON p.instance_id = c.instance_id
            WHERE c.player_id = $p;
            """;
        cmd.Parameters.AddWithValue("$p", playerId);
        var map = new Dictionary<string, CreatureRarity>(StringComparer.Ordinal);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            CreatureRarityIds.TryParse(r.GetString(1), out var rarity);
            map[r.GetString(0)] = rarity;
        }

        return map;
    }

    /// <summary>
    /// Signs a contract. Costs one day of upkeep up front — without that fee, binding before a
    /// battle and releasing after would dodge tribute entirely; with it, churn costs exactly what
    /// keeping the creature would have. The fee dedupes per creature per UTC day, so a release and
    /// re-sign on the same day is free.
    /// </summary>
    public (bool Ok, string Reason, ContractRow? Contract) BindContract(
        long playerId, string instanceId, DateTimeOffset? utcNow = null)
    {
        var id = (instanceId ?? "").Trim();
        if (id.Length == 0) return (false, "specimen.missing", null);
        var now = utcNow ?? ServerClock.UtcNow;

        lock (_gate)
        {
            using var db = OpenUnlocked();
            if (GetPlayerUnlocked(db, playerId) is null) return (false, "player.unknown", null);
            using var tx = db.BeginTransaction();
            SettleContractsUnlocked(db, playerId, now);

            var (refusal, replay, fee) = BindPreconditionsUnlocked(db, playerId, id);
            if (refusal is not null) return (false, refusal, null);
            if (replay is not null)
            {
                tx.Commit();
                return (true, "replay", replay);
            }

            var stamp = now.UtcDateTime.ToString("o");
            var day = now.UtcDateTime.ToString("yyyy-MM-dd");
            if (ReadSoulBalanceUnlocked(db, playerId).Balance < fee)
                return (false, "souls.insufficient", null);
            // A false return means this creature's day is already paid (a same-day re-sign). Not an error.
            AppendSoulLedgerUnlocked(db, playerId, 0, -fee, SoulEarnPolicy.Reasons.Upkeep,
                "contract", id, $"bind:{id}:{day}", stamp);

            BindContractRowUnlocked(db, playerId, id, stamp);
            tx.Commit();
            return (true, "", ReadContractUnlocked(db, id));
        }
    }

    /// <summary>
    /// build-preset BP2.2 (spec-piece-appliers.md): the read-only half of <see cref="BindContract"/>'s
    /// checks (specimen ownership, an already-bound replay, and capacity), plus the fee the write
    /// would then charge — shared with <see cref="QuoteBind"/> so a preview can never disagree with
    /// what the write then refuses or charges. The balance-sufficiency check stays inline in each
    /// caller (same shape as <see cref="PatronPreconditionsUnlocked"/>'s own switch-cost check) —
    /// it is one line, not worth a second shared function.
    /// </summary>
    /// <returns>A refusal reason (<c>Fee</c> 0), or a replay row (the write's own free re-sign case,
    /// <c>Fee</c> 0), or neither with the real <c>Fee</c>.</returns>
    internal (string? Refusal, ContractRow? Replay, long Fee) BindPreconditionsUnlocked(
        SqliteConnection db, long playerId, string instanceId)
    {
        var (specimenOk, rarity) = ReadContractableSpecimenUnlocked(db, playerId, instanceId);
        if (!specimenOk) return ("specimen.missing", null, 0);

        var existing = ReadContractUnlocked(db, instanceId);
        if (existing is { Bound: true }) return (null, existing, 0);

        var state = ReadContractStateUnlocked(db, playerId);
        if (CountBoundContractsUnlocked(db, playerId) >= ContractPolicy.Capacity(state?.PurchasedSlots ?? 0))
            return ("capacity.full", null, 0);

        var personality = existing?.Personality ?? ContractPolicy.PersonalityFor(instanceId);
        var fee = ContractPolicy.UpkeepPerDay(rarity, personality);
        return (null, null, fee);
    }

    /// <summary>
    /// build-preset BP2.2: what <see cref="BindContract"/> would refuse or charge for this specimen,
    /// without writing anything. <see cref="SettleContractsUnlocked"/> still runs, inside a
    /// transaction this method never commits — its own Dispose rolls it back — because the write
    /// calls it first too, and a quote against stale tribute/decay state could disagree with the
    /// real bind that follows it.
    /// </summary>
    public ContractQuote QuoteBind(long playerId, string instanceId, DateTimeOffset? utcNow = null)
    {
        var id = (instanceId ?? "").Trim();
        if (id.Length == 0) return new ContractQuote(false, "specimen.missing", 0, false);
        var now = utcNow ?? ServerClock.UtcNow;

        lock (_gate)
        {
            using var db = OpenUnlocked();
            if (GetPlayerUnlocked(db, playerId) is null) return new ContractQuote(false, "player.unknown", 0, false);
            using var tx = db.BeginTransaction();
            SettleContractsUnlocked(db, playerId, now);

            var (refusal, replay, fee) = BindPreconditionsUnlocked(db, playerId, id);
            if (refusal is not null) return new ContractQuote(false, refusal, 0, false);
            if (replay is not null) return new ContractQuote(true, "replay", 0, true);

            if (ReadSoulBalanceUnlocked(db, playerId).Balance < fee)
                return new ContractQuote(false, "souls.insufficient", 0, false);

            return new ContractQuote(true, "", fee, false);
            // tx is never committed here -- Dispose (via the outer `using`) rolls back whatever
            // SettleContractsUnlocked wrote, so this quote is genuinely read-only.
        }
    }

    /// <summary>
    /// Binds a specimen as a permanent warden (spec-loam-texture.md): the same capacity check and
    /// upkeep fee as <see cref="BindContract"/>, but the resulting bind is flagged non-releasable —
    /// <see cref="ReleaseContract"/> refuses it unconditionally, for the life of the world. Refuses
    /// an instance already bound as an ordinary (non-warden) contract, the same way binding an
    /// already-bound creature does today.
    /// </summary>
    public (bool Ok, string Reason, ContractRow? Contract) BindAsWarden(
        long playerId, string instanceId, DateTimeOffset? utcNow = null)
    {
        var id = (instanceId ?? "").Trim();
        if (id.Length == 0) return (false, "specimen.missing", null);
        var now = utcNow ?? ServerClock.UtcNow;

        lock (_gate)
        {
            using var db = OpenUnlocked();
            if (GetPlayerUnlocked(db, playerId) is null) return (false, "player.unknown", null);
            using var tx = db.BeginTransaction();
            SettleContractsUnlocked(db, playerId, now);

            var (specimenOk, rarity) = ReadContractableSpecimenUnlocked(db, playerId, id);
            if (!specimenOk) return (false, "specimen.missing", null);

            var existing = ReadContractUnlocked(db, id);
            if (existing is { Bound: true, Warden: true })
            {
                tx.Commit();
                return (true, "replay", existing);
            }
            if (existing is { Bound: true, Warden: false })
                return (false, "contract.already-bound", null);

            var state = ReadContractStateUnlocked(db, playerId);
            if (CountBoundContractsUnlocked(db, playerId) >= ContractPolicy.Capacity(state?.PurchasedSlots ?? 0))
                return (false, "capacity.full", null);

            var stamp = now.UtcDateTime.ToString("o");
            var day = now.UtcDateTime.ToString("yyyy-MM-dd");
            var personality = existing?.Personality ?? ContractPolicy.PersonalityFor(id);
            var fee = ContractPolicy.UpkeepPerDay(rarity, personality);
            if (ReadSoulBalanceUnlocked(db, playerId).Balance < fee)
                return (false, "souls.insufficient", null);
            // A false return means this creature's day is already paid (a same-day re-sign). Not an error.
            AppendSoulLedgerUnlocked(db, playerId, 0, -fee, SoulEarnPolicy.Reasons.Upkeep,
                "contract", id, $"bind:{id}:{day}", stamp);

            BindContractRowUnlocked(db, playerId, id, stamp, warden: true);
            tx.Commit();
            return (true, "", ReadContractUnlocked(db, id));
        }
    }

    /// <summary>Frees the slot. Free of charge, and the creature keeps every point of loyalty it earned —
    /// benched creatures neither pay nor decay.</summary>
    public (bool Ok, string Reason, ContractRow? Contract) ReleaseContract(
        long playerId, string instanceId, DateTimeOffset? utcNow = null)
    {
        var id = (instanceId ?? "").Trim();
        if (id.Length == 0) return (false, "specimen.missing", null);
        var now = utcNow ?? ServerClock.UtcNow;

        lock (_gate)
        {
            using var db = OpenUnlocked();
            if (GetPlayerUnlocked(db, playerId) is null) return (false, "player.unknown", null);
            using var tx = db.BeginTransaction();

            var (refusal, replay) = ReleasePreconditionsUnlocked(db, playerId, id);
            if (refusal is not null) return (false, refusal, null);
            if (replay is not null)
            {
                tx.Commit();
                return (true, "replay", replay);
            }

            ReleaseContractRowUnlocked(db, id, now.UtcDateTime.ToString("o"));
            tx.Commit();
            return (true, "", ReadContractUnlocked(db, id));
        }
    }

    /// <summary>
    /// build-preset BP2.2 (spec-piece-appliers.md): the read-only half of <see cref="ReleaseContract"/>'s
    /// checks, shared with <see cref="QuoteRelease"/>. Release costs nothing, so unlike
    /// <see cref="BindPreconditionsUnlocked"/> there is no fee to compute.
    /// </summary>
    /// <returns>A refusal reason, or a replay row (the write's own free "already unbound" case), or
    /// neither when the release would actually happen.</returns>
    internal (string? Refusal, ContractRow? Replay) ReleasePreconditionsUnlocked(
        SqliteConnection db, long playerId, string instanceId)
    {
        var row = ReadContractUnlocked(db, instanceId);
        if (row is null || row.PlayerId != playerId) return ("contract.unbound", null);
        if (!row.Bound) return (null, row);
        // Permanent, for the life of the world (spec-loam-texture.md) — unconditional, checked
        // before every other release blocker below, none of which can override it.
        if (row.Warden) return ("contract.warden-permanent", null);

        var actor = ReadUniqueActorUnlocked(db, instanceId);
        if (actor != null && !string.Equals(actor.Phase, UniqueActorPhases.Roster, StringComparison.Ordinal)
            && !string.Equals(actor.Phase, UniqueActorPhases.Retired, StringComparison.Ordinal))
            return ("contract.deployed", null);
        if (HasActiveExpeditionMembershipUnlocked(db, instanceId)) return ("contract.on-expedition", null);
        if (IsPatronUnlocked(db, playerId, instanceId)) return ("contract.is-patron", null);

        return (null, null);
    }

    /// <summary>build-preset BP2.2: what <see cref="ReleaseContract"/> would refuse for this
    /// specimen, without writing anything. Release is always free.</summary>
    public ContractQuote QuoteRelease(long playerId, string instanceId)
    {
        var id = (instanceId ?? "").Trim();
        if (id.Length == 0) return new ContractQuote(false, "specimen.missing", 0, false);

        lock (_gate)
        {
            using var db = OpenUnlocked();
            if (GetPlayerUnlocked(db, playerId) is null) return new ContractQuote(false, "player.unknown", 0, false);

            var (refusal, replay) = ReleasePreconditionsUnlocked(db, playerId, id);
            if (refusal is not null) return new ContractQuote(false, refusal, 0, false);
            if (replay is not null) return new ContractQuote(true, "replay", 0, true);

            return new ContractQuote(true, "", 0, false);
        }
    }

    /// <summary>A pact ritual: Souls for loyalty. The only way back for an insubordinate creature,
    /// since it cannot be fielded to earn its way up.</summary>
    public (bool Ok, string Reason, ContractRow? Contract) PerformRitual(
        long playerId, string instanceId, string correlationId, DateTimeOffset? utcNow = null)
    {
        var id = (instanceId ?? "").Trim();
        var corr = (correlationId ?? "").Trim();
        if (corr.Length == 0) return (false, "correlation.missing", null);
        if (id.Length == 0) return (false, "specimen.missing", null);
        var now = utcNow ?? ServerClock.UtcNow;

        lock (_gate)
        {
            using var db = OpenUnlocked();
            if (GetPlayerUnlocked(db, playerId) is null) return (false, "player.unknown", null);
            using var tx = db.BeginTransaction();
            SettleContractsUnlocked(db, playerId, now);

            var (specimenOk, rarity) = ReadContractableSpecimenUnlocked(db, playerId, id);
            if (!specimenOk) return (false, "specimen.missing", null);

            var row = ReadContractUnlocked(db, id);
            if (row is null || !row.Bound || row.PlayerId != playerId) return (false, "contract.unbound", null);

            var price = ContractPolicy.RitualPrice(
                rarity, VanillaPvzKillAndRunTheta, Core.Power.PowerTuningHub.Tuning);
            if (ReadSoulBalanceUnlocked(db, playerId).Balance < price)
                return (false, "souls.insufficient", null);

            var stamp = now.UtcDateTime.ToString("o");
            if (!AppendSoulLedgerUnlocked(db, playerId, 0, -price, SoulEarnPolicy.Reasons.ContractRitual,
                    "contract", id, corr, stamp))
            {
                // The correlation already bought a ritual: this is a retry, not a second purchase.
                tx.Commit();
                return (true, "replay", row);
            }

            var raised = Math.Min(ContractPolicy.LoyaltyMax,
                row.Loyalty + ContractPolicy.RitualGainFor(row.Personality));
            WriteLoyaltyUnlocked(db, id, raised, row.GainDay, row.GainToday);
            tx.Commit();
            return (true, "", ReadContractUnlocked(db, id));
        }
    }

    /// <summary>Buys one more binding slot at the next rung of the ladder.</summary>
    public (bool Ok, string Reason, ContractStateRow? State) BuyContractSlot(
        long playerId, string correlationId, DateTimeOffset? utcNow = null)
    {
        var corr = (correlationId ?? "").Trim();
        if (corr.Length == 0) return (false, "correlation.missing", null);
        var now = utcNow ?? ServerClock.UtcNow;

        lock (_gate)
        {
            using var db = OpenUnlocked();
            if (GetPlayerUnlocked(db, playerId) is null) return (false, "player.unknown", null);
            using var tx = db.BeginTransaction();
            SettleContractsUnlocked(db, playerId, now);

            var state = ReadContractStateUnlocked(db, playerId);
            var purchased = state?.PurchasedSlots ?? 0;
            if (!ContractPolicy.CanBuySlot(purchased)) return (false, "capacity.max", null);

            var price = ContractPolicy.NextSlotPrice(
                purchased, VanillaPvzKillAndRunTheta, Core.Power.PowerTuningHub.Tuning);
            if (ReadSoulBalanceUnlocked(db, playerId).Balance < price)
                return (false, "souls.insufficient", null);

            var stamp = now.UtcDateTime.ToString("o");
            if (!AppendSoulLedgerUnlocked(db, playerId, 0, -price, SoulEarnPolicy.Reasons.ContractSlot,
                    "contract", corr, corr, stamp))
            {
                tx.Commit();
                return (true, "replay", ReadContractStateUnlocked(db, playerId));
            }

            UpsertContractStateUnlocked(db, playerId, purchased + 1,
                state?.LastSettledUtc ?? stamp, state?.MigratedUtc);
            tx.Commit();
            return (true, "", ReadContractStateUnlocked(db, playerId));
        }
    }

    /// <summary>
    /// Credits one battle or expedition result to every contracted member of the squad. Wins are
    /// scaled by personality and metered by a rolling per-UTC-day window; losses are uncapped and
    /// are the only thing that can push a creature under the deploy floor. Unbound creatures are ignored:
    /// a benched creature neither earns nor suffers.
    /// </summary>
    public int ApplyContractResults(
        long playerId, IReadOnlyList<string> instanceIds, bool won, DateTimeOffset? utcNow = null)
    {
        if (instanceIds.Count == 0) return 0;
        var now = utcNow ?? ServerClock.UtcNow;

        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();
            var changed = ApplyContractResultsUnlocked(db, playerId, instanceIds, won, now);
            tx.Commit();
            return changed;
        }
    }

    /// <summary>
    /// The core of <see cref="ApplyContractResults"/>, reusable from a caller that already holds an
    /// open connection+transaction — party-dungeon D2.23's extraction settlement needs loyalty
    /// credited on the SAME transaction its own writes are in (spec-delve-attrition.md §9: "called
    /// once per delve at CloseDelve"), the identical reason <c>SettleContractsUnlocked</c> already
    /// exists beside the locking <c>SettleContracts</c>. Calling the PUBLIC method from inside another
    /// open transaction would open a second connection and block on the WAL writer lock until the
    /// outer transaction commits — a real deadlock, not a style preference.
    /// </summary>
    int ApplyContractResultsUnlocked(
        SqliteConnection db, long playerId, IReadOnlyList<string> instanceIds, bool won, DateTimeOffset now)
    {
        var day = now.UtcDateTime.ToString("yyyy-MM-dd");
        var changed = 0;
        foreach (var instanceId in instanceIds.Distinct(StringComparer.Ordinal))
        {
            var row = ReadContractUnlocked(db, instanceId);
            if (row is null || !row.Bound || row.PlayerId != playerId) continue;

            if (won)
            {
                var gainToday = string.Equals(row.GainDay, day, StringComparison.Ordinal) ? row.GainToday : 0;
                var (loyalty, spent) = ContractPolicy.ApplyGain(
                    row.Loyalty, gainToday, ContractPolicy.WinGain, row.Personality);
                WriteLoyaltyUnlocked(db, instanceId, loyalty, day, spent);
            }
            else
            {
                WriteLoyaltyUnlocked(db, instanceId, ContractPolicy.ApplyLoss(row.Loyalty),
                    row.GainDay, row.GainToday);
            }

            changed++;
        }

        return changed;
    }

    /// <summary>Consumption (fusion) frees the slot in the same transaction that retires the specimen —
    /// a Retired creature holding a contract would be a slot nobody could ever reclaim.</summary>
    internal void ReleaseContractOnRetireUnlocked(SqliteConnection db, string instanceId, string now)
    {
        if (ReadContractUnlocked(db, instanceId) is { Bound: true })
            ReleaseContractRowUnlocked(db, instanceId, now);
    }

    void ReleaseContractRowUnlocked(SqliteConnection db, string instanceId, string now)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            UPDATE rpg_creature_contracts
               SET bound = 0, released_utc = $now, revision = revision + 1
             WHERE instance_id = $i;
            """;
        cmd.Parameters.AddWithValue("$i", instanceId);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();
    }

    /// <summary>A specimen this player may contract: owned, a creature, and not consumed.</summary>
    (bool Ok, CreatureRarity Rarity) ReadContractableSpecimenUnlocked(
        SqliteConnection db, long playerId, string instanceId)
    {
        var actor = ReadUniqueActorUnlocked(db, instanceId);
        // save-identity SE4.24: the one ownership predicate — (player_id, empire_id), never PlayerId
        // alone (a Zomboss specimen of this save must never look contractable to the human).
        if (actor is null
            || !OwnsSpecimenUnlocked(db, new EmpireRef(new SaveId(playerId), HumanEmpireOf(playerId)), actor.InstanceId)
            || string.Equals(actor.Phase, UniqueActorPhases.Retired, StringComparison.Ordinal))
            return (false, CreatureRarity.Chaff);
        var profile = ReadCreatureProfileUnlocked(db, instanceId);
        if (profile is null) return (false, CreatureRarity.Chaff);
        CreatureRarityIds.TryParse(profile.Rarity, out var rarity);
        return (true, rarity);
    }

    /// <summary>Test seam: writes a raw soul-ledger row, to stage states settlement must survive.</summary>
    internal bool AppendSoulLedgerForTest(
        long playerId, long delta, string reason, string dedupeKey, DateTimeOffset utcNow)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();
            var ok = AppendSoulLedgerUnlocked(db, playerId, 0, delta, reason, "contract", null,
                dedupeKey, utcNow.UtcDateTime.ToString("o"));
            tx.Commit();
            return ok;
        }
    }

    /// <summary>Test seam: puts a creature's loyalty where a long play history would have.</summary>
    internal void SetLoyaltyForTest(string instanceId, int loyalty)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            var row = ReadContractUnlocked(db, instanceId)
                ?? throw new InvalidOperationException(
                    $"no contract for '{instanceId}' — an unbound creature has no loyalty to set");
            WriteLoyaltyUnlocked(db, instanceId, loyalty, row.GainDay, row.GainToday);
        }
    }

    public ContractRow? GetContract(string instanceId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            return ReadContractUnlocked(db, (instanceId ?? "").Trim());
        }
    }

    public List<ContractRow> ListContracts(long playerId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            return ListContractsUnlocked(db, playerId);
        }
    }

    public int CountBoundContracts(long playerId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            return CountBoundContractsUnlocked(db, playerId);
        }
    }

    public ContractStateRow? GetContractState(long playerId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            return ReadContractStateUnlocked(db, playerId);
        }
    }

    /// <summary>Test seam: rewinds a player to a pre-contracts database.</summary>
    internal void ClearContractsForTest(long playerId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                DELETE FROM rpg_creature_contracts WHERE player_id=$p;
                DELETE FROM rpg_contract_state WHERE player_id=$p;
                """;
            cmd.Parameters.AddWithValue("$p", playerId);
            cmd.ExecuteNonQuery();
        }
    }

    // ---- unlocked helpers -------------------------------------------------

    internal ContractRow? ReadContractUnlocked(SqliteConnection db, string instanceId)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT instance_id, player_id, bound, loyalty, personality, bound_utc, released_utc,
                   gain_day, gain_today, revision, warden
            FROM rpg_creature_contracts WHERE instance_id=$i;
            """;
        cmd.Parameters.AddWithValue("$i", instanceId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadContractFrom(r) : null;
    }

    internal List<ContractRow> ListContractsUnlocked(SqliteConnection db, long playerId)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT instance_id, player_id, bound, loyalty, personality, bound_utc, released_utc,
                   gain_day, gain_today, revision, warden
            FROM rpg_creature_contracts WHERE player_id=$p;
            """;
        cmd.Parameters.AddWithValue("$p", playerId);
        using var r = cmd.ExecuteReader();
        var rows = new List<ContractRow>();
        while (r.Read()) rows.Add(ReadContractFrom(r));
        return rows;
    }

    static ContractRow ReadContractFrom(SqliteDataReader r)
    {
        CreaturePersonalityIds.TryParse(r.GetString(4), out var personality);
        return new ContractRow(
            r.GetString(0), r.GetInt64(1), r.GetInt64(2) != 0, (int)r.GetInt64(3), personality,
            r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6),
            r.GetString(7), (int)r.GetInt64(8), r.GetInt64(9), r.GetInt64(10) != 0);
    }

    internal int CountBoundContractsUnlocked(SqliteConnection db, long playerId)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM rpg_creature_contracts WHERE player_id=$p AND bound=1;";
        cmd.Parameters.AddWithValue("$p", playerId);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    internal ContractStateRow? ReadContractStateUnlocked(SqliteConnection db, long playerId)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT player_id, purchased_slots, last_settled_utc, migrated_utc, revision
            FROM rpg_contract_state WHERE player_id=$p;
            """;
        cmd.Parameters.AddWithValue("$p", playerId);
        using var r = cmd.ExecuteReader();
        return r.Read()
            ? new ContractStateRow(r.GetInt64(0), (int)r.GetInt64(1), r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3), r.GetInt64(4))
            : null;
    }

    void UpsertContractStateUnlocked(
        SqliteConnection db, long playerId, int purchasedSlots, string lastSettledUtc, string? migratedUtc)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO rpg_contract_state(player_id, purchased_slots, last_settled_utc, migrated_utc, revision)
            VALUES($p, $slots, $settled, $migrated, 1)
            ON CONFLICT(player_id) DO UPDATE SET
              purchased_slots = $slots,
              last_settled_utc = $settled,
              migrated_utc = COALESCE(rpg_contract_state.migrated_utc, $migrated),
              revision = revision + 1;
            """;
        cmd.Parameters.AddWithValue("$p", playerId);
        cmd.Parameters.AddWithValue("$slots", purchasedSlots);
        cmd.Parameters.AddWithValue("$settled", lastSettledUtc);
        cmd.Parameters.AddWithValue("$migrated", (object?)migratedUtc ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Binds (or re-binds) a specimen. Loyalty never drops on binding — a creature released
    /// and re-signed keeps what it earned. <paramref name="warden"/> flags a permanent bind
    /// (spec-loam-texture.md); once set it is never cleared by an ordinary re-sign.</summary>
    internal void BindContractRowUnlocked(
        SqliteConnection db, long playerId, string instanceId, string now, bool warden = false)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO rpg_creature_contracts(
              instance_id, player_id, bound, loyalty, personality, bound_utc, released_utc,
              gain_day, gain_today, revision, warden)
            VALUES($i, $p, 1, $loyalty, $pers, $now, NULL, '', 0, 1, $warden)
            ON CONFLICT(instance_id) DO UPDATE SET
              bound = 1,
              loyalty = MAX(rpg_creature_contracts.loyalty, $loyalty),
              bound_utc = $now,
              released_utc = NULL,
              warden = warden OR $warden,
              revision = revision + 1;
            """;
        cmd.Parameters.AddWithValue("$i", instanceId);
        cmd.Parameters.AddWithValue("$p", playerId);
        cmd.Parameters.AddWithValue("$loyalty", ContractPolicy.BindLoyalty);
        cmd.Parameters.AddWithValue("$pers", ContractPolicy.PersonalityFor(instanceId).ToId());
        cmd.Parameters.AddWithValue("$now", now);
        cmd.Parameters.AddWithValue("$warden", warden ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    /// <summary>What every reader should use: the stored contract, or the default unbound view for a
    /// creature that has never signed one. Personality is derivable, so an absent row loses nothing.</summary>
    internal ContractRow ContractViewUnlocked(SqliteConnection db, long playerId, string instanceId) =>
        ReadContractUnlocked(db, instanceId)
        ?? new ContractRow(instanceId, playerId, false, ContractPolicy.BindLoyalty,
            ContractPolicy.PersonalityFor(instanceId), null, null, "", 0, 0, false);

    sealed record MigrationCandidate(string InstanceId, CreatureRarity Rarity, int Star, int Level, string CreatedUtc);

    /// <summary>Best-first ordering happens in C#, not SQL: rarity is a text column, so an
    /// ORDER BY would sort alphabetically ("epic" before "rare") and quietly bind the wrong creatures.</summary>
    List<MigrationCandidate> ReadMigrationCandidatesUnlocked(SqliteConnection db, long playerId)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT p.instance_id, p.rarity, p.star, a.level, a.created_utc
            FROM rpg_creature_profiles p
            JOIN rpg_unique_actors a ON a.instance_id = p.instance_id
            WHERE a.player_id = $p AND a.phase <> $retired;
            """;
        cmd.Parameters.AddWithValue("$p", playerId);
        cmd.Parameters.AddWithValue("$retired", UniqueActorPhases.Retired);
        var rows = new List<MigrationCandidate>();
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                CreatureRarityIds.TryParse(r.GetString(1), out var rarity);
                rows.Add(new MigrationCandidate(
                    r.GetString(0), rarity, (int)r.GetInt64(2), (int)r.GetInt64(3), r.GetString(4)));
            }
        }

        rows.Sort((a, b) =>
        {
            var c = b.Rarity.CompareTo(a.Rarity);
            if (c != 0) return c;
            c = b.Star.CompareTo(a.Star);
            if (c != 0) return c;
            c = b.Level.CompareTo(a.Level);
            if (c != 0) return c;
            c = string.CompareOrdinal(a.CreatedUtc, b.CreatedUtc);
            // Ordinal id as the final tiebreak: two specimens minted in the same tick must still
            // migrate in one fixed order, or "deterministic migration" is only true most of the time.
            return c != 0 ? c : string.CompareOrdinal(a.InstanceId, b.InstanceId);
        });
        return rows;
    }
}
