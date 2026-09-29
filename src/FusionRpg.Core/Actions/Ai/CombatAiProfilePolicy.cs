namespace FusionRpg.Core.Actions.Ai;

/// <summary>
/// combat-ai `profile-schema` (module 2, CAI1.6, spec-profile-schema.md §5): the static hub the hosts
/// configure, exactly as `SiegeTuningPolicy` does (`Battle/Board/SiegeTuning.cs:481-508`). No default:
/// this throws the same shape of error `SiegeTuningPolicy` does when `Configure` has not run — a
/// built-in default would be a second balance surface hiding in code.
/// </summary>
public static class CombatAiProfilePolicy
{
    static CombatAiTuning? _tuning;

    public static void Configure(CombatAiTuning tuning) =>
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));

    static CombatAiTuning Tuning => _tuning ?? throw new InvalidOperationException(
        "CombatAiProfilePolicy.Configure(...) has not run. Read data/tuning/combat-ai.v{n}.json at " +
        "startup -- there is no built-in default to fall back to.");

    /// <summary>Key resolution §3: `place/role` -> `place/default` -> `*/role` -> `*/default`. The
    /// root `*/default` row is required to exist (rejected at parse if missing), so this can never
    /// fail at decision time on the hot path.</summary>
    public static CombatAiProfile For(AiPlace place, AiRole role) => Resolve(Tuning.Profiles, place, role);

    /// <summary>The pure half of <see cref="For"/> — no static read, so a test can exercise the
    /// 4-step fallback chain directly against a locally-parsed <see cref="CombatAiTuning"/> without
    /// touching this class's own process-wide hub (which every OTHER concurrently-running test in this
    /// assembly may depend on being configured — CI's own regression, CAI1.8: a test that called
    /// <c>Configure</c>/<c>Reset</c> here raced `DistrictAssaultResolverTests`).</summary>
    public static CombatAiProfile Resolve(IReadOnlyDictionary<string, CombatAiProfile> profiles, AiPlace place, AiRole role)
    {
        var placeKey = FormatPlace(place);
        var roleKey = FormatRole(role);

        if (profiles.TryGetValue($"{placeKey}/{roleKey}", out var exact)) return exact;
        if (profiles.TryGetValue($"{placeKey}/default", out var placeDefault)) return placeDefault;
        if (profiles.TryGetValue($"*/{roleKey}", out var roleDefault)) return roleDefault;
        return profiles["*/default"];
    }

    /// <summary>The publish counter `replay-identity` (module 8) stamps into the match row.</summary>
    public static int TuningVersion => Tuning.Version;

    internal static string FormatPlace(AiPlace place) => place.ToString().ToLowerInvariant();
    internal static string FormatRole(AiRole role) => role.ToString().ToLowerInvariant();

    /// <summary>Tests only.</summary>
    public static void Reset() => _tuning = null;
}
