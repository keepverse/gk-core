using FusionRpg.Core.Delve.Domains;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using Xunit;

namespace FusionRpg.Data.Tests.Delve.Domains;

/// <summary>
/// D4.16 (spec-domain-catalog.md §1-§3), built 2026-09-07 — `RpgStore.ImportDungeonDomains`'s own
/// validate-first, one-transaction, all-or-nothing write. Uses a hand-built, fully-passing
/// `DomainPreflightInputs` fixture (mirroring `DomainPreflightTests.Passing()`'s own established shape)
/// to prove the WRITER's own mechanics independently of whether real content can pass preflight today
/// (it cannot — row 6's own threat-audit gap alone already guarantees every real domain refuses,
/// D4.17's own already-documented finding) — that real-content refusal is proven separately below, as
/// the correct verdict, not a defect to work around.
/// </summary>
public class DomainImportTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public DomainImportTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose() => _testStore.Dispose();

    static DomainRow Domain(string id = "domain.test-001") =>
        new(id, "Test Domain", "A test flavor.", "theme.overgrown", "fire", "shallow", "many",
            "layout.standard", "species.warden", null, "Lair", null);

    static DomainPreflightInputs Passing() => new(
        DangerBandOrdinals: new Dictionary<string, int>(StringComparer.Ordinal) { ["shallow"] = 2 },
        KnownLayoutIds: new[] { "layout.standard" },
        KnownSpeciesIds: new[] { "species.warden" },
        ThreatBandOrdinalFor: _ => 5,
        BossFloorRungOrdinal: 3,
        CellsLayoutCanPlace: _ => Array.Empty<(string, string)>(),
        CellsPaletteFills: _ => Array.Empty<(string, string)>(),
        CheckGraphs: _ => Array.Empty<DomainRefusal>(),
        CheckObjects: _ => Array.Empty<DomainRefusal>(),
        CheckEncounters: _ => Array.Empty<DomainRefusal>(),
        CheckEvents: _ => Array.Empty<DomainRefusal>(),
        CheckQuests: _ => Array.Empty<DomainRefusal>(),
        KnownDropTableIds: new[] { "table.forest-cache" },
        BoundLootKinds: new[] { "cache" },
        LootBindingFor: _ => new Dictionary<string, string>(StringComparer.Ordinal) { ["cache"] = "table.forest-cache" },
        OfferedRungCountFor: _ => 3);

    static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> EmptyRooms = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
    static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> EmptyQuests = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
    static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> EmptyLoot = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
    static readonly IReadOnlyDictionary<string, string> EmptyProvenance = new Dictionary<string, string>(StringComparer.Ordinal);

    [Fact]
    public void ImportDungeonDomains_null_arguments_throw()
    {
        var domains = new[] { Domain() };
        var inputs = Passing();
        Assert.Throws<ArgumentNullException>(() => _store.ImportDungeonDomains(null!, inputs, EmptyRooms, EmptyQuests, EmptyLoot, EmptyProvenance, 32));
        Assert.Throws<ArgumentNullException>(() => _store.ImportDungeonDomains(domains, null!, EmptyRooms, EmptyQuests, EmptyLoot, EmptyProvenance, 32));
        Assert.Throws<ArgumentNullException>(() => _store.ImportDungeonDomains(domains, inputs, null!, EmptyQuests, EmptyLoot, EmptyProvenance, 32));
        Assert.Throws<ArgumentNullException>(() => _store.ImportDungeonDomains(domains, inputs, EmptyRooms, null!, EmptyLoot, EmptyProvenance, 32));
        Assert.Throws<ArgumentNullException>(() => _store.ImportDungeonDomains(domains, inputs, EmptyRooms, EmptyQuests, null!, EmptyProvenance, 32));
        Assert.Throws<ArgumentNullException>(() => _store.ImportDungeonDomains(domains, inputs, EmptyRooms, EmptyQuests, EmptyLoot, null!, 32));
    }

    [Fact]
    public void A_fully_passing_domain_writes_the_domain_row_and_its_three_pools()
    {
        var domain = Domain();
        var rooms = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { [domain.DomainId] = new[] { "room.a", "room.b" } };
        var quests = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { [domain.DomainId] = new[] { "quest.a" } };
        var loot = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
            { [domain.DomainId] = new Dictionary<string, string>(StringComparer.Ordinal) { ["cache"] = "table.forest-cache" } };

        var outcome = _store.ImportDungeonDomains(new[] { domain }, Passing(), rooms, quests, loot, EmptyProvenance, 32);

        Assert.True(outcome.IsOk, string.Join(";", outcome.Refusals));
        Assert.True(outcome.Committed);

        var stored = _store.ReadDomains();
        Assert.Single(stored);
        Assert.Equal(domain.DomainId, stored[0].Domain.DomainId);
        Assert.Equal(domain.Name, stored[0].Domain.Name);
        Assert.Equal(0, stored[0].Revision);
        Assert.Equal(DomainSeedFile.EmptyProvenanceJson, stored[0].ProvenanceJson);
        Assert.Contains("\"CatalogRevision\"", stored[0].ValidatedJson);

        AssertPool(domain.DomainId, "room", ("", "room.a"), ("", "room.b"));
        AssertPool(domain.DomainId, "quest", ("", "quest.a"));
        AssertPool(domain.DomainId, "loot", ("cache", "table.forest-cache"));

        // The real public read surface (D4.16's own missing counterpart, built same window as this
        // test) -- what a real caller (e.g. CloseDelve's own quest-reward banking) would actually use.
        Assert.Equal(new[] { "room.a", "room.b" }, _store.ReadDomainPool(domain.DomainId, "room").Select(r => r.RefId));
        Assert.Equal(new[] { "quest.a" }, _store.ReadDomainPool(domain.DomainId, "quest").Select(r => r.RefId));
        var lootBinding = _store.ReadLootBinding(domain.DomainId);
        Assert.Equal("table.forest-cache", lootBinding["cache"]);
    }

    [Fact]
    public void ReadDomainPool_an_unknown_domain_or_pool_returns_empty_never_throws()
    {
        Assert.Empty(_store.ReadDomainPool("domain.does-not-exist", "room"));
        Assert.Empty(_store.ReadLootBinding("domain.does-not-exist"));
    }

    [Fact]
    public void A_refusing_domain_writes_nothing_at_all()
    {
        var domain = Domain(); // Passing() fixture, but we corrupt ONE field to force a refusal
        var badDomain = domain with { LayoutTemplateId = "layout.not-known" };

        var outcome = _store.ImportDungeonDomains(new[] { badDomain }, Passing(), EmptyRooms, EmptyQuests, EmptyLoot, EmptyProvenance, 32);

        Assert.False(outcome.IsOk);
        Assert.False(outcome.Committed);
        Assert.Single(outcome.Refusals);
        Assert.Equal("domain.ref-missing", outcome.Refusals[0].Rule);
        Assert.Empty(_store.ReadDomains());
    }

    /// <summary>One bad domain in a batch refuses the WHOLE batch — spec §1's own "one transaction that
    /// refuses before it writes," matching `ImportContent`'s own established all-or-nothing policy for
    /// every other content kind.</summary>
    [Fact]
    public void One_bad_domain_in_a_batch_refuses_the_whole_batch_not_just_itself()
    {
        var good = Domain("domain.good-001");
        var bad = Domain("domain.bad-001") with { BossSpeciesRef = "species.not-known" };

        var outcome = _store.ImportDungeonDomains(new[] { good, bad }, Passing(), EmptyRooms, EmptyQuests, EmptyLoot, EmptyProvenance, 32);

        Assert.False(outcome.IsOk);
        Assert.Empty(_store.ReadDomains());
    }

    [Fact]
    public void Re_importing_the_same_domain_upserts_never_duplicates_and_bumps_revision()
    {
        var domain = Domain();
        _store.ImportDungeonDomains(new[] { domain }, Passing(), EmptyRooms, EmptyQuests, EmptyLoot, EmptyProvenance, 32);
        var second = domain with { Name = "Renamed" };
        var outcome = _store.ImportDungeonDomains(new[] { second }, Passing(), EmptyRooms, EmptyQuests, EmptyLoot, EmptyProvenance, 32);

        Assert.True(outcome.IsOk);
        var stored = _store.ReadDomains();
        Assert.Single(stored);
        Assert.Equal("Renamed", stored[0].Domain.Name);
        Assert.Equal(1, stored[0].Revision);
    }

    /// <summary>Pools are delete-then-insert, never accumulated — a domain whose room palette shrank
    /// between two imports must not leave orphaned rows from the first import.</summary>
    [Fact]
    public void Re_importing_with_a_different_pool_replaces_it_whole_never_accumulates()
    {
        var domain = Domain();
        var firstRooms = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { [domain.DomainId] = new[] { "room.a", "room.b", "room.c" } };
        _store.ImportDungeonDomains(new[] { domain }, Passing(), firstRooms, EmptyQuests, EmptyLoot, EmptyProvenance, 32);

        var secondRooms = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { [domain.DomainId] = new[] { "room.x" } };
        _store.ImportDungeonDomains(new[] { domain }, Passing(), secondRooms, EmptyQuests, EmptyLoot, EmptyProvenance, 32);

        AssertPool(domain.DomainId, "room", ("", "room.x"));
    }

    [Fact]
    public void DryRun_writes_nothing_but_still_reports_ok()
    {
        var domain = Domain();
        var outcome = _store.ImportDungeonDomains(new[] { domain }, Passing(), EmptyRooms, EmptyQuests, EmptyLoot, EmptyProvenance, 32, dryRun: true);

        Assert.True(outcome.IsOk);
        Assert.False(outcome.Committed);
        Assert.Empty(_store.ReadDomains());
    }

    /// <summary>A `_provenance`-carrying caller value is stored verbatim, never overridden by the
    /// empty-placeholder default.</summary>
    [Fact]
    public void A_supplied_provenance_json_is_stored_verbatim_not_replaced_by_the_placeholder()
    {
        var domain = Domain();
        var provenance = new Dictionary<string, string>(StringComparer.Ordinal) { [domain.DomainId] = "{\"planHash\":\"abc123\"}" };
        _store.ImportDungeonDomains(new[] { domain }, Passing(), EmptyRooms, EmptyQuests, EmptyLoot, provenance, 32);

        Assert.Equal("{\"planHash\":\"abc123\"}", _store.ReadDomains()[0].ProvenanceJson);
    }

    /// <summary>D3.15's own bank-at-clear "which container" resolution (party-dungeon-todo.md,
    /// 2026-09-07): `FirstClearRef` is nullable and `null` is the EXPECTED value for a domain with no
    /// relic authored yet (D4.28's own finding: only 4/144 unique anchors are buildable today) — proven
    /// here as a real, positive case, not just an absent-field default.</summary>
    [Fact]
    public void FirstClearRef_defaults_to_null_and_round_trips_when_absent()
    {
        var domain = Domain(); // Domain()'s own 13th positional arg is omitted -- defaults to null
        _store.ImportDungeonDomains(new[] { domain }, Passing(), EmptyRooms, EmptyQuests, EmptyLoot, EmptyProvenance, 32);

        Assert.Null(_store.ReadDomains()[0].Domain.FirstClearRef);
    }

    [Fact]
    public void FirstClearRef_round_trips_a_real_container_id_and_survives_reimport()
    {
        var domain = Domain() with { FirstClearRef = "item.test-unique" };
        _store.ImportDungeonDomains(new[] { domain }, Passing(), EmptyRooms, EmptyQuests, EmptyLoot, EmptyProvenance, 32);

        Assert.Equal("item.test-unique", _store.ReadDomains()[0].Domain.FirstClearRef);

        // Re-import with the field cleared -- the column must follow, not stick to its old value
        // (`ON CONFLICT DO UPDATE` must name every column, first_clear_ref included).
        _store.ImportDungeonDomains(new[] { domain with { FirstClearRef = null } }, Passing(),
            EmptyRooms, EmptyQuests, EmptyLoot, EmptyProvenance, 32);

        Assert.Null(_store.ReadDomains()[0].Domain.FirstClearRef);
    }

    /// <summary>
    /// F13/F14 (party-dungeon, 2026-09-22) — the upgrade path `CREATE TABLE IF NOT EXISTS` cannot do.
    /// `first_clear_ref` sits in `EnsureDomainsSchemaUnlocked`'s own `CREATE TABLE`, but that statement
    /// is a no-op against a table that already exists, so an install written before the column never
    /// gained it while `ReadDomains` selects it — every domain read answered
    /// `no such column: first_clear_ref` (measured against the owner's real 546 MB `rpg-hot.sqlite`:
    /// 15 columns, the column absent from every table in the file). `EnsureColumn` registration is the
    /// fix; this is the test that keeps it from regressing silently, because the pre-fix shape is a
    /// database that boots fine and only fails on the first read.
    ///
    /// <para>Contract, not a column count: the column is part of `dungeon_domain` after the real
    /// `Init()` schema path, and the production reader/writer round-trip through it. The store is
    /// memory-backed (`CreateWithPreInitHot`), so nothing touches the disk.</para>
    /// </summary>
    [Fact]
    public void A_pre_F13_database_gains_first_clear_ref_at_boot_and_reads_and_writes_through_it()
    {
        // The exact pre-F13 `dungeon_domain` shape, plus one row written before Init runs.
        using var legacy = DataTestStore.CreateWithPreInitHot(seed =>
        {
            using var ddl = seed.CreateCommand();
            ddl.CommandText = """
                CREATE TABLE dungeon_domain (
                  domain_id TEXT PRIMARY KEY,
                  name TEXT NOT NULL, flavor TEXT NOT NULL,
                  theme TEXT NOT NULL, climate TEXT NOT NULL, danger_band TEXT NOT NULL,
                  entry TEXT NOT NULL,
                  layout_template_id TEXT NOT NULL, boss_species_ref TEXT NOT NULL, retinue_family TEXT,
                  entrance_hint TEXT NOT NULL,
                  permadeath_from_rung TEXT,
                  provenance_json TEXT NOT NULL, validated_json TEXT NOT NULL,
                  revision INTEGER NOT NULL DEFAULT 0
                );
                INSERT INTO dungeon_domain (domain_id, name, flavor, theme, climate, danger_band, entry,
                  layout_template_id, boss_species_ref, retinue_family, entrance_hint, permadeath_from_rung,
                  provenance_json, validated_json, revision)
                VALUES ('domain.legacy-001', 'Legacy Domain', 'A legacy flavor.', 'theme.overgrown', 'fire',
                  'shallow', 'many', 'layout.standard', 'species.warden', NULL, 'Lair', NULL, '{}', '{}', 0);
                """;
            ddl.ExecuteNonQuery();
        });
        var store = legacy.Store;

        // Init ran EnsureDomainsSchemaUnlocked: its CREATE TABLE IF NOT EXISTS was a no-op against the
        // table above, so only the EnsureColumn registration can have widened it.
        var columns = new List<string>();
        using (var db = SqliteConnectionFactory.Open(store.HotPath))
        using (var info = db.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(dungeon_domain);";
            using var r = info.ExecuteReader();
            while (r.Read()) columns.Add(r.GetString(1));
        }
        Assert.Contains("first_clear_ref", columns);

        // The production reader whose SELECT list named the missing column reads the pre-existing row.
        var stored = Assert.Single(store.ReadDomains());
        Assert.Equal("domain.legacy-001", stored.Domain.DomainId);
        Assert.Null(stored.Domain.FirstClearRef);

        // And the production writer round-trips a real value through the column.
        var outcome = store.ImportDungeonDomains(
            new[] { Domain("domain.legacy-001") with { FirstClearRef = "item.test-unique" } },
            Passing(), EmptyRooms, EmptyQuests, EmptyLoot, EmptyProvenance, 32);
        Assert.True(outcome.IsOk, string.Join(";", outcome.Refusals));
        Assert.Equal("item.test-unique", Assert.Single(store.ReadDomains()).Domain.FirstClearRef);
    }

    void AssertPool(string domainId, string pool, params (string Key, string RefId)[] expected)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT key, ref_id FROM dungeon_domain_pool WHERE domain_id = $id AND pool = $pool ORDER BY seq;";
        cmd.Parameters.AddWithValue("$id", domainId);
        cmd.Parameters.AddWithValue("$pool", pool);
        using var r = cmd.ExecuteReader();
        var actual = new List<(string, string)>();
        while (r.Read()) actual.Add((r.GetString(0), r.GetString(1)));
        Assert.Equal(expected, actual);
    }
}
