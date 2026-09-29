namespace FusionRpg.Core.Power;

/// <summary>
/// Host-injected <see cref="PowerTuning"/> (tunables-ssot.md §7.2: Core parses a stream, the host
/// reads the file and calls <see cref="Configure"/> once at startup). No built-in default — a
/// missing <see cref="Configure"/> call is a startup-ordering bug, not something to paper over.
/// </summary>
public static class PowerTuningHub
{
    static PowerTuning? _tuning;

    public static void Configure(PowerTuning tuning) =>
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));

    public static PowerTuning Tuning => _tuning ?? throw new InvalidOperationException(
        "PowerTuningHub.Configure(...) has not run. Hosts read data/tuning/power-scale.v{n}.json " +
        "and call Configure at startup — there is no built-in default to fall back to.");
}
