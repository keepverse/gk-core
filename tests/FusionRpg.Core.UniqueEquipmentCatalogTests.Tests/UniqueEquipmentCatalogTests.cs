using System.Text.Json;
using FusionRpg.Core.Match;
using Xunit;

namespace FusionRpg.Core.Tests;

public class UniqueEquipmentCatalogTests
{
    [Fact]
    public void NormalizeSlot_allowlist_only()
    {
        Assert.Equal("weapon", UniqueEquipmentCatalog.NormalizeSlot("Weapon"));
        Assert.Equal("armor", UniqueEquipmentCatalog.NormalizeSlot("ARMOR"));
        Assert.Throws<ArgumentException>(() => UniqueEquipmentCatalog.NormalizeSlot("hat"));
        Assert.Throws<ArgumentException>(() => UniqueEquipmentCatalog.NormalizeSlot(""));
        Assert.False(UniqueEquipmentCatalog.IsAllowedSlot("foo"));
        Assert.True(UniqueEquipmentCatalog.IsAllowedSlot("trinket"));
    }

    [Fact]
    public void IsKnownItem_stub_catalog()
    {
        Assert.True(UniqueEquipmentCatalog.IsKnownItem("stub.atk_ring"));
        Assert.False(UniqueEquipmentCatalog.IsKnownItem("stub.unknown"));
        Assert.False(UniqueEquipmentCatalog.IsKnownItem(""));
    }

    [Fact]
    public void IsKnownItem_and_TryGetGrant_recognize_real_relics()
    {
        Assert.True(UniqueEquipmentCatalog.IsKnownItem("relic.ashen_reliquary"));
        Assert.True(UniqueEquipmentCatalog.TryGetGrant("relic.ashen_reliquary", out var grant));
        Assert.Equal("fx.passive_atk_flat", grant.EffectId);
        Assert.Equal("equip-relic-ashen_reliquary", grant.GrantId);
    }

    [Fact]
    public void SlotMatchesItem_rejects_a_relic_in_the_wrong_slot_but_allows_stub_items_anywhere()
    {
        Assert.True(UniqueEquipmentCatalog.SlotMatchesItem("weapon", "relic.ashen_reliquary"));
        Assert.False(UniqueEquipmentCatalog.SlotMatchesItem("armor", "relic.ashen_reliquary"));
        Assert.True(UniqueEquipmentCatalog.SlotMatchesItem("armor", "stub.atk_ring"));
    }

    [Fact]
    public void BuildModsJson_excludes_a_relic_grant_too()
    {
        // 2026-09-06: relic.cracked_seal (fx.entity_atk) was the LAST relic still on the legacy grant
        // path — mods-absorption's own doc comment called it out by name. Migrated to a deliberately
        // empty atom-backed container (item.fx-entity-atk), same as the other three relics before it,
        // so every real relic now excludes its grant here — there is no more "includes a relic grant"
        // case left to prove against real content.
        var json = UniqueEquipmentCatalog.BuildModsJson(
            "{}",
            new[] { ("trinket", "relic.cracked_seal") });
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(0, doc.RootElement.GetProperty("grants").GetArrayLength());
    }

    [Fact]
    public void BuildModsJson_excludes_grant_for_atom_backed_items()
    {
        // mods-absorption (spec-mods-absorption.md): an atom-backed item's grant no longer reaches
        // mods_json at all — RpgStore.ReconcileUniqueEquipmentAtomBindingsUnlocked is its only path
        // now, or the actor would carry the same slot's effect through both at once.
        var json = UniqueEquipmentCatalog.BuildModsJson(
            "{}",
            new[] { ("weapon", "stub.atk_ring") });
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(0, doc.RootElement.GetProperty("grants").GetArrayLength());
    }

    [Fact]
    public void BuildModsJson_preserves_nested_and_flat_absolutes()
    {
        // stub.hp_charm is atom-backed now too (2026-09-06) — every known item/relic excludes its
        // grant here, so this test's own point (absolutes survive regardless of what's equipped) is
        // proven against a 0-grant result, not a 1-grant one.
        var json = UniqueEquipmentCatalog.BuildModsJson(
            """{"absolutes":{"hp":42},"atk":9}""",
            new[] { ("weapon", "stub.hp_charm") });
        using var doc = JsonDocument.Parse(json);
        var abs = doc.RootElement.GetProperty("absolutes");
        Assert.Equal(42, abs.GetProperty("hp").GetInt32());
        Assert.Equal(9, abs.GetProperty("atk").GetInt32());
        Assert.Equal(0, doc.RootElement.GetProperty("grants").GetArrayLength());
    }

    [Fact]
    public void BuildModsJson_same_atom_backed_item_in_two_slots_grants_nothing_in_either()
    {
        // Was "unique grantIds per slot" while stub.hp_charm was still legacy; now every known item is
        // atom-backed (2026-09-06), so the real regression to guard is that equipping the SAME
        // atom-backed item in two slots does not leak a grant into mods_json for either — each slot's
        // own real binding is effect_binding's job (ModsAbsorptionTests), never this method's.
        var json = UniqueEquipmentCatalog.BuildModsJson(
            "{}",
            new[]
            {
                ("weapon", "stub.hp_charm"),
                ("armor", "stub.hp_charm")
            });
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(0, doc.RootElement.GetProperty("grants").GetArrayLength());
    }

    [Fact]
    public void BuildModsJson_skips_unknown_item()
    {
        var json = UniqueEquipmentCatalog.BuildModsJson(
            """{"absolutes":{"hp":1}}""",
            new[] { ("weapon", "stub.nope") });
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(0, doc.RootElement.GetProperty("grants").GetArrayLength());
        Assert.Equal(1, doc.RootElement.GetProperty("absolutes").GetProperty("hp").GetInt32());
    }

    [Fact]
    public void UniqueLoadoutMerge_empty_deploy_keeps_equip_mods()
    {
        // Every known item is atom-backed now (2026-09-06), so mods_json's own grants list is always
        // empty for real content — this proves the merge preserves equip's own content (its absolutes)
        // through a null deploy, via the absolutes half instead of the now-always-empty grants half.
        var mods = UniqueEquipmentCatalog.BuildModsJson(
            """{"absolutes":{"hp":7}}""",
            new[] { ("trinket", "stub.hp_charm") });
        var merged = UniqueLoadoutMerge.Merge(null, mods);
        Assert.Equal(mods, merged);
        Assert.Contains("\"hp\":7", merged.Replace(" ", ""), StringComparison.Ordinal);
        var deployWins = UniqueLoadoutMerge.Merge("""{"absolutes":{"hp":1}}""", mods);
        Assert.Contains("\"hp\":1", deployWins.Replace(" ", ""), StringComparison.Ordinal);
        Assert.DoesNotContain("\"hp\":7", deployWins.Replace(" ", ""), StringComparison.Ordinal);
    }
}
