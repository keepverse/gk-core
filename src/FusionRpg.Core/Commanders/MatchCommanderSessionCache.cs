using FusionRpg.Core.Stats.Aptitudes;

namespace FusionRpg.Core.Commanders;

/// <summary>
/// Injector session cache for commander snapshot data — populated by background REST refresh, read
/// synchronously at <c>board.start</c> (never await HTTP inside <c>MatchHost.Apply</c>).
///
/// <para><b>The implicit fallback is data, not a name.</b> A cache miss reports the human empire's
/// default commander and its display name through the injected <see cref="ICommanderDirectory"/>. No
/// player is known here, so the directory answers with its neutral authored label — never "Crazy Dave"
/// (which is the player's own name now, ruling 2026-09-18, and would be another person's name on
/// someone else's machine).</para>
/// </summary>
public static class MatchCommanderSessionCache
{
    static readonly object Gate = new();
    static bool _hasCache;
    static string _defaultId = "";
    static string _displayName = "";
    static string? _activeAuraId;
    static string? _activeAuraName;
    static AptitudeAllocation _allocation = AptitudeAllocation.Empty;
    static long _cacheRevision;

    /// <summary>True when the last <see cref="BuildFromSessionCache"/> used the neutral fallback.</summary>
    public static bool LastBuildUsedFallback { get; private set; }

    public static long CacheRevision
    {
        get { lock (Gate) return _cacheRevision; }
    }

    /// <summary>
    /// The directory the cache resolves through — <see cref="CommanderDirectoryHub.Current"/>, one
    /// process-wide configuration point shared with the store's persisted-string encoders.
    /// </summary>
    public static void Configure(ICommanderDirectory directory) => CommanderDirectoryHub.Configure(directory);

    public static void Apply(
        string defaultLawnCommanderId,
        string leadingDisplayName,
        string? activeAuraId,
        string? activeAuraName,
        AptitudeAllocation allocation)
    {
        var directory = RequireDirectory();
        if (string.IsNullOrWhiteSpace(defaultLawnCommanderId)
            || !directory.TryResolve(defaultLawnCommanderId, out var commander))
            return;

        lock (Gate)
        {
            _defaultId = defaultLawnCommanderId.Trim();
            _displayName = string.IsNullOrWhiteSpace(leadingDisplayName)
                ? directory.DisplayName(commander, playerName: null)
                : leadingDisplayName;
            _activeAuraId = activeAuraId;
            _activeAuraName = activeAuraName;
            _allocation = allocation;
            _hasCache = true;
            checked { _cacheRevision++; }
        }
    }

    public static MatchCommanderSnapshot BuildFromSessionCache()
    {
        var directory = RequireDirectory();
        lock (Gate)
        {
            if (!_hasCache || !directory.TryResolve(_defaultId, out _))
            {
                LastBuildUsedFallback = true;
                return NeutralFallback(directory, revision: 0);
            }

            LastBuildUsedFallback = false;
            var rev = _cacheRevision;
            return new MatchCommanderSnapshot(
                _defaultId,
                _displayName,
                _activeAuraId,
                _activeAuraName,
                _allocation,
                rev,
                rev);
        }
    }

    /// <summary>Tests only — reset poll state (the configured directory is kept).</summary>
    internal static void ResetForTests()
    {
        lock (Gate) ResetLocked();
    }

    static void ResetLocked()
    {
        _hasCache = false;
        _defaultId = "";
        _displayName = "";
        _activeAuraId = null;
        _activeAuraName = null;
        _allocation = AptitudeAllocation.Empty;
        _cacheRevision = 0;
        LastBuildUsedFallback = false;
    }

    static MatchCommanderSnapshot NeutralFallback(ICommanderDirectory directory, long revision)
    {
        var commander = directory.DefaultFor(EmpireId.Dave);
        return new(
            commander.StableId,
            directory.DisplayName(commander, playerName: null),
            null,
            null,
            AptitudeAllocation.Empty,
            revision,
            revision);
    }

    static ICommanderDirectory RequireDirectory() => CommanderDirectoryHub.Current;
}
