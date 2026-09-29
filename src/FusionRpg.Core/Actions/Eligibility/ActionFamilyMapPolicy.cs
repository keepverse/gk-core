namespace FusionRpg.Core.Actions.Eligibility;

/// <summary>
/// T59.7 (spec-action-instance-and-grant.md §4): the process-wide, configured-once
/// <c>speciesKey → familyId</c> map <see cref="ActionEligibility.Candidates"/> needs, mirroring
/// <c>RungPolicy</c>/<c>CreatureSpeciesCatalog</c>'s own "no built-in default, configure once at
/// startup" shape exactly. `FamilyMap` itself stays a pure parser (no I/O); this is the seam that
/// lets `RpgStore` (which cannot read `AppContext.BaseDirectory`-relative content files itself) reach
/// the SAME map `FusionRpg.Server`'s startup already loads, without threading a new parameter through
/// every `RpgStore` call site that might one day need it.
/// </summary>
public static class ActionFamilyMapPolicy
{
    static IReadOnlyDictionary<string, IReadOnlyList<string>>? _map;

    public static void Configure(IReadOnlyDictionary<string, IReadOnlyList<string>> map) => _map = map ?? throw new ArgumentNullException(nameof(map));

    /// <summary>Empty, never throwing, until configured — a specimen's species/family-scoped actions
    /// are simply unreachable (falls back to an empty family set inside
    /// `ActionEligibility.Candidates`) rather than failing every unlock roll outright when no host has
    /// configured this yet.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Map => _map ?? EmptyMap;

    static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> EmptyMap =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
}
