using System.Reflection;
using System.Text.Json;
using FusionRpg.Contracts;
using FusionRpg.Core.Items;
using FusionRpg.Core.Match;
using Xunit;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// ⭐ <b>The relic disposition, closed 2026-09-06.</b> `tasks/item-todo.md` module 4 (P1.4) and
/// module 17 (P5.1) each deferred "the relic row migration and retiring `rpg_unique_equipment`" to
/// the other, and neither did it. This file pins the three things that decision now rests on:
/// the alias map that makes the move possible, the <c>/api/relics</c> shape that must not move with
/// it, and the structural reason <c>item_unique</c> is not — and cannot today be — the relic's
/// definition home.
/// </summary>
public class RelicHomeTests
{
    // ---- I2's alias map (ssot-equip-slots.md §5.7 step 1, D1 §10 M1) -----------------------

    [Theory]
    [InlineData("weapon", ItemRole.ArmamentPrimary)]
    [InlineData("armor", ItemRole.CoreGuard)]
    [InlineData("trinket", ItemRole.JewelMinorA)]
    public void The_three_legacy_slots_map_to_their_canonical_roles(string legacy, ItemRole expected)
    {
        Assert.True(LegacyEquipSlots.TryFromLegacy(legacy, out var role));
        Assert.Equal(expected, role);

        Assert.True(LegacyEquipSlots.TryToLegacy(expected, out var back));
        Assert.Equal(legacy, back);
    }

    /// <summary>The wire is case-insensitive today (<c>NormalizeSlot</c> lowercases before the
    /// allowlist check), so the map has to be too or a <c>PUT …/equipment/Weapon</c> that used to
    /// work would start refusing.</summary>
    [Theory]
    [InlineData("Weapon")]
    [InlineData("ARMOR")]
    [InlineData("  trinket  ")]
    public void The_map_tolerates_exactly_what_the_shipped_slot_validator_tolerates(string input)
    {
        Assert.True(LegacyEquipSlots.TryFromLegacy(input, out var role));
        Assert.True(LegacyEquipSlots.TryToLegacy(role, out _));
        Assert.Equal(UniqueEquipmentCatalog.NormalizeSlot(input), LegacyEquipSlots.All[
            LegacyEquipSlots.Roles.ToList().IndexOf(role)]);
    }

    /// <summary>⭐ Closed on purpose. Widening the wire to all fifteen roles is D1 §10's <b>M3</b>,
    /// which moves the REST payload and the FE literal together; until then a role the legacy
    /// payload cannot name must be refused here rather than silently emitted to a renderer that
    /// has no label for it.</summary>
    [Fact]
    public void The_twelve_roles_the_legacy_wire_cannot_name_are_refused_not_guessed()
    {
        Assert.Equal(3, LegacyEquipSlots.All.Count);
        Assert.Equal(3, LegacyEquipSlots.Roles.Count);

        foreach (ItemRole role in Enum.GetValues(typeof(ItemRole)))
        {
            var addressable = LegacyEquipSlots.Roles.Contains(role);
            Assert.Equal(addressable, LegacyEquipSlots.TryToLegacy(role, out _));
        }
        Assert.False(LegacyEquipSlots.TryFromLegacy("head-guard", out _));
        Assert.False(LegacyEquipSlots.TryFromLegacy("hat", out _));
        Assert.False(LegacyEquipSlots.TryFromLegacy("", out _));
        Assert.False(LegacyEquipSlots.TryFromLegacy(null, out _));
    }

    /// <summary>The map and the shipped allowlist are the same three strings in the same order —
    /// two lists that must never drift, checked rather than trusted.</summary>
    [Fact]
    public void The_map_and_the_shipped_slot_allowlist_agree_exactly()
    {
        Assert.Equal(UniqueEquipmentCatalog.DefaultSlots, LegacyEquipSlots.All);
        foreach (var slot in UniqueEquipmentCatalog.DefaultSlots)
            Assert.True(LegacyEquipSlots.TryFromLegacy(slot, out _));
    }

