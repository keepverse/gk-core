namespace FusionRpg.Core.Items;

/// <summary>
/// Which <see cref="ClassLadder"/> a role draws from, per `words.v1.json poolAccess.roleToLadders`
/// (spec-base-types.md). `armament-secondary` draws from two (`weapon` and `offhand` — it may accept
/// an armament base type as a pseudo-dual-wield second weapon); the dominance lint uses its first,
/// primary ladder (`weapon`), the one every one of its own class-ladder rows actually lists it under.
/// </summary>
public static class BaseTypeSlate
{
    static readonly IReadOnlyDictionary<string, ClassLadder> RoleLadder = new Dictionary<string, ClassLadder>(StringComparer.Ordinal)
    {
        ["armament-primary"] = ClassLadder.Weapon,
        ["armament-secondary"] = ClassLadder.Weapon,
        ["core-guard"] = ClassLadder.Armour,
        ["ward-array"] = ClassLadder.Armour,
        ["manipulator"] = ClassLadder.Armour,
        ["mantle"] = ClassLadder.Armour,
        ["head-guard"] = ClassLadder.Armour,
        ["girdle"] = ClassLadder.Armour,
        ["sense"] = ClassLadder.Armour,
        ["footing"] = ClassLadder.Armour,
        ["infusion"] = ClassLadder.Armour,
        ["jewel-major"] = ClassLadder.Jewel,
        ["jewel-minor-a"] = ClassLadder.Jewel,
        ["jewel-minor-b"] = ClassLadder.Jewel,
        ["retinue"] = ClassLadder.Jewel,
        ["standard"] = ClassLadder.Standard,
    };

    public static ClassLadder LadderOf(string roleId) =>
        RoleLadder.TryGetValue(roleId, out var l)
            ? l
            : throw new KeyNotFoundException($"base-type-slate: role '{roleId}' names no class ladder");

    public static bool TryLadderOf(string roleId, out ClassLadder ladder) =>
        RoleLadder.TryGetValue(roleId, out ladder);
}
