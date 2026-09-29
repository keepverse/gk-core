namespace FusionRpg.Core.Vfx;

public readonly record struct BurstParticle(
    float PosX, float PosY, float VelX, float VelY, float Size, float Energy);

/// <summary>
/// Per-particle emission math for pooled bursts — SPEC W4. Pure; the injector pool maps the
/// floats onto Unity vectors. Radial reproduces the legacy OverlayWorldFx pattern verbatim so
/// default recipes look identical; size/energy formulas are shared by every shape.
/// </summary>
public static class VfxBurstMath
{
    static VfxTuning? _tuning;

    public static void Configure(VfxTuning tuning) =>
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));

    static VfxTuning Tuning => _tuning ?? throw new InvalidOperationException(
        "VfxBurstMath.Configure(...) has not run. Cone/side factors read data/tuning/vfx.v{n}.json " +
        "(tunables-ssot.md T5) — there is no built-in default to fall back to.");

    /// <summary>Half-angle (radians) of the Rising / Directional cones. Config-backed
    /// (tunables-ssot.md T1) — gk-core/data/tuning/vfx.v1.json's burst.</summary>
    public static float ConeHalfAngle => (float)Tuning.BurstConeHalfAngle;
    static float RisingSideFactor => (float)Tuning.BurstRisingSideFactor;
    static float DirectionalSideFactor => (float)Tuning.BurstDirectionalSideFactor;

    public static BurstParticle Particle(VfxBurstShape shape, int index, int count, float span, float life)
    {
        if (count < 1) count = 1;
        if (index < 0) index = 0;
        var rad = span * (0.04f + 0.12f * (index % 5) / 5f);
        var speed = span * (0.8f + index % 7 * 0.18f);
        var size = span * (0.16f + index % 4 * 0.08f);
        var energy = life * (0.55f + index % 5 * 0.08f);

        switch (shape)
        {
            case VfxBurstShape.Rising:
            {
                var theta = Cone(index, count);
                return new BurstParticle(
                    MathF.Sin(theta) * rad, 0f,
                    MathF.Sin(theta) * speed * RisingSideFactor, MathF.Cos(theta) * speed,
                    size, energy);
            }
            case VfxBurstShape.Directional:
            {
                var theta = Cone(index, count);
                return new BurstParticle(
                    0f, MathF.Sin(theta) * rad,
                    -MathF.Cos(theta) * speed, MathF.Sin(theta) * speed * DirectionalSideFactor,
                    size, energy);
            }
            default:
            {
                var ang = index / (float)count * MathF.PI * 2f;
                return new BurstParticle(
                    MathF.Cos(ang) * rad, MathF.Sin(ang) * rad,
                    MathF.Cos(ang) * speed, MathF.Sin(ang) * speed,
                    size, energy);
            }
        }
    }

    static float Cone(int index, int count) =>
        count <= 1 ? 0f : -ConeHalfAngle + 2f * ConeHalfAngle * (index / (float)(count - 1));
}