    /// <summary>Every relic's declared slot is addressable, or the migration would silently drop
    /// that relic's rows.</summary>
    [Fact]
    public void Every_shipped_relic_declares_a_slot_the_map_can_carry()
    {
        Assert.Equal(4, RelicCatalog.Items.Count);
        foreach (var relic in RelicCatalog.Items)
            Assert.True(LegacyEquipSlots.TryFromLegacy(relic.Slot, out _), relic.Id);
    }

    // ---- the /api/relics shape, deliberately unmoved ---------------------------------------

    /// <summary>
    /// ⭐ <b>The decision, pinned: the data moved and the response did not.</b> D1 §10 M2 requires
    /// the SSOT switch to leave the output shape unchanged, and the FE half of the relic surface is
    /// frozen (item-todo P5.4 records the web tree mid-refactor by the world-stage stream). So the
    /// migration keeps <c>/api/relics</c> byte-identical — six fields, these names, these values —
    /// and <c>RelicsLayer.tsx</c> needed no edit at all.
    /// </summary>
    [Fact]
    public void The_relics_endpoint_payload_is_unchanged_by_the_migration()
    {
        var json = JsonSerializer.Serialize(new RelicCatalogListDto { Items = RelicCatalog.Items.ToList() });

        using var doc = JsonDocument.Parse(json);
        var items = doc.RootElement.GetProperty("items");
        Assert.Equal(4, items.GetArrayLength());

        foreach (var item in items.EnumerateArray())
        {
            Assert.Equal(
                new[] { "id", "name", "rarity", "slot", "description", "effectId" },
                item.EnumerateObject().Select(p => p.Name));
        }

        // The four rows, field for field — the FE reads id/name/slot directly and adaptRelic()
        // reads rarity and description, so a change to any of them is a change to what renders.
        var first = items[0];
        Assert.Equal("relic.ashen_reliquary", first.GetProperty("id").GetString());
        Assert.Equal("Ashen Reliquary", first.GetProperty("name").GetString());
        Assert.Equal(4, first.GetProperty("rarity").GetInt32());
        Assert.Equal("weapon", first.GetProperty("slot").GetString());
        Assert.Equal("fx.passive_atk_flat", first.GetProperty("effectId").GetString());

        Assert.Equal(
            new[] { "relic.ashen_reliquary", "relic.sunworn_charm", "relic.tidewrack_band", "relic.cracked_seal" },
            items.EnumerateArray().Select(i => i.GetProperty("id").GetString()));
    }

    /// <summary>The relic definitions never lived in the retired table, so retiring it cannot
    /// delete them — the boundary `spec-equip-assign.md` names ("never retire
    /// `rpg_unique_equipment` before relics have a home"), asserted from the definition side.</summary>
    [Fact]
    public void The_relic_definitions_are_still_whole_and_still_resolvable_by_id()
    {
        foreach (var relic in RelicCatalog.Items)
        {
            Assert.True(RelicCatalog.IsKnownRelic(relic.Id));
            Assert.True(RelicCatalog.TryGetRelic(relic.Id, out var got));
            Assert.False(string.IsNullOrWhiteSpace(got.Name));
            Assert.False(string.IsNullOrWhiteSpace(got.Description));
            Assert.False(string.IsNullOrWhiteSpace(got.EffectId));
            Assert.InRange(got.Rarity, 1, 4);
            Assert.True(UniqueEquipmentCatalog.IsKnownItem(relic.Id));
            Assert.True(UniqueEquipmentCatalog.TryGetGrant(relic.Id, out _));
        }
    }

    // ---- why item_unique is not the definition home (yet) ----------------------------------

