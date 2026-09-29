using FusionRpg.Core.Stats.Plugins;

namespace FusionRpg.Core.Stats;

public static class StatSystemBootstrap
{
    /// <summary>Register default cheat + stub RPG plugins. Idempotent by plugin id.</summary>
    public static StatSystem CreateDefault()
    {
        var sys = new StatSystem();
        RegisterDefaults(sys);
        return sys;
    }

    public static void RegisterDefaults(StatSystem sys)
    {
        sys.Plugins.Register(new ClassStatPlugin());
        sys.Plugins.Register(new AchievementStatPlugin());
        sys.Plugins.Register(new ItemStatPlugin());
        sys.Plugins.Register(new BuffStatPlugin());
        sys.Plugins.Register(new PvzStatsPlugin());
        sys.Plugins.Register(new CheatScaleStatPlugin());
        sys.Plugins.Register(new CheatAbsoluteStatPlugin());
    }
}
