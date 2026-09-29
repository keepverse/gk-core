using System.Text.Json;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items.Drops;
using FusionRpg.Core.Power;
using Microsoft.Data.Sqlite;
using FusionRpg.Core.Time;

namespace FusionRpg.Data;

/// <summary>One <c>item_drop_log</c> row — idempotency, replay, and the inflow measurement module 20's
/// loot filter needs.</summary>
public sealed record ItemDropLogRow(
    long Id, string PlayerId, string CorrelationId, string SourceKind, string SourceId,
    string LootSeed, long CatalogRevision, long DropTableRevision, int ItemLevel,
    string ContextJson, string ResultJson, string Notes, string CreatedUtc);

/// <summary>
/// One <c>item_generation</c> row — the per-instance stamp, written once and never updated.
///
/// <para>⛔ <b>There is no <c>socket_count</c> column, and its absence is the design.</b> It was a
/// third copy of one fact: module 16 derives the count from <c>DeriveStream(roll_seed, "item.socket")</c>
/// and states that nothing is stored ("nothing is stored, so nothing can drift"), and D2 §6 makes
/// <c>item_socket</c> the SSOT — "it is not a materialized view of anything". Three copies is how a
/// socket count silently disagrees with the sockets an item has. The columns that DO stay are
/// decisions the pipeline made that nothing else records.</para>
/// </summary>
public sealed record ItemGenerationRow(
    string InstanceId, long DropLogId, string BaseTypeId, int RarityOrdinal, int ItemLevel,
    string Frame, string Role, string AffixChannel,
    /// <summary>
    /// species-gear-chain T26 — the FIRST ordinal this instance was promoted from, or null when it was
    /// never promoted (the shipped state for every existing row).
    /// </summary>
    int? PromotedFromOrdinal = null);

public sealed partial class RpgStore
{
    // Empire-development Task 1.3a: this lane raises drop.* content rules itself
    // (MintRelicUnlocked's drop.unknown-relic / drop.mint-kind-unsupported), so it registers the
    // namespace at its own load point rather than free-riding on whichever Core static ctor
    // (LootPipeline / LootMintAt / DropTableValidator) happens to run first — a direct caller of
    // MintRelic that never resolved a pipeline would otherwise hit ContentRule's
    // unregistered-namespace throw (caught by this task's own non-relic-grant test).
    static RpgStore() => ContentRuleNamespaces.Register("drop");

    // ---- loot (module 11, drop-volume) --------------------------------------------------------------

