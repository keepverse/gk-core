using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// E18's three tables (spec-element-roster-data.md).
///
/// <para>The ordinal is the thing worth guarding. It names every generated channel —
/// <c>combat.power.fire</c> exists because fire is in the roster at ordinal 0 — so an ordinal that
/// moves renames content that already exists, and a retired one that comes back re-points content
/// still naming the old element.</para>
/// </summary>
public class ElementStoreTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public ElementStoreTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose() => _testStore.Dispose();

    [Fact]
    public void An_empty_database_reads_the_shipped_roster()
    {
        // A host that has imported nothing must behave exactly as it always has, or E18 would be a
        // behaviour change wearing a migration's clothes.
        var table = _store.GetElementTable();

        Assert.Equal(6, table.Elements.Count);
        Assert.Equal(0, table.Find("fire")!.Ordinal);
        Assert.Equal(5, table.Find("dark")!.Ordinal);
    }

    [Fact]
    public void The_shipped_roster_round_trips_through_the_tables()
    {
        var shipped = ElementTable.Shipped();

        Assert.True(_store.UpsertElementTable(shipped).Ok);
        var read = _store.GetElementTable();

        Assert.Equal(
            shipped.Elements.Select(e => (e.ElementId, e.Ordinal, e.Enabled)),
            read.Elements.Select(e => (e.ElementId, e.Ordinal, e.Enabled)));

        foreach (var row in shipped.CombatRows)
            Assert.Equal(row.Unit, read.CombatUnit(row.Attacker, row.Defender));
        foreach (var row in shipped.ShieldRows)
            Assert.Equal(row.Unit, read.ShieldUnit(row.Attacker, row.Defender));
    }

    [Fact]
    public void An_element_may_not_move_to_a_different_ordinal()
    {
        _store.UpsertElementTable(ElementTable.Shipped());
        var shipped = ElementTable.Shipped();
        var moved = shipped.Elements
            .Select(e => e.ElementId == "dark" ? e with { Ordinal = 9 } : e)
            .ToList();

        var verdict = _store.UpsertElementTable(
            new ElementTable(moved, shipped.CombatRows, shipped.ShieldRows));

        Assert.False(verdict.Ok);
        Assert.Contains("append-only", verdict.Reason, StringComparison.Ordinal);
        Assert.Equal(5, _store.GetElementTable().Find("dark")!.Ordinal);
    }

    [Fact]
    public void A_retired_elements_ordinal_is_never_reused()
    {
        _store.UpsertElementTable(ElementTable.Shipped());
        var shipped = ElementTable.Shipped();

        // "shadow" claims dark's slot. Every stored channel naming combat.power.dark would silently
        // become a channel about something else.
        var reused = shipped.Elements
            .Where(e => e.ElementId != "dark")
            .Append(new ElementRow("shadow", "Shadow", 5))
            .ToList();

        var verdict = _store.UpsertElementTable(
            new ElementTable(reused, shipped.CombatRows, shipped.ShieldRows));

        Assert.False(verdict.Ok);
        Assert.Contains("already belongs to 'dark'", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_elements_claiming_one_ordinal_in_the_same_write_are_refused()
    {
        var shipped = ElementTable.Shipped();
        var clash = shipped.Elements.Append(new ElementRow("void", "Void", 5)).ToList();

        var verdict = _store.UpsertElementTable(
            new ElementTable(clash, shipped.CombatRows, shipped.ShieldRows));

        Assert.False(verdict.Ok);
        Assert.Empty(_store.GetElementTable().Elements.Where(e => e.ElementId == "void"));
    }

    [Fact]
    public void A_seventh_element_appended_at_a_free_ordinal_is_accepted()
    {
        // The other half. Without this, the append-only rule could be refusing everything and the
        // tests above would still pass.
        _store.UpsertElementTable(ElementTable.Shipped());
        var shipped = ElementTable.Shipped();
        var grown = shipped.Elements.Append(new ElementRow("void", "Void", 6)).ToList();

        var verdict = _store.UpsertElementTable(
            new ElementTable(grown, shipped.CombatRows, shipped.ShieldRows));

        Assert.True(verdict.Ok, verdict.Reason);
        Assert.Equal(7, _store.GetElementTable().Elements.Count);
    }

    // ---- the content hash ----------------------------------------------------------------------

    [Fact]
    public void The_registry_covers_the_three_element_tables_from_version_two_on()
    {
        // Version 2 is where they joined; later versions only add. Asserting the current version is
        // exactly 2 would make every later module edit this test for no reason — E9 did, immediately.
        var atV2 = ContentHashRegistry.For(2).Select(t => t.TableName).ToList();
        Assert.Equal(9, atV2.Count); // the six from v1 plus these three

        var covered = ContentHashRegistry.Current.Select(t => t.TableName).ToList();
        Assert.Contains("effect_element", covered);
        Assert.Contains("effect_element_matrix_combat", covered);
        Assert.Contains("effect_element_matrix_shield", covered);
    }

    [Fact]
    public void Every_earlier_registry_version_is_a_prefix_of_the_current_one()
    {
        // A table may join the covered set; one may never quietly leave it, or a stamp made at an
        // earlier version would compare against a set that no longer contains what it hashed.
        var current = ContentHashRegistry.Current.Select(t => t.TableName).ToHashSet(StringComparer.Ordinal);

        for (var v = 1; v <= ContentHashRegistry.CurrentSchemaVersion; v++)
            foreach (var table in ContentHashRegistry.For(v))
                Assert.Contains(table.TableName, current);
    }

    [Fact]
    public void A_roster_edit_moves_the_content_hash()
    {
        // An element addition changes the hash AND the channel count together, so it can never be
        // mistaken for a code regression and a golden that moves has an attributable cause.
        _store.UpsertElementTable(ElementTable.Shipped());
        var before = _store.ComputeContentHash().Hash;

        var shipped = ElementTable.Shipped();
        _store.UpsertElementTable(new ElementTable(
            shipped.Elements.Append(new ElementRow("void", "Void", 6)).ToList(),
            shipped.CombatRows, shipped.ShieldRows));

        Assert.NotEqual(before, _store.ComputeContentHash().Hash);
    }

    [Fact]
    public void A_matrix_edit_moves_the_content_hash_too()
    {
        _store.UpsertElementTable(ElementTable.Shipped());
        var before = _store.ComputeContentHash().Hash;

        var shipped = ElementTable.Shipped();
        _store.UpsertElementTable(new ElementTable(
            shipped.Elements, shipped.CombatRows,
            shipped.ShieldRows.Select(r =>
                r.Attacker == "fire" && r.Defender == "ice" ? r with { Unit = -1 } : r).ToList()));

        Assert.NotEqual(before, _store.ComputeContentHash().Hash);
    }

    [Fact]
    public void Re_importing_the_same_roster_does_not_move_the_hash()
    {
        _store.UpsertElementTable(ElementTable.Shipped());
        var hash = _store.ComputeContentHash().Hash;

        _store.UpsertElementTable(ElementTable.Shipped());

        Assert.Equal(hash, _store.ComputeContentHash().Hash);
    }

    [Fact]
    public void A_version_the_registry_does_not_know_is_still_refused()
    {
        // Tracks the registry rather than naming a number, so adding a version does not break it.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ContentHashRegistry.For(ContentHashRegistry.CurrentSchemaVersion + 1));
    }
}
