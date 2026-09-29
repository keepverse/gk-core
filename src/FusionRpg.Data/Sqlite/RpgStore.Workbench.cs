using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Drops;
using FusionRpg.Core.Items.Materials;
using FusionRpg.Core.Items.Mutation;
using FusionRpg.Core.Items.Sockets;
using FusionRpg.Core.Power;
using Microsoft.Data.Sqlite;
using FusionRpg.Core.Time;

namespace FusionRpg.Data;

/// <summary>One material line a workbench operation MINTS rather than spends (upcycle's output, a
/// salvage yield). Deliberately its own type so a grant can never be handed to a spend by mistake.</summary>
public readonly record struct WorkbenchGrant(string MaterialId, long Qty);

/// <summary>One fungible-container stock movement — the insert an operation consumed.</summary>
public readonly record struct WorkbenchStockDelta(string ContainerId, int Delta);

/// <summary>
/// The persistent half of one workbench operation, already <b>decided</b> by Core. Every field is a
/// materialised result; nothing here is a formula, a tuning read or an RNG draw, which is what keeps
/// D2 clause 4 (<i>record the result, never the recipe</i>) true at the storage boundary rather than
/// only in the policy.
/// </summary>
/// <param name="Sockets">Module 16's <c>item_socket</c> SSOT write, or <c>null</c> when the verb
/// touches no socket. D2 clause 13: socket ops are appended for audit and idempotency only, so this
/// is the state and the op row is the receipt — never the other way round.</param>
/// <param name="PityCounter">Module 15's counter, or <c>null</c> when unchanged. Separate from the
/// append because a failed attempt moves the counter without moving the level.</param>
/// <param name="DurabilityCurrent">
/// species-gear-chain T24 — the attempt's craft wear, already decided by Core
/// (<c>CraftRiskPolicy.WearFor</c>/<c>AfterWear</c>), or <c>null</c> when the attempt must not touch
/// durability (potential still remains, or no derivation wired). Applied inside the same transaction
/// as the op, so wear and craft commit together.</param>
public sealed record WorkbenchMutation(
    string InstanceId,
    MutationOpKind Kind,
    MutationResult Result,
    string? StateHash,
    string? OriginValuesJson,
    string AppliedUtc,
    long CatalogRevision = 0,
    int RulesVersion = 0,
    IReadOnlyList<SocketSlot>? Sockets = null,
    int? PityCounter = null,
    long? DurabilityCurrent = null,
    /// <summary>
    /// species-gear-chain T23 — D1's destroy, decided by `RepairPolicy` before any restore. The op
    /// records the attempt and this sets the item's disposition in the SAME transaction, through the
    /// same salvage-shaped UPDATE a voluntary salvage uses: never a row deletion, never a new path.
    /// </summary>
    /// <summary>
    /// species-gear-chain T61 — the item's craft potential AFTER this op spent its verb's cost (floored at
    /// zero), or null for a verb that spends none. Written in the same transaction as the op, exactly like
    /// `DurabilityCurrent` above: a spent cost with no op is theft, an op with no cost is duplication.
    /// </summary>
    long? PotentialCurrent = null,
    bool DestroyItem = false,
    /// <summary>
    /// species-gear-chain T26 — the item's NEW rung ordinal after a promotion, and the source ordinal
    /// it was promoted FROM. Null on every verb that is not a promotion. Written in the same
    /// transaction as the op, beside the mark's first-wins rule.
    /// </summary>
    int? PromotedRarityOrdinal = null,
    int? PromotedFromOrdinal = null);

/// <summary>What one workbench operation did. <c>Replayed</c> is the idempotent retry.</summary>
public sealed record WorkbenchApplyResult(bool Ok, string Reason, string OutcomeRef, int OpSeq, bool Replayed);

/// <summary>What a salvage returned, and whether it happened at all.</summary>
public sealed record WorkbenchSalvageResult(bool Ok, string Reason, IReadOnlyList<MaterialCostLine> Granted);

