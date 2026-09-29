using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using Xunit;

namespace FusionRpg.Data.Tests.Delve.Domains;

/// <summary>
/// F14 (party-dungeon-todo.md): F13's one-line <c>EnsureColumn</c> registration had no committed
/// regression test — lane `f13-schema`'s runner fence excluded <c>tests/**</c>, so the only proof was
/// <c>tasks/reports/f13-schema-upgrade-proof.ps1</c>, which runs only when someone remembers to run it.
///
/// <para><b>What is being pinned.</b> <c>dungeon_domain.first_clear_ref</c> sits in the
/// <c>CREATE TABLE IF NOT EXISTS dungeon_domain</c> DDL, and <c>CREATE TABLE IF NOT EXISTS</c> is a
/// no-op against a table that already exists — so an install whose table predates the column never
/// gains it, while <c>ReadDomains</c> selects it, and every domain read fails with
/// <c>no such column: first_clear_ref</c> (measured on the owner's real 546 MB <c>rpg-hot.sqlite</c>:
/// 15 columns, the column absent from every table in the file). <c>EnsureDomainsSchemaUnlocked</c>
/// registers it additively; this test builds exactly that legacy shape in memory, runs <c>Init</c>
/// over it and asserts the column and the real read path both work.</para>
///
/// <para><b>In memory, no file</b> — <c>DataTestStore.CreateWithPreInitHot</c> is the repo's own
/// "a save written by an older build" helper (<c>docs/contributing/testing-standard.md</c>: a store
/// test runs in memory; disk only when the disk is the thing under test). The seeded DDL below is the
/// real <c>CREATE TABLE</c> minus <c>first_clear_ref</c> and nothing else, so it stays honest if the
/// table grows another column later: this test only ever claims the ONE column it names.</para>
/// </summary>
public class DomainSchemaMigrationTests
{
    /// <summary>The real `dungeon_domain` shape as it shipped BEFORE F13 — the current
    /// `CREATE TABLE IF NOT EXISTS` body with `first_clear_ref` removed, copied from
    /// `RpgStore.Domains.cs`'s own DDL rather than paraphrased.</summary>
    const string LegacyDungeonDomainDdl = """
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
        """;

    static IReadOnlyList<string> ColumnsOf(RpgStore store, string table)
    {
        using var db = SqliteConnectionFactory.Open(store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        using var reader = cmd.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(1));
        return names;
    }

    /// <summary>
    /// F14's own acceptance, verbatim: "opens a database built from the OLD schema and asserts
    /// `dungeon_domain.first_clear_ref` exists after `Init`". The column assertion alone would pass if
    /// `Init` had recreated the table; the legacy row below proves it did NOT (the table was seeded
    /// before `Init`, so `CREATE TABLE IF NOT EXISTS` was a no-op and only `EnsureColumn` can have
    /// widened it), and the real `ReadDomains` call proves the actual production read path works —
    /// that read is what 500'd.
    /// </summary>
    [Fact]
    public void Init_adds_first_clear_ref_to_a_dungeon_domain_table_that_predates_it()
    {
        using var test = DataTestStore.CreateWithPreInitHot(seed =>
        {
            using var cmd = seed.CreateCommand();
            cmd.CommandText = LegacyDungeonDomainDdl;
            cmd.ExecuteNonQuery();

            // The pre-condition, asserted inside the seeder because this is the ONLY moment the
            // legacy shape is observable — `CreateWithPreInitHot` runs `Init` before it returns.
            cmd.CommandText = "PRAGMA table_info(dungeon_domain);";
            using (var reader = cmd.ExecuteReader())
            {
                var seeded = new List<string>();
                while (reader.Read()) seeded.Add(reader.GetString(1));
                Assert.DoesNotContain("first_clear_ref", seeded);
                Assert.Contains("permadeath_from_rung", seeded);
            }

            // A row written by the OLDER build. Its survival is the proof that `Init` widened the
            // existing table instead of replacing it — a recreated table would have dropped it.
            cmd.CommandText = """
                INSERT INTO dungeon_domain
                  (domain_id, name, flavor, theme, climate, danger_band, entry, layout_template_id,
                   boss_species_ref, retinue_family, entrance_hint, permadeath_from_rung,
                   provenance_json, validated_json, revision)
                VALUES ('domain.legacy-001', 'Legacy', 'f', 't', 'fire', 'shallow', 'many',
                        'layout.medium-wide-webbed-001', 'SuperTorch', NULL, 'Vault', NULL,
                        '{}', '{}', 0);
                """;
            cmd.ExecuteNonQuery();
        });

        Assert.Contains("first_clear_ref", ColumnsOf(test.Store, "dungeon_domain"));

        // The read that 500'd on the owner's real file now succeeds, and the legacy row is still there
        // with a NULL first-clear ref (the honest "none authored" reading for every pre-existing row).
        var domains = test.Store.ReadDomains();
        var legacy = Assert.Single(domains);
        Assert.Equal("domain.legacy-001", legacy.Domain.DomainId);
        Assert.Null(legacy.Domain.FirstClearRef);
    }

    /// <summary>
    /// The idempotency half: a table that ALREADY carries the column must survive a second `Init`
    /// unchanged. `EnsureColumn` is additive-only by construction, but nothing pinned that against a
    /// real table — and a "widen" that dropped or renamed a populated column is exactly the failure
    /// this row exists to prevent.
    /// </summary>
    [Fact]
    public void A_second_Init_over_a_table_that_already_carries_the_column_changes_nothing()
    {
        using var test = DataTestStore.Create();

        // The first Init already created the current shape; a Reopen runs the whole schema pass again
        // over the same storage — the "restart against an up-to-date file" case.
        using var reopened = test.Reopen();

        Assert.Contains("first_clear_ref", ColumnsOf(reopened, "dungeon_domain"));
        Assert.Empty(reopened.ReadDomains());
    }
}
