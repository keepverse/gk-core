namespace FusionRpg.Core.Vfx;

/// <summary>
/// Sustained tint composition — vfx-v3 M4. Pure: layers lerp the base toward each status
/// color, strength hard-clamped to <see cref="MaxStrength"/> so a unit never loses identity.
/// </summary>
public static class VfxTintMath
{
    static VfxTuning? _tuning;

    public static void Configure(VfxTuning tuning) =>
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));

    static VfxTuning Tuning => _tuning ?? throw new InvalidOperationException(
        "VfxTintMath.Configure(...) has not run. MaxStrength reads data/tuning/vfx.v{n}.json " +
        "(tunables-ssot.md T5) — there is no built-in default to fall back to.");

    /// <summary>Config-backed (tunables-ssot.md T1) — gk-core/data/tuning/vfx.v1.json's tint.maxStrength.</summary>
    public static float MaxStrength => (float)Tuning.TintMaxStrength;

    public static (byte R, byte G, byte B) Composite(
        (byte R, byte G, byte B) baseRgb,
        IEnumerable<((byte R, byte G, byte B) Rgb, float Strength)> layers)
    {
        float r = baseRgb.R, g = baseRgb.G, b = baseRgb.B;
        foreach (var (rgb, strength) in layers)
        {
            var s = Math.Clamp(strength, 0f, MaxStrength);
            r += (rgb.R - r) * s;
            g += (rgb.G - g) * s;
            b += (rgb.B - b) * s;
        }

        return (Clamp(r), Clamp(g), Clamp(b));
    }

    static byte Clamp(float v) =>
        v <= byte.MinValue ? byte.MinValue : v >= byte.MaxValue ? byte.MaxValue : (byte)Math.Round(v);
}
