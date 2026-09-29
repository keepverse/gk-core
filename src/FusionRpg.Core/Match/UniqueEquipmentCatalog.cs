using System.Text.Json;
using FusionRpg.Contracts;

namespace FusionRpg.Core.Match;

/// <summary>
/// Stub item_id → grant template map for W8-A Cold equip (not a gear shop).
/// EffectIds align with offline effect fixtures where possible.
///
/// <para><b>⚠ Deliberately still live after the 2026-09-06 relic row migration — stated, not an
/// oversight.</b> `decision-d1-durable-ownership.md` §10 retires this class's <see cref="Items"/>
/// dictionary at <b>M4</b>: *"for `ref_kind = 'rolled'`, read `effect_instance` and take compiled
/// grants from E7 rather than `UniqueEquipmentCatalog.Items`. Then drop `rpg_unique_equipment` and
/// `UniqueEquipmentCatalog.Items`."* That precondition is genuinely absent: no concrete unique
/// container has been minted (module 17's own top-listed deferral — the seed→concrete generator is
/// the runtime generator's under a binding repo rule), so there is no rolled grant path to replace
/// the stub template with. Retiring it today would leave equipping with no grant source at all.</para>
///
/// <para><b>What DID move (M1/M2):</b> the per-actor equipped-slot rows, from
/// <c>rpg_unique_equipment</c> to module 4's <c>rpg_item_assignment</c>. This class keeps exactly
/// four live jobs — the item-id allowlist (<see cref="IsKnownItem"/>,
/// <see cref="SlotMatchesItem"/>), the legacy slot validator (<see cref="NormalizeSlot"/>,
/// <see cref="IsAllowedSlot"/>), the atom-backed container map
/// (<see cref="TryGetAtomBackedContainerId"/>), and the legacy grant blob
/// (<see cref="BuildModsJson"/>). None of them reads or writes the retired table.
/// <c>LegacyEquipTableRetirementTests</c> pins that.</para>
/// </summary>
public static class UniqueEquipmentCatalog
{
    public static readonly string[] DefaultSlots = { "weapon", "armor", "trinket" };

    static readonly HashSet<string> AllowedSlots =
        new(DefaultSlots, StringComparer.OrdinalIgnoreCase);

    static readonly string[] FlatAbsoluteKeys = { "hp", "maxHp", "atk", "HP", "MaxHp", "ATK" };

    /// <summary>Known stub items operators may equip.
    /// // DEBT stub — cold-equip-one: this dictionary is an item-id allowlist only, not combat
    /// SSOT. Magnitudes come from the bound atoms' own seeded containers (reconciler binds them;
    /// mods_json grants are skipped for atom-backed items). Delete when a rolled-grant path replaces
    /// the stub definitions — blocked today: no concrete unique container has been minted, so there
    /// is no rolled grant source to replace these definitions with (see class doc).</summary>
    public static IReadOnlyDictionary<string, EffectGrantDto> Items { get; } =
        new Dictionary<string, EffectGrantDto>(StringComparer.OrdinalIgnoreCase)
        {
            ["stub.atk_ring"] = Grant("equip-stub-atk", "fx.passive_atk_flat"),
            ["stub.butter_bead"] = Grant("equip-stub-butter", "fx.butter_on_hit"),
            ["stub.hp_charm"] = Grant("equip-stub-hp", "fx.entity_atk") // placeholder effect id for bag prove
        };

