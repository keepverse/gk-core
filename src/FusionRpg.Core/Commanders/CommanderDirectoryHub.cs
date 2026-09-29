namespace FusionRpg.Core.Commanders;

/// <summary>
/// The one process-wide commander directory, configured by the host that read the authored registry
/// (<c>gk-data/packs/fusion/data/seed/commanders/_registry/default-commanders.v1.json</c>) — Core never touches a path, the
/// same rule every other Core catalog hub follows. Readers that cannot take an injected instance — the
/// Data layer's persisted-string encoders, the injector's session cache — resolve through here.
///
/// <para>A reader with no configured directory throws rather than invent a commander: a guessed stable
/// id or scope key would be written into persisted rows, which is a save-compatibility defect.</para>
/// </summary>
public static class CommanderDirectoryHub
{
    static ICommanderDirectory? _current;

    public static void Configure(ICommanderDirectory directory) =>
        _current = directory ?? throw new ArgumentNullException(nameof(directory));

    public static ICommanderDirectory Current =>
        _current ?? throw new InvalidOperationException(
            "CommanderDirectoryHub.Configure(...) has not run — no commander directory is configured");

    public static bool IsConfigured => _current is not null;
}
