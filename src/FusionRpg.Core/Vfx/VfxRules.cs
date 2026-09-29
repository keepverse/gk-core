using FusionRpg.Core.Effects;

namespace FusionRpg.Core.Vfx;

/// <summary>
/// Locked policy constants — vfx-ssot.md §7. Floater curve values stay in
/// <see cref="DamageFxFloaterRules"/> (test-locked); these alias them so there is one source.
/// </summary>
public static class VfxRules
{
    static VfxTuning? _tuning;

    public static void Configure(VfxTuning tuning) =>
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));

    static VfxRulesTuning Tuning => (_tuning ?? throw new InvalidOperationException(
        "VfxRules.Configure(...) has not run. Vfx rules read data/tuning/vfx.v{n}.json " +
        "(tunables-ssot.md T5) — there is no built-in default to fall back to.")).Rules;

    public static int FloaterCap => DamageFxFloaterRules.Cap;                       // 64
    public static int BurstCap => Tuning.BurstCap;
    public static float FloaterLifeSeconds => DamageFxFloaterRules.LifeSeconds;     // 0.9
    public static float BurstLifeSeconds => (float)Tuning.BurstLifeSeconds;
    public static float RisePixels => DamageFxFloaterRules.RisePixels;              // 56

    public static float FloaterRateLimitSeconds => (float)Tuning.FloaterRateLimitSeconds;   // per (cueId, TargetPtr)
    public static float BurstRateLimitSeconds => (float)Tuning.BurstRateLimitSeconds;       // per (cueId, cell)
    public static int GlobalCuePerTickCap => Tuning.GlobalCuePerTickCap;
    public static int CueQueueCap => Tuning.CueQueueCap;

    /// <summary>Crit keeps distinctness via size, not color — vfx-ssot.md §16.4.</summary>
    public static float CritFontScale => (float)Tuning.CritFontScale;

    /// <summary>Crit pop: start big, settle to CritFontScale by this normalized life t.</summary>
    public static float CritPopStartScale => (float)Tuning.CritPopStartScale;
    public static float CritPopSettleT => (float)Tuning.CritPopSettleT;

    /// <summary>Amount tiers (|amount|): small hits shrink, big hits grow — numeric labels only.</summary>
    public static long AmountTierSmallBelow => Tuning.AmountTierSmallBelow;
    public static long AmountTierBigFrom => Tuning.AmountTierBigFrom;
    public static float AmountTierSmallScale => (float)Tuning.AmountTierSmallScale;
    public static float AmountTierBigScale => (float)Tuning.AmountTierBigScale;

    public static float PopScale(float t)
    {
        if (t <= 0f) return CritPopStartScale;
        if (t >= CritPopSettleT) return CritFontScale;
        return CritPopStartScale - (CritPopStartScale - CritFontScale) * (t / CritPopSettleT);
    }

    public static float AmountScale(long amount)
    {
        var a = Math.Abs(amount);
        if (a < AmountTierSmallBelow) return AmountTierSmallScale;
        if (a < AmountTierBigFrom) return 1f;
        return AmountTierBigScale;
    }
}

/// <summary>Enumerated skip reasons — debug.fx.skipped payload contract (vfx-ssot.md §11).</summary>
public static class VfxSkipReasons
{
    public const string Disabled = "disabled";
    public const string UnknownCue = "unknown-cue";
    public const string Muted = "muted";
    public const string RateLimited = "rate-limited";
    public const string Cap = "cap";
    public const string Missing = "missing";
    public const string NoShader = "no-shader";
    public const string AssetMissing = "asset-missing";
    public const string ParticleFail = "particle-fail";
    /// <summary>Every renderable spec required an element color and the cue had none.</summary>
    public const string NoElement = "no-element";
}

/// <summary>Sustained-visual policy — SPEC vfx-v3 §3 (tight budget locked by owner).</summary>
public static class VfxSustainedRules
{
    static VfxTuning? _tuning;

    public static void Configure(VfxTuning tuning) =>
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));

    static VfxSustainedTuning Tuning => (_tuning ?? throw new InvalidOperationException(
        "VfxSustainedRules.Configure(...) has not run. Sustained-visual rules read " +
        "data/tuning/vfx.v{n}.json (tunables-ssot.md T5) — there is no built-in default to fall back to.")).Sustained;

    public static int GlobalCap => Tuning.GlobalCap;
    public static int PerHostCap => Tuning.PerHostCap;
    /// <summary>TTL = status duration + this grace; the expire cue normally ends the visual first.</summary>
    public static double TtlGraceSeconds => Tuning.TtlGraceSeconds;
    /// <summary>Statuses with no known duration re-confirm via re-apply within this window.</summary>
    public static double InfiniteTtlSeconds => Tuning.InfiniteTtlSeconds;
    public static float AuraPulseSeconds => (float)Tuning.AuraPulseSeconds;
    public static int AuraMaxParticles => Tuning.AuraMaxParticles;
}

/// <summary>Enumerated sustained end reasons — debug.fx.state.ended payload contract.</summary>
public static class VfxStateEndReasons
{
    public const string Expired = "expired";
    public const string HostGone = "host-gone";
    public const string TtlCap = "ttl-cap";
    public const string Evicted = "evicted";
    public const string MatchEnd = "match-end";
    public const string Disabled = "disabled";
}
