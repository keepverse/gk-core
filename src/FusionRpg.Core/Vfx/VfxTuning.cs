using System.Text.Json;

namespace FusionRpg.Core.Vfx;

public sealed record VfxRulesTuning(
    int BurstCap, double BurstLifeSeconds, double FloaterRateLimitSeconds, double BurstRateLimitSeconds,
    int GlobalCuePerTickCap, int CueQueueCap, double CritFontScale, double CritPopStartScale,
    double CritPopSettleT, long AmountTierSmallBelow, long AmountTierBigFrom,
    double AmountTierSmallScale, double AmountTierBigScale);

public sealed record VfxSustainedTuning(
    int GlobalCap, int PerHostCap, double TtlGraceSeconds, double InfiniteTtlSeconds,
    double AuraPulseSeconds, int AuraMaxParticles, double SpanScale);

public sealed record VfxShieldBarTuning(
    double BarWorldWidth, double BarWorldHeight, double WorldYOffset, int MaxSegments, int Cap, int MaxPips);

public sealed record VfxRenderTuning(
    int BurstParticles, int ParticleSortingOrder, int SortOffsetAboveUnit, double SustainedWorldYOffset,
    int ParticleTextureSize, double MarkerEdgeSoftness, double MarkerGlowStrength,
    double MarkerSizeScale, double MarkerYOffsetScale,
    VfxShieldBarTuning ShieldBar, double TintReassertSeconds);

/// <summary>`StatusVfxIdentity`'s own collision-detection thresholds (guard-magic-numbers.ps1 M2,
/// 2026-08-30) — an offline dev-facing audit heuristic (how far apart two statuses' colors must be
/// before they read as visually distinct), not a runtime/gameplay balance number, but still a number
/// a VFX pass could legitimately want to retune without a rebuild.</summary>
public sealed record VfxIdentityTuning(int SimilarRgbDistanceThreshold, int SimilarApplyRgbDistanceThreshold);

public sealed record VfxImpactStampTuning(
    int PoolCap, double LifeSeconds, double SpanScale,
    double StartAlpha, double EndAlpha, double StartScale, double EndScale, int SortOffset,
    double DelaySeconds = 0d);

/// <summary>Presentation-only settings for the Earth source charge and source-to-target travel phases.</summary>
public sealed record VfxEarthPhaseTuning(
    int PoolCap, double ChargeLifeSeconds, double TravelLifeSeconds,
    double ChargeSpanScale, double TravelLengthScale, double TravelThicknessScale,
    double StartAlpha, double EndAlpha, int SortOffset, double TravelDelaySeconds = 0d);

/// <summary>Vfx balance surface (tunables-ssot.md T1) — vfx-ssot.md. VfxCatalog.cs and
/// VfxAuraMath.cs are hand-authored content/shape math (CONTENT_FILE), not here. rules deliberately
/// omits floaterCap/floaterLifeSeconds/risePixels — VfxRules aliases DamageFxFloaterRules
/// (gk-core/data/tuning/effects.v1.json), the one source.</summary>
public sealed record VfxTuning(
    int SchemaVersion, int Version,
    double TintMaxStrength,
    double BurstConeHalfAngle, double BurstRisingSideFactor, double BurstDirectionalSideFactor,
    VfxRulesTuning Rules, VfxSustainedTuning Sustained, VfxRenderTuning Render,
    VfxIdentityTuning Identity, VfxImpactStampTuning? ImpactStamp, VfxEarthPhaseTuning? EarthPhase = null);

public sealed class VfxTuningRejection : Exception
{
    public VfxTuningRejection(string message) : base(message) { }
}

