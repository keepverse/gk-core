namespace FusionRpg.Core.Vfx;

/// <summary>
/// Per-particle emission math for sustained auras — SPEC vfx-v3 M3. Pure; deterministic by
/// (style, index, count, phase). Grammar: Drip = generic DoT fallback; WispOut/BubbleRise/ChunkFall =
/// batch-1 drip-cluster identity; SparkStrobe/ShardGlitter = batch-2 crackle-cluster identity;
/// SporeDrift/CharmHeartbeat = batch-3 orbit-cluster identity; PactFootPulse/CommandCrownPulse =
/// batch-5 pulsering-cluster identity; Orbit = generic passive/link fallback;
/// RiseSparkle = buff; CrackleJitter = generic armor/electric fallback;
/// PulseRing = active mark; StreamOut = drain.
/// Reuses <see cref="BurstParticle"/>; the injector pool maps floats onto Unity vectors.
/// </summary>
public static class VfxAuraMath
{
    public static BurstParticle Particle(
        VfxAuraStyle style, int index, int count, float phase, float span, float life)
    {
        if (count < 1) count = 1;
        if (index < 0) index = 0;
        if (span < 0.2f) span = 0.2f;
        var u = (index % count + 0.5f) / count;          // stratified 0..1
        var jitter = (index * 7 + (int)(phase * 13f)) % 11 / 11f; // deterministic variety
        var size = span * (0.10f + index % 3 * 0.05f);
        var energy = life * (0.8f + index % 3 * 0.1f);

        switch (style)
        {
            case VfxAuraStyle.Orbit:
            {
                var ang = phase * 2.2f + u * MathF.PI * 2f;
                var r = span * 0.45f;
                return new BurstParticle(
                    MathF.Cos(ang) * r, MathF.Sin(ang) * r * 0.6f + span * 0.1f,
                    -MathF.Sin(ang) * span * 0.25f, MathF.Cos(ang) * span * 0.15f + span * 0.08f,
                    size, energy);
            }
            case VfxAuraStyle.RiseSparkle:
            {
                var x = (u - 0.5f) * span * 0.7f;
                return new BurstParticle(
                    x, -span * 0.2f + jitter * span * 0.2f,
                    (jitter - 0.5f) * span * 0.15f, span * (0.45f + jitter * 0.3f),
                    size * 0.8f, energy);
            }
            case VfxAuraStyle.CrackleJitter:
            {
                var x = (u - 0.5f) * span;
                var y = (jitter - 0.5f) * span * 1.2f;
                return new BurstParticle(
                    x, y,
                    (u - 0.5f) * span * 0.05f, (jitter - 0.5f) * span * 0.05f,
                    size * 0.6f, energy * 0.45f);
            }
            case VfxAuraStyle.PulseRing:
            {
                var ang = u * MathF.PI * 2f;
                var r0 = span * 0.15f;
                return new BurstParticle(
                    MathF.Cos(ang) * r0, MathF.Sin(ang) * r0 * 0.5f,
                    MathF.Cos(ang) * span * 0.8f, MathF.Sin(ang) * span * 0.4f,
                    size * 0.7f, energy * 0.7f);
            }
            case VfxAuraStyle.StreamOut:
            {
                var ang = u * MathF.PI * 2f;
                var r = span * 0.5f;
                return new BurstParticle(
                    MathF.Cos(ang) * r, MathF.Sin(ang) * r * 0.7f + span * 0.15f,
                    -MathF.Cos(ang) * span * 0.35f, -MathF.Abs(MathF.Sin(ang)) * span * 0.3f - span * 0.1f,
                    size * 0.8f, energy);
            }
            case VfxAuraStyle.WispOut:
            {
                var ang = u * MathF.PI * 2f;
                var r = span * 0.12f + jitter * span * 0.08f;
                var px = MathF.Cos(ang) * r;
                var py = jitter * span * 0.1f;
                return new BurstParticle(
                    px, py,
                    MathF.Cos(ang) * span * 0.22f, span * (0.28f + jitter * 0.12f),
                    size * 0.55f, energy * 0.75f);
            }
            case VfxAuraStyle.BubbleRise:
            {
                var sway = (u - 0.5f) * span * 0.6f;
                var px = sway + (jitter - 0.5f) * span * 0.15f;
                var py = -span * 0.35f + jitter * span * 0.08f;
                return new BurstParticle(
                    px, py,
                    (jitter - 0.5f) * span * 0.12f, span * (0.38f + jitter * 0.18f),
                    size * 1.15f, energy);
            }
            case VfxAuraStyle.ChunkFall:
            {
                var px = (u - 0.5f) * span * 0.3f;
                var py = span * (0.25f + jitter * 0.2f);
                return new BurstParticle(
                    px, py,
                    (jitter - 0.5f) * span * 0.04f, -span * (0.42f + jitter * 0.2f),
                    size * 1.35f, energy * 0.9f);
            }
            case VfxAuraStyle.SparkStrobe:
            {
                var hop = MathF.Sin(phase * 9f + u * MathF.PI * 2f) * span * 0.08f;
                var px = (u - 0.5f) * span * 0.35f + hop;
                var py = (jitter - 0.5f) * span * 0.45f - hop * 0.5f;
                return new BurstParticle(
                    px, py,
                    (jitter - 0.5f) * span * 0.18f, (u - 0.5f) * span * 0.14f,
                    size * 0.5f, energy * 0.35f);
            }
            case VfxAuraStyle.ShardGlitter:
            {
                var px = (u - 0.5f) * span * 1.1f;
                var py = span * (0.15f + jitter * 0.25f);
                return new BurstParticle(
                    px, py,
                    (u - 0.5f) * span * 0.65f, -span * (0.05f + jitter * 0.06f),
                    size * 0.65f, energy * 0.5f);
            }
            case VfxAuraStyle.SporeDrift:
            {
                var ang = phase * 1.8f + u * MathF.PI * 2f;
                var r = span * 0.55f;
                var px = MathF.Cos(ang) * r;
                var py = MathF.Sin(ang) * r * 0.55f + span * 0.05f;
                return new BurstParticle(
                    px, py,
                    -MathF.Sin(ang) * span * 0.18f, span * (0.22f + jitter * 0.12f),
                    size * 1.05f, energy * 0.85f);
            }
            case VfxAuraStyle.CharmHeartbeat:
            {
                var ang = phase * 2.2f + u * MathF.PI * 2f;
                var beat = 0.85f + 0.25f * MathF.Sin(phase * 5.5f);
                var r = span * 0.38f * beat;
                var px = MathF.Cos(ang) * r;
                var py = MathF.Sin(ang) * r * 0.65f + span * 0.08f;
                return new BurstParticle(
                    px, py,
                    MathF.Cos(ang) * span * 0.12f * beat, MathF.Sin(ang) * span * 0.08f * beat,
                    size * 0.75f, energy * 0.65f);
            }
            case VfxAuraStyle.PactFootPulse:
            {
                var ang = u * MathF.PI * 2f;
                var r0 = span * 0.12f;
                var px = MathF.Cos(ang) * r0;
                var py = MathF.Sin(ang) * r0 * 0.45f - span * 0.32f;
                return new BurstParticle(
                    px, py,
                    MathF.Cos(ang) * span * 0.65f, MathF.Sin(ang) * span * 0.25f,
                    size * 0.65f, energy * 0.65f);
            }
            case VfxAuraStyle.CommandCrownPulse:
            {
                var ang = u * MathF.PI * 2f;
                var r0 = span * 0.18f;
                var px = MathF.Cos(ang) * r0;
                var py = MathF.Sin(ang) * r0 * 0.5f + span * 0.28f;
                return new BurstParticle(
                    px, py,
                    MathF.Cos(ang) * span * 0.85f, MathF.Sin(ang) * span * 0.45f,
                    size * 0.75f, energy * 0.72f);
            }
            default: // Drip
            {
                var x = (u - 0.5f) * span * 0.8f;
                return new BurstParticle(
                    x, span * (0.3f + jitter * 0.3f),
                    (jitter - 0.5f) * span * 0.08f, -span * (0.35f + jitter * 0.25f),
                    size, energy);
            }
        }
    }
}