    /// <summary>
    /// `mods-absorption` (T6.1, `spec-mods-absorption.md`): which of the shipped `EffectId`s already
    /// have a real, seeded atom (`gk-data/packs/fusion/data/seed/containers/unique-equip.json`, wrapping the SAME atoms
    /// `EffectAtomCatalog.Generated.cs` already compiles from — found real, not invented, 2026-09-02).
    ///
    /// <para><c>fx.entity_atk</c> (2026-09-06): its own doc comment on <see cref="Items"/> already
    /// called it a placeholder id with no real effect behind it, verified by grep across every seed
    /// file at the time — nothing produced it. Migrated here with a deliberately EMPTY atom list
    /// (`item.fx-entity-atk`, zero atoms — the exact fixed-core-marker shape `patron.aura` shipped
    /// with before patron-absorption filled it in): this closes T6.1's own "never both paths" gap for
    /// the last two legacy items (`stub.hp_charm`, `relic.cracked_seal`) WITHOUT inventing a real
    /// effect or a balance number — the placeholder's current no-op behavior is preserved exactly, not
    /// changed. Giving it a real magnitude is a separate, genuine game-balance decision (what should a
    /// charm/relic trinket actually grant), correctly left for that decision to make, not this
    /// migration.</para>
    /// </summary>
    static readonly IReadOnlyDictionary<string, string> AtomBackedContainerByEffectId =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["fx.passive_atk_flat"] = "item.fx-passive-atk-flat",
            ["fx.butter_on_hit"] = "item.fx-butter-on-hit",
            ["fx.shield_grant"] = "item.fx-shield-grant",
            ["fx.cold_on_hit"] = "item.fx-cold-on-hit",
            ["fx.entity_atk"] = "item.fx-entity-atk",
        };

    /// <summary>The real container id for an equipped item/relic's own effect, or null when none
    /// exists yet (the item stays on the legacy `mods_json` grant path for that case).</summary>
    public static bool TryGetAtomBackedContainerId(string? itemId, out string containerId)
    {
        containerId = "";
        if (!TryGetGrant(itemId, out var grant)) return false;
        return AtomBackedContainerByEffectId.TryGetValue(grant.EffectId, out containerId!);
    }

    public static bool IsAllowedSlot(string? slot)
    {
        if (string.IsNullOrWhiteSpace(slot)) return false;
        return AllowedSlots.Contains(slot.Trim());
    }

    public static bool IsKnownItem(string? itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId)) return false;
        var id = itemId.Trim();
        return Items.ContainsKey(id) || RelicCatalog.IsKnownRelic(id);
    }

    public static bool TryGetGrant(string? itemId, out EffectGrantDto grant)
    {
        grant = null!;
        if (string.IsNullOrWhiteSpace(itemId)) return false;
        var id = itemId.Trim();
        if (Items.TryGetValue(id, out var g) && g is not null)
        {
            grant = Clone(g);
            return true;
        }
        return RelicCatalog.TryGetGrant(id, out grant);
    }

    /// <summary>True unless the item is a known relic declared for a different slot.
    /// Stub items (<see cref="Items"/>) carry no slot of their own, so any allowed slot fits them.</summary>
    public static bool SlotMatchesItem(string normalizedSlot, string itemId)
    {
        if (!RelicCatalog.TryGetRelic(itemId, out var relic)) return true;
        return string.Equals(relic.Slot, normalizedSlot, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Normalize to lowercase allowlisted slot; throws on empty/unknown.</summary>
    public static string NormalizeSlot(string? slot)
    {
        var s = (slot ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(s) || !AllowedSlots.Contains(s))
            throw new ArgumentException("slot required", nameof(slot));
        return s;
    }

    /// <summary>
    /// Build mods_json: keep existing absolutes (nested + flat root keys); replace grants from equipped slots.
    /// GrantIds are stamped <c>base:slot</c> so the same stub in two slots does not collapse.
    ///
    /// <para><b>`mods-absorption` (spec-mods-absorption.md) — the grant half stops here for
    /// atom-backed items.</b> An item <see cref="TryGetAtomBackedContainerId"/> maps now grants
    /// exclusively through <c>effect_binding</c>
    /// (<c>RpgStore.ReconcileUniqueEquipmentAtomBindingsUnlocked</c>, called alongside this rebuild on
    /// every equip/unequip) — this method never writes that same item's grant into <c>mods_json</c> too,
    /// or the actor would carry the same slot's effect through both paths at once, exactly the
    /// double-grant the migration exists to close. As of 2026-09-06 every shipped `EffectId`
    /// (including `fx.entity_atk`, migrated to a deliberately empty atom-backed container — see
    /// <see cref="AtomBackedContainerByEffectId"/>'s own doc comment) now maps here, so this loop's
    /// `TryGetAtomBackedContainerId` skip is always taken for every currently-known item/relic — the
    /// legacy grant path stays live in the code (a future item with no atom yet would still need it),
    /// but nothing shipped today actually reaches it.</para>
    /// </summary>
    public static string BuildModsJson(
        string? existingModsJson,
        IEnumerable<(string Slot, string ItemId)> equipped)
    {
        var absolutes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(existingModsJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(existingModsJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    var root = doc.RootElement;
                    if (root.TryGetProperty("absolutes", out var abs) &&
                        abs.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var p in abs.EnumerateObject())
                        {
                            if (p.Value.TryGetInt32(out var n))
                                absolutes[p.Name] = n;
                        }
                    }

                    foreach (var key in FlatAbsoluteKeys)
                    {
                        if (root.TryGetProperty(key, out var flat) &&
                            flat.TryGetInt32(out var n) &&
                            !absolutes.ContainsKey(key))
                            absolutes[key] = n;
                    }
                }
            }
            catch
            {
                /* ignore bad prior json */
            }
        }

        var grants = new List<EffectGrantDto>();
        foreach (var (slotRaw, itemId) in equipped)
        {
            if (string.IsNullOrWhiteSpace(itemId)) continue;
            string slot;
            try { slot = NormalizeSlot(slotRaw); }
            catch { continue; }
            // Atom-backed: the reconciler already binds this slot's real effect through
            // effect_binding — never also stamp it into the legacy grant blob (the double-grant
            // invariant this module exists to close).
            if (TryGetAtomBackedContainerId(itemId, out _)) continue;
            if (!TryGetGrant(itemId, out var g)) continue;
            g.GrantId = $"{g.GrantId}:{slot}";
            grants.Add(g);
        }

        var payload = new Dictionary<string, object?>
        {
            ["absolutes"] = absolutes,
            ["grants"] = grants
        };
        return JsonSerializer.Serialize(payload);
    }

    static EffectGrantDto Grant(string grantId, string effectId) => new()
    {
        GrantId = grantId,
        EffectId = effectId,
        OwnerKind = "instance",
        OwnerKey = "instance:pending",
        PluginId = "unique.equip",
        Priority = 0
    };

    static EffectGrantDto Clone(EffectGrantDto g) => new()
    {
        GrantId = g.GrantId,
        EffectId = g.EffectId,
        OwnerKind = g.OwnerKind,
        OwnerKey = g.OwnerKey,
        PluginId = g.PluginId,
        Priority = g.Priority,
        Overlay = g.Overlay is null ? null : new Dictionary<string, object?>(g.Overlay)
    };
}
