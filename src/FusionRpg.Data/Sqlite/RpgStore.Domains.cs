using System.Text.Json;
using FusionRpg.Core.Delve.Domains;
using Microsoft.Data.Sqlite;

namespace FusionRpg.Data;

/// <summary>One `dungeon_domain` row's own storage-only fields (`DomainRow`, D4.15, deliberately
/// carries none of these — provenance/staleness are storage concerns, not catalog-validation ones,
/// per that type's own doc comment). Written by `ImportDungeonDomains` (D4.16, below); still empty in
/// production today because every real domain refuses `DomainPreflight.Run` on row 6's own
/// threat-audit gap (D4.17's own already-documented finding) — the writer itself is real and tested,
/// not a placeholder.</summary>
public sealed record DomainStorageRow(DomainRow Domain, string ProvenanceJson, string ValidatedJson, long Revision);

/// <summary>D4.16 (spec-domain-catalog.md §3): "what THIS import proved the row against" —
/// deliberately narrower than the spec's own full `{registryVersions, dungeonTuningHash,
/// encounterTuningHash, catalogRevision, dropTableRevision, sampleSeeds}` citation. No tuning-content-
/// hash or registry-version-tracking mechanism exists anywhere in this codebase yet (confirmed by
/// `grep`, 2026-09-07) — inventing one here, unreviewed, to fill an object nobody reads yet would be
/// worse than an honest, narrower first cut. `CatalogRevision`/`SampleSeeds` are the two fields this
/// import CAN construct correctly today; the rest are a named, real follow-up, not silently dropped.</summary>
public sealed record DomainValidatedFacts(long CatalogRevision, int SampleSeeds);

/// <summary>What one `ImportDungeonDomains` call did, or refused to do — the domain-kind mirror of
/// <see cref="ImportOutcome"/>, kept separate because a domain refusal is a list of
/// <see cref="DomainRefusal"/> (Core's own type), never a <see cref="SeedError"/>.</summary>
public sealed record DomainImportOutcome(bool Committed, IReadOnlyList<DomainRefusal> Refusals, long CatalogRevision)
{
    public bool IsOk => Refusals.Count == 0;
}

/// <summary>One `rpg_domain_progress.clears_json` entry — `{rungId, oath, delveId}`, spec-domain-catalog.md
/// §5's exact shape. `RungId` also carries a tail step's own label (`rungIdOrTailLabel`, §5a) — the
/// column is untyped text either way, matching `PlayerClears`' own `RungIds`/`TailSteps` split being a
/// Core-side, not a storage-side, distinction.</summary>
public sealed record DomainClearJsonRow(string RungId, bool Oath, long DelveId);

/// <summary>One `rpg_domain_progress` row — discovery plus every clear. "Highest rung cleared is
/// derived from `clears_json`, never stored" (spec §5, verbatim) — there is no separate column for it
/// here either.</summary>
public sealed record DomainProgressRow(
    long PlayerId, string DomainId, string FoundVia, string FoundRef,
    IReadOnlyList<DomainClearJsonRow> Clears, long Revision);

