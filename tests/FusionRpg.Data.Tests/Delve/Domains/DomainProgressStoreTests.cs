using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using Xunit;

namespace FusionRpg.Data.Tests.Delve.Domains;

/// <summary>D4.18 (spec-domain-catalog.md §5) — the three domain tables landing, and
/// `RecordDomainClearUnlocked` / `RecordFoundUnlocked` as tx-scoped writers. Discovery is first-wins;
/// a clear is exactly-once per `(player, domain, rung)`; a clear before discovery is a no-op (the
/// domain was never found, matching `DelveStart.Run`'s own future `domain.not-found` refusal, D4.21 —
/// unreachable in production, reachable and asserted here because nothing calls these yet).</summary>
public class DomainProgressStoreTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public DomainProgressStoreTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose() => _testStore.Dispose();

    // ---- the three tables land -------------------------------------------------------------------

    [Fact]
    public void Init_creates_all_three_domain_tables()
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name IN ('dungeon_domain', 'dungeon_domain_pool', 'rpg_domain_progress');";
        using var r = cmd.ExecuteReader();
        var names = new List<string>();
        while (r.Read()) names.Add(r.GetString(0));

        Assert.Equal(3, names.Count);
    }

    // ---- discovery: first wins ------------------------------------------------------------------

    [Fact]
    public void RecordFound_creates_a_progress_row_with_no_clears()
    {
        _store.RecordFound(1, "domain.forest-shallow-001", "expedition", "exp-1");

        var row = _store.ReadDomainProgress(1, "domain.forest-shallow-001");

        Assert.NotNull(row);
        Assert.Equal("expedition", row!.FoundVia);
        Assert.Equal("exp-1", row.FoundRef);
        Assert.Empty(row.Clears);
    }

    [Fact]
    public void An_unfound_domain_reads_back_null()
    {
        Assert.Null(_store.ReadDomainProgress(1, "domain.never-found"));
    }

    [Fact]
    public void RecordFound_called_twice_keeps_the_first_found_via_and_ref()
    {
        _store.RecordFound(1, "domain.forest-shallow-001", "expedition", "exp-1");
        _store.RecordFound(1, "domain.forest-shallow-001", "clear", "clear-of-band-1");

        var row = _store.ReadDomainProgress(1, "domain.forest-shallow-001");

        Assert.Equal("expedition", row!.FoundVia);
        Assert.Equal("exp-1", row.FoundRef);
    }

    [Fact]
    public void Two_players_finding_the_same_domain_get_independent_rows()
    {
        _store.RecordFound(1, "domain.forest-shallow-001", "expedition", "exp-1");
        _store.RecordFound(2, "domain.forest-shallow-001", "debug", "");

        Assert.Equal("expedition", _store.ReadDomainProgress(1, "domain.forest-shallow-001")!.FoundVia);
        Assert.Equal("debug", _store.ReadDomainProgress(2, "domain.forest-shallow-001")!.FoundVia);
    }

    // ---- clears: exactly once per (player, domain, rung) ----------------------------------------

    [Fact]
    public void RecordDomainClear_on_a_found_domain_appends_a_clear()
    {
        _store.RecordFound(1, "domain.forest-shallow-001", "expedition", "exp-1");

        _store.RecordDomainClear(1, "domain.forest-shallow-001", "r4", oath: false, delveId: 42);

        var row = _store.ReadDomainProgress(1, "domain.forest-shallow-001");
        var clear = Assert.Single(row!.Clears);
        Assert.Equal("r4", clear.RungId);
        Assert.False(clear.Oath);
        Assert.Equal(42, clear.DelveId);
    }

    [Fact]
    public void RecordDomainClear_replayed_for_the_same_rung_appends_nothing()
    {
        _store.RecordFound(1, "domain.forest-shallow-001", "expedition", "exp-1");
        _store.RecordDomainClear(1, "domain.forest-shallow-001", "r4", oath: false, delveId: 42);

        _store.RecordDomainClear(1, "domain.forest-shallow-001", "r4", oath: false, delveId: 999);

        var row = _store.ReadDomainProgress(1, "domain.forest-shallow-001");
        var clear = Assert.Single(row!.Clears);
        Assert.Equal(42, clear.DelveId); // the replay's delveId (999) never overwrote the first clear
    }

    [Fact]
    public void RecordDomainClear_for_a_different_rung_adds_a_second_entry()
    {
        _store.RecordFound(1, "domain.forest-shallow-001", "expedition", "exp-1");
        _store.RecordDomainClear(1, "domain.forest-shallow-001", "r4", oath: false, delveId: 42);

        _store.RecordDomainClear(1, "domain.forest-shallow-001", "r6", oath: true, delveId: 43);

        var row = _store.ReadDomainProgress(1, "domain.forest-shallow-001");
        Assert.Equal(2, row!.Clears.Count);
        Assert.Contains(row.Clears, c => c.RungId == "r4" && !c.Oath);
        Assert.Contains(row.Clears, c => c.RungId == "r6" && c.Oath);
    }

    [Fact]
    public void RecordDomainClear_before_discovery_is_a_silent_no_op()
    {
        _store.RecordDomainClear(1, "domain.never-found", "r4", oath: false, delveId: 42);

        Assert.Null(_store.ReadDomainProgress(1, "domain.never-found"));
    }

    [Fact]
    public void RecordDomainClear_bumps_revision()
    {
        _store.RecordFound(1, "domain.forest-shallow-001", "expedition", "exp-1");

        _store.RecordDomainClear(1, "domain.forest-shallow-001", "r4", oath: false, delveId: 42);

        Assert.Equal(1, _store.ReadDomainProgress(1, "domain.forest-shallow-001")!.Revision);
    }

    // ---- reads for a whole player -----------------------------------------------------------------

    [Fact]
    public void ReadDomainProgressForPlayer_returns_every_found_domain_in_ordinal_order()
    {
        _store.RecordFound(1, "domain.z-last", "expedition", "exp-1");
        _store.RecordFound(1, "domain.a-first", "expedition", "exp-2");
        _store.RecordFound(2, "domain.other-player", "expedition", "exp-3");

        var rows = _store.ReadDomainProgressForPlayer(1);

        Assert.Equal(new[] { "domain.a-first", "domain.z-last" }, rows.Select(r => r.DomainId));
    }

    [Fact]
    public void ReadDomainProgressForPlayer_is_empty_for_a_player_who_found_nothing()
    {
        Assert.Empty(_store.ReadDomainProgressForPlayer(1));
    }

    // ---- the tx-scoped primitives really compose into one caller-owned transaction ----------------

    [Fact]
    public void The_unlocked_writers_compose_into_one_externally_owned_transaction()
    {
        // The whole reason RecordFoundUnlocked/RecordDomainClearUnlocked exist as `internal static
        // (db, tx, ...)` rather than only the public self-contained wrappers above: a future caller
        // (CloseDelve, D4.20/21) must be able to fold a discovery AND a clear into its OWN already-open
        // transaction. Proven here by opening a raw connection ourselves and committing once.
        using (var db = SqliteConnectionFactory.Open(_store.HotPath))
        using (var tx = db.BeginTransaction())
        {
            RpgStore.RecordFoundUnlocked(db, tx, 5, "domain.composed", "expedition", "exp-9");
            RpgStore.RecordDomainClearUnlocked(db, tx, 5, "domain.composed", "r4", oath: false, delveId: 7);
            tx.Commit();
        }

        var row = _store.ReadDomainProgress(5, "domain.composed");
        Assert.NotNull(row);
        Assert.Equal("expedition", row!.FoundVia);
        Assert.Single(row.Clears);
    }

    [Fact]
    public void The_unlocked_writers_never_commit_their_own_transaction()
    {
        // If either Unlocked function secretly called tx.Commit() (or opened its own connection), a
        // rollback on the caller's transaction would still leave the row behind. Proves it does not.
        using (var db = SqliteConnectionFactory.Open(_store.HotPath))
        using (var tx = db.BeginTransaction())
        {
            RpgStore.RecordFoundUnlocked(db, tx, 6, "domain.rolled-back", "expedition", "exp-10");
            tx.Rollback();
        }

        Assert.Null(_store.ReadDomainProgress(6, "domain.rolled-back"));
    }

    // ---- Reset() clears per-player progress --------------------------------------------------------

    [Fact]
    public void Reset_clears_domain_progress()
    {
        _store.RecordFound(1, "domain.forest-shallow-001", "expedition", "exp-1");

        _store.Reset();

        Assert.Null(_store.ReadDomainProgress(1, "domain.forest-shallow-001"));
    }

    // ---- D4.22: ReadDomains -- no writer exists yet (D4.16's own import arm is still unbuilt), so
    // every row here is seeded by raw SQL, proving only the READ projection, never a write path. ------

    [Fact]
    public void ReadDomains_is_empty_against_a_fresh_store()
    {
        Assert.Empty(_store.ReadDomains());
    }

    [Fact]
    public void ReadDomains_projects_a_seeded_row_back_field_for_field()
    {
        using (var db = SqliteConnectionFactory.Open(_store.HotPath))
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO dungeon_domain (domain_id, name, flavor, theme, climate, danger_band, entry,
                    layout_template_id, boss_species_ref, retinue_family, entrance_hint, permadeath_from_rung,
                    provenance_json, validated_json, revision)
                VALUES ('domain.fire-shallow-001', 'Wyrmroot Hollow', 'Something coils beneath.',
                    'theme.overgrown', 'fire', 'shallow', 'many', 'layout.standard', 'species.warden',
                    NULL, 'Lair', NULL, '{"planHash":"h1"}', '{"catalogRevision":1}', 0);
                """;
            cmd.ExecuteNonQuery();
        }

        var rows = _store.ReadDomains();

        var row = Assert.Single(rows);
        Assert.Equal("domain.fire-shallow-001", row.Domain.DomainId);
        Assert.Equal("Wyrmroot Hollow", row.Domain.Name);
        Assert.Equal("fire", row.Domain.Climate);
        Assert.Equal("many", row.Domain.Entry);
        Assert.Null(row.Domain.RetinueFamily);
        Assert.Null(row.Domain.PermadeathFromRung);
        Assert.Equal("""{"planHash":"h1"}""", row.ProvenanceJson);
        Assert.Equal("""{"catalogRevision":1}""", row.ValidatedJson);
        Assert.Equal(0, row.Revision);
    }

    [Fact]
    public void ReadDomains_orders_by_domainId_ordinal()
    {
        using (var db = SqliteConnectionFactory.Open(_store.HotPath))
        {
            foreach (var id in new[] { "domain.z", "domain.a" })
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO dungeon_domain (domain_id, name, flavor, theme, climate, danger_band, entry,
                        layout_template_id, boss_species_ref, entrance_hint, provenance_json, validated_json)
                    VALUES ($id, 'n', 'f', 't', 'fire', 'shallow', 'many', 'layout.standard', 'species.warden', 'Lair', '{}', '{}');
                    """;
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
        }

        var rows = _store.ReadDomains();

        Assert.Equal(new[] { "domain.a", "domain.z" }, rows.Select(r => r.Domain.DomainId));
    }
}
