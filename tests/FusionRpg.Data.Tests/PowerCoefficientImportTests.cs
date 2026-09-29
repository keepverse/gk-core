using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Effects.Atoms.Power;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// E44 criterion 0 (spec-power-sweep.md §4.1) — the coefficient seed data path. Four modules
/// (projectile-control, entity-fields-12plus, spawn-non-grid, ui-attach-point) were blocked on this
/// exact gap: <c>power_coefficient</c> was a real, hash-covered, sweep-fed table with no seed reader
/// to put a hand-authored (or later, fitted) row into.
///
/// <para>This module is the pipe, not the content. It proves a <c>power-coefficient</c> seed file
/// loads through <see cref="AtomSeedFile"/>, imports through <see cref="RpgStore.ImportContent"/>
/// into <c>power_coefficient</c>, and the content hash moves — the same three claims
/// <c>AtomImportTests</c> already proves for atoms, reused here rather than re-derived.</para>
/// </summary>
public class PowerCoefficientImportTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public PowerCoefficientImportTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    // ---- fixtures -----------------------------------------------------------------------------

    // The spec's own §4.1 example, verbatim — the canonical shape a later session's real fitted rows
    // will take, not real content itself (fitting the sweep is separate, owner-gated work).
    const string CoefficientFile = """
        { "schemaVersion": 1, "kind": "power-coefficient", "entries": [
            { "kindId": "stat.derived", "channel": "combat.dodge.fire",
              "coeffMilli": 1000, "referenceScale": 1 } ] }
        """;

    static SeedContent Read(params (string Path, string Json)[] files)
    {
        var result = AtomSeedFile.Collect(files);
        Assert.True(result.IsOk, string.Join("; ", result.Errors));
        return result.Content;
    }

    // ---- the happy path -------------------------------------------------------------------------

    [Fact]
    public void A_coefficient_seed_file_imports_and_reads_back_through_GetPowerTables()
    {
        var outcome = _store.ImportContent(Read(("p.json", CoefficientFile)));

        Assert.True(outcome.Committed, string.Join("; ", outcome.Errors));
        Assert.Equal(1, outcome.Coefficients);
        Assert.Equal(1, outcome.RowsChanged);

        var row = _store.GetPowerTables().Find("stat.derived", "combat.dodge.fire");
        Assert.NotNull(row);
        Assert.Equal(1000, row!.CoeffMilli);
        Assert.Equal(1, row.ReferenceScale);
    }

    [Fact]
    public void The_content_hash_moves_once_a_coefficient_is_imported()
    {
        var before = _store.ComputeContentHash().Hash;

        var outcome = _store.ImportContent(Read(("p.json", CoefficientFile)));

        Assert.True(outcome.Committed);
        Assert.NotEqual(before, _store.ComputeContentHash().Hash);
    }

    [Fact]
    public void An_import_carrying_no_coefficients_leaves_the_table_alone()
    {
        // Absent means "leave it", the same rule the roster (E18) already follows — the folders are
        // swept independently and a run that touched only atoms must not wipe out an authored table.
        _store.ImportContent(Read(("p.json", CoefficientFile)));

        const string atomOnly = """
            { "schemaVersion": 1, "kind": "atom", "entries": [
                { "kind": "stat.modify", "family": "atom.vitality", "tier": 1,
                  "params": { "channel": "maxHp", "op": "flat", "amount": 45 } } ] }
            """;
        _store.ImportContent(Read(("a.json", atomOnly)));

        Assert.NotNull(_store.GetPowerTables().Find("stat.derived", "combat.dodge.fire"));
    }

    [Fact]
    public void A_second_coefficient_file_adds_to_the_first_rather_than_replacing_it()
    {
        // WritePowerTablesUnlocked is a whole-table delete-then-insert; the import path must overlay
        // the incoming batch onto what is already stored, or a second, unrelated coefficients file
        // would silently wipe every row the first one wrote.
        _store.ImportContent(Read(("p1.json", CoefficientFile)));

        const string second = """
            { "schemaVersion": 1, "kind": "power-coefficient", "entries": [
                { "kindId": "shield.grant", "coeffMilli": 500, "referenceScale": 10 } ] }
            """;
        var outcome = _store.ImportContent(Read(("p2.json", second)));

        Assert.True(outcome.Committed, string.Join("; ", outcome.Errors));
        var tables = _store.GetPowerTables();
        Assert.NotNull(tables.Find("stat.derived", "combat.dodge.fire"));
        Assert.NotNull(tables.Find("shield.grant", null));
    }

    // ---- idempotency ------------------------------------------------------------------------------

    [Fact]
    public void Importing_the_same_coefficient_file_twice_moves_neither_the_hash_nor_the_revision()
    {
        _store.ImportContent(Read(("p.json", CoefficientFile)));
        var hash = _store.ComputeContentHash().Hash;
        var revision = _store.GetCatalogRevision();

        var second = _store.ImportContent(Read(("p.json", CoefficientFile)));

        Assert.True(second.Committed);
        Assert.Equal(0, second.RowsChanged);
        Assert.Equal(hash, _store.ComputeContentHash().Hash);
        Assert.Equal(revision, _store.GetCatalogRevision());
    }

    [Fact]
    public void Retuning_one_coefficient_registers_as_exactly_one_changed_row()
    {
        _store.ImportContent(Read(("p.json", CoefficientFile)));

        const string retuned = """
            { "schemaVersion": 1, "kind": "power-coefficient", "entries": [
                { "kindId": "stat.derived", "channel": "combat.dodge.fire",
                  "coeffMilli": 1500, "referenceScale": 1 } ] }
            """;
        var outcome = _store.ImportContent(Read(("p.json", retuned)));

        Assert.True(outcome.Committed, string.Join("; ", outcome.Errors));
        Assert.Equal(1, outcome.RowsChanged);
        Assert.Equal(1500, _store.GetPowerTables().Find("stat.derived", "combat.dodge.fire")!.CoeffMilli);
    }

    // ---- refusal ------------------------------------------------------------------------------------

    [Fact]
    public void A_zero_reference_scale_is_refused_the_same_way_UpsertPowerTables_already_refuses_it()
    {
        // ReadCoefficient only checks the field is explicit; the units-trap check (a scale of zero or
        // less divides by itself during pricing) is RpgStore.UpsertPowerTables's own rule, reused
        // unchanged here rather than duplicated.
        const string zeroScale = """
            { "schemaVersion": 1, "kind": "power-coefficient", "entries": [
                { "kindId": "stat.derived", "channel": "combat.dodge.fire",
                  "coeffMilli": 1000, "referenceScale": 0 } ] }
            """;

        var outcome = _store.ImportContent(Read(("p.json", zeroScale)));

        Assert.False(outcome.Committed);
        Assert.Contains(outcome.Errors, e => e.Reason == AtomRejectionReason.BadParamValue);

        // Nothing was written, so the table is still empty and GetPowerTables falls all the way back
        // to PowerTables.Authored() — whose own generic "stat.derived" row (channel "") is exactly
        // what Find("stat.derived", "combat.dodge.fire") legitimately falls back to. The refusal is
        // proven by that row still being the AUTHORED one (coeffMilli 1000, its shipped value), not
        // by Find returning null — a null would be the wrong assertion given Find's own documented
        // channel-less fallback.
        var fallback = _store.GetPowerTables().Find("stat.derived", "combat.dodge.fire");
        Assert.NotNull(fallback);
        Assert.Equal("", fallback!.Channel);
        Assert.Equal(PowerTables.Authored().Find("stat.derived", null)!.CoeffMilli, fallback.CoeffMilli);
    }

    // ---- the no-database fallback is untouched -----------------------------------------------------

    [Fact]
    public void Before_any_import_GetPowerTables_still_returns_the_hand_authored_fallback()
    {
        // CoefficientTable.Authored() (PowerTables.Authored() in CoefficientTable.cs) is the
        // no-database fallback and stays untouched by this module, by explicit design — deleting a
        // seed file must restore exactly today's behaviour.
        var before = _store.GetPowerTables();

        Assert.Equal(PowerTables.Authored().Coefficients.Count, before.Coefficients.Count);
    }
}
