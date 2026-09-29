namespace FusionRpg.Core.Items;

/// <summary>
/// I2's alias map (<c>ssot-equip-slots.md</c> §5.7 step 1, restated by
/// <c>decision-d1-durable-ownership.md</c> §10 M1): the three legacy
/// <c>rpg_unique_equipment.slot</c> strings and the canonical <see cref="ItemRole"/> each one means.
///
/// <para><b>Why this exists at all.</b> Module 4's durable record is
/// <c>rpg_item_assignment(specimen_id, role, …)</c>, keyed on a canonical fifteen-value role
/// vocabulary. The shipped equipment wire — <c>GET/PUT/DELETE /api/unique/actors/{id}/equipment</c>,
/// <c>RelicDto.Slot</c>, and <c>web/…/layers/relics/RelicsLayer.tsx</c> — still speaks
/// <c>weapon|armor|trinket</c>. D1 §10 M2 is explicit that the SSOT switch keeps the
/// <b>output shape unchanged</b>, so this map is the seam: canonical roles in the table, legacy
/// labels on the wire, and exactly one place that knows both.</para>
///
/// <para><b>Structural, not tunable — deliberately not a <c>gk-core/data/tuning/*.json</c> row.</b> These
/// three strings are a closed set of *already-shipped persisted values*: a balance pass can never
/// want to change what <c>"weapon"</c> historically meant in a row already written to a player's
/// database. Changing one would not retune the game, it would silently re-home saved equipment.
/// That is the <c>tunables-ssot.md</c> "changing it breaks whether the system works rather than how
/// the game feels" test, answered on the structural side.</para>
///
/// <para><b>Closed and one-way-widening.</b> Widening the wire to the full fifteen roles is D1 §10's
/// <b>M3</b>, a separate step that changes the REST payload and the FE literal. Until then
/// <see cref="TryFromLegacy"/> is the only door: a caller handing in a canonical role id that is not
/// one of these three is refused, rather than being quietly accepted into a wire the FE cannot
/// render.</para>
/// </summary>
public static class LegacyEquipSlots
{
    /// <summary>The three legacy slot labels, in the order the shipped allowlist declares them
    /// (<c>UniqueEquipmentCatalog.DefaultSlots</c>) — order is observable via
    /// <c>ORDER BY slot</c> on the shipped equipment list, so it is pinned here too.</summary>
    public const string Weapon = "weapon";
    public const string Armor = "armor";
    public const string Trinket = "trinket";

    static readonly (string Legacy, ItemRole Role)[] Pairs =
    {
        (Weapon, ItemRole.ArmamentPrimary),
        (Armor, ItemRole.CoreGuard),
        (Trinket, ItemRole.JewelMinorA),
    };

    /// <summary>The three legacy labels, lowercase, in allowlist order.</summary>
    public static IReadOnlyList<string> All { get; } = Pairs.Select(p => p.Legacy).ToArray();

    /// <summary>The three canonical roles the legacy wire can currently address.</summary>
    public static IReadOnlyList<ItemRole> Roles { get; } = Pairs.Select(p => p.Role).ToArray();

    /// <summary>Legacy slot label → canonical role. Case-insensitive, trimmed; false for anything
    /// outside the three.</summary>
    public static bool TryFromLegacy(string? slot, out ItemRole role)
    {
        role = default;
        if (string.IsNullOrWhiteSpace(slot)) return false;
        var s = slot.Trim();
        foreach (var (legacy, r) in Pairs)
        {
            if (string.Equals(legacy, s, StringComparison.OrdinalIgnoreCase)) { role = r; return true; }
        }
        return false;
    }

    /// <summary>Canonical role → legacy slot label. False for the twelve roles the legacy wire
    /// cannot name — an assignment in one of those is real and durable, it simply has no
    /// <c>weapon|armor|trinket</c> spelling until M3 widens the payload.</summary>
    public static bool TryToLegacy(ItemRole role, out string slot)
    {
        foreach (var (legacy, r) in Pairs)
        {
            if (r == role) { slot = legacy; return true; }
        }
        slot = "";
        return false;
    }
}