/// <summary>
/// ⭐ <b>The workbench executor's atomic write.</b> item modules 14, 15 and 16 each shipped their own
/// half of one loop — module 14's pricing and <see cref="TrySpendRecipe"/>, module 15's
/// <see cref="AppendMutationOp"/>, module 16's <see cref="SetSockets"/> — and each recorded the same
/// blocker: <i>nothing calls them against a stored item</i>. This file is the missing joint at the
/// storage layer; <c>ItemWorkbench</c> in the server is the decision half that drives it.
///
/// <para><b>Why the composite lives here rather than in the caller.</b> <c>spec-salvage-craft.md</c>
/// §"The spend transaction" is one six-step, gate-serialised transaction whose step 5 is <i>"the
/// owning module's mutation or mint, in the SAME transaction"</i>, and
/// <c>spec-enhance-reroll.md</c>'s Boundaries repeat it: <i>"commit op row, material debit and head
/// rewrite in one transaction"</i>. A caller outside <c>FusionRpg.Data</c> cannot hold that
/// transaction — it would need a <c>SqliteConnection</c>, which <c>guard-dal.ps1</c> forbids, and
/// re-entering the store from inside <c>perform</c> would open a second connection against a database
/// its own write transaction already holds. So the transaction is here and the <b>decision</b> arrives
/// as data.</para>
///
/// <para>⛔ <b>No policy runs in this file.</b> Nothing here reads a tuning, resolves a cost, rolls a
/// die or decides an outcome — those are Core's (<c>MaterialRecipeCatalog</c>, <c>EnhancePolicy</c>,
/// <c>SalvagePolicy</c>, <c>SocketOperations</c>) and the executor's. This is persistence with the
/// atomicity the three specs ask for and nothing else.</para>
/// </summary>
public sealed partial class RpgStore
{
    /// <summary>
    /// Debit module 14's resolved cost and apply the owning module's write, <b>in one transaction</b>.
    /// Runs entirely inside <see cref="TrySpendRecipe"/>'s own six steps, so every property that path
    /// already proves — replay returns the recorded outcome and spends nothing, a reused correlation
    /// with different arguments is <c>correlation.mismatch</c>, a refusal writes nothing, the spend
    /// order is fixed — holds for the whole operation and not merely for its cost legs.
    ///
    /// <para>A refusal from the mutation half (an unknown instance, a correlation collision on the op
    /// ledger, the <c>mutation_seq</c> overflow) <b>throws out of <c>perform</c></b> and rolls the
    /// debit back with it. That direction is deliberate: D2 clause 11 — <i>"a spent cost with no op is
    /// theft; an op with no cost is duplication"</i>.</para>
    /// </summary>
    public WorkbenchApplyResult TrySpendAndApply(
        long playerId,
        string recipeId,
        IReadOnlyList<MaterialCostLine> lines,
        string correlationId,
        WorkbenchMutation? mutation = null,
        IReadOnlyList<WorkbenchGrant>? grants = null,
        IReadOnlyList<WorkbenchStockDelta>? stock = null,
        string? playerKey = null)
    {
        if (lines is null) throw new ArgumentNullException(nameof(lines));

        // Clause 11's record of the spend, in module 14's vocabulary — built from the SAME lines the
        // debit uses, so the op's cost_json cannot drift from what was actually taken.
        var costJson = CostJson(lines);
        var owner = playerKey ?? playerId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var nowUtc = mutation?.AppliedUtc ?? ServerClock.UtcNowDateTime.ToString("O");

        var opSeq = 0;
        var replayedOp = false;

        try
        {
            var spend = TrySpendRecipe(playerId, recipeId, lines, correlationId, perform: (db, _) =>
        {
            if (mutation is { } m)
            {
                var appended = AppendMutationOpUnlocked(
                    db, m.InstanceId, m.Kind, correlationId, DeriveOpSeed(m.InstanceId, correlationId),
                    m.Result, m.StateHash, m.OriginValuesJson, m.AppliedUtc,
                    m.CatalogRevision, m.RulesVersion, costJson);

                if (!appended.Ok)
                    throw new WorkbenchApplyRefused(appended.Reason);

                opSeq = appended.Seq;
                replayedOp = appended.Replayed;

                if (m.PityCounter is { } pity) SetInstancePityCounterUnlocked(db, m.InstanceId, pity);
                if (m.Sockets is { } sockets) SetSocketsUnlocked(db, m.InstanceId, sockets);
                // species-gear-chain T24: the craft's wear, in the SAME transaction as the op it
                // belongs to — a decrement with no op would be as unattributable as a spend with none.
                if (m.DurabilityCurrent is { } durability) SetDurabilityCurrentUnlocked(db, m.InstanceId, durability);
                // species-gear-chain T23: D1's destroy, in the same transaction as the attempt it
                // belongs to — the salvage path's own disposition write, never a row deletion.
                if (m.DestroyItem) MarkItemDestroyedUnlocked(db, owner, m.InstanceId);
                // ⭐ T61 half B: the potential spend, in the op's own transaction.
                if (m.PotentialCurrent is { } potential) SetPotentialCurrentUnlocked(db, m.InstanceId, potential);
                // species-gear-chain T26: the promotion's rung move and its first-wins mark, in the
                // same transaction as the op that caused them.
                if (m.PromotedRarityOrdinal is { } promoted)
                    PromoteInstanceUnlocked(db, m.InstanceId, promoted, m.PromotedFromOrdinal ?? promoted);
            }

            if (grants is { Count: > 0 })
                GrantMaterialsUnlocked(db, playerId, grants.Select(g => (g.MaterialId, g.Qty)).ToList(), nowUtc);

            foreach (var delta in stock ?? Array.Empty<WorkbenchStockDelta>())
                AdjustStockUnlocked(db, owner, delta.ContainerId, delta.Delta, nowUtc);

            return mutation?.InstanceId ?? recipeId;
            });

            return new WorkbenchApplyResult(
                spend.Ok, spend.Reason, spend.OutcomeRef, opSeq, replayedOp || spend.Reason == "replay");
        }
        catch (WorkbenchApplyRefused refused)
        {
            // The debit rolled back with it (the `using var tx` in TrySpendRecipe), so this is a
            // refusal the caller can render, not a fault. Nothing was spent.
            return new WorkbenchApplyResult(false, refused.Reason, "", 0, false);
        }
    }