public sealed partial class RpgStore
{
    /// <summary>D4.18 (spec-domain-catalog.md §1, §5) — the three domain tables. `dungeon_domain` /
    /// `dungeon_domain_pool` land here so the schema exists ahead of the import write arm that targets
    /// them (`ImportDungeonDomains` below, D4.16, built 2026-09-07); `rpg_domain_progress` is this
    /// table's own, fully wired below. Called from
    /// <c>EnsureWorldSchemaUnlocked</c> right after <c>EnsureDelveSchemaUnlocked</c> — a domain is what
    /// a delve is entered into, the same "schema setup lives beside the sibling it composes with"
    /// reasoning <see cref="EnsureDelveSchemaUnlocked"/>'s own doc comment states.</summary>
    void EnsureDomainsSchemaUnlocked(SqliteConnection db)
    {
        Exec(db, """
            CREATE TABLE IF NOT EXISTS dungeon_domain (
              domain_id TEXT PRIMARY KEY,
              name TEXT NOT NULL, flavor TEXT NOT NULL,
              theme TEXT NOT NULL, climate TEXT NOT NULL, danger_band TEXT NOT NULL,
              entry TEXT NOT NULL,
              layout_template_id TEXT NOT NULL, boss_species_ref TEXT NOT NULL, retinue_family TEXT,
              entrance_hint TEXT NOT NULL,
              permadeath_from_rung TEXT,
              first_clear_ref TEXT,
              provenance_json TEXT NOT NULL, validated_json TEXT NOT NULL,
              revision INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS dungeon_domain_pool (
              domain_id TEXT NOT NULL, pool TEXT NOT NULL,
              seq INTEGER NOT NULL, key TEXT NOT NULL DEFAULT '', ref_id TEXT NOT NULL,
              PRIMARY KEY (domain_id, pool, seq)
            );

            CREATE TABLE IF NOT EXISTS rpg_domain_progress (
              player_id INTEGER NOT NULL, domain_id TEXT NOT NULL,
              found_via TEXT NOT NULL, found_ref TEXT NOT NULL,
              clears_json TEXT NOT NULL DEFAULT '[]',
              revision INTEGER NOT NULL DEFAULT 0,
              PRIMARY KEY (player_id, domain_id)
            );
            """);

        // party-dungeon F13 (2026-09-22): `first_clear_ref` is a column of the CREATE TABLE above, but
        // `CREATE TABLE IF NOT EXISTS` is a no-op against an existing `dungeon_domain` — so an install
        // whose table predates this column never gains it, while `ReadDomains` selects it, and every
        // domain read 500s with `no such column: first_clear_ref` (measured against the owner's real
        // 546 MB rpg-hot.sqlite: 15 columns, and the column is absent from EVERY table in the file).
        // Registered here so boot widens an existing table additively — the same shape
        // `RpgStore.Actions.cs` uses for its eleven pre-existing-`rpg_action` columns. NULL means "no
        // first-clear ref authored", which is what every pre-existing row already reads.
        EnsureColumn(db, "dungeon_domain", "first_clear_ref", "TEXT");
    }

