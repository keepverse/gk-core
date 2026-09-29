namespace FusionRpg.CheatCore;

/// <summary>Canonical cheat.action names understood by the injector command runner.</summary>
public static class CheatActionNames
{
    public static readonly HashSet<string> All = new(StringComparer.OrdinalIgnoreCase)
    {
        "reapply", "reapply-living", "push-now", "push-scales",
        "apply-plants", "apply-zombies",
        "spawn-plant", "spawn-zombie",
        "delete-plants", "delete-zombies", "kill-zombies",
        "hypno-all", "oneshot",
        "economy", "board-config", "load-board-config",
        "summon", "huge-wave", "wave-timer",
        "recipes", "reinforce", "set-zombie-hp",
        "spawn-pet", "spawn-grid", "spawn-bucket", "present",
        "travel-buff", "clear-selection", "clear-failed",
        "reset-all", "reset-group",
        "push-stats", "pull-stats", "set-spawn-cell",
        "save-persist", "reload-persist", "run-pack"
    };

    public static bool IsKnown(string action) => All.Contains(action.Trim());
}