    /// <summary>
    /// Debit module 14's resolved cost, write the socket state, and save the socket-insert's own
    /// freshly minted insert instance, <b>in one transaction</b> (species-gear-chain T22): the spend
    /// half is <see cref="TrySpendRecipe"/> unchanged, and the product half is the mutation's
    /// <c>Sockets</c> write plus <see cref="SaveInstanceUnlocked"/> on the SAME connection and
    /// transaction. The mint DECISION (build the container, freeze the rolls) stays in the executor —
    /// "no policy runs in this file" — so what arrives here is a materialised <see cref="InstanceRow"/>
    /// and its id, alongside the socket rows that already name that id. A spend with no instance is
    /// theft and an instance with no spend is duplication (D2 clause 11, both directions), exactly
    /// like the forge shape <see cref="TryForgeAndApply"/> sets beside it.
    /// </summary>
    public WorkbenchApplyResult TrySpendSocketInsertAndApply(
        long playerId,
        string recipeId,
        IReadOnlyList<MaterialCostLine> lines,
        string correlationId,
        WorkbenchMutation mutation,
        InstanceRow gemInstance,
        string gemInstanceId,
        IReadOnlyList<WorkbenchStockDelta>? stock = null,
        string? playerKey = null)
    {
        if (lines is null) throw new ArgumentNullException(nameof(lines));
        if (mutation is null) throw new ArgumentNullException(nameof(mutation));
        if (gemInstance is null) throw new ArgumentNullException(nameof(gemInstance));
        if (string.IsNullOrWhiteSpace(gemInstanceId))
            throw new ArgumentException("the socket rows already name the insert's instance id", nameof(gemInstanceId));

        var costJson = CostJson(lines);
        var owner = playerKey ?? playerId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var nowUtc = mutation.AppliedUtc;

        // Hoisted so the perform lambda below closes over a definitely non-null reference rather
        // than the parameter the guard above already rejected as null.
        var decided = mutation;

        var opSeq = 0;
        var replayedOp = false;

        try
        {
            var spend = TrySpendRecipe(playerId, recipeId, lines, correlationId, perform: (db, tx) =>
            {
                var appended = AppendMutationOpUnlocked(
                    db, decided.InstanceId, decided.Kind, correlationId, DeriveOpSeed(decided.InstanceId, correlationId),
                    decided.Result, decided.StateHash, decided.OriginValuesJson, decided.AppliedUtc,
                    decided.CatalogRevision, decided.RulesVersion, costJson);

                if (!appended.Ok)
                    throw new WorkbenchApplyRefused(appended.Reason);

                opSeq = appended.Seq;
                replayedOp = appended.Replayed;

                if (decided.Sockets is { } sockets) SetSocketsUnlocked(db, decided.InstanceId, sockets);

                // The insert's own rows, under the id the socket write above already names. Upsert
                // semantics (ON CONFLICT UPDATE), so a retried perform converges rather than
                // colliding — though in practice a retry never reaches here: the spend log
                // short-circuits it as a replay before perform runs again.
                SaveInstanceUnlocked(db, tx, gemInstance, gemInstanceId);

                foreach (var delta in stock ?? Array.Empty<WorkbenchStockDelta>())
                    AdjustStockUnlocked(db, owner, delta.ContainerId, delta.Delta, nowUtc);

                return decided.InstanceId;
            });

            return new WorkbenchApplyResult(
                spend.Ok, spend.Reason, spend.OutcomeRef, opSeq, replayedOp || spend.Reason == "replay");
        }
        catch (WorkbenchApplyRefused refused)
        {
            return new WorkbenchApplyResult(false, refused.Reason, "", 0, false);
        }
    }