    /// <summary>
    /// ⭐ <b>The correction that closes the ping-pong, as a test rather than a note.</b> Module 4
    /// deferred the row migration because module 17 "did not exist to migrate them into." Module 17
    /// exists — and its <c>UniqueRow</c> still cannot receive a relic: it is a nine-column
    /// classification flag with no name, rarity, slot, description or effect id. An equipped-slot
    /// row was never going there; it belongs in <c>rpg_item_assignment</c>, which is module 4's own
    /// table. This asserts the shape so the wrong premise cannot be restated.
    /// </summary>
    [Fact]
    public void Module_17s_unique_row_carries_no_field_a_relic_definition_needs()
    {
        var fields = typeof(FusionRpg.Core.Items.Uniques.UniqueRow)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var missing in new[] { "Name", "Rarity", "Slot", "Role", "Description", "EffectId" })
            Assert.DoesNotContain(missing, fields);

        // What it does carry is a classification, keyed on a container.
        Assert.Contains("ContainerId", fields);
        Assert.Contains("CounterPressure", fields);
        Assert.Contains("PowerAxis", fields);
    }

    /// <summary>
    /// ⛔ <b>The second half of "relics become uniques" is a content decision, and this is why.</b>
    /// <c>item_unique</c> is keyed 1:1 on an <c>effect_container</c>. Half of the four relics share
    /// their container with a stub item, so flagging the container would flag the stub too. Making
    /// the disposition literal needs a dedicated container per relic plus three authored values
    /// (<c>counter_pressure</c>, <c>power_axis</c>, <c>derived_from</c>) — which
    /// `spec-equip-assign.md`'s Boundaries mark **Ask first**. Pinned so the blocker is a measured
    /// fact and not a remembered one.
    ///
    /// <para>2026-09-06: <c>relic.cracked_seal</c> (fx.entity_atk) moved off "no container at all" —
    /// T6.1's own migration gave it a real, deliberately empty container, shared with
    /// <c>stub.hp_charm</c>. This does not weaken the argument; it strengthens it (now half the
    /// relics, not one quarter, would flag their shared stub) and the "no container at all" case this
    /// test used to also cover is simply gone.</para>
    /// </summary>
    [Fact]
    public void Half_the_relics_share_a_container_with_a_stub_so_none_can_be_flagged_a_unique_today()
    {
        var containerByRelic = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var relic in RelicCatalog.Items)
        {
            if (UniqueEquipmentCatalog.TryGetAtomBackedContainerId(relic.Id, out var containerId))
                containerByRelic[relic.Id] = containerId;
        }

        // Every relic now resolves to a real container (fx.entity_atk's own migration closed the
        // last "no container at all" case) — the blocker moved from "some have none" to "sharing"
        // alone, which is still enough on its own to refuse the disposition.
        Assert.Equal(4, containerByRelic.Count);

        // relic.ashen_reliquary shares stub.atk_ring's container; relic.cracked_seal shares
        // stub.hp_charm's — an item_unique row on either would classify its stub as a unique too.
        Assert.True(UniqueEquipmentCatalog.TryGetAtomBackedContainerId("stub.atk_ring", out var atkRingContainer));
        Assert.Equal(containerByRelic["relic.ashen_reliquary"], atkRingContainer);
        Assert.True(UniqueEquipmentCatalog.TryGetAtomBackedContainerId("stub.hp_charm", out var hpCharmContainer));
        Assert.Equal(containerByRelic["relic.cracked_seal"], hpCharmContainer);

        // relic.sunworn_charm/relic.tidewrack_band's own containers are not shared with any stub
        // today — they are blocked by the missing counter_pressure/power_axis/derived_from values
        // alone, not by sharing, which is exactly why the Ask-first gate names BOTH reasons.
        Assert.DoesNotContain(containerByRelic["relic.sunworn_charm"],
            new[] { atkRingContainer, hpCharmContainer });
        Assert.DoesNotContain(containerByRelic["relic.tidewrack_band"],
            new[] { atkRingContainer, hpCharmContainer });
    }
}