/// <summary>Pure parser, no file I/O (tunables-ssot.md §7.2).</summary>
public static class VfxTuningLoader
{
    public static VfxTuning Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new VfxTuningRejection("vfx tuning: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new VfxTuningRejection($"vfx tuning: not valid JSON — {ex.Message}"); }

        using (doc)
        {
            var root = doc.RootElement;
            var tint = Obj(root, "tint");
            var burst = Obj(root, "burst");
            var rules = Obj(root, "rules");
            var sustained = Obj(root, "sustained");
            var render = Obj(root, "render");
            var shieldBar = Obj(render, "shieldBar");
            var identity = Obj(root, "identity");
            var impactStamp = root.TryGetProperty("impactStamp", out var configuredImpactStamp)
                ? Obj(root, "impactStamp")
                : default;
            var earthPhase = root.TryGetProperty("earthPhase", out var configuredEarthPhase)
                ? Obj(root, "earthPhase")
                : default;
            if (root.GetProperty("version").GetInt32() >= 4 && impactStamp.ValueKind != JsonValueKind.Object)
                throw new VfxTuningRejection("vfx tuning: v4 requires an impactStamp object");
            if (root.GetProperty("version").GetInt32() >= 5 && earthPhase.ValueKind != JsonValueKind.Object)
                throw new VfxTuningRejection("vfx tuning: v5 requires an earthPhase object");
            if (root.GetProperty("version").GetInt32() >= 7 &&
                (impactStamp.ValueKind != JsonValueKind.Object || earthPhase.ValueKind != JsonValueKind.Object))
                throw new VfxTuningRejection("vfx tuning: v7 requires Earth phase timing objects");

            return new VfxTuning(
                SchemaVersion: Int(root, "schemaVersion", "$"),
                Version: Int(root, "version", "$"),
                TintMaxStrength: Double(tint, "maxStrength", "tint"),
                BurstConeHalfAngle: Double(burst, "coneHalfAngle", "burst"),
                BurstRisingSideFactor: Double(burst, "risingSideFactor", "burst"),
                BurstDirectionalSideFactor: Double(burst, "directionalSideFactor", "burst"),
                Rules: new VfxRulesTuning(
                    BurstCap: Int(rules, "burstCap", "rules"),
                    BurstLifeSeconds: Double(rules, "burstLifeSeconds", "rules"),
                    FloaterRateLimitSeconds: Double(rules, "floaterRateLimitSeconds", "rules"),
                    BurstRateLimitSeconds: Double(rules, "burstRateLimitSeconds", "rules"),
                    GlobalCuePerTickCap: Int(rules, "globalCuePerTickCap", "rules"),
                    CueQueueCap: Int(rules, "cueQueueCap", "rules"),
                    CritFontScale: Double(rules, "critFontScale", "rules"),
                    CritPopStartScale: Double(rules, "critPopStartScale", "rules"),
                    CritPopSettleT: Double(rules, "critPopSettleT", "rules"),
                    AmountTierSmallBelow: Long(rules, "amountTierSmallBelow", "rules"),
                    AmountTierBigFrom: Long(rules, "amountTierBigFrom", "rules"),
                    AmountTierSmallScale: Double(rules, "amountTierSmallScale", "rules"),
                    AmountTierBigScale: Double(rules, "amountTierBigScale", "rules")),
                Sustained: new VfxSustainedTuning(
                    GlobalCap: Int(sustained, "globalCap", "sustained"),
                    PerHostCap: Int(sustained, "perHostCap", "sustained"),
                    TtlGraceSeconds: Double(sustained, "ttlGraceSeconds", "sustained"),
                    InfiniteTtlSeconds: Double(sustained, "infiniteTtlSeconds", "sustained"),
                    AuraPulseSeconds: Double(sustained, "auraPulseSeconds", "sustained"),
                    AuraMaxParticles: Int(sustained, "auraMaxParticles", "sustained"),
                    SpanScale: Double(sustained, "spanScale", "sustained")),
                Render: new VfxRenderTuning(
                    BurstParticles: Int(render, "burstParticles", "render"),
                    ParticleSortingOrder: Int(render, "particleSortingOrder", "render"),
                    SortOffsetAboveUnit: Int(render, "sortOffsetAboveUnit", "render"),
                    SustainedWorldYOffset: Double(render, "sustainedWorldYOffset", "render"),
                    ParticleTextureSize: Int(render, "particleTextureSize", "render"),
                    MarkerEdgeSoftness: Double(render, "markerEdgeSoftness", "render"),
                    MarkerGlowStrength: Double(render, "markerGlowStrength", "render"),
                    MarkerSizeScale: Double(render, "markerSizeScale", "render"),
                    MarkerYOffsetScale: Double(render, "markerYOffsetScale", "render"),
                    ShieldBar: new VfxShieldBarTuning(
                        BarWorldWidth: Double(shieldBar, "barWorldWidth", "render.shieldBar"),
                        BarWorldHeight: Double(shieldBar, "barWorldHeight", "render.shieldBar"),
                        WorldYOffset: Double(shieldBar, "worldYOffset", "render.shieldBar"),
                        MaxSegments: Int(shieldBar, "maxSegments", "render.shieldBar"),
                        Cap: Int(shieldBar, "cap", "render.shieldBar"),
                        MaxPips: Int(shieldBar, "maxPips", "render.shieldBar")),
                    TintReassertSeconds: Double(render, "tintReassertSeconds", "render")),
                Identity: new VfxIdentityTuning(
                    SimilarRgbDistanceThreshold: Int(identity, "similarRgbDistanceThreshold", "identity"),
                    SimilarApplyRgbDistanceThreshold: Int(identity, "similarApplyRgbDistanceThreshold", "identity")),
                ImpactStamp: impactStamp.ValueKind == JsonValueKind.Object ? new VfxImpactStampTuning(
                    PoolCap: Int(impactStamp, "poolCap", "impactStamp"),
                    LifeSeconds: Double(impactStamp, "lifeSeconds", "impactStamp"),
                    SpanScale: Double(impactStamp, "spanScale", "impactStamp"),
                    StartAlpha: Double(impactStamp, "startAlpha", "impactStamp"),
                    EndAlpha: Double(impactStamp, "endAlpha", "impactStamp"),
                    StartScale: Double(impactStamp, "startScale", "impactStamp"),
                    EndScale: Double(impactStamp, "endScale", "impactStamp"),
                    SortOffset: Int(impactStamp, "sortOffset", "impactStamp"),
                    DelaySeconds: root.GetProperty("version").GetInt32() >= 7
                        ? Double(impactStamp, "delaySeconds", "impactStamp") : 0d) : null,
                EarthPhase: earthPhase.ValueKind == JsonValueKind.Object ? new VfxEarthPhaseTuning(
                    PoolCap: Int(earthPhase, "poolCap", "earthPhase"),
                    ChargeLifeSeconds: Double(earthPhase, "chargeLifeSeconds", "earthPhase"),
                    TravelLifeSeconds: Double(earthPhase, "travelLifeSeconds", "earthPhase"),
                    ChargeSpanScale: Double(earthPhase, "chargeSpanScale", "earthPhase"),
                    TravelLengthScale: Double(earthPhase, "travelLengthScale", "earthPhase"),
                    TravelThicknessScale: Double(earthPhase, "travelThicknessScale", "earthPhase"),
                    StartAlpha: Double(earthPhase, "startAlpha", "earthPhase"),
                    EndAlpha: Double(earthPhase, "endAlpha", "earthPhase"),
                    SortOffset: Int(earthPhase, "sortOffset", "earthPhase"),
                    TravelDelaySeconds: root.GetProperty("version").GetInt32() >= 7
                        ? Double(earthPhase, "travelDelaySeconds", "earthPhase") : 0d) : null);
        }
    }

