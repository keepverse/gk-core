namespace FusionRpg.Core.Stats.Aptitudes;

/// <summary>
/// The aptitude tuning hub — class-system-todo.md P2.1, spec-aptitude-tuning.md §4. Host-only
/// `Configure` (Injector/`RpgHost`, Server/`Program.cs`, or a test's inline construction); every
/// consumer reads <see cref="Tuning"/>. No built-in default — `data/tuning/aptitudes.v{n}.json` (the
/// version a host's own startup code names — currently v2, class-system-todo.md P8.2/P8.3) is the
/// only source (tunables-ssot.md §7.2).
/// </summary>
public static class AptitudeTuningHub
{
    static AptitudeTuning? _tuning;

    /// <summary>Host-only (Injector/Server startup, or a test's inline construction).</summary>
    public static void Configure(AptitudeTuning tuning) =>
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));

    public static AptitudeTuning Tuning => _tuning ?? throw new InvalidOperationException(
        "AptitudeTuningHub.Configure(...) has not run. Every aptitude read goes through data/tuning/" +
        "aptitudes.v{n}.json (tunables-ssot.md §7.2) — there is no built-in default to fall back to.");

    /// <summary>Whether a host has loaded the tuning yet — the same additive probe every sibling hub
    /// carries (`AptitudePresetTuningHub.IsConfigured`, `CommanderDirectoryHub.IsConfigured`), for the
    /// readers that must degrade rather than throw when a caller never needed this file (the world-turn
    /// seam's best-effort commander-pool read, EP4.18). Reading <see cref="Tuning"/> still throws.</summary>
    public static bool IsConfigured => _tuning is not null;
}
