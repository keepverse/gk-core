namespace FusionRpg.Core.World;

/// <summary>
/// A legion's display name (spec-world-playback.md §4): "Legion I", never "E Dave Legion 1" — a
/// raw kebab id like <c>e-dave-legion-1</c> is not derivable into a name client-side without
/// inventing one, so this is a `world-wire` field, computed once, server-side, from stable state
/// (world-stage W8).
///
/// Pure over <see cref="WorldState"/> — no persisted counter, no field on <see cref="WorldEntity"/>
/// itself: the ordinal is each entity's rank, by stable id order, among its own faction's entities of
/// the same <see cref="WorldEntityKind"/>. Deterministic and replay-safe without being hashed state.
/// </summary>
public static class EntityNaming
{
    public static string DisplayName(WorldState world, WorldEntity entity)
    {
        var ordinal = 1;
        foreach (var candidate in world.Entities.OrderBy(e => e.EntityId, StringComparer.Ordinal))
        {
            if (string.Equals(candidate.EntityId, entity.EntityId, StringComparison.Ordinal)) break;
            if (candidate.Kind == entity.Kind &&
                string.Equals(candidate.OwnerFactionId, entity.OwnerFactionId, StringComparison.Ordinal))
                ordinal++;
        }

        return $"{KindLabel(entity.Kind)} {RomanNumeral(ordinal)}";
    }

    static string KindLabel(WorldEntityKind kind) => kind switch
    {
        WorldEntityKind.Legion => "Legion",
        WorldEntityKind.Warband => "Warband",
        WorldEntityKind.Guard => "Guard",
        WorldEntityKind.Caravan => "Caravan",
        WorldEntityKind.Warlord => "Warlord",
        _ => kind.ToString()
    };

    // Roman numeral place values, high to low — a structural constant (the notation itself, not a
    // balance number), same exemption `tunables-ssot.md` grants a bounded ratio or retention tail.
    static readonly (int Value, string Symbol)[] RomanPlaces =
    {
        (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"),
        (100, "C"), (90, "XC"), (50, "L"), (40, "XL"),
        (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I"),
    };

    static string RomanNumeral(int value)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var (placeValue, symbol) in RomanPlaces)
            while (value >= placeValue)
            {
                sb.Append(symbol);
                value -= placeValue;
            }
        return sb.ToString();
    }
}
