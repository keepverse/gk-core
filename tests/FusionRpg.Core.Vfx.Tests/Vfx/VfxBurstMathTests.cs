using FusionRpg.Core.Vfx;
using Xunit;

namespace FusionRpg.Core.Tests.Vfx;

/// <summary>Locks SPEC W4: per-shape emission envelopes; Radial reproduces the legacy pool math verbatim.</summary>
public class VfxBurstMathTests
{
    const float Span = 1.2f;
    const float Life = 0.55f;
    const int Count = 28;

    [Fact]
    public void Radial_matches_legacy_constants_exactly()
    {
        foreach (var i in new[] { 0, 3, 11, 27 })
        {
            var p = VfxBurstMath.Particle(VfxBurstShape.Radial, i, Count, Span, Life);
            var ang = i / (float)Count * MathF.PI * 2f;
            var rad = Span * (0.04f + 0.12f * (i % 5) / 5f);
            var speed = Span * (0.8f + i % 7 * 0.18f);
            Assert.Equal(MathF.Cos(ang) * rad, p.PosX, 4);
            Assert.Equal(MathF.Sin(ang) * rad, p.PosY, 4);
            Assert.Equal(MathF.Cos(ang) * speed, p.VelX, 4);
            Assert.Equal(MathF.Sin(ang) * speed, p.VelY, 4);
            Assert.Equal(Span * (0.16f + i % 4 * 0.08f), p.Size, 4);
            Assert.Equal(Life * (0.55f + i % 5 * 0.08f), p.Energy, 4);
        }
    }

    [Fact]
    public void Rising_always_moves_up_and_up_dominates()
    {
        for (var i = 0; i < Count; i++)
        {
            var p = VfxBurstMath.Particle(VfxBurstShape.Rising, i, Count, Span, Life);
            Assert.True(p.VelY > 0f, $"i={i} velY={p.VelY}");
            Assert.True(p.VelY > MathF.Abs(p.VelX), $"i={i}");
        }
    }

    [Fact]
    public void Directional_cones_toward_negative_x()
    {
        for (var i = 0; i < Count; i++)
        {
            var p = VfxBurstMath.Particle(VfxBurstShape.Directional, i, Count, Span, Life);
            Assert.True(p.VelX < 0f, $"i={i} velX={p.VelX}");
            Assert.True(MathF.Abs(p.VelX) > MathF.Abs(p.VelY), $"i={i}");
        }
    }

    [Fact]
    public void Size_and_energy_are_shape_independent()
    {
        for (var i = 0; i < Count; i++)
        {
            var r = VfxBurstMath.Particle(VfxBurstShape.Radial, i, Count, Span, Life);
            var u = VfxBurstMath.Particle(VfxBurstShape.Rising, i, Count, Span, Life);
            var d = VfxBurstMath.Particle(VfxBurstShape.Directional, i, Count, Span, Life);
            Assert.Equal(r.Size, u.Size, 5);
            Assert.Equal(r.Size, d.Size, 5);
            Assert.Equal(r.Energy, u.Energy, 5);
            Assert.Equal(r.Energy, d.Energy, 5);
        }
    }

    [Fact]
    public void Single_particle_count_is_safe()
    {
        foreach (var shape in new[] { VfxBurstShape.Radial, VfxBurstShape.Rising, VfxBurstShape.Directional })
        {
            var p = VfxBurstMath.Particle(shape, 0, 1, Span, Life);
            Assert.False(float.IsNaN(p.VelX) || float.IsNaN(p.VelY));
        }
    }

    [Fact]
    public void Spec_defaults_to_radial_so_existing_recipes_are_unchanged()
    {
        Assert.Equal(VfxBurstShape.Radial, new VfxPrimitiveSpec().Shape);
    }
}