    void EnsureLootSchemaUnlocked(SqliteConnection db)
    {
        Exec(db, """
            -- ssot-generation.md §5.1, with spec-drop-volume.md's two corrections applied:
            --   * item_loot_pity keys on RUNG IDS (heirloom/sunwoven), not I12's seven-rung r4/r6 labels
            --   * item_generation drops socket_count (item_socket is the SSOT, D2 §6)
            -- Consumers named per SC7. Nothing here is a row without a reader.

            -- WHO points at WHICH table, and what level the content is.
            -- Consumer: LootPipeline step 4, and the FE's "where does this drop" panel.
            CREATE TABLE IF NOT EXISTS loot_source (
              source_kind        TEXT NOT NULL,
              source_id          TEXT NOT NULL,
              table_id           TEXT NOT NULL,
              content_level      INTEGER NOT NULL,
              first_clear_grant  TEXT,
              PRIMARY KEY (source_kind, source_id)
            );

            -- source_allow MUST contain 'web' -- standalone-first (§4.6 rule 2), enforced at import
            -- by DropTableValidator, not by a promise in prose.
            CREATE TABLE IF NOT EXISTS drop_table (
              table_id      TEXT PRIMARY KEY,
              source_allow  TEXT NOT NULL,
              min_ilvl      INTEGER,
              max_ilvl      INTEGER,
              enabled       INTEGER NOT NULL DEFAULT 1,
              revision      INTEGER NOT NULL DEFAULT 0
            );

            -- A group is an INDEPENDENT draw unit -- the opposite of effect_container_pool.group,
            -- which is an EXCLUSION unit. `rolls` is the PRE-SCALE count step 5a reads.
            CREATE TABLE IF NOT EXISTS drop_table_group (
              table_id   TEXT NOT NULL,
              group_key  TEXT NOT NULL,
              seq        INTEGER NOT NULL,
              rolls      INTEGER NOT NULL DEFAULT 1,
              PRIMARY KEY (table_id, group_key)
            );

            -- affix_channel is X4's supply, declared HERE and never on the affix: the channel is a
            -- call-site fact, and storing it on the affix would make the affix single-source and
            -- rebuild the problem one level down.
            CREATE TABLE IF NOT EXISTS drop_table_entry (
              table_id                 TEXT NOT NULL,
              group_key                TEXT NOT NULL,
              seq                      INTEGER NOT NULL,
              entry_kind               TEXT NOT NULL,
              ref_id                   TEXT NOT NULL DEFAULT '',
              weight                   INTEGER NOT NULL,
              min_count                INTEGER NOT NULL DEFAULT 1,
              max_count                INTEGER NOT NULL DEFAULT 1,
              min_ilvl                 INTEGER,
              max_ilvl                 INTEGER,
              rarity_floor             TEXT,
              rarity_weight_shift_json TEXT,
              enabled                  INTEGER NOT NULL DEFAULT 1,
              affix_channel            TEXT NOT NULL DEFAULT 'drop',
              frame                    TEXT,
              role                     TEXT,
              trophy_scope             TEXT,
              trophy_slot              INTEGER,
              PRIMARY KEY (table_id, group_key, seq)
            );

            CREATE TABLE IF NOT EXISTS item_drop_log (
              id                  INTEGER PRIMARY KEY AUTOINCREMENT,
              player_id           TEXT NOT NULL,
              correlation_id      TEXT NOT NULL,
              source_kind         TEXT NOT NULL,
              source_id           TEXT NOT NULL,
              loot_seed           TEXT NOT NULL,
              catalog_revision    INTEGER NOT NULL,
              drop_table_revision INTEGER NOT NULL,
              item_level          INTEGER NOT NULL,
              context_json        TEXT NOT NULL,
              result_json         TEXT NOT NULL,
              notes               TEXT NOT NULL DEFAULT '',
              t                   TEXT NOT NULL,
              UNIQUE(player_id, correlation_id)
            );

            -- No socket_count column. See ItemGenerationRow's own doc comment.
            CREATE TABLE IF NOT EXISTS item_generation (
              instance_id     TEXT PRIMARY KEY,
              drop_log_id     INTEGER NOT NULL,
              base_type_id    TEXT NOT NULL,
              rarity_ordinal  INTEGER NOT NULL,
              item_level      INTEGER NOT NULL,
              frame           TEXT NOT NULL,
              role            TEXT NOT NULL,
              affix_channel   TEXT NOT NULL DEFAULT 'drop'
            );

            -- species-gear-chain T26: the promotion mark (added by the EnsureColumn below; NULL = never
            -- promoted, and once set it is the FIRST source ordinal and never moves, so the card can say
            -- how far an item has climbed. rarity_ordinal above is the CURRENT rung, rewritten by each
            -- promotion).

            -- Correction 5: keyed on RUNG IDS. I12's items_since_r4 / items_since_r6 name a
            -- seven-rung ladder that no longer exists; module 7's ten-rung table guards ordinals
            -- 70 (heirloom) and 90 (sunwoven). The string id is the join.
            CREATE TABLE IF NOT EXISTS item_loot_pity (
              player_id              TEXT PRIMARY KEY,
              items_since_heirloom   INTEGER NOT NULL DEFAULT 0,
              items_since_sunwoven   INTEGER NOT NULL DEFAULT 0,
              updated_utc            TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS item_first_clear (
              player_id    TEXT NOT NULL,
              source_kind  TEXT NOT NULL,
              source_id    TEXT NOT NULL,
              granted_utc  TEXT NOT NULL,
              PRIMARY KEY (player_id, source_kind, source_id)
            );

            CREATE INDEX IF NOT EXISTS ix_item_drop_log_player_t ON item_drop_log(player_id, t);
            CREATE INDEX IF NOT EXISTS ix_item_generation_drop_log ON item_generation(drop_log_id);
            """);
        // species-gear-chain T26: `CREATE TABLE IF NOT EXISTS` is a no-op against a database created
        // before the mark existed, so the addition has to be explicit — the same idempotent shape the
        // instance head columns use.
        EnsureColumn(db, "item_generation", "promoted_from_ordinal", "INTEGER");
        // species-gear-chain T30b (`creature-drop-tables` d): same idempotent-additive shape as
        // promoted_from_ordinal above — a database created before the trophy columns existed needs
        // them added explicitly; both are NULL on every pre-existing row (no ref_id or trophy scope
        // conflict possible on data that predates the concept).
        EnsureColumn(db, "drop_table_entry", "trophy_scope", "TEXT");
        EnsureColumn(db, "drop_table_entry", "trophy_slot", "INTEGER");
    }