    /// <summary>
    /// Debit the upgrade's cost and <b>consume the input while producing the successor instance</b>, in
    /// one transaction (species-gear-chain T37, `spec-item-upgrade-tree.md` § the consume-and-replace
    /// seam): the spend half is <see cref="TrySpendRecipe"/> unchanged, and the product half is the op
    /// row for the CONSUMED instance, its salvage-shaped disposition, and
    /// <see cref="SaveInstanceUnlocked"/> on the SAME connection and transaction — the shape
    /// <see cref="TrySpendSocketInsertAndApply"/> already sets beside this one. A spend with no item is
    /// theft and an item with no spend is duplication (D2 clause 11, both directions).
    ///
    /// <para>The op ledger's <c>instance_id</c> is the consumed item's: an upgrade reads as that item's
    /// own history, and the successor is named by the returned <c>OutcomeRef</c> instead. The consumed
    /// row is never DELETED — the disposition write T23 established keeps it readable for the ledger.</para>
    /// </summary>
    public WorkbenchApplyResult TryUpgradeAndApply(
        long playerId,
        string recipeId,
        IReadOnlyList<MaterialCostLine> lines,
        string correlationId,
        WorkbenchMutation consumedMutation,
        InstanceRow successorInstance,
        ItemGenerationRow successorGeneration,
        /// <summary>
        /// species-gear-chain T60 — the successor's durability, carrying the CONSUMED item's used fraction
        /// (the caller computes it from the two maxes). Null leaves it underived, which is the pre-fix
        /// state and is only correct for a caller that has no wear inputs.
        /// </summary>
        long? successorDurabilityCurrent = null,
        /// <summary>
        /// species-gear-chain T60 (potential half) — the successor's craft potential, carrying the CONSUMED
        /// item's used fraction of the OTHER head pair, by the same rule and for the same reason. Null
        /// leaves it underived (the successor then derives its own full pair on first read), which is
        /// correct only for a caller with no potential ceiling to scale against.
        /// </summary>
        long? successorPotentialCurrent = null,
        IReadOnlyList<WorkbenchStockDelta>? stock = null,
        string? playerKey = null)
    {
        if (lines is null) throw new ArgumentNullException(nameof(lines));
        if (consumedMutation is null) throw new ArgumentNullException(nameof(consumedMutation));
        if (successorInstance is null) throw new ArgumentNullException(nameof(successorInstance));
        if (successorGeneration is null) throw new ArgumentNullException(nameof(successorGeneration));
        if (string.IsNullOrWhiteSpace(successorGeneration.InstanceId))
            throw new ArgumentException("the successor needs its own instance id", nameof(successorGeneration));
        if (string.Equals(successorGeneration.InstanceId, consumedMutation.InstanceId, StringComparison.Ordinal))
            throw new ArgumentException(
                "the successor must be a DIFFERENT instance — an upgrade consumes the input (spec § 298)",
                nameof(successorGeneration));

        var costJson = CostJson(lines);
        var owner = playerKey ?? playerId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var nowUtc = consumedMutation.AppliedUtc;
        var decided = consumedMutation;

        var opSeq = 0;
        var replayedOp = false;

        try
        {
            var spend = TrySpendRecipe(playerId, recipeId, lines, correlationId, perform: (db, tx) =>
            {
                var appended = AppendMutationOpUnlocked(
                    db, decided.InstanceId, decided.Kind, correlationId, DeriveOpSeed(decided.InstanceId, correlationId),
                    decided.Result, decided.StateHash, decided.OriginValuesJson, decided.AppliedUtc,
                    decided.CatalogRevision, decided.RulesVersion, costJson);

                if (!appended.Ok)
                    throw new WorkbenchApplyRefused(appended.Reason);

                opSeq = appended.Seq;
                replayedOp = appended.Replayed;

                // The input leaves the world through the same salvage-shaped disposition a voluntary
                // salvage or a repair-destroy uses: never a row deletion, so the op above stays readable.
                MarkItemDestroyedUnlocked(db, owner, decided.InstanceId);

                // ⛔ And it must leave the LOADOUT too (species-gear-chain T37 criterion 6, found by
                // the Data probe this task added): an assignment that still names the consumed instance
                // keeps it in the projection's own `desired` set, so the withdraw-on-absence reaper
                // keeps its binding — a live binding on a destroyed item, which would go on composing
                // its atoms. Clearing the assignment is what lets the EXISTING reconcile withdraw the
                // binding by absence; no second withdrawal path is written here.
                // ⚠ Re-pointing the loadout at the SUCCESSOR instead is a UX decision nobody has ruled
                // on, so it is deliberately NOT invented here: today the piece leaves the loadout and
                // the successor is equipped like any other item.
                ExecIn(db, tx, """
                    DELETE FROM rpg_item_assignment WHERE ref_kind = 'rolled' AND ref_id = $id;
                    """, ("$id", decided.InstanceId));
                ExecIn(db, tx, """
                    DELETE FROM rpg_player_item_assignment WHERE ref_kind = 'rolled' AND ref_id = $id;
                    """, ("$id", decided.InstanceId));

                // The successor: its own instance id, its own container, the SAME carried atoms, plus its
                // own generation row (provenance keeps pointing at the original acquisition).
                SaveInstanceUnlocked(db, tx, successorInstance, successorGeneration.InstanceId);

                // ⭐ T60: wear crosses by USED FRACTION, in the successor's own transaction — otherwise an
                // upgrade launders a worn piece into a pristine one and strictly dominates `Repair`.
                if (successorDurabilityCurrent is { } carriedDurability)
                    SetDurabilityCurrentUnlocked(db, successorGeneration.InstanceId, carriedDurability);
                // ⭐ T60 (potential half): the same carry for the other head pair — `craft_potential_current`
                // on the SUCCESSOR's id, so an upgrade is not a potential-reset loop either (spec § Open
                // question 3). This carry IS the upgrade's potential consumption: the successor's own max
                // already absorbs the input's spend, so a second `potentialCostPerVerb[upgrade]` decrement
                // would charge the same craft twice.
                if (successorPotentialCurrent is { } carriedPotential)
                    SetPotentialCurrentUnlocked(db, successorGeneration.InstanceId, carriedPotential);
                ExecIn(db, tx, """
                    INSERT INTO item_generation
                      (instance_id, drop_log_id, base_type_id, rarity_ordinal, item_level, frame, role, affix_channel)
                    VALUES ($iid, $log, $bt, $ord, $ilvl, $frame, $role, $chan)
                    ON CONFLICT(instance_id) DO UPDATE SET
                      drop_log_id = excluded.drop_log_id, base_type_id = excluded.base_type_id,
                      rarity_ordinal = excluded.rarity_ordinal, item_level = excluded.item_level,
                      frame = excluded.frame, role = excluded.role, affix_channel = excluded.affix_channel;
                    """,
                    ("$iid", successorGeneration.InstanceId), ("$log", successorGeneration.DropLogId),
                    ("$bt", successorGeneration.BaseTypeId), ("$ord", successorGeneration.RarityOrdinal),
                    ("$ilvl", successorGeneration.ItemLevel), ("$frame", successorGeneration.Frame),
                    ("$role", successorGeneration.Role), ("$chan", successorGeneration.AffixChannel));

                foreach (var delta in stock ?? Array.Empty<WorkbenchStockDelta>())
                    AdjustStockUnlocked(db, owner, delta.ContainerId, delta.Delta, nowUtc);

                // The successor, never the consumed item: a caller reads the new item back by this id.
                return successorGeneration.InstanceId;
            });

            return new WorkbenchApplyResult(
                spend.Ok, spend.Reason, spend.OutcomeRef, opSeq, replayedOp || spend.Reason == "replay");
        }
        catch (WorkbenchApplyRefused refused)
        {
            return new WorkbenchApplyResult(false, refused.Reason, "", 0, false);
        }
    }