    static JsonElement Obj(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Object)
            throw new VfxTuningRejection($"vfx tuning: missing or non-object '{key}'");
        return el;
    }

    static int Int(JsonElement parent, string key, string path)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var v))
            throw new VfxTuningRejection($"vfx tuning: missing or non-integer '{path}.{key}'");
        return v;
    }

    static long Long(JsonElement parent, string key, string path)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt64(out var v))
            throw new VfxTuningRejection($"vfx tuning: missing or non-integer '{path}.{key}'");
        return v;
    }

    static double Double(JsonElement parent, string key, string path)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number)
            throw new VfxTuningRejection($"vfx tuning: missing or non-number '{path}.{key}'");
        return el.GetDouble();
    }
}

/// <summary>Fans one vfx.v{n}.json load out to every class that reads it, including the Injector's
/// render pools which read <see cref="Tuning"/> directly (tunables-ssot.md §7.2).</summary>
public static class VfxTuningHub
{
    static VfxTuning? _tuning;

    public static void Configure(VfxTuning tuning)
    {
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));
        VfxTintMath.Configure(tuning);
        VfxBurstMath.Configure(tuning);
        VfxRules.Configure(tuning);
        VfxSustainedRules.Configure(tuning);
    }

    public static VfxTuning Tuning => _tuning ?? throw new InvalidOperationException(
        "VfxTuningHub.Configure(...) has not run. Vfx reads data/tuning/vfx.v{n}.json " +
        "(tunables-ssot.md T5) — there is no built-in default to fall back to.");
}