    /// <summary>
    /// Replace the loaded loot corpus in one transaction. Validated FIRST and whole — E14's policy is
    /// all-or-nothing: one bad row and nothing is imported.
    /// </summary>
    public void ImportLootCorpus(LootCorpus corpus, DropVolumeTuning tuning, DropContentLookups? lookups = null)
    {
        if (corpus is null) throw new ArgumentNullException(nameof(corpus));

        var check = DropTableValidator.Validate(corpus.Sources, corpus.Tables, tuning, lookups);
        if (!check.IsOk)
            throw new InvalidOperationException($"loot corpus rejected: {check}");

        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();

            LootExec(db, tx, "DELETE FROM drop_table_entry;");
            LootExec(db, tx, "DELETE FROM drop_table_group;");
            LootExec(db, tx, "DELETE FROM drop_table;");
            LootExec(db, tx, "DELETE FROM loot_source;");

            foreach (var t in corpus.Tables)
            {
                LootExec(db, tx, """
                    INSERT INTO drop_table (table_id, source_allow, min_ilvl, max_ilvl, enabled, revision)
                    VALUES ($id, $allow, $lo, $hi, $en, $rev);
                    """,
                    ("$id", t.TableId), ("$allow", string.Join(",", t.SourceAllow)),
                    ("$lo", (object?)t.MinIlvl ?? DBNull.Value), ("$hi", (object?)t.MaxIlvl ?? DBNull.Value),
                    ("$en", t.Enabled ? 1 : 0), ("$rev", t.Revision));

                foreach (var g in t.Groups)
                {
                    LootExec(db, tx, """
                        INSERT INTO drop_table_group (table_id, group_key, seq, rolls)
                        VALUES ($id, $gk, $seq, $rolls);
                        """,
                        ("$id", t.TableId), ("$gk", g.GroupKey), ("$seq", g.Seq), ("$rolls", g.Rolls));

                    foreach (var e in g.Entries)
                        LootExec(db, tx, """
                            INSERT INTO drop_table_entry
                              (table_id, group_key, seq, entry_kind, ref_id, weight, min_count, max_count,
                               min_ilvl, max_ilvl, rarity_floor, rarity_weight_shift_json, enabled,
                               affix_channel, frame, role, trophy_scope, trophy_slot)
                            VALUES ($id, $gk, $seq, $kind, $ref, $w, $minc, $maxc, $lo, $hi, $floor,
                                    $shift, $en, $chan, $frame, $role, $tscope, $tslot);
                            """,
                            ("$id", t.TableId), ("$gk", g.GroupKey), ("$seq", e.Seq),
                            ("$kind", LootCorpusReader.KindName(e.Kind)), ("$ref", e.RefId), ("$w", e.Weight),
                            ("$minc", e.MinCount), ("$maxc", e.MaxCount),
                            ("$lo", (object?)e.MinIlvl ?? DBNull.Value), ("$hi", (object?)e.MaxIlvl ?? DBNull.Value),
                            ("$floor", (object?)e.RarityFloor ?? DBNull.Value),
                            ("$shift", e.RarityWeightShift is { Count: > 0 }
                                ? JsonSerializer.Serialize(e.RarityWeightShift.ToDictionary(k => k.Key.ToString(), v => v.Value))
                                : (object)DBNull.Value),
                            ("$en", e.Enabled ? 1 : 0), ("$chan", e.AffixChannel),
                            ("$frame", (object?)e.Frame ?? DBNull.Value), ("$role", (object?)e.Role ?? DBNull.Value),
                            // species-gear-chain T30b: NULL for every ordinary entry (every row that
                            // predates this concept and every non-trophy row authored after it).
                            ("$tscope", (object?)e.TrophyScope ?? DBNull.Value),
                            ("$tslot", (object?)e.TrophySlot ?? DBNull.Value));
                }
            }

            foreach (var s in corpus.Sources)
                LootExec(db, tx, """
                    INSERT INTO loot_source (source_kind, source_id, table_id, content_level, first_clear_grant)
                    VALUES ($k, $i, $t, $lvl, $grant);
                    """,
                    ("$k", s.SourceKind), ("$i", s.SourceId), ("$t", s.TableId),
                    ("$lvl", s.ContentLevel), ("$grant", (object?)s.FirstClearGrant ?? DBNull.Value));

            tx.Commit();
        }
    }

    public LootCorpus LoadLootCorpus()
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();

            var entriesByGroup = new Dictionary<(string, string), List<DropTableEntryRow>>();
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT table_id, group_key, seq, entry_kind, ref_id, weight, min_count, max_count,
                           min_ilvl, max_ilvl, rarity_floor, rarity_weight_shift_json, enabled,
                           affix_channel, frame, role, trophy_scope, trophy_slot
                    FROM drop_table_entry ORDER BY table_id, group_key, seq;
                    """;
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    if (!LootCorpusReader.TryKind(r.GetString(3), out var kind))
                        throw new InvalidOperationException($"drop_table_entry carries unknown entry_kind '{r.GetString(3)}'");

                    Dictionary<int, int>? shift = null;
                    if (!r.IsDBNull(11))
                    {
                        shift = new Dictionary<int, int>();
                        foreach (var kv in JsonSerializer.Deserialize<Dictionary<string, int>>(r.GetString(11))!)
                            shift[int.Parse(kv.Key)] = kv.Value;
                    }

                    var key = (r.GetString(0), r.GetString(1));
                    if (!entriesByGroup.TryGetValue(key, out var list))
                        entriesByGroup[key] = list = new List<DropTableEntryRow>();

                    list.Add(new DropTableEntryRow(
                        r.GetInt32(2), kind, r.GetString(4), r.GetInt32(5), r.GetInt32(6), r.GetInt32(7),
                        r.IsDBNull(8) ? null : r.GetInt32(8), r.IsDBNull(9) ? null : r.GetInt32(9),
                        r.IsDBNull(10) ? null : r.GetString(10), shift, r.GetInt32(12) != 0,
                        r.GetString(13), r.IsDBNull(14) ? null : r.GetString(14),
                        r.IsDBNull(15) ? null : r.GetString(15),
                        // species-gear-chain T30b
                        r.IsDBNull(16) ? null : r.GetString(16),
                        r.IsDBNull(17) ? null : r.GetInt32(17)));
                }
            }

            var groupsByTable = new Dictionary<string, List<DropTableGroupRow>>();
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = "SELECT table_id, group_key, seq, rolls FROM drop_table_group ORDER BY table_id, seq;";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var tableId = r.GetString(0);
                    var groupKey = r.GetString(1);
                    if (!groupsByTable.TryGetValue(tableId, out var list))
                        groupsByTable[tableId] = list = new List<DropTableGroupRow>();
                    list.Add(new DropTableGroupRow(groupKey, r.GetInt32(2), r.GetInt32(3),
                        entriesByGroup.TryGetValue((tableId, groupKey), out var es)
                            ? es
                            : new List<DropTableEntryRow>()));
                }
            }

            var tables = new List<DropTableRow>();
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = "SELECT table_id, source_allow, min_ilvl, max_ilvl, enabled, revision FROM drop_table ORDER BY table_id;";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var tableId = r.GetString(0);
                    tables.Add(new DropTableRow(
                        tableId,
                        r.GetString(1).Split(',', StringSplitOptions.RemoveEmptyEntries),
                        r.IsDBNull(2) ? null : r.GetInt32(2), r.IsDBNull(3) ? null : r.GetInt32(3),
                        r.GetInt32(4) != 0, r.GetInt64(5),
                        groupsByTable.TryGetValue(tableId, out var gs) ? gs : new List<DropTableGroupRow>()));
                }
            }

            var sources = new List<LootSourceRow>();
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = "SELECT source_kind, source_id, table_id, content_level, first_clear_grant FROM loot_source ORDER BY source_kind, source_id;";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    sources.Add(new LootSourceRow(r.GetString(0), r.GetString(1), r.GetString(2),
                        r.GetInt32(3), r.IsDBNull(4) ? null : r.GetString(4)));
            }

            return new LootCorpus(sources, tables);
        }
    }

    public LootPityState GetLootPity(string playerId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT items_since_heirloom, items_since_sunwoven FROM item_loot_pity WHERE player_id = $p;";
            cmd.Parameters.AddWithValue("$p", playerId);
            using var r = cmd.ExecuteReader();
            return r.Read() ? new LootPityState(r.GetInt64(0), r.GetInt64(1)) : LootPityState.Empty;
        }
    }

    public bool HasFirstClear(string playerId, string sourceKind, string sourceId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM item_first_clear WHERE player_id = $p AND source_kind = $k AND source_id = $i;";
            cmd.Parameters.AddWithValue("$p", playerId);
            cmd.Parameters.AddWithValue("$k", sourceKind);
            cmd.Parameters.AddWithValue("$i", sourceId);
            return cmd.ExecuteScalar() is not null;
        }
    }

    /// <summary>Same read as <see cref="HasFirstClear"/>, on the caller's own connection/transaction —
    /// D3.15's own bank-at-clear hook (`RpgStore.Delve.ApplyBossFirstClearGrantUnlocked`) needs this
    /// INSIDE its own transaction so the idempotency check and the write it gates land atomically,
    /// mirroring `RecordedLootManifestUnlocked`'s identical "same query, caller's own tx" shape.</summary>
    internal static bool HasFirstClearUnlocked(SqliteConnection db, SqliteTransaction tx, string playerId, string sourceKind, string sourceId)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT 1 FROM item_first_clear WHERE player_id = $p AND source_kind = $k AND source_id = $i;";
        cmd.Parameters.AddWithValue("$p", playerId);
        cmd.Parameters.AddWithValue("$k", sourceKind);
        cmd.Parameters.AddWithValue("$i", sourceId);
        return cmd.ExecuteScalar() is not null;
    }

    /// <summary>The same `item_first_clear` write <see cref="PersistLootUnlocked"/>'s own
    /// `manifest.FirstClearGrant` arm makes, standalone — D3.15's own bank-at-clear hook does not
    /// produce a <see cref="LootManifest"/> (`DelveLoot.InstantiateBossFirstClearGrant` returns a raw
    /// `InstanceRow`, never a manifest), so it needs this write on its own rather than reusing
    /// `PersistLootUnlocked` wholesale. Same `ON CONFLICT DO NOTHING` idempotency, same table, same
    /// caller's-own-`(db, tx)` shape as every other `*Unlocked` writer in this family.</summary>
    internal static void RecordFirstClearUnlocked(SqliteConnection db, SqliteTransaction tx, string playerId, string sourceKind, string sourceId, string nowUtc)
    {
        LootExec(db, tx, """
            INSERT INTO item_first_clear (player_id, source_kind, source_id, granted_utc)
            VALUES ($p, $k, $i, $t)
            ON CONFLICT(player_id, source_kind, source_id) DO NOTHING;
            """,
            ("$p", playerId), ("$k", sourceKind), ("$i", sourceId), ("$t", nowUtc));
    }

    /// <summary>Step 1's gate, as the store sees it: the recorded manifest for an already-resolved
    /// (player, correlation) pair, or <c>null</c>.</summary>
    public string? RecordedLootManifest(string playerId, string correlationId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT result_json FROM item_drop_log WHERE player_id = $p AND correlation_id = $c;";
            cmd.Parameters.AddWithValue("$p", playerId);
            cmd.Parameters.AddWithValue("$c", correlationId);
            return cmd.ExecuteScalar() as string;
        }
    }

    /// <summary>
    /// The real, live `LootContentView` — confirmed by `grep` (party-dungeon-todo.md D4.12/D3.11/D3.3,
    /// 2026-09-07) to have NEVER existed anywhere in `src/` for any caller of `LootPipeline.Resolve`.
    /// Mostly composition, not new reads: `LoadLootCorpus()` (already shipped) already assembles
    /// `Sources`/`Tables` correctly, and `HasFirstClear`/`RecordedLootManifest`/`GetContainer` already
    /// back three of the optional delegates exactly. `Mint` is deliberately left `null` here — it is
    /// the CALLER's own parameter on every real orchestrator (`RollRoom`/`RollQuestReward`'s own
    /// `mintAt`), never the content view's job, matching how both already compose it themselves
    /// (`view with { Mint = grant => mintAt(...) }`).
    ///
    /// <para><b>`BaseTypesFor` RESOLVED 2026-09-07, same day</b> — reads the real `item_base_type` table
    /// (<see cref="RpgStore.BaseTypeIdsFor"/>), imported from `gk-data/packs/fusion/data/seed/items/base-types/**/*.json` via
    /// <see cref="FusionRpg.Core.Items.Drops.BaseTypeSeedFile"/>. The earlier "no Core-side reader
    /// either" claim was a stale grep result: `FusionRpg.Server.ItemBaseTypeCorpus` already read this
    /// exact content (for the item-card display shape) — wrong layer for `RpgStore` to depend on, and
    /// shaped for display keys, not a `(frame, role)` forward index, so a dedicated minimal reader was
    /// still the right, small fix rather than a genuinely-new investigation.</para>
    /// </summary>
    /// <param name="familyMap">
    /// species-gear-chain T34c — the species -> family ids map (<c>FamilyMap.Parse</c> over
    /// `gk-data/packs/fusion/data/seed/actions/_generated/family-map.json`), wired even though no live caller supplies it
    /// yet, the SAME "wire it now, real callers catch up later" precedent this method's own
    /// `SocketKindFor` parameter already set (T4). An optional PARAMETER, never a file read inside
    /// this Data-layer method — the host loads and injects.
    /// </param>
    public LootContentView BuildLiveLootContentView(
        IReadOnlyDictionary<string, IReadOnlyList<string>>? familyMap = null)
    {
        var corpus = LoadLootCorpus();
        var sources = corpus.Sources.ToDictionary(s => s.Key, StringComparer.Ordinal);
        var tables = corpus.Tables.ToDictionary(t => t.TableId, StringComparer.Ordinal);

        var ladder = new List<RarityRung>();
        foreach (var r in ListRarities().OrderBy(r => r.Ordinal))
        {
            var dropWeight = GetRarityBudget(r.RarityId, "drop_weight_default")
                ?? throw new InvalidOperationException(
                    $"rarity '{r.RarityId}' has no 'drop_weight_default' budget row -- item-rarity.v{{n}}.json import never ran");
            ladder.Add(new RarityRung(r.RarityId, r.Ordinal, r.PrefixRolls, r.SuffixRolls, r.MinTier, r.MaxTier, dropWeight));
        }

        return new LootContentView(
            sources, tables, ladder,
            BaseTypesFor: BaseTypeIdsFor,
            FirstClearAlreadyGranted: HasFirstClear,
            RecordedManifestFor: RecordedLootManifest,
            UniqueRarityFor: refId => GetContainer(refId)?.Rarity,
            // D3.15/D4.12/D3.11: the same GetContainer(refId) read UniqueRarityFor already makes,
            // reading the two fields UniqueContainerBuild.From now populates alongside Rarity. Both
            // must be present to return a real pair -- a container written before this fix landed (or
            // any non-unique container someone mistakenly passes here) correctly resolves to null.
            UniqueBaseTypeFor: refId => GetContainer(refId) is { Frame: { } f, BaseTypeId: { } b } ? (f, b) : null,
            // species-gear-chain T4: the step-10 kind read. Set membership off item_set_member (the
            // same read SocketHost.IsSetPiece makes); unique/boss kinds arrive through their own
            // paths, never here. Wired even though no live caller supplies SocketTuning yet, so the
            // roll activates complete the day the hosts supply the tuning — never half-supplied.
            SocketKindFor: baseTypeId => IsSetMember(baseTypeId)
                ? FusionRpg.Core.Items.Sockets.SocketKinds.Set
                : FusionRpg.Core.Items.Sockets.SocketKinds.Ordinary,
            FamiliesForSpecies: familyMap != null
                ? speciesId => familyMap.TryGetValue(speciesId, out var families)
                    ? families : Array.Empty<string>()
                : null);
    }

    /// <summary>
    /// Empire-development Task 1.3a (spec-relic-item-kind.md §Design 3, §Code style) — the MintRelic
    /// host implementation behind <c>LootContentView.MintRelic</c>.
    ///
    /// <para>A relic mint writes <c>effect_instance</c> (via <c>SaveInstanceUnlocked</c>) and
    /// <c>rpg_item</c> (via <c>AcquireItemUnlocked</c>) — and nothing else. It NEVER writes
    /// <c>item_generation</c>: that table's three NOT NULL columns (<c>base_type_id</c>, <c>role</c>,
    /// <c>frame</c>) are an equip-shaped provenance stamp a relic has none of by definition, and the
    /// persisted <c>rpg_item</c> row itself carries no such columns either (there is nowhere on that
    /// table to put them — see <c>RpgStore.Items.cs</c>). A relic mint that ever needs an
    /// <c>item_generation</c> row means the design has drifted back toward treating a relic as
    /// equipment.</para>
    ///
    /// <para>The instance itself is built by the real <c>LootMintAt.Mint</c> Relic arm (Core, pure):
    /// straight from the relic's own <c>ContainerKind.Relic</c> container, fixed core and optional
    /// pool, never a base type, role, frame or rarity draw. The container must exist and be
    /// <c>Kind == Relic</c> — anything else refuses <c>drop.unknown-relic</c> by name.</para>
    ///
    /// <para>Ownership is policy, not content (<c>RpgStore.Items.cs:40-44</c>): the
    /// <c>effect_instance</c> row carries the content-derived fingerprint only (no
    /// <c>player_id</c>), and this method's own <c>rpg_item</c> write carries who owns it
    /// (<c>origin_kind: "drop"</c>, <c>origin_ref:</c> the relic container id).</para>
    /// </summary>
    internal LootMintResult MintRelicUnlocked(
        SqliteConnection db, SqliteTransaction tx, LootGrant grant, string playerId, int thetaContent,
        PowerTuning tuning, long catalogRevision = 0, string? instanceId = null, string? createdUtc = null)
    {
        if (grant is null) throw new ArgumentNullException(nameof(grant));
        if (string.IsNullOrWhiteSpace(playerId)) throw new ArgumentNullException(nameof(playerId));
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));

        if (grant.Kind != DropEntryKind.Relic)
            return new LootMintResult(
                AtomRejection.ContentRule("drop.mint-kind-unsupported",
                    $"grant {grant.Index} is '{grant.Kind}' — MintRelic mints only Relic grants"),
                null);

        if (GetContainer(grant.RefId) is not { Kind: ContainerKind.Relic })
            return new LootMintResult(
                AtomRejection.ContentRule("drop.unknown-relic",
                    $"relic container '{grant.RefId}' does not exist or is not a Relic-kind container"),
                null);

        var lookups = new LootMintLookups(
            LookupAtom: GetAtom,
            LookupAffix: GetAffix,
            Equipment: null,
            ContainerFor: GetContainer);

        var utc = createdUtc ?? ServerClock.UtcNowDateTime.ToString("o");
        var rejection = LootMintAt.Mint(grant, thetaContent, lookups, tuning, out var instance,
            InstanceOrigin.Drop, catalogRevision);
        if (!rejection.IsOk || instance is null) return new LootMintResult(rejection, null);

        var id = SaveInstanceUnlocked(db, tx, instance, instanceId, utc);
        var acquired = AcquireItemUnlocked(db, tx, new RpgItemRow
        {
            InstanceId = id, PlayerId = playerId, AcquiredUtc = utc,
            OriginKind = "drop", OriginRef = grant.RefId,
        });
        if (!acquired.IsOk) return new LootMintResult(acquired, null);

        return new LootMintResult(AtomRejection.Ok, id);
    }

    /// <summary>Public, self-committing form — the shape a standalone caller or a test wants; a
    /// turn-commit-composed caller (wonder-build-flow) uses <see cref="MintRelicUnlocked"/> instead.
    /// Matches the established "public locked + internal Unlocked" pair discipline
    /// (<c>SaveInstance</c>/<c>SaveInstanceUnlocked</c>, <c>PersistLoot</c>/<c>PersistLootUnlocked</c>).</summary>
    public LootMintResult MintRelic(
        LootGrant grant, string playerId, int thetaContent, PowerTuning tuning,
        long catalogRevision = 0, string? instanceId = null, string? createdUtc = null)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();
            var result = MintRelicUnlocked(db, tx, grant, playerId, thetaContent, tuning,
                catalogRevision, instanceId, createdUtc);
            if (result.Rejection.IsOk) tx.Commit();
            return result;
        }
    }

    /// <summary>
    /// Step 11 — <b>ONE transaction</b>: the drop log, every <c>item_generation</c> stamp, the pity
    /// update and the first-clear mark, committed together or not at all.
    ///
    /// <para>The summoning flow already paid for this lesson (its two-transaction bug), <b>with one
    /// extra hazard: nothing is spent here, so a partial commit mints FREE items rather than losing
    /// paid ones.</b></para>
    ///
    /// <para>A retry mints nothing: <c>UNIQUE(player_id, correlation_id)</c> is the second net under
    /// the pipeline's own gate, and this returns the recorded id rather than inserting a second row.</para>
    /// </summary>
    public long PersistLoot(
        string playerId, LootManifest manifest, string sourceKind, string sourceId,
        long catalogRevision, long dropTableRevision,
        IReadOnlyList<ItemGenerationRow> generations, string? nowUtc = null)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();
            var logId = PersistLootUnlocked(db, tx, playerId, manifest, sourceKind, sourceId,
                catalogRevision, dropTableRevision, generations, nowUtc);
            tx.Commit();
            return logId;
        }
    }

    /// <summary>Same writes on the caller's connection/transaction — `delve-quests` D4.12/D4.14's own
    /// named gap: banking a quest reward's loot must land in the SAME transaction `CloseDelve`
    /// commits, "committed together or not at all" per this method's own Step-11 doc comment above,
    /// now equally true when `CloseDelve` is the one holding the transaction. The early-return on an
    /// already-recorded correlation id no longer commits itself (the caller's own commit covers it,
    /// same as every other write here) — the idempotency guarantee is unchanged, only who commits.
    /// See <see cref="AppendMutationOpUnlocked"/> for the established "why" this whole `*Unlocked`
    /// family shares.</summary>
    internal long PersistLootUnlocked(
        SqliteConnection db, SqliteTransaction tx,
        string playerId, LootManifest manifest, string sourceKind, string sourceId,
        long catalogRevision, long dropTableRevision,
        IReadOnlyList<ItemGenerationRow> generations, string? nowUtc = null)
    {
        if (manifest is null) throw new ArgumentNullException(nameof(manifest));
        generations ??= Array.Empty<ItemGenerationRow>();
        var t = nowUtc ?? ServerClock.UtcNowDateTime.ToString("o");

        using (var existing = db.CreateCommand())
        {
            existing.Transaction = tx;
            existing.CommandText = "SELECT id FROM item_drop_log WHERE player_id = $p AND correlation_id = $c;";
            existing.Parameters.AddWithValue("$p", playerId);
            existing.Parameters.AddWithValue("$c", manifest.CorrelationId);
            if (existing.ExecuteScalar() is { } already)
                return Convert.ToInt64(already);
        }

        LootExec(db, tx, """
            INSERT INTO item_drop_log
              (player_id, correlation_id, source_kind, source_id, loot_seed, catalog_revision,
               drop_table_revision, item_level, context_json, result_json, notes, t)
            VALUES ($p, $c, $sk, $si, $seed, $cat, $rev, $ilvl, $ctx, $res, $notes, $t);
            """,
            ("$p", playerId), ("$c", manifest.CorrelationId), ("$sk", sourceKind), ("$si", sourceId),
            ("$seed", manifest.LootSeed.ToString()), ("$cat", catalogRevision), ("$rev", dropTableRevision),
            ("$ilvl", manifest.ItemLevel), ("$ctx", manifest.ContextJson),
            ("$res", JsonSerializer.Serialize(manifest.Grants)),
            ("$notes", string.Join(",", manifest.Notes)), ("$t", t));

        long logId;
        using (var idCmd = db.CreateCommand())
        {
            idCmd.Transaction = tx;
            idCmd.CommandText = "SELECT last_insert_rowid();";
            logId = Convert.ToInt64(idCmd.ExecuteScalar());
        }

        foreach (var g in generations)
            LootExec(db, tx, """
                INSERT INTO item_generation
                  (instance_id, drop_log_id, base_type_id, rarity_ordinal, item_level, frame, role, affix_channel)
                VALUES ($iid, $log, $bt, $ord, $ilvl, $frame, $role, $chan);
                """,
                ("$iid", g.InstanceId), ("$log", logId), ("$bt", g.BaseTypeId), ("$ord", g.RarityOrdinal),
                ("$ilvl", g.ItemLevel), ("$frame", g.Frame), ("$role", g.Role), ("$chan", g.AffixChannel));

        LootExec(db, tx, """
            INSERT INTO item_loot_pity (player_id, items_since_heirloom, items_since_sunwoven, updated_utc)
            VALUES ($p, $h, $s, $t)
            ON CONFLICT(player_id) DO UPDATE SET
              items_since_heirloom = excluded.items_since_heirloom,
              items_since_sunwoven = excluded.items_since_sunwoven,
              updated_utc = excluded.updated_utc;
            """,
            ("$p", playerId), ("$h", manifest.PityOut.ItemsSinceHeirloom),
            ("$s", manifest.PityOut.ItemsSinceSunwoven), ("$t", t));

        if (manifest.FirstClearGrant is { Length: > 0 })
            LootExec(db, tx, """
                INSERT INTO item_first_clear (player_id, source_kind, source_id, granted_utc)
                VALUES ($p, $k, $i, $t)
                ON CONFLICT(player_id, source_kind, source_id) DO NOTHING;
                """,
                ("$p", playerId), ("$k", sourceKind), ("$i", sourceId), ("$t", t));

        CreditManifestGrantsUnlocked(db, playerId, manifest, t);

        return logId;
    }

    /// <summary>
    /// species-gear-chain T29 — <b>the shelf credit.</b> A drawn <c>Material</c> entry is added to
    /// <c>manifest.Grants</c> and never reaches <c>LootMintAt.Mint</c> (its own doc comment says so),
    /// and a <c>Currency</c> entry is a <c>souls</c> award; neither was ever credited, so a kill that
    /// dropped materials paid its drop-log row and nothing else. Both are credited here — in the
    /// caller's transaction and <b>after</b> the <c>(player_id, correlation_id)</c> early return — so
    /// a retried resolution credits nothing a second time and a failed credit rolls the drop-log row
    /// back with it.
    ///
    /// <para>The drop log carries the player id as TEXT; the credit helpers key on <c>long</c>. It is
    /// parsed exactly <b>once</b>, checked, and only when there is something to credit — a non-numeric
    /// id refuses the whole persist by name rather than crediting a row for some default player.</para>
    /// </summary>
    void CreditManifestGrantsUnlocked(
        SqliteConnection db, string playerId, LootManifest manifest, string t)
    {
        List<(string MaterialId, long Qty)>? materials = null;
        long souls = 0;
        foreach (var grant in manifest.Grants)
        {
            switch (grant.Kind)
            {
                case DropEntryKind.Material when grant.Count > 0:
                    (materials ??= new List<(string, long)>()).Add((grant.RefId, grant.Count));
                    break;
                case DropEntryKind.Currency when grant.Count > 0:
                    // The loot corpus's one currency id is `souls` (gk-data/packs/fusion/data/seed/loot/tables.v1.json).
                    // Anything else is a content gap the validator already refuses, so it is a
                    // by-name refusal here rather than a silent skip.
                    if (!string.Equals(grant.RefId, SoulsCurrencyId, StringComparison.Ordinal))
                        throw new ArgumentException(
                            $"loot.credit-currency-unwired: '{grant.RefId}' is not the '{SoulsCurrencyId}' currency");
                    souls = checked(souls + grant.Count);
                    break;
            }
        }

        if (materials is null && souls == 0) return;

        var creditPlayerId = ParseCreditPlayerId(playerId);
        if (materials is not null) GrantMaterialsUnlocked(db, creditPlayerId, materials, t);
        if (souls > 0)
        {
            GuardSoulAwardOrThrow(ReadSoulBalanceUnlocked(db, creditPlayerId).Balance, souls);
            AppendSoulLedgerUnlocked(db, creditPlayerId, 0, souls, "loot", "loot",
                manifest.CorrelationId, "loot:" + manifest.CorrelationId, t);
        }
    }

    /// <summary>The one currency id the loot corpus authors.</summary>
    const string SoulsCurrencyId = "souls";

    /// <summary>The drop log's TEXT player id as the credit helpers' <c>long</c> — once, checked.</summary>
    static long ParseCreditPlayerId(string playerId) =>
        long.TryParse(playerId, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var id)
            ? id
            : throw new ArgumentException(
                $"loot.credit-player-id-not-numeric: drop-log player id '{playerId}' is not a numeric player id");

    public IReadOnlyList<ItemDropLogRow> ListDropLog(string playerId, int limit = 100)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT id, player_id, correlation_id, source_kind, source_id, loot_seed, catalog_revision,
                       drop_table_revision, item_level, context_json, result_json, notes, t
                FROM item_drop_log WHERE player_id = $p ORDER BY id DESC LIMIT $n;
                """;
            cmd.Parameters.AddWithValue("$p", playerId);
            cmd.Parameters.AddWithValue("$n", limit);
            using var r = cmd.ExecuteReader();
            var rows = new List<ItemDropLogRow>();
            while (r.Read())
                rows.Add(new ItemDropLogRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3),
                    r.GetString(4), r.GetString(5), r.GetInt64(6), r.GetInt64(7), r.GetInt32(8),
                    r.GetString(9), r.GetString(10), r.GetString(11), r.GetString(12)));
            return rows;
        }
    }

    public IReadOnlyList<ItemGenerationRow> ListGenerations(long dropLogId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT instance_id, drop_log_id, base_type_id, rarity_ordinal, item_level, frame, role,
                       affix_channel, promoted_from_ordinal
                FROM item_generation WHERE drop_log_id = $id ORDER BY instance_id;
                """;
            cmd.Parameters.AddWithValue("$id", dropLogId);
            using var r = cmd.ExecuteReader();
            var rows = new List<ItemGenerationRow>();
            while (r.Read())
                rows.Add(new ItemGenerationRow(r.GetString(0), r.GetInt64(1), r.GetString(2), r.GetInt32(3),
                    r.GetInt32(4), r.GetString(5), r.GetString(6), r.GetString(7),
                    r.IsDBNull(8) ? null : r.GetInt32(8)));
            return rows;
        }
    }

    /// <summary>
    /// species-gear-chain T26 — one rarity promotion, on the caller's connection so it commits with the
    /// craft that caused it. <c>rarity_ordinal</c> is the item's CURRENT rung and is rewritten every
    /// time; the mark is the FIRST source ordinal and never moves (<c>COALESCE</c>), so a second
    /// promotion leaves it alone and the card can say how far the item has climbed.
    /// </summary>
    internal void PromoteInstanceUnlocked(SqliteConnection db, string instanceId, int newOrdinal, int fromOrdinal)
    {
        ExecOn(db, """
            UPDATE item_generation
            SET rarity_ordinal = $new,
                promoted_from_ordinal = COALESCE(promoted_from_ordinal, $from)
            WHERE instance_id = $id;
            """,
            ("$new", newOrdinal), ("$from", fromOrdinal), ("$id", instanceId));
    }

    /// <summary>
    /// One instance's generation stamp. <c>item_generation.instance_id</c> is the PK, so this is the
    /// point lookup <see cref="ListGenerations"/>'s drop-log sweep could never be — it is what lets a
    /// reader go from an instance id to the item level / frame / role / rarity ordinal the pipeline
    /// decided, without knowing which drop produced it.
    ///
    /// <para>Added 2026-09-06 for <see cref="GetItemCardInput"/>: the card's own <c>ItemLevel</c> and
    /// the frame the equip gate compares against both live here and nowhere else.</para>
    /// </summary>
    public ItemGenerationRow? GetItemGeneration(string instanceId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT instance_id, drop_log_id, base_type_id, rarity_ordinal, item_level, frame, role,
                       affix_channel, promoted_from_ordinal
                FROM item_generation WHERE instance_id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", instanceId);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            return new ItemGenerationRow(r.GetString(0), r.GetInt64(1), r.GetString(2), r.GetInt32(3),
                r.GetInt32(4), r.GetString(5), r.GetString(6), r.GetString(7),
                r.IsDBNull(8) ? null : r.GetInt32(8));
        }
    }

    /// <summary>
    /// The inflow measurement module 20's loot filter needs — I12 §8's `40/day` tripwire read as
    /// written. ⛔ It is a <b>measurement</b>, never a counter that could become a gate: this method
    /// only reads, and nothing in the pipeline consults it.
    /// </summary>
    public int CountEquipmentMinted(string playerId, string sinceUtc)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT COUNT(*) FROM item_generation g
                JOIN item_drop_log l ON l.id = g.drop_log_id
                WHERE l.player_id = $p AND l.t >= $since;
                """;
            cmd.Parameters.AddWithValue("$p", playerId);
            cmd.Parameters.AddWithValue("$since", sinceUtc);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    /// <summary>
    /// The watermarked tail-trim, shipped on day one rather than deferred — the soul ledger already
    /// paid for that lesson. What trims is <c>context_json</c> / <c>result_json</c> beyond the
    /// horizon; the ROW stays, so <see cref="CountEquipmentMinted"/> keeps working and
    /// <c>item_generation</c> remains the permanent record. The horizon is the owner's
    /// (<c>log.retentionHorizonDays</c> in the tuning file).
    /// </summary>
    public int TrimDropLog(string beforeUtc)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();
            int affected;
            using (var cmd = db.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    UPDATE item_drop_log SET context_json = '{}', result_json = '[]'
                    WHERE t < $before AND (context_json <> '{}' OR result_json <> '[]');
                    """;
                cmd.Parameters.AddWithValue("$before", beforeUtc);
                affected = cmd.ExecuteNonQuery();
            }

            tx.Commit();
            return affected;
        }
    }

    static void LootExec(SqliteConnection db, SqliteTransaction tx, string sql,
        params (string Name, object Value)[] args)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }
}
