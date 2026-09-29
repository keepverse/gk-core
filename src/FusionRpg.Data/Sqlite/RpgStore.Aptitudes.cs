using FusionRpg.Contracts;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Generation;
using FusionRpg.Core.Progression;
using FusionRpg.Core.Saves;
using FusionRpg.Core.Stats.Aptitudes;
using Microsoft.Data.Sqlite;

namespace FusionRpg.Data;

/// <summary>
/// class-system-todo.md P6.2 — <c>AllocationStore</c> (spec-point-economy.md, read in full this
/// session). Persists P6.1's <see cref="AptitudeAllocation"/>, one <c>(scope, scopeKey)</c> at a time
/// — INPUTS only, never a resolved channel value (spec-point-economy.md §6: "Save inputs... not
/// computed totals," `stat-system.md`'s own invariant, applied here — a stored channel value would be
/// a second SSOT that goes stale the moment a coefficient moves).
///
/// <para><b>Joins the existing <see cref="RpgStore"/> partial-class convention</b>
/// (<c>RpgStore.ChannelPolicy.cs</c> is the template this file follows) rather than the standalone
/// class spec-point-economy.md §5 literally names. That file listing predates this session's own
/// survey of `FusionRpg.Data`'s real conventions: every other feature — souls, unique actors, creatures,
/// contracts — is a partial-class slice sharing ONE connection/lock (<c>_gate</c>), ONE
/// <c>EnsureHotSchema</c> dispatch, and ONE <c>Reset()</c>. A standalone class with its own connection
/// would fork that pipeline and silently drop out of <c>Reset()</c> — corrected in the spec in place,
/// not a silent rewrite, matching this session's own established "code beats docs" precedent.</para>
///
/// <para><b>Scope key shape</b> mirrors the existing <c>effect_binding</c> precedent
/// (<c>owner_kind</c>+<c>owner_key</c>, `gk-core/src/FusionRpg.Core/Effects/Atoms/OwnerScope.cs`): a
/// <c>scope</c> TEXT column plus a <c>scope_key</c> TEXT column, since the four
/// <see cref="AllocationScope"/> values key on four different things (spec-point-economy.md §2) — a
/// bare <c>player_id</c> or <c>instance_id</c> column alone fits none of the four uniformly. Commander
/// stringifies its `long` player id; the others carry their own natural string key
/// (<c>typeId</c> / <c>typeId:element</c> / <c>instanceId</c>) — this store does not interpret which,
/// only persists what the caller passes.</para>
/// </summary>
public sealed partial class RpgStore
{
    void EnsureAptitudeAllocationSchemaUnlocked(SqliteConnection db)
    {
        Exec(db, """
            CREATE TABLE IF NOT EXISTS rpg_aptitude_allocation (
              scope        TEXT    NOT NULL,
              scope_key    TEXT    NOT NULL,
              aptitude_id  TEXT    NOT NULL,
              points       INTEGER NOT NULL,
              PRIMARY KEY (scope, scope_key, aptitude_id)
            );
            """);
    }

    /// <summary>class-system-todo.md P6.2's own "unknown scope rejects" (§7 test 7) — the
    /// TEXT&lt;-&gt;<see cref="AllocationScope"/> boundary every row crosses in both directions. Throws
    /// naming the bad value rather than defaulting; tunables-ssot.md §7.2's "no built-in default"
    /// discipline, applied to a persistence key rather than a tuning value.
    ///
    /// <para>species-progression SP3.8: the vocabulary itself moved to Core
    /// (<see cref="AllocationScopeText"/>) so a Core-only consumer reads the same mapping — this
    /// method's own signature and every existing caller are unchanged, it is now a thin delegate.</para>
    /// </summary>
    public static string ScopeToText(AllocationScope scope) => AllocationScopeText.ToText(scope);

    public static AllocationScope ScopeFromText(string text) => AllocationScopeText.FromText(text);

