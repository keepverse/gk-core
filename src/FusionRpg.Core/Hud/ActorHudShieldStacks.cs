using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Combat.Shield;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Hud;

/// <summary>HP-weighted shield stack aggregation for HUD observe payloads.</summary>
public static class ActorHudShieldStacks
{
    public static IReadOnlyList<ActorHudShieldStack> AggregateByElement(IReadOnlyList<ShieldInstance> shields)
    {
        if (shields == null || shields.Count == 0)
            return Array.Empty<ActorHudShieldStack>();

        var ordered = new List<(string Element, long Hp, long Max, int Order)>();
        for (var i = 0; i < shields.Count; i++)
        {
            var s = shields[i];
            if (s.Hp <= 0 && s.MaxHp <= 0)
                continue;
            var element = ElementId(s.Element);
            ordered.Add((element, s.Hp, s.MaxHp, i));
        }

        if (ordered.Count == 0)
            return Array.Empty<ActorHudShieldStack>();

        var byElement = new Dictionary<string, (long Hp, long Max, int Order)>(StringComparer.Ordinal);
        foreach (var row in ordered)
        {
            if (byElement.TryGetValue(row.Element, out var existing))
            {
                byElement[row.Element] = (
                    existing.Hp + row.Hp,
                    existing.Max + row.Max,
                    Math.Min(existing.Order, row.Order));
            }
            else
            {
                byElement[row.Element] = (row.Hp, row.Max, row.Order);
            }
        }

        return byElement
            .Select(kv => (Element: kv.Key, kv.Value.Hp, kv.Value.Max, kv.Value.Order))
            .OrderBy(x => x.Order)
            .ThenBy(x => x.Element, StringComparer.Ordinal)
            .Select(x => new ActorHudShieldStack(x.Element, x.Hp, x.Max))
            .ToList();
    }

    public static (long Hp, long Max) Totals(IReadOnlyList<ShieldInstance> shields)
    {
        if (shields == null || shields.Count == 0)
            return (0, 0);

        long hp = 0, max = 0;
        for (var i = 0; i < shields.Count; i++)
        {
            hp += shields[i].Hp;
            max += shields[i].MaxHp;
        }

        return (hp, max);
    }

    static string ElementId(ElementTypeId? element) =>
        element is null ? "none" : ElementTable.IdOf(element.Value);
}
