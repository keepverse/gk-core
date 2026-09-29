using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Creatures;

/// <summary>One banner definition (spec-creature-summoning.md).</summary>
public sealed record SummonBannerDef(
    string BannerId,
    int CostPerPull,
    int CostPerTen,
    bool HasElementFocus,
    double FocusWeightMultiplier);

/// <summary>Config-backed (tunables-ssot.md T1) — gk-core/data/tuning/summoning.v1.json's banners map.
/// Banner ids/HasElementFocus stay here (schema); cost/focus-weight magnitudes are loaded.</summary>
public static class SummonBannerCatalog
{
    public const string StandardRift = "standard-rift";
    public const string ElementFocus = "element-focus";

    static SummoningTuning? _tuning;

    public static void Configure(SummoningTuning tuning) =>
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));

    static SummoningTuning Tuning => _tuning ?? throw new InvalidOperationException(
        "SummonBannerCatalog.Configure(...) has not run. Every banner reads " +
        "data/tuning/summoning.v{n}.json (tunables-ssot.md T5) — there is no built-in default to fall back to.");

    static IReadOnlyList<SummonBannerDef>? _all;

    public static IReadOnlyList<SummonBannerDef> All => _all ??= new[]
    {
        Of(StandardRift, hasElementFocus: false),
        Of(ElementFocus, hasElementFocus: true)
    };

    static SummonBannerDef Of(string bannerId, bool hasElementFocus)
    {
        var b = Tuning.Banners.TryGetValue(bannerId, out var v) ? v
            : throw new InvalidOperationException($"summoning tuning: missing banner '{bannerId}'.");
        return new SummonBannerDef(bannerId, b.CostPerPull, b.CostPerTen, hasElementFocus, b.FocusWeightMultiplier);
    }

    public static SummonBannerDef? TryGet(string? bannerId) =>
        All.FirstOrDefault(b => string.Equals(b.BannerId, bannerId, StringComparison.Ordinal));

    /// <summary>
    /// Deterministic focus rotation for the element-focus banner: pure function of the UTC date
    /// (ISO day number / 7 → weekly), so the server resolves it at pull time and records it —
    /// replay returns stored results and never re-derives.
    /// </summary>
    public static ElementTypeId FocusFor(DateOnly utcDate)
    {
        var week = utcDate.DayNumber / 7;
        return ElementRoster.Concrete[week % ElementRoster.Concrete.Count];
    }
}
