using FusionRpg.Core.Delve.Quests;
using FusionRpg.Core.Items.Drops;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Quests;

/// <summary>D4.13's own real remaining bridge, closed 2026-09-07 -- `QuestOffer.Satisfiable`'s own
/// `lootBindingOffersRole(role)` delegate. Hand-built `DropTableRow` fixtures throughout: this bridge
/// is pure over already-loaded content, and the real shipped loot corpus is Data-layer (SQL) content
/// this Core.Tests project has no store to load.</summary>
public class QuestLootBindingBridgeTests
{
    static DropTableEntryRow Equipment(string frame, string role, bool enabled = true) =>
        new(Seq: 0, Kind: DropEntryKind.Equipment, RefId: "", Weight: 1, Enabled: enabled, Frame: frame, Role: role);

    static DropTableEntryRow Material(string refId) =>
        new(Seq: 1, Kind: DropEntryKind.Material, RefId: refId, Weight: 1);

    static DropTableRow Table(string id, params DropTableEntryRow[] entries) =>
        new(id, new[] { "web" }, MinIlvl: null, MaxIlvl: null, Enabled: true, Revision: 0,
            Groups: new[] { new DropTableGroupRow("g0", 0, 1, entries) });

    static IReadOnlyList<string> BaseTypesForWeaponCore(string frame, string role) =>
        frame == "weapon" && role == "core-guard" ? new[] { "item.base.weapon-core-guard-01" } : Array.Empty<string>();

    // ---- TableOffersRole ----

    [Fact]
    public void TableOffersRole_null_arguments_throw()
    {
        var table = Table("t1", Equipment("weapon", "core-guard"));
        Assert.Throws<ArgumentNullException>(() => QuestLootBindingBridge.TableOffersRole(null!, "core-guard", BaseTypesForWeaponCore));
        Assert.Throws<ArgumentNullException>(() => QuestLootBindingBridge.TableOffersRole(table, null!, BaseTypesForWeaponCore));
        Assert.Throws<ArgumentNullException>(() => QuestLootBindingBridge.TableOffersRole(table, "core-guard", null!));
    }

    [Fact]
    public void An_enabled_equipment_entry_backed_by_a_real_base_type_offers_its_role()
    {
        var table = Table("t1", Equipment("weapon", "core-guard"));
        Assert.True(QuestLootBindingBridge.TableOffersRole(table, "core-guard", BaseTypesForWeaponCore));
    }

    [Fact]
    public void An_authored_but_content_less_frame_role_pair_does_not_offer_the_role()
    {
        // "armor"/"core-guard" is authored on the entry but BaseTypesForWeaponCore never backs it --
        // an authored pair with zero real base types must not count as satisfiable.
        var table = Table("t1", Equipment("armor", "core-guard"));
        Assert.False(QuestLootBindingBridge.TableOffersRole(table, "core-guard", BaseTypesForWeaponCore));
    }

    [Fact]
    public void A_disabled_entry_never_offers_its_role()
    {
        var table = Table("t1", Equipment("weapon", "core-guard", enabled: false));
        Assert.False(QuestLootBindingBridge.TableOffersRole(table, "core-guard", BaseTypesForWeaponCore));
    }

    [Fact]
    public void An_entry_naming_a_different_role_does_not_offer_the_asked_role()
    {
        var table = Table("t1", Equipment("weapon", "retinue"));
        Assert.False(QuestLootBindingBridge.TableOffersRole(table, "core-guard", BaseTypesForWeaponCore));
    }

    [Fact]
    public void A_non_equipment_entry_never_offers_a_role_even_if_its_RefId_collides_with_the_role_name()
    {
        var table = Table("t1", Material("core-guard"));
        Assert.False(QuestLootBindingBridge.TableOffersRole(table, "core-guard", BaseTypesForWeaponCore));
    }

    [Fact]
    public void An_empty_table_never_offers_any_role()
    {
        var table = Table("t1");
        Assert.False(QuestLootBindingBridge.TableOffersRole(table, "core-guard", BaseTypesForWeaponCore));
    }

    // ---- Build ----

    [Fact]
    public void Build_null_arguments_throw()
    {
        var tables = new Dictionary<string, DropTableRow>(StringComparer.Ordinal);
        var binding = new Dictionary<string, string>(StringComparer.Ordinal);
        Assert.Throws<ArgumentNullException>(() => QuestLootBindingBridge.Build(null!, tables, BaseTypesForWeaponCore));
        Assert.Throws<ArgumentNullException>(() => QuestLootBindingBridge.Build(binding, null!, BaseTypesForWeaponCore));
        Assert.Throws<ArgumentNullException>(() => QuestLootBindingBridge.Build(binding, tables, null!));
    }

    [Fact]
    public void Any_one_of_several_bound_tables_offering_the_role_is_enough()
    {
        var tables = new Dictionary<string, DropTableRow>(StringComparer.Ordinal)
        {
            ["drop.fight"] = Table("drop.fight", Equipment("armor", "retinue")), // does not offer core-guard
            ["drop.elite"] = Table("drop.elite", Equipment("weapon", "core-guard")), // does
        };
        var binding = new Dictionary<string, string>(StringComparer.Ordinal) { ["fight"] = "drop.fight", ["elite"] = "drop.elite" };

        var offersRole = QuestLootBindingBridge.Build(binding, tables, BaseTypesForWeaponCore);

        Assert.True(offersRole("core-guard"));
        Assert.False(offersRole("retinue")); // authored on drop.fight but never backed by a real base type
    }

    [Fact]
    public void A_binding_naming_an_unknown_table_id_never_throws_and_offers_nothing()
    {
        var tables = new Dictionary<string, DropTableRow>(StringComparer.Ordinal);
        var binding = new Dictionary<string, string>(StringComparer.Ordinal) { ["fight"] = "drop.does-not-exist" };

        var offersRole = QuestLootBindingBridge.Build(binding, tables, BaseTypesForWeaponCore);

        Assert.False(offersRole("core-guard"));
    }

    [Fact]
    public void An_empty_binding_offers_nothing()
    {
        var offersRole = QuestLootBindingBridge.Build(
            new Dictionary<string, string>(StringComparer.Ordinal), new Dictionary<string, DropTableRow>(StringComparer.Ordinal), BaseTypesForWeaponCore);

        Assert.False(offersRole("core-guard"));
    }
}
