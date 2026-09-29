using FusionRpg.Contracts;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using FusionRpg.Core.Match;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FusionRpg.Data.Tests.Items;

/// <summary>
/// ⭐ <b>The relic row migration — `decision-d1-durable-ownership.md` §10 M1/M2.</b>
///
/// <para><b>Why this file exists.</b> `tasks/item-todo.md` module 4 (P1.4) deferred "retiring
/// `rpg_unique_equipment` / `UniqueEquipmentCatalog` and the relic row migration" to module 17,
/// because relics supposedly had no home to migrate into. Module 17 (P5.1), built the next day,
/// re-confirmed the same item and deferred it back: *"the row migration is module 4's."* Two
/// self-consistent notes, one unbuilt item. The first note's premise was wrong — module 17's
/// <c>item_unique</c> is a classification flag keyed on an <c>effect_container</c>, with no name,
/// rarity, slot, description or effect column, so an equipped-slot row was never going there. The
/// home these rows needed is <c>rpg_item_assignment</c>, module 4's own table, shipped 2026-09-04.</para>
///
/// <para>Every test below writes the <b>legacy</b> shape directly through SQL — the pre-migration
/// state a real save carries — so the migration is proven against the data it actually has to
/// survive, not against a fixture written in the new shape.</para>
/// </summary>
public class RelicRowMigrationTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    readonly long _playerId;

    public RelicRowMigrationTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _playerId = _store.GetCurrentPlayerId();
    }

    public void Dispose() => _testStore.Dispose();

    SqliteConnection OpenRaw()
    {
        // The store's own HotPath (a memory URI under the memory plan), not a constructed file path.
        return SqliteConnectionFactory.Open(_store.HotPath);
    }

    /// <summary>Write a row in the pre-migration shape, exactly as the retired writer did.</summary>
    void WriteLegacyRow(string instanceId, string slot, string itemId)
    {
        using var db = OpenRaw();
        using var cmd = db.CreateCommand();
        cmd.CommandText =
            "INSERT INTO rpg_unique_equipment(instance_id, slot, item_id) VALUES($i, $s, $t) " +
            "ON CONFLICT(instance_id, slot) DO UPDATE SET item_id = excluded.item_id;";
        cmd.Parameters.AddWithValue("$i", instanceId);
        cmd.Parameters.AddWithValue("$s", slot);
        cmd.Parameters.AddWithValue("$t", itemId);
        cmd.ExecuteNonQuery();
    }

    List<(string Role, string RefKind, string RefId)> ReadAssignments(string specimenId)
    {
        using var db = OpenRaw();
        using var cmd = db.CreateCommand();
        cmd.CommandText =
            "SELECT role, ref_kind, ref_id FROM rpg_item_assignment WHERE specimen_id = $s ORDER BY role;";
        cmd.Parameters.AddWithValue("$s", specimenId);
        using var r = cmd.ExecuteReader();
        var list = new List<(string, string, string)>();
        while (r.Read()) list.Add((r.GetString(0), r.GetString(1), r.GetString(2)));
        return list;
    }

    long CountLegacyRows()
    {
        using var db = OpenRaw();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM rpg_unique_equipment;";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    string NewActor() => _store.CreateUniqueActor(_playerId, "plant", 1).InstanceId;

    /// <summary>The atom-backed containers relics bind through are seeded from the real shipped
    /// tree at server boot, not by <c>RpgStore.Init</c> — so a test that wants a real binding has to
    /// import them, exactly as <c>UniqueEquipmentAtomBindingTests</c> already does. Real files, not
    /// a fixture: the point is that <i>these four relics</i> still resolve.</summary>
    void ImportRealSeedTree()
    {
        var root = RepoRoot();
        var files = Directory
            .GetFiles(Path.Combine(root, "data", "seed", "atoms"), "fx-*.json", SearchOption.AllDirectories)
            .Concat(new[] { Path.Combine(root, "data", "seed", "containers", "unique-equip.json") })
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => (f, File.ReadAllText(f)))
            .ToArray();

        var collected = AtomSeedFile.Collect(files);
        Assert.True(collected.IsOk, string.Join("; ", collected.Errors));
        var outcome = _store.ImportContent(collected.Content);
        Assert.True(outcome.Committed, string.Join("; ", outcome.Errors));
        Assert.NotNull(_store.GetContainer("item.fx-passive-atk-flat"));
    }

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "seed", "atoms"))) return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("repo root");
    }

    // ---- the shipped data ------------------------------------------------------------------

    /// <summary>
    /// ⭐ <b>No data loss, checked field by field against what was there before.</b> All four
    /// shipped relics are equipped in the legacy table, the migration runs, and every one comes
    /// back with the same slot, the same item id, and the same fully-resolvable
    /// <see cref="RelicDto"/> — id, name, rarity, slot, description and effect id.
    /// </summary>
    [Fact]
    public void The_four_shipped_relics_survive_the_migration_field_by_field()
    {
        Assert.Equal(4, RelicCatalog.Items.Count);

        // Two specimens, because the four relics cover only three distinct slots — a single
        // specimen physically cannot wear all four, and a migration proven on three rows would
        // not have exercised the "same slot, different specimen" case at all.
        var a = NewActor();
        var b = NewActor();
        var before = new List<(string Specimen, string Slot, RelicDto Relic)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var relic in RelicCatalog.Items)
        {
            var specimen = seen.Add(relic.Slot) ? a : b;
            WriteLegacyRow(specimen, relic.Slot, relic.Id);
            before.Add((specimen, relic.Slot, relic));
        }
        Assert.Equal(4, CountLegacyRows());

        Assert.Equal(4, _store.MigrateUniqueEquipmentToAssignments());

        foreach (var (specimen, slot, relic) in before)
        {
            var wire = _store.ListUniqueEquipment(specimen);
            var row = Assert.Single(wire, s => s.Slot == slot);
            Assert.Equal(relic.Id, row.ItemId);

            // The definition is still whole — every field, not just the id that was persisted.
            Assert.True(RelicCatalog.TryGetRelic(row.ItemId, out var after));
            Assert.Equal(relic.Id, after.Id);
            Assert.Equal(relic.Name, after.Name);
            Assert.Equal(relic.Rarity, after.Rarity);
            Assert.Equal(relic.Slot, after.Slot);
            Assert.Equal(relic.Description, after.Description);
            Assert.Equal(relic.EffectId, after.EffectId);
        }
    }

    /// <summary>The alias map is applied, not the raw string: a legacy <c>weapon</c> row becomes an
    /// <c>armament-primary</c> assignment with I13 §4.4's <c>stock</c> ref kind.</summary>
    [Fact]
    public void A_migrated_row_lands_as_a_stock_assignment_under_its_canonical_role()
    {
        var a = NewActor();
        WriteLegacyRow(a, "weapon", "relic.ashen_reliquary");
        WriteLegacyRow(a, "armor", "relic.tidewrack_band");
        WriteLegacyRow(a, "trinket", "relic.cracked_seal");

        Assert.Equal(3, _store.MigrateUniqueEquipmentToAssignments());

        Assert.Equal(new[]
        {
            ("armament-primary", "stock", "relic.ashen_reliquary"),
            ("core-guard", "stock", "relic.tidewrack_band"),
            ("jewel-minor-a", "stock", "relic.cracked_seal"),
        }, ReadAssignments(a));
    }

    /// <summary>D1 calls the migration *"one-way and idempotent"*. A second run copies nothing and
    /// changes nothing — which is what lets it sit unconditionally in <c>Init</c>.</summary>
    [Fact]
    public void The_migration_is_idempotent()
    {
        var a = NewActor();
        WriteLegacyRow(a, "weapon", "relic.ashen_reliquary");

        Assert.Equal(1, _store.MigrateUniqueEquipmentToAssignments());
        var first = ReadAssignments(a);

        Assert.Equal(0, _store.MigrateUniqueEquipmentToAssignments());
        Assert.Equal(0, _store.MigrateUniqueEquipmentToAssignments());
        Assert.Equal(first, ReadAssignments(a));
    }

    /// <summary>⭐ The dangerous case a naive upsert would get wrong: a stale legacy row must never
    /// clobber an assignment written after the cutover. The migration skips an occupied
    /// <c>(specimen, role)</c> rather than overwriting it.</summary>
    [Fact]
    public void A_stale_legacy_row_never_overwrites_a_newer_assignment()
    {
        var a = NewActor();
        WriteLegacyRow(a, "weapon", "relic.ashen_reliquary");   // the old, superseded choice
        _store.SaveAssignment(a, ItemRole.ArmamentPrimary, "stock", "relic.sunworn_charm");

        Assert.Equal(0, _store.MigrateUniqueEquipmentToAssignments());

        var row = Assert.Single(ReadAssignments(a));
        Assert.Equal("relic.sunworn_charm", row.RefId);
    }

    /// <summary>The legacy table keeps its rows — D1 §10 makes dropping it M4's, *"the only
    /// irreversible act; do it last."* The migration reads, never deletes.</summary>
    [Fact]
    public void The_migration_is_one_way_and_leaves_the_legacy_rows_in_place()
    {
        var a = NewActor();
        WriteLegacyRow(a, "weapon", "relic.ashen_reliquary");
        _store.MigrateUniqueEquipmentToAssignments();
        Assert.Equal(1, CountLegacyRows());
    }

    /// <summary>An existing save cuts over on boot, without anyone calling the migration by hand —
    /// otherwise a player who upgrades finds their relics unequipped.</summary>
    [Fact]
    public void Init_migrates_a_pre_cutover_save_so_equipment_is_not_silently_lost()
    {
        var a = NewActor();
        WriteLegacyRow(a, "armor", "relic.tidewrack_band");

        var reopened = _testStore.Reopen();

        var row = Assert.Single(reopened.ListUniqueEquipment(a));
        Assert.Equal("armor", row.Slot);
        Assert.Equal("relic.tidewrack_band", row.ItemId);
    }

    // ---- the post-M2 read/write path -------------------------------------------------------

    /// <summary>M2's whole point: equipping writes the new table and no longer touches the old
    /// one.</summary>
    [Fact]
    public void Equipping_writes_rpg_item_assignment_and_never_the_retired_table()
    {
        var a = NewActor();
        _store.UpsertUniqueEquipment(a, "weapon", "relic.ashen_reliquary");

        Assert.Equal(0, CountLegacyRows());
        var row = Assert.Single(ReadAssignments(a));
        Assert.Equal(("armament-primary", "stock", "relic.ashen_reliquary"), row);
    }

    /// <summary>Unequip is one assignment row deleted — module 4's §6.4 atomicity claim, now
    /// reached through the legacy wire too.</summary>
    [Fact]
    public void Unequipping_deletes_the_assignment_row()
    {
        var a = NewActor();
        _store.UpsertUniqueEquipment(a, "weapon", "relic.ashen_reliquary");
        _store.ClearUniqueEquipmentSlot(a, "weapon");

        Assert.Empty(ReadAssignments(a));
        Assert.Empty(_store.ListUniqueEquipment(a));
    }

    /// <summary>⭐ <b>The wire shape is byte-identical across the switch</b>, ordering included.
    /// The retired query ended <c>ORDER BY slot ASC</c> over the legacy labels; ordering by
    /// <c>role</c> instead would answer weapon, armor, trinket rather than armor, trinket, weapon —
    /// same data, different array, and <c>RelicsLayer.tsx</c> renders the array.</summary>
    [Fact]
    public void The_equipment_payload_keeps_its_legacy_labels_and_its_legacy_order()
    {
        var a = NewActor();
        _store.UpsertUniqueEquipment(a, "weapon", "relic.ashen_reliquary");
        _store.UpsertUniqueEquipment(a, "trinket", "relic.cracked_seal");
        var dto = _store.UpsertUniqueEquipment(a, "armor", "relic.tidewrack_band");

        Assert.Equal(new[] { "armor", "trinket", "weapon" }, dto.Items.Select(i => i.Slot));
        Assert.Equal(
            new[] { "relic.tidewrack_band", "relic.cracked_seal", "relic.ashen_reliquary" },
            dto.Items.Select(i => i.ItemId));
        Assert.Equal(a, dto.InstanceId);
    }

    /// <summary>The two downstream effects of equipping are unchanged: the legacy grant blob still
    /// carries the non-atom-backed relic, and the atom-backed one still produces a real
    /// <c>unique-equip</c> binding at <c>UniqueActor</c> scope.</summary>
    [Fact]
    public void The_grant_blob_and_the_unique_equip_bindings_still_come_out_of_an_equip()
    {
        ImportRealSeedTree();
        var a = NewActor();

        // 2026-09-06: relic.cracked_seal -> fx.entity_atk moved to item.fx-entity-atk (a deliberately
        // empty atom-backed container) — it must NOT appear in the blob and MUST appear as a binding
        // now too, same as every other relic.
        var withStub = _store.UpsertUniqueEquipment(a, "trinket", "relic.cracked_seal");
        Assert.DoesNotContain("equip-relic-cracked_seal", withStub.ModsJson);

        // relic.ashen_reliquary -> fx.passive_atk_flat -> item.fx-passive-atk-flat: atom-backed,
        // so it must NOT appear in the blob and MUST appear as a binding.
        var withAtom = _store.UpsertUniqueEquipment(a, "weapon", "relic.ashen_reliquary");
        Assert.DoesNotContain("equip-relic-ashen_reliquary", withAtom.ModsJson);

        var bindings = _store.ListBindings(
            new FusionRpg.Core.Effects.Atoms.OwnerScope(
                FusionRpg.Core.Effects.Atoms.OwnerKind.UniqueActor, a));
        Assert.Contains(bindings, b => b.Source == "unique-equip" && b.Slot == "weapon");
        Assert.Contains(bindings, b => b.Source == "unique-equip" && b.Slot == "trinket");
    }

    /// <summary>⛔ <b>Found while landing M2, and fixed here:</b> <c>Reset()</c> cleared
    /// <c>rpg_unique_equipment</c> but never <c>rpg_item_assignment</c>. Now that assignments are
    /// the equipment SSOT, leaving them behind is the same orphan bug the world and delve rows in
    /// that same list already carry comments about.</summary>
    [Fact]
    public void Reset_clears_the_assignment_table()
    {
        var a = NewActor();
        _store.UpsertUniqueEquipment(a, "weapon", "relic.ashen_reliquary");
        Assert.Single(ReadAssignments(a));

        _store.Reset();

        Assert.Empty(ReadAssignments(a));
    }

    /// <summary>⭐ <b>No double grant, proven rather than assumed.</b> Module 5's
    /// <c>ApplyEquipProjection</c> also writes <c>effect_binding</c> at <c>UniqueActor</c> scope
    /// from this same table — a different <c>Source</c> tag (<c>equip-assign</c> vs
    /// <c>unique-equip</c>), so neither reconciler withdraws the other's rows and a relic reaching
    /// both would be granted twice. It cannot: the projection filters to
    /// <c>ref_kind == "rolled"</c>, and every row the legacy equip wire writes is <c>stock</c>.
    /// Fed the real assignment this equip produced, the projector binds nothing.</summary>
    [Fact]
    public void A_stock_assignment_is_not_also_projected_by_the_equip_projector()
    {
        var a = NewActor();
        _store.UpsertUniqueEquipment(a, "weapon", "relic.ashen_reliquary");

        var assignments = _store.ListAssignments(a);
        Assert.Equal("stock", Assert.Single(assignments).RefKind);

        // The projector's own output shape, with the live assignment offered as a binding — the
        // most permissive input it could get. ApplyEquipProjection still writes nothing.
        _store.ApplyEquipProjection(a, new ProjectionResult(
            Bindings: assignments,
            Shortfalls: Array.Empty<(EquipAssignment, EquipRefusal)>(),
            Skipped: Array.Empty<(EquipAssignment, EquipRefusal)>()));

        var bindings = _store.ListBindings(
            new FusionRpg.Core.Effects.Atoms.OwnerScope(
                FusionRpg.Core.Effects.Atoms.OwnerKind.UniqueActor, a));
        Assert.DoesNotContain(bindings, b => b.Source == "equip-assign");
    }

    // ---- R1: the relic wire's missing symmetric refusal -------------------------------------

    const string LiveItem = "inst-blade-r1";

    /// <summary>
    /// ⛔ <b>Defect R1, measured 2026-09-06 and fixed here.</b> Two flows write
    /// <c>rpg_item_assignment</c>, and only one of them refused the other. <c>POST /api/items/equip</c>
    /// answered <c>equip.role-held-by-relic</c> from the day it shipped; this wire answered <b>200</b>
    /// and let its upsert replace a live <c>rolled</c> row, so a player's equipped item came off with
    /// no refusal and no notice.
    ///
    /// <para>Both halves are asserted, because a guard that names the rule and writes anyway is the
    /// failure a refusal-only assertion cannot see: the refusal, <b>and</b> the item's own row
    /// unchanged.</para>
    /// </summary>
    [Fact]
    public void Equipping_a_relic_into_a_role_a_live_item_holds_is_refused_by_name()
    {
        var a = NewActor();
        _store.SaveAssignment(a, ItemRole.ArmamentPrimary, EquipRefKinds.Rolled, LiveItem);

        var ex = Assert.Throws<UniqueEquipmentSlotClaimed>(
            () => _store.UpsertUniqueEquipment(a, "weapon", "relic.ashen_reliquary"));

        Assert.Contains("slot.claimed_by_item", ex.Reason, StringComparison.Ordinal);
        Assert.Equal(
            ("armament-primary", EquipRefKinds.Rolled, LiveItem),
            Assert.Single(ReadAssignments(a)));
    }

    /// <summary>The <c>DELETE</c> half is the same defect with a worse ending: clearing the legacy
    /// slot runs <c>DELETE FROM rpg_item_assignment WHERE specimen_id AND role</c>, which does not
    /// care whose row it is. Unguarded, <c>DELETE /api/unique/actors/{id}/equipment/weapon</c> would
    /// unequip an item the relic flow never put there. Covered for free by enforcing at the single
    /// write point, and pinned here so it stays covered.</summary>
    [Fact]
    public void Clearing_a_relic_slot_a_live_item_holds_never_deletes_the_items_row()
    {
        var a = NewActor();
        _store.SaveAssignment(a, ItemRole.ArmamentPrimary, EquipRefKinds.Rolled, LiveItem);

        Assert.Throws<UniqueEquipmentSlotClaimed>(() => _store.ClearUniqueEquipmentSlot(a, "weapon"));

        Assert.Equal(
            ("armament-primary", EquipRefKinds.Rolled, LiveItem),
            Assert.Single(ReadAssignments(a)));
    }

    /// <summary>⚠ <b>Only <c>rolled</c> is refused, and that boundary is the point.</b> A
    /// <c>stock</c> occupant is this wire's OWN row, so swapping one relic for another in the same
    /// slot stays exactly what it always was — a 200 and a replaced row. A guard that refused every
    /// occupant would break the relic flow to protect the item flow.</summary>
    [Fact]
    public void A_relic_still_replaces_another_relic_in_the_same_slot()
    {
        var a = NewActor();
        _store.UpsertUniqueEquipment(a, "weapon", "relic.ashen_reliquary");

        var after = _store.UpsertUniqueEquipment(a, "weapon", "relic.sunworn_charm");

        Assert.Equal("relic.sunworn_charm", Assert.Single(after.Items).ItemId);
        Assert.Equal(
            ("armament-primary", EquipRefKinds.Stock, "relic.sunworn_charm"),
            Assert.Single(ReadAssignments(a)));
    }

    /// <summary>A rolled row in a DIFFERENT role is not this write's business — the guard is keyed on
    /// the role being written, not on "this specimen owns an item somewhere".</summary>
    [Fact]
    public void A_rolled_assignment_in_another_role_does_not_block_the_relic_wire()
    {
        var a = NewActor();
        _store.SaveAssignment(a, ItemRole.CoreGuard, EquipRefKinds.Rolled, LiveItem);

        var after = _store.UpsertUniqueEquipment(a, "weapon", "relic.ashen_reliquary");

        // Both rows are surfaced — the reader projects every ref_kind, not just this wire's own.
        Assert.Equal("relic.ashen_reliquary",
            Assert.Single(after.Items, i => i.Slot == "weapon").ItemId);
        Assert.Equal(LiveItem, Assert.Single(after.Items, i => i.Slot == "armor").ItemId);
        Assert.Equal(2, ReadAssignments(a).Count);
    }
}
