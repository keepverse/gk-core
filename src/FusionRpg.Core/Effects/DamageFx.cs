using FusionRpg.Contracts;

namespace FusionRpg.Core.Effects;

public interface IDamageFxSink
{
    void Show(DamageFxDto fx);
}

public sealed class NoopDamageFxSink : IDamageFxSink
{
    public static readonly NoopDamageFxSink Instance = new();
    public void Show(DamageFxDto fx) { }
}

public sealed class RecordingDamageFxSink : IDamageFxSink
{
    public List<DamageFxDto> Items { get; } = new();
    public void Show(DamageFxDto fx) => Items.Add(fx);
}

/// <summary>GUI palette + labels. Injector maps RGB to Unity Color.</summary>
public static class DamageFxPalette
{
    public static (byte R, byte G, byte B) Rgb(DamageFxTag tag) => tag switch
    {
        DamageFxTag.Heal => (80, 220, 80),
        DamageFxTag.Weak => (255, 220, 40),
        DamageFxTag.Resist => (160, 160, 160),
        DamageFxTag.Null => (80, 140, 255),
        DamageFxTag.Absorb => (40, 220, 220),
        DamageFxTag.Reflect => (180, 80, 220),
        DamageFxTag.Dodge => (255, 255, 255),
        DamageFxTag.Crit => (255, 140, 40),
        DamageFxTag.Penetrate => (255, 200, 60),
        DamageFxTag.Block => (140, 140, 140),
        _ => (255, 255, 255)
    };

    public static string Label(DamageFxTag tag, long amount) => tag switch
    {
        DamageFxTag.Dodge => "MISS",
        DamageFxTag.Block => "BLOCK",
        DamageFxTag.Null => "NULL",
        DamageFxTag.Absorb => amount > 0 ? "+" + amount : "ABSORB",
        DamageFxTag.Heal => "+" + Math.Abs(amount),
        _ => Math.Abs(amount).ToString()
    };
}

/// <summary>IMGUI floater timing. Overlay Tick/Draw must use these so tests can lock the values.
/// Config-backed (tunables-ssot.md T1) — gk-core/data/tuning/effects.v1.json's damageFxFloater; the ONE
/// source VfxRules.FloaterCap/FloaterLifeSeconds/RisePixels alias.</summary>
public static class DamageFxFloaterRules
{
    static EffectsTuning? _tuning;

    public static void Configure(EffectsTuning tuning) =>
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));

    static EffectsTuning Tuning => _tuning ?? throw new InvalidOperationException(
        "DamageFxFloaterRules.Configure(...) has not run. Every floater rule reads " +
        "data/tuning/effects.v{n}.json (tunables-ssot.md T5) — there is no built-in default to fall back to.");

    public static int Cap => Tuning.DamageFxFloater.Cap;
    public static float LifeSeconds => (float)Tuning.DamageFxFloater.LifeSeconds;
    public static float RisePixels => (float)Tuning.DamageFxFloater.RisePixels;

    public static bool AtCap(int count) => count >= Cap;
    public static bool Expired(float age) => age >= LifeSeconds;
    public static float T(float age)
    {
        var t = age / LifeSeconds;
        if (t < 0f) return 0f;
        if (t > 1f) return 1f;
        return t;
    }

    public static float GuiY(float screenHeight, float screenPointY, float t) =>
        GuiY(screenHeight, screenPointY, pixelRectY: 0f, t);

    /// <param name="pixelRectY">Camera.pixelRect.y — WorldToScreen Y is camera-local.</param>
    public static float GuiY(float screenHeight, float screenPointY, float pixelRectY, float t) =>
        screenHeight - (screenPointY + pixelRectY) - t * RisePixels;

    public static float Alpha(float t) => 1f - t;
}
