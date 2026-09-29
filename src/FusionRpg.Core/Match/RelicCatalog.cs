using FusionRpg.Contracts;

namespace FusionRpg.Core.Match;

/// <summary>
/// A small, real, seeded relic catalog (T14 — no acquisition system exists yet, so every
/// player holds the full catalog; see game-gui-todo.md's honest scoping note). Effect ids are
/// drawn from the existing, already-shipped effect vocabulary — nothing new is added to
/// Foundation here.
///
/// <para><b>⭐ Relics now have a durable home: <c>rpg_item_assignment</c></b>
/// (`decision-d1-durable-ownership.md` §10 M1/M2, landed 2026-09-06). Equipping a relic no longer
/// touches <c>rpg_unique_equipment</c> — <c>RpgStore.UpsertUniqueEquipment</c> writes module 4's
/// assignment row with <c>ref_kind = 'stock'</c> and <c>ref_id</c> = the relic id below, and every
/// reader projects it back through <see cref="FusionRpg.Core.Items.LegacyEquipSlots"/>. The wire is
/// byte-identical, so <c>/api/relics</c> and <c>RelicsLayer.tsx</c> are unchanged. This class stays
/// the <b>definition</b> source (name, rarity, slot, description, effect id) and the item-id
/// allowlist; only the per-actor equipped-slot rows moved.</para>
///
/// <para><b>⚠ Why these four are not <c>item_unique</c> rows.</b> Module 17's <c>item_unique</c> is
/// a nine-column classification flag keyed 1:1 on an <c>effect_container</c> — it has no name,
/// rarity, slot, description or effect column, so it cannot hold a relic definition. Making
/// "relics become uniques" literal needs a dedicated container per relic — <b>two of the four share
/// one with a stub today</b>, so flagging either container would classify the stub as a unique too:
/// <c>item.fx-passive-atk-flat</c> backs <c>relic.ashen_reliquary</c> and <c>stub.atk_ring</c>, and
/// <c>item.fx-entity-atk</c> backs <c>relic.cracked_seal</c> and <c>stub.hp_charm</c>. ⚠ This
/// paragraph previously said <c>relic.cracked_seal</c> had <i>"no container at all"</i>; T6.1's own
/// migration (2026-09-06) gave it a real, deliberately empty one, which strengthens the argument
/// rather than weakening it. Pinned by
/// <c>Half_the_relics_share_a_container_with_a_stub_so_none_can_be_flagged_a_unique_today</c>. The
/// dedicated containers also need three authored content values per row — <c>counter_pressure</c>,
/// <c>power_axis</c> and a <c>derived_from</c> base type. `spec-equip-assign.md`'s Boundaries mark
/// the relic disposition **Ask first**; that half is a content decision, not this migration.</para>
/// </summary>
public static class RelicCatalog
{
    public static readonly IReadOnlyList<RelicDto> Items = new List<RelicDto>
    {
        new()
        {
            Id = "relic.ashen_reliquary",
            Name = "Ashen Reliquary",
            Rarity = 4,
            Slot = "weapon",
            Description = "A reliquary warm to the touch. Channels raw offense.",
            EffectId = "fx.passive_atk_flat"
        },
        new()
        {
            Id = "relic.sunworn_charm",
            Name = "Sunworn Charm",
            Rarity = 2,
            Slot = "weapon",
            Description = "A sun-bleached charm, favoring survival over aggression.",
            EffectId = "fx.shield_grant"
        },
        new()
        {
            Id = "relic.tidewrack_band",
            Name = "Tidewrack Band",
            Rarity = 3,
            Slot = "armor",
            Description = "Salt-crusted band pulled from a flooded lawn.",
            EffectId = "fx.cold_on_hit"
        },
        new()
        {
            Id = "relic.cracked_seal",
            Name = "Cracked Seal",
            Rarity = 1,
            Slot = "trinket",
            Description = "A minor ward, barely holding together.",
            EffectId = "fx.entity_atk"
        }
    };

    static readonly IReadOnlyDictionary<string, RelicDto> ById =
        Items.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);

    public static bool IsKnownRelic(string? relicId) =>
        !string.IsNullOrWhiteSpace(relicId) && ById.ContainsKey(relicId.Trim());

    public static bool TryGetRelic(string? relicId, out RelicDto relic)
    {
        relic = null!;
        if (string.IsNullOrWhiteSpace(relicId)) return false;
        return ById.TryGetValue(relicId.Trim(), out relic!);
    }

    public static bool TryGetGrant(string? relicId, out EffectGrantDto grant)
    {
        grant = null!;
        if (!TryGetRelic(relicId, out var relic)) return false;
        grant = new EffectGrantDto
        {
            GrantId = $"equip-relic-{relic.Id[6..]}",
            EffectId = relic.EffectId,
            OwnerKind = "instance",
            OwnerKey = "instance:pending",
            PluginId = "unique.equip",
            Priority = 0
        };
        return true;
    }
}