    /// <summary>D4.18 (spec §5b) — first discovery only: `found_via`/`found_ref` are set once and never
    /// overwritten (`ON CONFLICT DO NOTHING`, the `item_first_clear` idiom applied to an upsert instead
    /// of a plain insert-if-absent, since this row also carries `clears_json` for the SAME primary key).
    /// Public, self-contained transaction — no real caller composes this into a larger one yet
    /// (`DomainDiscovery`/`ApplyExpeditionRewards` wiring is D4.20's own task).</summary>
    public void RecordFound(long playerId, string domainId, string foundVia, string foundRef)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();
            RecordFoundUnlocked(db, tx, playerId, domainId, foundVia, foundRef);
            tx.Commit();
        }
    }

    /// <summary>The tx-scoped primitive spec §5b cites — `internal` (this project's own
    /// `InternalsVisibleTo("FusionRpg.Data.Tests")`, the same test-only seam
    /// <c>RpgStore.WorldGraphDiff.cs</c> already documents) so it is directly testable ahead of having
    /// a real composing caller, and so `DomainDiscovery`'s own transaction (D4.20) can call it without
    /// opening a second one.</summary>
    internal static void RecordFoundUnlocked(SqliteConnection db, SqliteTransaction tx, long playerId, string domainId, string foundVia, string foundRef)
    {
        using var cmd = Prepared(db, tx,
            """
            INSERT INTO rpg_domain_progress (player_id, domain_id, found_via, found_ref, clears_json, revision)
            VALUES ($p, $d, $fv, $fr, '[]', 0)
            ON CONFLICT(player_id, domain_id) DO NOTHING;
            """,
            "$p", "$d", "$fv", "$fr");
        ExecuteWith(cmd, playerId, domainId, foundVia, foundRef);
    }

    /// <summary>D4.18 (spec §5a) — appends to `clears_json` iff no entry already carries that
    /// `rungId` — "exactly once per (player, domain, rung)" even under a replayed caller. Public,
    /// self-contained transaction; see <see cref="RecordFound"/>'s own doc comment for why a public
    /// wrapper exists ahead of a real composing caller.</summary>
    public void RecordDomainClear(long playerId, string domainId, string rungIdOrTailLabel, bool oath, long delveId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();
            RecordDomainClearUnlocked(db, tx, playerId, domainId, rungIdOrTailLabel, oath, delveId);
            tx.Commit();
        }
    }

    /// <summary>The tx-scoped primitive spec §5a cites verbatim: "Inside `RpgStore.Delve.CloseDelve`
    /// — the writer every sibling names — ... `RecordDomainClearUnlocked(db, tx, playerId, domainId,
    /// rungIdOrTailLabel, oath, delveId)` appends to `clears_json` iff no entry carries that `rungId`."
    /// Wiring this INTO `CloseDelve`'s own already-open transaction is D4.20/D4.21's task, not this
    /// one's (this file's own Files line names only `DomainStaleness.cs` and this file) — a domain
    /// must be found before it can be entered (`DelveStart.Run`'s own `domain.not-found` refusal,
    /// D4.21), so a missing progress row here is unreachable in production and is left as a silent
    /// no-op rather than a defensive throw for a caller this module does not yet have.</summary>
    internal static void RecordDomainClearUnlocked(SqliteConnection db, SqliteTransaction tx, long playerId, string domainId, string rungIdOrTailLabel, bool oath, long delveId)
    {
        var current = ReadDomainProgressClearsJsonUnlocked(db, tx, playerId, domainId);
        if (current is null) return;

        var clears = JsonSerializer.Deserialize<List<DomainClearJsonRow>>(current) ?? new List<DomainClearJsonRow>();
        if (clears.Any(c => string.Equals(c.RungId, rungIdOrTailLabel, StringComparison.Ordinal))) return;

        clears.Add(new DomainClearJsonRow(rungIdOrTailLabel, oath, delveId));
        var json = JsonSerializer.Serialize(clears);
        using var cmd = Prepared(db, tx,
            "UPDATE rpg_domain_progress SET clears_json = $j, revision = revision + 1 WHERE player_id = $p AND domain_id = $d;",
            "$j", "$p", "$d");
        ExecuteWith(cmd, json, playerId, domainId);
    }

    static string? ReadDomainProgressClearsJsonUnlocked(SqliteConnection db, SqliteTransaction tx, long playerId, string domainId)
    {
        using var cmd = Prepared(db, tx, "SELECT clears_json FROM rpg_domain_progress WHERE player_id = $p AND domain_id = $d;", "$p", "$d");
        cmd.Parameters["$p"].Value = playerId;
        cmd.Parameters["$d"].Value = domainId;
        using var r = cmd.ExecuteReader();
        return r.Read() ? r.GetString(0) : null;
    }

    /// <summary>One player's one domain-progress row, or null if never found — read-back for
    /// <c>DomainOffers.For</c> (D4.19's own first parameter).</summary>
    public DomainProgressRow? ReadDomainProgress(long playerId, string domainId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT found_via, found_ref, clears_json, revision FROM rpg_domain_progress WHERE player_id = $p AND domain_id = $d;";
            cmd.Parameters.AddWithValue("$p", playerId);
            cmd.Parameters.AddWithValue("$d", domainId);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            var clears = JsonSerializer.Deserialize<List<DomainClearJsonRow>>(r.GetString(2)) ?? new List<DomainClearJsonRow>();
            return new DomainProgressRow(playerId, domainId, r.GetString(0), r.GetString(1), clears, r.GetInt64(3));
        }
    }

    /// <summary>Every domain this player has found, ordinal by `domainId` — <c>DomainOffers.For</c>'s
    /// own "per **found** domain, in `domainId` ordinal order" (spec §4).</summary>
    public IReadOnlyList<DomainProgressRow> ReadDomainProgressForPlayer(long playerId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT domain_id, found_via, found_ref, clears_json, revision FROM rpg_domain_progress WHERE player_id = $p ORDER BY domain_id;";
            cmd.Parameters.AddWithValue("$p", playerId);
            using var r = cmd.ExecuteReader();
            var rows = new List<DomainProgressRow>();
            while (r.Read())
            {
                var clears = JsonSerializer.Deserialize<List<DomainClearJsonRow>>(r.GetString(3)) ?? new List<DomainClearJsonRow>();
                rows.Add(new DomainProgressRow(playerId, r.GetString(0), r.GetString(1), r.GetString(2), clears, r.GetInt64(4)));
            }
            return rows;
        }
    }

    /// <summary>
    /// D4.16's own SQL-import arm (spec-domain-catalog.md §1-§3), built 2026-09-07 — the seed content
    /// does NOT fit `AtomSeedFile`'s `{kind,entries}` batch format every other kind uses (a domain
    /// anchor is one raw, unwrapped JSON object per file, the same shape every other dungeon kind
    /// already uses its own direct-file reader for), so this is a dedicated writer, not a tenth
    /// `SeedContent` case, matching this module's own spec "Structure" section (`RpgStore.Domains.cs`,
    /// not `RpgStore.Import.cs`).
    ///
    /// <para><b>Validate-first, one transaction, all-or-nothing</b> — spec §1's own "one transaction
    /// that refuses before it writes": every domain in <paramref name="domains"/> is checked through
    /// the REAL <see cref="DomainPreflight.Run"/> chain (whatever bridges the caller has composed into
    /// <paramref name="preflightInputs"/> — this function validates nothing itself, by construction);
    /// ANY refusal anywhere refuses the WHOLE batch, nothing is written, matching `ImportContent`'s own
    /// established policy for every other content kind.</para>
    ///
    /// <para><b>Pools are delete-then-insert</b> (`dungeon_domain_pool`, keyed `(domain_id, pool, seq)`)
    /// — the same "recompute this row's own child rows whole" idiom `RpgStore.Import.cs`'s own element
    /// matrix write already uses, never a diff. `pool='loot'` rows use `key`=room kind, `ref_id`=table
    /// id (spec §1's own schema comment); `room`/`quest` rows leave `key` at its default empty string.</para>
    /// </summary>
    public DomainImportOutcome ImportDungeonDomains(
        IReadOnlyList<DomainRow> domains,
        DomainPreflightInputs preflightInputs,
        IReadOnlyDictionary<string, IReadOnlyList<string>> roomPaletteByDomainId,
        IReadOnlyDictionary<string, IReadOnlyList<string>> questPoolByDomainId,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> lootBindingByDomainId,
        IReadOnlyDictionary<string, string> provenanceJsonByDomainId,
        int preflightSampleSeeds,
        bool dryRun = false)
    {
        if (domains is null) throw new ArgumentNullException(nameof(domains));
        if (preflightInputs is null) throw new ArgumentNullException(nameof(preflightInputs));
        if (roomPaletteByDomainId is null) throw new ArgumentNullException(nameof(roomPaletteByDomainId));
        if (questPoolByDomainId is null) throw new ArgumentNullException(nameof(questPoolByDomainId));
        if (lootBindingByDomainId is null) throw new ArgumentNullException(nameof(lootBindingByDomainId));
        if (provenanceJsonByDomainId is null) throw new ArgumentNullException(nameof(provenanceJsonByDomainId));

        lock (_gate)
        {
            var refusals = DomainPreflight.Run(domains, preflightInputs);
            if (refusals.Count > 0)
                return new DomainImportOutcome(false, refusals, GetCatalogRevision());

            var catalogRevision = GetCatalogRevision();

            using (var db = OpenUnlocked())
            using (var tx = db.BeginTransaction())
            {
                foreach (var domain in domains)
                {
                    var validatedJson = JsonSerializer.Serialize(new DomainValidatedFacts(catalogRevision, preflightSampleSeeds));
                    var provenanceJson = provenanceJsonByDomainId.TryGetValue(domain.DomainId, out var p) ? p : DomainSeedFile.EmptyProvenanceJson;

                    using (var cmd = Prepared(db, tx, """
                        INSERT INTO dungeon_domain (domain_id, name, flavor, theme, climate, danger_band, entry,
                          layout_template_id, boss_species_ref, retinue_family, entrance_hint, permadeath_from_rung,
                          first_clear_ref, provenance_json, validated_json, revision)
                        VALUES ($id, $name, $flavor, $theme, $climate, $band, $entry, $layout, $boss, $retinue,
                          $entrance, $permadeath, $fcr, $prov, $valid, 0)
                        ON CONFLICT(domain_id) DO UPDATE SET
                          name=excluded.name, flavor=excluded.flavor, theme=excluded.theme, climate=excluded.climate,
                          danger_band=excluded.danger_band, entry=excluded.entry, layout_template_id=excluded.layout_template_id,
                          boss_species_ref=excluded.boss_species_ref, retinue_family=excluded.retinue_family,
                          entrance_hint=excluded.entrance_hint, permadeath_from_rung=excluded.permadeath_from_rung,
                          first_clear_ref=excluded.first_clear_ref,
                          provenance_json=excluded.provenance_json, validated_json=excluded.validated_json,
                          revision=dungeon_domain.revision + 1;
                        """,
                        "$id", "$name", "$flavor", "$theme", "$climate", "$band", "$entry", "$layout",
                        "$boss", "$retinue", "$entrance", "$permadeath", "$fcr", "$prov", "$valid"))
                    {
                        ExecuteWith(cmd,
                            domain.DomainId, domain.Name, domain.Flavor, domain.Theme, domain.Climate,
                            domain.DangerBand, domain.Entry, domain.LayoutTemplateId, domain.BossSpeciesRef,
                            (object?)domain.RetinueFamily ?? DBNull.Value, domain.EntranceHint,
                            (object?)domain.PermadeathFromRung ?? DBNull.Value,
                            (object?)domain.FirstClearRef ?? DBNull.Value, provenanceJson, validatedJson);
                    }

                    ExecIn(db, tx, "DELETE FROM dungeon_domain_pool WHERE domain_id = $id;", ("$id", domain.DomainId));

                    if (roomPaletteByDomainId.TryGetValue(domain.DomainId, out var rooms))
                        WriteDomainPoolRowsUnlocked(db, tx, domain.DomainId, "room", rooms.Select(r => ("", r)));
                    if (questPoolByDomainId.TryGetValue(domain.DomainId, out var quests))
                        WriteDomainPoolRowsUnlocked(db, tx, domain.DomainId, "quest", quests.Select(q => ("", q)));
                    if (lootBindingByDomainId.TryGetValue(domain.DomainId, out var loot))
                        WriteDomainPoolRowsUnlocked(db, tx, domain.DomainId, "loot", loot.Select(kv => (kv.Key, kv.Value)));
                }

                if (!dryRun) tx.Commit();
            }

            return new DomainImportOutcome(!dryRun, Array.Empty<DomainRefusal>(), GetCatalogRevision());
        }
    }

    static void WriteDomainPoolRowsUnlocked(SqliteConnection db, SqliteTransaction tx, string domainId, string pool, IEnumerable<(string Key, string RefId)> rows)
    {
        var seq = 0;
        foreach (var (key, refId) in rows)
        {
            ExecIn(db, tx,
                "INSERT INTO dungeon_domain_pool (domain_id, pool, seq, key, ref_id) VALUES ($id, $pool, $seq, $key, $ref);",
                ("$id", domainId), ("$pool", pool), ("$seq", seq), ("$key", key), ("$ref", refId));
            seq++;
        }
    }

    /// <summary>D4.22 — every `dungeon_domain` row, ordinal by `domainId`. Closes D4.18's own named
    /// gap (schema-only, no read/write) now that an endpoint actually needs one; the WRITE side landed
    /// with D4.16's own `ImportDungeonDomains` above.</summary>
    public IReadOnlyList<DomainStorageRow> ReadDomains()
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT domain_id, name, flavor, theme, climate, danger_band, entry, layout_template_id,
                       boss_species_ref, retinue_family, entrance_hint, permadeath_from_rung, first_clear_ref,
                       provenance_json, validated_json, revision
                FROM dungeon_domain ORDER BY domain_id;
                """;
            using var r = cmd.ExecuteReader();
            var rows = new List<DomainStorageRow>();
            while (r.Read())
            {
                var domain = new DomainRow(
                    r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
                    r.GetString(5), r.GetString(6), r.GetString(7), r.GetString(8),
                    r.IsDBNull(9) ? null : r.GetString(9), r.GetString(10), r.IsDBNull(11) ? null : r.GetString(11),
                    r.IsDBNull(12) ? null : r.GetString(12));
                rows.Add(new DomainStorageRow(domain, r.GetString(13), r.GetString(14), r.GetInt64(15)));
            }
            return rows;
        }
    }

    /// <summary>The read side of `ImportDungeonDomains`'s own `dungeon_domain_pool` write (D4.16) —
    /// genuinely missing until now (confirmed by `grep`: the table had a schema and a writer, no
    /// reader anywhere). `key` is `""` for `room`/`quest` rows (their own `ref_id` alone is the whole
    /// fact) and the room kind for `loot` rows (spec §1's own schema comment) — returned verbatim,
    /// ordinal by `seq`, never re-sorted, since `seq` is itself the authored/import order.</summary>
    public IReadOnlyList<(string Key, string RefId)> ReadDomainPool(string domainId, string pool)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT key, ref_id FROM dungeon_domain_pool WHERE domain_id = $id AND pool = $pool ORDER BY seq;";
            cmd.Parameters.AddWithValue("$id", domainId);
            cmd.Parameters.AddWithValue("$pool", pool);
            using var r = cmd.ExecuteReader();
            var rows = new List<(string, string)>();
            while (r.Read()) rows.Add((r.GetString(0), r.GetString(1)));
            return rows;
        }
    }

    /// <summary>`ReadDomainPool(domainId, "loot")` reshaped into the `(room kind -&gt; drop_table id)`
    /// map `Quests.QuestReward.Request`/row 9's own `LootBindingFor` both need — the one pool kind
    /// whose `key` column is load-bearing rather than blank.</summary>
    public IReadOnlyDictionary<string, string> ReadLootBinding(string domainId)
    {
        var binding = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, refId) in ReadDomainPool(domainId, "loot"))
            binding[key] = refId;
        return binding;
    }

    /// <summary>D3.15's own bank-at-clear hook (<c>RpgStore.Delve.ApplyBossFirstClearGrantUnlocked</c>)
    /// needs "which container" resolved INSIDE its own <c>RecordClear</c> transaction — <see
    /// cref="ReadDomains"/> is whole-table and self-locked (a second connection this hook must not open
    /// mid-write), so this is the single-row, tx-scoped sibling, mirroring
    /// <c>RecordedLootManifestUnlocked</c>'s identical "same query, caller's own tx" shape
    /// (`RpgStore.Delve.cs`). Returns <c>null</c> both when the domain row itself is missing and when it
    /// has none authored — <see cref="DomainRow.FirstClearRef"/>'s own doc comment: "`null` is a
    /// legitimate, expected value... not a content gap to fill in later."</summary>
    internal static string? ReadDomainFirstClearRefUnlocked(SqliteConnection db, SqliteTransaction tx, string domainId)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT first_clear_ref FROM dungeon_domain WHERE domain_id = $id;";
        cmd.Parameters.AddWithValue("$id", domainId);
        return cmd.ExecuteScalar() is string s ? s : null;
    }
}
