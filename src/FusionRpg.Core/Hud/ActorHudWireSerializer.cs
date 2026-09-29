namespace FusionRpg.Core.Hud;

/// <summary>Observe wire shape — camelCase nested dict for entity.stats / debug.board-stats.</summary>
public static class ActorHudWireSerializer
{
    public static Dictionary<string, object> ToDictionary(ActorHudSnapshot snapshot)
    {
        var identity = new Dictionary<string, object>
        {
            ["tier"] = TierWire(snapshot.Identity.Tier),
            ["role"] = snapshot.Identity.Role,
            ["flags"] = snapshot.Identity.Flags.ToArray(),
        };
        if (snapshot.Identity.LevelBand is int band)
            identity["levelBand"] = band;

        var d = new Dictionary<string, object>
        {
            ["identity"] = identity,
            ["statuses"] = snapshot.Statuses.Select(StatusWire).ToArray(),
            ["overflow"] = new Dictionary<string, object>
            {
                ["statusCount"] = snapshot.Overflow.StatusCount,
            },
        };

        if (snapshot.Resources is not null)
        {
            var resources = new Dictionary<string, object>();
            if (snapshot.Resources.Shield is ActorHudShield shield)
            {
                resources["shield"] = new Dictionary<string, object>
                {
                    ["hp"] = shield.Hp,
                    ["max"] = shield.Max,
                    ["stacks"] = shield.Stacks.Select(s => new Dictionary<string, object>
                    {
                        ["element"] = s.Element,
                        ["hp"] = s.Hp,
                        ["max"] = s.Max,
                    }).ToArray(),
                };
            }

            if (snapshot.Resources.HpSliver is ActorHudHpSliver sliver)
                resources["hpSliver"] = new Dictionary<string, object> { ["ratio"] = sliver.Ratio };

            if (snapshot.Resources.Meters is { Count: > 0 } meters)
            {
                resources["meters"] = meters.Select(m => new Dictionary<string, object>
                {
                    ["id"] = m.Id,
                    ["ratio"] = m.Ratio,
                }).ToArray();
            }

            if (resources.Count > 0)
                d["resources"] = resources;
        }

        if (snapshot.Elements is ActorHudElements elements)
        {
            var elementWire = new Dictionary<string, object>
            {
                ["primary"] = elements.Primary,
            };
            if (!string.IsNullOrWhiteSpace(elements.Secondary))
                elementWire["secondary"] = elements.Secondary!;
            d["elements"] = elementWire;
        }

        return d;
    }

    static Dictionary<string, object> StatusWire(ActorHudStatusToken token) => new()
    {
        ["id"] = token.Id,
        ["cc"] = token.Cc,
        ["magnitudeBand"] = BandWire(token.MagnitudeBand),
    };

    static string TierWire(ActorHudTier tier) => tier switch
    {
        ActorHudTier.Unique => "unique",
        ActorHudTier.Elite => "elite",
        ActorHudTier.Boss => "boss",
        _ => "normal",
    };

    static string BandWire(MagnitudeBand band) => band switch
    {
        MagnitudeBand.Mid => "mid",
        MagnitudeBand.High => "high",
        _ => "low",
    };
}