    /// <summary>
    /// Debit module 14's resolved cost and mint the forged instance, <b>in one transaction</b>
    /// (species-gear-chain T14): the spend half is <see cref="TrySpendRecipe"/> unchanged — replay,
    /// mismatch, fixed order, log row — and the product half is <see cref="MintGrantUnlocked"/> on
    /// the SAME connection and transaction, so a spend with no item is theft and an item with no
    /// spend is duplication (D2 clause 11, both directions). A mint refusal (unknown base type,
    /// unresolvable pool) throws <see cref="WorkbenchApplyRefused"/> out of <c>perform</c> and rolls
    /// the debit back with it, exactly like a mutation refusal.
    /// </summary>
    public WorkbenchApplyResult TryForgeAndApply(
        long playerId,
        string recipeId,
        IReadOnlyList<MaterialCostLine> lines,
        string correlationId,
        LootGrant grant,
        int thetaContent,
        IReadOnlyList<RoleFamilyCell> roleFamilyCells,
        PowerTuning tuning,
        string? playerKey = null)
    {
        if (grant is null) throw new ArgumentNullException(nameof(grant));
        if (roleFamilyCells is null) throw new ArgumentNullException(nameof(roleFamilyCells));
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));

        try
        {
            var spend = TrySpendRecipe(playerId, recipeId, lines, correlationId, perform: (db, tx) =>
            {
                var minted = MintGrantUnlocked(db, tx, grant, thetaContent, roleFamilyCells, tuning,
                    InstanceOrigin.Craft, GetCatalogRevision());
                if (!minted.Rejection.IsOk || minted.InstanceId is null)
                    throw new WorkbenchApplyRefused(
                        $"forge.mint-refused — {minted.Rejection.Reason}: {minted.Rejection.Detail}");
                return minted.InstanceId;
            });

            return new WorkbenchApplyResult(
                spend.Ok, spend.Reason, spend.OutcomeRef, 0, spend.Reason == "replay");
        }
        catch (WorkbenchApplyRefused refused)
        {
            return new WorkbenchApplyResult(false, refused.Reason, "", 0, false);
        }
    }

    /// <summary>
    /// ⭐ Module 14's converter, committed. Salvage has <b>no debit</b> — it is the credit side — so it
    /// does not run through <see cref="TrySpendRecipe"/> and does not write a spend-log row; putting a
    /// zero-cost row in a table named for spends would smear the two directions together.
    ///
    /// <para><b>Its idempotency is the item's own disposition</b>, which is stronger than a log: the
    /// grant is gated on <c>UPDATE … WHERE disposition = 'owned'</c> inside the same transaction, so a
    /// second salvage of the same item updates zero rows and returns
    /// <c>item.not-owned</c> having granted nothing — no new table, no new <c>op_kind</c> (the
    /// namespace is closed at ten and adding one is ask-first), and no correlation the caller has to
    /// remember. <c>RpgItemRow.Disposition</c>'s own contract already says <i>"deleting the underlying
    /// instance is always a disposition, never a side effect of another operation"</i>.</para>
    /// </summary>
    public WorkbenchSalvageResult TrySalvageItem(
        long playerId, string playerKey, string instanceId,
        IReadOnlyList<MaterialCostLine> yield, string appliedUtc, string eventId)
    {
        if (yield is null) throw new ArgumentNullException(nameof(yield));

        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();

            int changed;
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = """
                    UPDATE rpg_item SET disposition = 'salvaged', revision = revision + 1
                    WHERE instance_id = $id AND player_id = $p AND disposition = 'owned' AND locked = 0;
                    """;
                cmd.Parameters.AddWithValue("$id", instanceId);
                cmd.Parameters.AddWithValue("$p", playerKey);
                changed = cmd.ExecuteNonQuery();
            }

            if (changed == 0)
            {
                tx.Rollback();
                return new WorkbenchSalvageResult(false, "item.not-owned", Array.Empty<MaterialCostLine>());
            }

            // Salvage never mints currency, so a souls line would be a bug upstream rather than a
            // number to pay out. Refused here as well as in SalvagePolicy: the yield crosses a
            // process boundary between the two, and this is the last place it can be checked.
            foreach (var line in yield)
                if (line.Class == MaterialClass.Souls)
                    throw new ArgumentException(
                        "a salvage yield may not contain a souls line — salvage is a converter, not a faucet " +
                        "(spec-salvage-craft.md §Salvage)", nameof(yield));

            GrantMaterialsUnlocked(db, playerId,
                yield.Select(l => (l.MaterialId, l.Qty)).ToList(), appliedUtc);

            SaveItemEventUnlocked(db, new RpgItemEventRow(
                eventId, instanceId, playerKey, "salvaged", CostJson(yield), appliedUtc));

            tx.Commit();
            return new WorkbenchSalvageResult(true, "", yield);
        }
    }

    /// <summary>
    /// The op's recorded seed. Deterministic in <c>(instance, correlation)</c> so the same retried
    /// operation decides the same thing, and domain-separated per op kind by
    /// <c>MutationOpKinds.StreamName</c> at the point it is drawn from.
    /// </summary>
    public static long DeriveOpSeed(string instanceId, string correlationId)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(instanceId + "|" + correlationId));
        return BitConverter.ToInt64(bytes, 0);
    }

    /// <summary>Run one statement on the caller's connection. <c>db.CreateCommand()</c> inherits the
    /// connection's active transaction, which is what lets an unlocked write join the caller's.</summary>
    static void ExecOn(SqliteConnection db, string sql, params (string Name, object Value)[] args)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }
}

/// <summary>
/// Raised from inside <see cref="RpgStore.TrySpendRecipe"/>'s <c>perform</c> step when the owning
/// module's write refuses. Throwing is the ONLY way to roll a debit back from step 5, and D2 clause 11
/// requires exactly that: <i>"a spent cost with no op is theft"</i>.
/// </summary>
public sealed class WorkbenchApplyRefused : Exception
{
    public WorkbenchApplyRefused(string reason) : base(reason) => Reason = reason;

    public string Reason { get; }
}