    /// <summary>Persists ONLY this <c>(scope, scopeKey)</c>'s own points — one row per aptitude with a
    /// nonzero spend. A full upsert-and-prune (deletes this key's prior rows first, inside the same
    /// transaction), not an additive merge: the store holds the CURRENT allocation, not a change log,
    /// so a respec that zeroes an aptitude must actually remove its row, not leave a stale one behind.</summary>
    public void SaveAllocation(AllocationScope scope, string scopeKey, AptitudeAllocation allocation)
    {
        if (string.IsNullOrWhiteSpace(scopeKey))
            throw new ArgumentException("scopeKey must not be empty", nameof(scopeKey));
        if (allocation is null) throw new ArgumentNullException(nameof(allocation));

        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();
            SaveAllocationUnlocked(db, tx, scope, scopeKey, allocation);
            tx.Commit();
        }
    }

    /// <summary>species-build-todo.md T4.2 — extracted so <c>TryRespecSpecies</c>
    /// (<c>RpgStore.SpeciesRespec.cs</c>) can write the override in the SAME transaction as its soul
    /// spend and counter update; a public <see cref="SaveAllocation"/> call would open its own
    /// connection/transaction and break the "neither applied on failure" atomicity that module needs.
    /// Same delete-then-insert-nonzero-only shape as before this extraction — no behavior change.</summary>
    void SaveAllocationUnlocked(SqliteConnection db, SqliteTransaction tx, AllocationScope scope, string scopeKey, AptitudeAllocation allocation)
    {
        var scopeText = ScopeToText(scope);

        ExecIn(db, tx, "DELETE FROM rpg_aptitude_allocation WHERE scope = $scope AND scope_key = $key;",
            ("$scope", scopeText), ("$key", scopeKey));

        foreach (var apt in AptitudeCatalog.All)
        {
            var points = allocation.PointsAt(scope, apt.Id);
            if (points == 0) continue; // no row for an unspent aptitude -- nothing to persist.

            ExecIn(db, tx, """
                INSERT INTO rpg_aptitude_allocation (scope, scope_key, aptitude_id, points)
                VALUES ($scope, $key, $apt, $points);
                """,
                ("$scope", scopeText), ("$key", scopeKey), ("$apt", apt.Id), ("$points", points));
        }
    }

    /// <summary>Reconstructs an <see cref="AptitudeAllocation"/> from ONLY this
    /// <c>(scope, scopeKey)</c>'s own persisted rows — empty (not null, not a thrown error) if nothing
    /// was ever saved for it, matching <see cref="AptitudeAllocation"/>'s own "empty means all-zero
    /// shares, never invent a default" contract.</summary>
    public AptitudeAllocation LoadAllocation(AllocationScope scope, string scopeKey)
    {
        if (string.IsNullOrWhiteSpace(scopeKey))
            throw new ArgumentException("scopeKey must not be empty", nameof(scopeKey));

        lock (_gate)
        {
            using var db = OpenUnlocked();
            return LoadAllocationUnlocked(db, scope, scopeKey);
        }
    }

    /// <summary>species-build-todo.md T4.2 — extracted for the same reason as
    /// <see cref="SaveAllocationUnlocked"/>: <c>TryRespecSpecies</c> needs to read the CURRENT override
    /// inside its own transaction (to decide free-vs-priced) without opening a second connection.</summary>
    AptitudeAllocation LoadAllocationUnlocked(SqliteConnection db, AllocationScope scope, string scopeKey)
    {
        var scopeText = ScopeToText(scope);

        using var cmd = db.CreateCommand();
        cmd.CommandText =
            "SELECT aptitude_id, points FROM rpg_aptitude_allocation " +
            "WHERE scope = $scope AND scope_key = $key;";
        cmd.Parameters.AddWithValue("$scope", scopeText);
        cmd.Parameters.AddWithValue("$key", scopeKey);
        using var r = cmd.ExecuteReader();

        var allocation = AptitudeAllocation.Empty;
        while (r.Read())
            allocation += AptitudeAllocation.Single(scope, r.GetString(0), r.GetInt64(1));
        return allocation;
    }

    /// <summary>
    /// `creature-type-allocation` (module 5) — THE named entry point for a species' effective allocation
    /// (spec-creature-type-allocation.md: "composition lives behind a single named entry point... and
    /// `LoadAllocation` is not called directly by any consumer of species allocation" —
    /// `SpeciesAllocationSeamTests` guards this). Override REPLACES the baseline wholesale, never
    /// layers (spec's own "Override semantics"): a nonzero CreatureType override wins outright; otherwise
    /// the baseline is computed fresh from the committed plan and the species' CURRENT level — never
    /// persisted (audit finding A9). A species the player has never overridden reads its baseline, not
    /// zero — the exact silent-zero risk this module's own design section calls out by name.
    ///
    /// <para>An override whose OWN total happens to be zero is indistinguishable from "no override" —
    /// not a new gap: `SaveAllocation`'s delete-then-insert-nonzero-only shape already gives Commander
    /// this same property (an all-zero save leaves no rows, identical to never having allocated), so
    /// CreatureType inherits it rather than inventing a new "explicitly zero" state nothing else in this
    /// codebase tracks.</para>
    ///
    /// <para><b>Best-effort on the committed plan</b> (found running the real `battle-allocation`
    /// call site for real, `AuraDerivedEndpointsTests`): callers reached from `SpeciesAllocationSource`
    /// — battle setup, the derived-stat inspection endpoint — resolve species for EVERY actor, some of
    /// which have no reason to expect `SpeciesBuildPlanCatalog` configured (test fixtures that predate
    /// this module, matching the exact shape `RpgXpAwardMap.WithSpeciesPlacement` already treats as
    /// optional enrichment for the same reason). An un-configured plan catalog here returns
    /// <see cref="AptitudeAllocation.Empty"/> for the baseline half — an existing override still wins —
    /// rather than throwing and taking the WHOLE resolve down with it. The real server always
    /// configures this at startup (`Program.cs`), so production never reaches this fallback.</para>
    /// </summary>
    /// <param name="empire">
    /// solid-remediation T4.1 (S1/S3): which empire's species progression to resolve. Defaults to the
    /// player's own so every existing caller keeps its exact behaviour and every persisted row keeps
    /// resolving — `SpeciesAllocation.ScopeKey` maps Dave onto the unchanged key shape on purpose.
    /// A lawn zombie passes <c>EmpireId.Zomboss</c> and stops reading the human player's row.
    /// </param>
    /// <summary>Overload for the human empire: <see cref="Core.Commanders.EmpireId.Dave"/>. A record
    /// struct's well-known value is not a compile-time constant, so it cannot be a default parameter.</summary>
    public AptitudeAllocation EffectiveSpeciesAllocation(
        long playerId, string speciesId, AptitudeTuning tuning) =>
        EffectiveSpeciesAllocation(playerId, speciesId, tuning, Core.Commanders.EmpireId.Dave);

    public AptitudeAllocation EffectiveSpeciesAllocation(
        long playerId, string speciesId, AptitudeTuning tuning,
        Core.Commanders.EmpireId empire)
    {
        if (string.IsNullOrWhiteSpace(speciesId))
            throw new ArgumentException("speciesId must not be empty", nameof(speciesId));

        var overrideAllocation = LoadAllocation(AllocationScope.CreatureType,
            Core.Stats.Aptitudes.SpeciesAllocation.ScopeKey(playerId, empire, speciesId));
        if (overrideAllocation.TotalForScope(AllocationScope.CreatureType) > 0)
            return overrideAllocation;

        return SpeciesBaselineAllocation(playerId, speciesId, tuning, empire);
    }

    /// <summary>
    /// solid-remediation T4.4 (S2/S7) — the same resolve as <see cref="EffectiveSpeciesAllocation"/>,
    /// against a connection and transaction the caller already holds.
    ///
    /// <para><b>Why a second entry point rather than a flag.</b> The public method takes
    /// <c>lock (_gate)</c> and opens its own connection. The world-turn commit already holds both, and a
    /// second connection against an open write transaction is exactly the shape that deadlocks under
    /// SQLite. Every other in-transaction read here answers that with an <c>...Unlocked(db, tx, …)</c>
    /// sibling (<see cref="LoadAllocationUnlocked"/> is the established one), so this follows that
    /// precedent instead of inventing a re-entrancy mode for it.</para>
    ///
    /// <para>The resolution rule is identical and deliberately not restated: override first, baseline
    /// second, and an empire that owns no rows gets <see cref="AptitudeAllocation.Empty"/>.</para>
    /// </summary>
    internal AptitudeAllocation EffectiveSpeciesAllocationUnlocked(
        SqliteConnection db, long playerId, string speciesId, AptitudeTuning tuning) =>
        EffectiveSpeciesAllocationUnlocked(db, playerId, speciesId, tuning, Core.Commanders.EmpireId.Dave);

    internal AptitudeAllocation EffectiveSpeciesAllocationUnlocked(
        SqliteConnection db, long playerId, string speciesId, AptitudeTuning tuning,
        Core.Commanders.EmpireId empire)
    {
        if (string.IsNullOrWhiteSpace(speciesId))
            throw new ArgumentException("speciesId must not be empty", nameof(speciesId));

        var overrideAllocation = LoadAllocationUnlocked(db, AllocationScope.CreatureType,
            Core.Stats.Aptitudes.SpeciesAllocation.ScopeKey(playerId, empire, speciesId));
        if (overrideAllocation.TotalForScope(AllocationScope.CreatureType) > 0)
            return overrideAllocation;

        if (!SpeciesBuildPlanCatalog.IsConfigured)
            return AptitudeAllocation.Empty;

        // ai-empire-species EP4.15 (R1): the Empty guard is gone. The level row is keyed
        // (save, empire, kind, type) in the re-keyed rpg_actor_progression, so an AI empire's own
        // species level now answers - and a never-credited one answers level 1, whose budget is 0, so
        // it resolves Empty by the SAME arithmetic the human baseline uses, not by a special branch.
        var creatureTypeId = CreatureSpeciesCatalog.Get(speciesId).CreatureTypeId;
        var level = SpeciesLevelOfUnlocked(db, new SaveId(playerId), empire, creatureTypeId);
        var shares = SpeciesBuildPlanCatalog.SharesFor(speciesId);
        return Core.Stats.Aptitudes.SpeciesAllocation.Baseline(shares, level, tuning);
    }

    /// <summary>
    /// species-build-todo.md T5.1 — the shipped plan's own baseline, ALWAYS, regardless of whether an
    /// override exists (unlike <see cref="EffectiveSpeciesAllocation"/>, which returns whichever of the
    /// two currently applies). `spec-allocation-surface.md`'s own design needs BOTH numbers at once —
    /// "the shipped baseline... the player's override, if any, shown as a deviation FROM the
    /// baseline" — so the two are exposed as separate values here rather than only ever the winner.
    /// Same best-effort-on-an-unconfigured-plan-catalog contract as `EffectiveSpeciesAllocation`.
    /// </summary>
    public AptitudeAllocation SpeciesBaselineAllocation(
        long playerId, string speciesId, AptitudeTuning tuning) =>
        SpeciesBaselineAllocation(playerId, speciesId, tuning, Core.Commanders.EmpireId.Dave);

    public AptitudeAllocation SpeciesBaselineAllocation(
        long playerId, string speciesId, AptitudeTuning tuning,
        Core.Commanders.EmpireId empire)
    {
        if (string.IsNullOrWhiteSpace(speciesId))
            throw new ArgumentException("speciesId must not be empty", nameof(speciesId));
        if (!SpeciesBuildPlanCatalog.IsConfigured)
            return AptitudeAllocation.Empty;

        // ai-empire-species EP4.15 (R1): this guard used to answer Empty for every non-Dave empire
        // because "there is no Zomboss-empire species level anywhere, because nothing writes one"
        // (solid-remediation T4.4). EP4.14 now writes one - the zombie species XP paths credit
        // (save, EmpireId.Zomboss) - so the honest answer is the empire's OWN level through the one
        // reader, exactly as the unlocked path above. The override half stays human-only for the
        // reason it always was: only a player empire has a respec surface to write an override with.
        var creatureTypeId = CreatureSpeciesCatalog.Get(speciesId).CreatureTypeId;
        var level = SpeciesLevelOf(new SaveId(playerId), empire, creatureTypeId);
        var shares = SpeciesBuildPlanCatalog.SharesFor(speciesId);
        return Core.Stats.Aptitudes.SpeciesAllocation.Baseline(shares, level, tuning);
    }

    /// <summary>Whether the player has ever set a CreatureType override for this species — the exact
    /// signal <c>spec-allocation-surface.md</c>'s "shown as a deviation" UI needs to decide whether to
    /// render the override state at all, distinct from the override happening to equal the baseline.</summary>
    public bool HasSpeciesOverride(long playerId, string speciesId) =>
        HasSpeciesOverride(playerId, speciesId, Core.Commanders.EmpireId.Dave);

    public bool HasSpeciesOverride(
        long playerId, string speciesId,
        Core.Commanders.EmpireId empire) =>
        LoadAllocation(AllocationScope.CreatureType, Core.Stats.Aptitudes.SpeciesAllocation.ScopeKey(playerId, empire, speciesId))
            .TotalForScope(AllocationScope.CreatureType) > 0;

    // ---- ai-empire-species EP4.17 (R23) — the one empire-keyed commander pool ---------------------

    /// <summary>
    /// `ai-empire-species` EP4.17 (R23) — **the one empire-keyed commander-pool read.** Every empire's
    /// `Commander` allocation resolves through here; nothing else encodes a per-empire commander key or
    /// a second default.
    ///
    /// <para><b>Resolution.</b> An explicit allocation under the empire's own commander scope key wins
    /// WHOLESALE (map D2). Otherwise the answer depends on who runs the empire — and that is read from
    /// DATA (<c>rpg_save_empires.controller</c>), never a <c>switch</c> over <see cref="Core.Commanders.EmpireId"/>:
    /// a <b>human</b> empire's own sheet has no silent default (map D1 — the player is the one who can
    /// click), so it resolves Empty; an <b>AI</b> empire has nobody to click, so it gets
    /// <see cref="AssignLadder.Suggest"/>'s commander-context default at that empire's own budget
    /// (<see cref="PointBudget.PointsFor"/> over the Theta the caller composed for it — EP4.16's
    /// <c>ActorIndexFor</c>).</para>
    ///
    /// <para><b>Computed at read, never persisted</b> (map D1): the ladder's rows go through the SAME
    /// <see cref="AptitudePresetMaterialize.Materialize"/> largest-remainder permille-to-points math
    /// preset activation already uses — no third rounding function. A zero budget therefore resolves
    /// Empty by construction.</para>
    ///
    /// <para><b>The commander context an AI actually has:</b> no active preset (an AI has no preset
    /// surface), no species favour, no posture — so the walk records those skips and lands on
    /// <c>even</c> today. An authored <c>active-preset</c> binding for its pool would win later with no
    /// code change here.</para>
    ///
    /// <para><b>Never a guessed empire:</b> the save must actually carry <paramref name="owner"/>'s
    /// empire in <c>rpg_save_empires</c>, the same rule the species credit and the Zomboss commander
    /// clock already apply. An unseeded save reads Empty, never a defaulted pool.</para>
    /// </summary>
    public EffectiveAllocation CommanderPoolOf(EmpireRef owner, long theta, AptitudeTuning tuning)
    {
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));
        lock (_gate)
        {
            using var db = OpenUnlocked();
            return CommanderPoolOfUnlocked(db, owner, theta, tuning);
        }
    }

    /// <summary>The in-transaction sibling of <see cref="CommanderPoolOf"/>, for a caller that already
    /// holds a connection and a transaction (the world-turn/siege seam) — the same
    /// <c>...Unlocked(db, …)</c> shape every other read in this store gives that caller.
    ///
    /// <para><b><paramref name="tuning"/> may be null, and that is the best-effort contract</b> (EP4.18):
    /// an unconfigured tuning hub cannot price a computed default, so a non-human empire resolves Empty
    /// while an EXPLICIT allocation still wins — a pool row needs no tuning to read, and returning Empty
    /// for one that exists would be a wider lie than the missing default.</para>
    /// </summary>
    internal EffectiveAllocation CommanderPoolOfUnlocked(
        SqliteConnection db, EmpireRef owner, long theta, AptitudeTuning? tuning)
    {
        if (theta < 0)
            throw new ArgumentOutOfRangeException(nameof(theta), theta, "a commander's Theta cannot be negative");

        var empty = new EffectiveAllocation(AptitudeAllocation.Empty, IsDefault: false, null, Array.Empty<AssignSkip>());

        // Which empires get a computed default is DATA (rpg_save_empires.controller), never a switch over
        // EmpireId. Checked BEFORE any key is derived, so an empire this save does not carry reads Empty
        // instead of asking the commander directory for a default it has no row for.
        FusionRpg.Core.Saves.EmpireController? controller = null;
        foreach (var row in EmpiresOfUnlocked(db, owner.Save.Value))
            if (row.Empire == owner.Empire) controller = row.Controller;
        if (controller is null) return empty;

        var directory = FusionRpg.Core.Commanders.CommanderDirectoryHub.Current;
        var scopeKey = directory.AllocationScopeKey(directory.DefaultFor(owner.Empire), owner.Save.Value);

        var explicitAllocation = LoadAllocationUnlocked(db, AllocationScope.Commander, scopeKey);
        if (explicitAllocation.TotalForScope(AllocationScope.Commander) > 0)
            return new EffectiveAllocation(explicitAllocation, IsDefault: false, null, Array.Empty<AssignSkip>());

        // A human empire's own sheet has no silent default (map D1 — the player is the one who can
        // click); an AI empire has nobody to click, so it gets the ladder's own computed default below.
        if (controller is FusionRpg.Core.Saves.EmpireController.Human) return empty;

        // Best-effort on the ladder's own tuning, the SAME contract this file already gives an
        // unconfigured SpeciesBuildPlanCatalog: a caller reached indirectly through an unrelated fixture
        // must not have its whole resolve taken down by a hard throw. The real server always configures
        // AptitudePresetTuningHub at boot, so production never reaches this fallback.
        if (tuning is null || !AptitudePresetTuningHub.IsConfigured)
            return new EffectiveAllocation(AptitudeAllocation.Empty, IsDefault: true, null, Array.Empty<AssignSkip>());

        var ctx = new AssignContext(
            ActivePresetRows: null, SpeciesFavourPermille: null, SpeciesPosture: null, FavourAllowed: false);
        var suggestion = AssignLadder.Suggest(ctx, AptitudePresetTuningHub.Tuning.AssignLadder);
        var budget = PointBudget.PointsFor(AllocationScope.Commander, theta, tuning);

        // The ladder's consumer shape EP1.13 established for the specimen default, mirrored here for the
        // commander pool: a distribution rung goes through this scope's own largest-remainder Baseline
        // (so the points sum to the budget), an `active-preset` rung through D13 Materialize's own math —
        // never a third rounding function either way. The active-preset arm cannot win today (an AI has no
        // preset surface, so the walk records that skip), and it stays for the day one is authored.
        if (suggestion.RuleId == AptitudeAutoAssignRules.ActivePreset)
        {
            var materialized = AptitudePresetMaterialize.Materialize(suggestion.Rows, budget);
            var presetAllocation = materialized.Ok
                ? materialized.Shares.Aggregate(AptitudeAllocation.Empty,
                    (acc, kv) => acc + AptitudeAllocation.Single(AllocationScope.Commander, kv.Key, kv.Value))
                : AptitudeAllocation.Empty;
            return new EffectiveAllocation(presetAllocation, IsDefault: true, suggestion.RuleId, suggestion.Skipped);
        }

        var allocation = CommanderAllocation.Baseline(ToPermilleDictionary(suggestion.Rows), theta, tuning);
        return new EffectiveAllocation(allocation, IsDefault: true, suggestion.RuleId, suggestion.Skipped);
    }

    /// <summary>
    /// `ai-empire-species` EP4.18 (R23) — the empire-keyed commander pool for a caller that has neither a
    /// power provider nor a tuning of its own: this store's configured host Theta
    /// (<see cref="ConfigureActorTheta"/>) and the loaded aptitude tuning. This is the ONE entry point the
    /// world-turn/siege seam, the sheet compose and the web squad use, so no consumer threads Theta
    /// itself and no consumer can disagree about which empire's pool it read.
    /// </summary>
    public EffectiveAllocation CommanderPoolFor(EmpireRef owner)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            return CommanderPoolForUnlocked(db, owner);
        }
    }

    /// <summary>The in-transaction sibling of <see cref="CommanderPoolFor"/>.</summary>
    internal EffectiveAllocation CommanderPoolForUnlocked(SqliteConnection db, EmpireRef owner) =>
        CommanderPoolOfUnlocked(db, owner, _actorThetaFor?.Invoke(owner.Save, owner.Empire) ?? 0,
            AptitudeTuningHub.IsConfigured ? AptitudeTuningHub.Tuning : null);

    // ---- EP1.13 (spec-default-build.md) — EffectiveUniqueAllocation(Unlocked) ---------------------

    /// <summary>
    /// EP1.13 (spec-default-build.md) — THE named entry point for a <see cref="AllocationScope.UniqueCreature"/>
    /// specimen's effective allocation, the exact unique-scope twin of <see cref="EffectiveSpeciesAllocation(long,string,AptitudeTuning)"/>.
    /// Resolution (identical in shape to the species path): an explicit allocation with a nonzero total
    /// wins WHOLESALE, never topped up (D2); else the assign ladder (<see cref="AssignLadder.Suggest"/>)
    /// picks a rung from the specimen's own context (its species' plan row, its primary aptitude's
    /// posture, and any active preset binding), and that rung's rows are turned into points at the
    /// specimen's own <see cref="AllocationScope.UniqueCreature"/> budget — <see cref="UniqueCreatureAllocation.Baseline"/>
    /// for a distribution rung, <see cref="AptitudePresetMaterialize.Materialize"/> for the
    /// `active-preset` rung, matching preset activation's own math (no third favour-to-points function).
    /// Never persisted (map D2): a plan or tuning change moves every unbuild specimen's default at once.
    /// A never-levelled specimen (<c>specimenLevel &lt;= 1</c>) resolves Empty by construction — its
    /// <see cref="AllocationScope.UniqueCreature"/> budget is already zero
    /// (<see cref="PointBudget.UniqueCreatureSourceFromLevel"/>), so the ladder's own rows never get a
    /// budget to spend against.
    /// </summary>
    public EffectiveAllocation EffectiveUniqueAllocation(string instanceId, AptitudeTuning tuning)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
            throw new ArgumentException("instanceId must not be empty", nameof(instanceId));

        lock (_gate)
        {
            using var db = OpenUnlocked();
            return EffectiveUniqueAllocationUnlocked(db, instanceId, tuning);
        }
    }

    /// <summary>The siege member read (`RpgStore.WorldTurns.cs:585-586`) needs this resolved inside its
    /// OWN transaction — same <c>...Unlocked(db, …)</c> sibling shape <see cref="EffectiveSpeciesAllocationUnlocked(SqliteConnection,long,string,AptitudeTuning)"/>
    /// already gives the species path, for the same reason (a second connection against an open write
    /// transaction deadlocks under SQLite).</summary>
    internal EffectiveAllocation EffectiveUniqueAllocationUnlocked(SqliteConnection db, string instanceId, AptitudeTuning tuning)
    {
        var explicitAllocation = LoadAllocationUnlocked(db, AllocationScope.UniqueCreature, instanceId);
        if (explicitAllocation.TotalForScope(AllocationScope.UniqueCreature) > 0)
            return new EffectiveAllocation(explicitAllocation, IsDefault: false, null, Array.Empty<AssignSkip>());

        var actor = ReadUniqueActorUnlocked(db, instanceId)
            ?? throw new InvalidOperationException($"unique actor '{instanceId}' not found");

        // EP1.14 -- best-effort on the ladder's own tuning, the SAME contract this file already gives
        // an unconfigured SpeciesBuildPlanCatalog (EffectiveSpeciesAllocationUnlocked, above): a real
        // server always configures AptitudePresetTuningHub at boot, but a caller reached indirectly
        // through an unrelated fixture (a squad build, a hub compose, a world-turn commit a test never
        // meant to exercise aptitudes through) must not have its whole resolve taken down by a hard
        // throw here. Empty is the honest answer a never-configured ladder gives -- production never
        // reaches this fallback.
        if (!AptitudePresetTuningHub.IsConfigured)
            return new EffectiveAllocation(AptitudeAllocation.Empty, IsDefault: true, null, Array.Empty<AssignSkip>());

        var ctx = UniqueAssignContextUnlocked(db, actor);
        var suggestion = AssignLadder.Suggest(ctx, AptitudePresetTuningHub.Tuning.AssignLadder);
        var points = suggestion.RuleId == AptitudeAutoAssignRules.ActivePreset
            ? MaterializePresetRowsAtUniqueBudget(suggestion.Rows, actor.Level, tuning)
            : UniqueCreatureAllocation.Baseline(ToPermilleDictionary(suggestion.Rows), actor.Level, tuning);
        return new EffectiveAllocation(points, IsDefault: true, suggestion.RuleId, suggestion.Skipped);
    }

    /// <summary>The unique-scope twin of `AptitudePresetEndpoints.BuildAssignContext`'s "unique" branch
    /// (Server layer, EP1.4) — same favour/posture derivation, read here against the Data layer's own
    /// connection so the resolver never re-opens one. `FavourAllowed` is always true for this scope
    /// (Mode C's own commander-pool exclusion, `favourAllowed = scope != "commander"`, never applies to
    /// a specimen).</summary>
    AssignContext UniqueAssignContextUnlocked(SqliteConnection db, UniqueActorDto actor)
    {
        IReadOnlyList<AptitudePresetRowSpec>? activeRows = null;
        var active = GetAptitudePresetActiveUnlocked(db, actor.PlayerId, "unique", actor.InstanceId);
        if (active is not null)
        {
            var preset = GetAptitudePresetUnlocked(db, active.PresetId);
            if (preset is not null)
                activeRows = ToRowSpecs(GetAptitudePresetEntriesUnlocked(db, preset.PresetId));
        }

        IReadOnlyDictionary<string, long>? favour = null;
        Posture? posture = null;
        var speciesId = ReadCreatureProfileUnlocked(db, actor.InstanceId)?.SpeciesId;
        if (!string.IsNullOrWhiteSpace(speciesId) && SpeciesBuildPlanCatalog.IsConfigured)
        {
            var shares = SpeciesBuildPlanCatalog.SharesFor(speciesId);
            if (shares.Count > 0)
            {
                favour = shares;
                var primaryId = shares
                    .OrderByDescending(kv => kv.Value)
                    .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                    .First().Key;
                if (AptitudeCatalog.TryGet(primaryId, out var row))
                    posture = row.Posture;
            }
        }

        return new AssignContext(activeRows, favour, posture, FavourAllowed: true);
    }

    /// <summary>The `active-preset` rung's own points math (spec's own "goes through Materialize, as
    /// preset activation already does"): the specimen's OWN `UniqueCreature` budget at its own level,
    /// then <see cref="AptitudePresetMaterialize.Materialize"/> at that budget — identical to what
    /// `RpgStore.AptitudePresets.cs`'s activation path does with a saved preset's rows. A saved preset's
    /// rows always sum to exactly 1000 (`SaveAptitudePreset`'s own E5 check at write time), so
    /// `Materialize` failing here would mean a corrupt row nothing else caught; Empty is the same
    /// defensive fallback <see cref="EffectiveSpeciesAllocationUnlocked(SqliteConnection,long,string,AptitudeTuning)"/>
    /// already uses for an unconfigured plan catalog, never a thrown error that takes the whole resolve down.</summary>
    static AptitudeAllocation MaterializePresetRowsAtUniqueBudget(
        IReadOnlyList<AptitudePresetRowSpec> rows, long specimenLevel, AptitudeTuning tuning)
    {
        var source = PointBudget.UniqueCreatureSourceFromLevel(specimenLevel);
        var budget = PointBudget.PointsFor(AllocationScope.UniqueCreature, source, tuning);
        var materialized = AptitudePresetMaterialize.Materialize(rows, budget);
        if (!materialized.Ok) return AptitudeAllocation.Empty;
        return materialized.Shares.Aggregate(AptitudeAllocation.Empty,
            (acc, kv) => acc + AptitudeAllocation.Single(AllocationScope.UniqueCreature, kv.Key, kv.Value));
    }

    /// <summary>The ladder's rows (permille, a list) into the shape <see cref="UniqueCreatureAllocation.Baseline"/>
    /// takes (permille, a dictionary keyed by aptitude id) — the one conversion point between the two.</summary>
    static IReadOnlyDictionary<string, long> ToPermilleDictionary(IReadOnlyList<AptitudePresetRowSpec> rows) =>
        rows.ToDictionary(r => r.AptitudeId, r => r.TargetPermille, StringComparer.Ordinal);
}
