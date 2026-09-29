namespace FusionRpg.Core.Actions;

/// <summary>Config-backed action-timing tuning (tunables-ssot.md T1), mirroring
/// <see cref="Rungs.RungPolicy"/>'s own shape exactly — a static holder configured once at host
/// startup, read explicitly wherever the timing derivation runs (`RpgStore.BuildActionCatalog`), the
/// same "passed explicitly, not read from inside" idiom that method already uses for
/// <see cref="Rungs.RungPolicy.Table"/>.</summary>
public static class ActionTimingPolicy
{
    static ActionTimingTuning? _tuning;

    public static void Configure(ActionTimingTuning tuning) =>
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));

    public static ActionTimingTuning Tuning => _tuning ?? throw new InvalidOperationException(
        "ActionTimingPolicy.Configure(...) has not run. Every action's timing envelope reads " +
        "data/tuning/action-timing.v{n}.json — there is no built-in default to fall back to.");
}
