namespace FusionRpg.Core.Effects.Plugins;

/// <summary>Default Secondary grant plugins for offline sim and LIVE Injector.</summary>
public static class SecondaryPluginRegistry
{
    /// <summary>Production match-start plugins only — no prove-era auto-grants.</summary>
    public static IEnumerable<IEffectGrantPlugin> CreateDefault()
    {
        yield return new PatronSecondaryPlugin();
    }

    /// <summary>Early-prove match auto-grants — register explicitly in tests, not LIVE default.</summary>
    public static IEnumerable<IEffectGrantPlugin> CreateProve()
    {
        yield return new MatchButterSecondaryPlugin();
        yield return new MatchPassiveAtkSecondaryPlugin();
    }

    public static void RegisterById(EffectPluginHost host, IEnumerable<string> pluginIds)
    {
        foreach (var id in pluginIds)
        {
            switch (id)
            {
                case "sec.match.butter":
                    host.Register(new MatchButterSecondaryPlugin());
                    break;
                case "sec.match.passive_atk":
                    host.Register(new MatchPassiveAtkSecondaryPlugin());
                    break;
                case "sec.patron.aura":
                    host.Register(new PatronSecondaryPlugin());
                    break;
                default:
                    throw new InvalidOperationException("unknown Secondary plugin id: " + id);
            }
        }
    }
}
