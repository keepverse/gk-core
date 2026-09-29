namespace FusionRpg.Core.Saves;

/// <summary>
/// The one process-wide new-save registries, configured by the host that read the authored file
/// (<c>gk-data/packs/fusion/data/seed/saves/_registry/new-save-empires.v1.json</c>) — Core never touches a path, the same
/// rule every other Core catalog hub follows. The store seeds a save's empires from here; a reader
/// with nothing configured throws rather than invent them.
/// </summary>
public static class NewSaveEmpiresHub
{
    static NewSaveEmpires? _current;

    public static void Configure(NewSaveEmpires registry) =>
        _current = registry ?? throw new ArgumentNullException(nameof(registry));

    public static NewSaveEmpires Current =>
        _current ?? throw new InvalidOperationException(
            "NewSaveEmpiresHub.Configure(...) has not run — no new-save empires registry is configured");

    public static bool IsConfigured => _current is not null;

    /// <summary>Tests only — drop the configuration, to prove a host that never configured still boots
    /// (the store resolves the authored file itself). Callers restore it afterwards.</summary>
    internal static void ResetForTests() => _current = null;
}
