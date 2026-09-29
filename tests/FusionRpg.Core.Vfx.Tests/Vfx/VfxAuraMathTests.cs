using FusionRpg.Core.Vfx;
using Xunit;

namespace FusionRpg.Core.Tests.Vfx;

/// <summary>vfx-v3 M3: per-style motion envelopes — the §4 grammar must be mechanically true.</summary>
public class VfxAuraMathTests
{
    const float Span = 1.2f;
    const float Life = 0.45f;
    const int Count = 6;

    static IEnumerable<BurstParticle> All(VfxAuraStyle style, float phase = 0.7f)
    {
        for (var i = 0; i < Count; i++)
            yield return VfxAuraMath.Particle(style, i, Count, phase, Span, Life);
    }

    [Fact]
    public void Drip_always_falls_from_above()
    {
        foreach (var p in All(VfxAuraStyle.Drip))
        {
            Assert.True(p.VelY < 0f, $"velY={p.VelY}");
            Assert.True(p.PosY > 0f, "drips start above the anchor");
        }
    }

    [Fact]
    public void Rise_always_climbs_and_up_dominates()
    {
        foreach (var p in All(VfxAuraStyle.RiseSparkle))
        {
            Assert.True(p.VelY > 0f);
            Assert.True(p.VelY > MathF.Abs(p.VelX));
        }
    }

    [Fact]
    public void Orbit_stays_on_the_ring()
    {
        foreach (var p in All(VfxAuraStyle.Orbit))
        {
            // x on the r-circle; y is ellipse-squashed and body-offset — check x-extent only
            Assert.True(MathF.Abs(p.PosX) <= Span * 0.45f + 0.001f);
        }
        // and the ring actually rotates with phase
        var a = VfxAuraMath.Particle(VfxAuraStyle.Orbit, 0, Count, 0f, Span, Life);
        var b = VfxAuraMath.Particle(VfxAuraStyle.Orbit, 0, Count, 1f, Span, Life);
        Assert.NotEqual(a.PosX, b.PosX);
    }

    [Fact]
    public void Crackle_stays_in_body_box_and_barely_moves()
    {
        foreach (var p in All(VfxAuraStyle.CrackleJitter))
        {
            Assert.True(MathF.Abs(p.PosX) <= Span * 0.5f + 0.001f);
            Assert.True(MathF.Abs(p.PosY) <= Span * 0.6f + 0.001f);
            Assert.True(MathF.Abs(p.VelX) <= Span * 0.05f + 0.001f);
            Assert.True(p.Energy < Life); // glints are brief
        }
    }

    [Fact]
    public void PulseRing_expands_outward()
    {
        foreach (var p in All(VfxAuraStyle.PulseRing))
        {
            var dot = p.PosX * p.VelX + p.PosY * p.VelY;
            Assert.True(dot > 0f, "velocity must point away from center");
        }
    }

    [Fact]
    public void PactFootPulse_expands_outward()
    {
        const float anchorY = -Span * 0.32f;
        foreach (var p in All(VfxAuraStyle.PactFootPulse))
        {
            var dot = p.PosX * p.VelX + (p.PosY - anchorY) * p.VelY;
            Assert.True(dot > 0f, "velocity must point away from foot anchor");
        }
    }

    [Fact]
    public void CommandCrownPulse_expands_outward()
    {
        const float anchorY = Span * 0.28f;
        foreach (var p in All(VfxAuraStyle.CommandCrownPulse))
        {
            var dot = p.PosX * p.VelX + (p.PosY - anchorY) * p.VelY;
            Assert.True(dot > 0f, "velocity must point away from crown anchor");
        }
    }

    [Fact]
    public void StreamOut_flows_inward_and_down()
    {
        foreach (var p in All(VfxAuraStyle.StreamOut))
        {
            Assert.True(p.PosX * p.VelX <= 0f, "x-velocity points back toward the host");
            Assert.True(p.VelY < 0f, "drain sinks");
        }
    }

    [Fact]
    public void WispOut_rises_and_spreads_outward()
    {
        var particles = All(VfxAuraStyle.WispOut).ToList();
        Assert.True(particles.Average(p => p.VelY) > 0f);
        Assert.True(particles.Average(p => MathF.Abs(p.VelX)) > Span * 0.05f);
    }

    [Fact]
    public void BubbleRise_starts_low_and_climbs_with_sway()
    {
        var particles = All(VfxAuraStyle.BubbleRise).ToList();
        Assert.All(particles, p => Assert.True(p.PosY < 0f, "bubbles spawn below anchor"));
        Assert.True(particles.Average(p => p.VelY) > 0f);
        Assert.True(StdDev(particles.Select(p => p.PosX)) > Span * 0.1f);
    }

    [Fact]
    public void ChunkFall_drops_in_a_narrow_column()
    {
        var bubbleX = All(VfxAuraStyle.BubbleRise).Select(p => p.PosX).ToList();
        var chunkX = All(VfxAuraStyle.ChunkFall).Select(p => p.PosX).ToList();
        Assert.True(All(VfxAuraStyle.ChunkFall).Average(p => p.VelY) < 0f);
        Assert.True(StdDev(chunkX) < StdDev(bubbleX));
    }

    static float StdDev(IEnumerable<float> values)
    {
        var list = values.ToList();
        var mean = list.Average();
        return MathF.Sqrt(list.Average(v => (v - mean) * (v - mean)));
    }

    [Fact]
    public void SparkStrobe_position_varies_with_phase()
    {
        var a = VfxAuraMath.Particle(VfxAuraStyle.SparkStrobe, 0, Count, 0f, Span, Life);
        var b = VfxAuraMath.Particle(VfxAuraStyle.SparkStrobe, 0, Count, 1f, Span, Life);
        Assert.NotEqual(a.PosX, b.PosX);
        Assert.NotEqual(a.PosY, b.PosY);
    }

    [Fact]
    public void SparkStrobe_tighter_and_faster_than_CrackleJitter()
    {
        var strobe = All(VfxAuraStyle.SparkStrobe).ToList();
        var crackle = All(VfxAuraStyle.CrackleJitter).ToList();
        Assert.True(strobe.Max(p => MathF.Abs(p.PosX)) < crackle.Max(p => MathF.Abs(p.PosX)));
        Assert.True(strobe.Average(p => MathF.Abs(p.VelX) + MathF.Abs(p.VelY)) >
                    crackle.Average(p => MathF.Abs(p.VelX) + MathF.Abs(p.VelY)));
        Assert.True(strobe.Average(p => p.Energy) < crackle.Average(p => p.Energy));
    }

    [Fact]
    public void ShardGlitter_horizontal_bias()
    {
        var particles = All(VfxAuraStyle.ShardGlitter).ToList();
        Assert.True(particles.Average(p => MathF.Abs(p.VelX)) > particles.Average(p => MathF.Abs(p.VelY)));
        Assert.True(particles.Average(p => p.PosY) > 0f, "shards spawn on upper torso");
    }

    [Fact]
    public void SparkStrobe_vs_ShardGlitter_low_pair_risk()
    {
        Assert.Equal("low", StatusVfxIdentityScoring.PairRisk("spark", "shatter"));
    }

    [Fact]
    public void WispOut_rises_while_StreamOut_sinks()
    {
        var wispVy = All(VfxAuraStyle.WispOut).Average(p => p.VelY);
        var streamVy = All(VfxAuraStyle.StreamOut).Average(p => p.VelY);
        Assert.True(wispVy > 0f);
        Assert.True(streamVy < 0f);
        Assert.Equal("low", StatusVfxIdentityScoring.PairRisk("wither", "leech"));
    }

    [Fact]
    public void SporeDrift_rises_and_spreads_wider_than_Orbit()
    {
        var spore = All(VfxAuraStyle.SporeDrift).ToList();
        var orbit = All(VfxAuraStyle.Orbit).ToList();
        Assert.True(spore.Average(p => p.VelY) > 0f);
        Assert.True(spore.Average(p => p.VelY) > orbit.Average(p => p.VelY));
        Assert.True(spore.Max(p => MathF.Abs(p.PosX)) > orbit.Max(p => MathF.Abs(p.PosX)));
    }

    [Fact]
    public void CharmHeartbeat_radius_pulses_with_phase()
    {
        var near = VfxAuraMath.Particle(VfxAuraStyle.CharmHeartbeat, 0, Count, 0f, Span, Life);
        var far = VfxAuraMath.Particle(VfxAuraStyle.CharmHeartbeat, 0, Count, MathF.PI / 2f, Span, Life);
        var rNear = MathF.Sqrt(near.PosX * near.PosX + near.PosY * near.PosY);
        var rFar = MathF.Sqrt(far.PosX * far.PosX + far.PosY * far.PosY);
        Assert.NotEqual(rNear, rFar);
    }

    [Fact]
    public void CharmHeartbeat_radius_spreads_over_phase_sweep()
    {
        var radii = new List<float>();
        for (var step = 0; step < 24; step++)
        {
            var phase = step * MathF.PI / 12f;
            var p = VfxAuraMath.Particle(VfxAuraStyle.CharmHeartbeat, 0, Count, phase, Span, Life);
            radii.Add(MathF.Sqrt(p.PosX * p.PosX + p.PosY * p.PosY));
        }

        Assert.True(radii.Max() - radii.Min() > Span * 0.08f);
    }

    [Fact]
    public void SporeDrift_vs_CharmHeartbeat_low_pair_risk()
    {
        Assert.Equal("low", StatusVfxIdentityScoring.PairRisk("spore", "charm_pulse"));
    }

    [Fact]
    public void PactFootPulse_sits_below_CommandCrownPulse()
    {
        var foot = All(VfxAuraStyle.PactFootPulse).ToList();
        var crown = All(VfxAuraStyle.CommandCrownPulse).ToList();
        var ring = All(VfxAuraStyle.PulseRing).ToList();
        Assert.True(foot.Average(p => p.PosY) < crown.Average(p => p.PosY));
        Assert.True(foot.Average(p => p.PosY) < ring.Average(p => p.PosY));
        Assert.True(crown.Average(p => p.PosY) > ring.Average(p => p.PosY));
    }

    [Fact]
    public void PactFootPulse_vs_CommandCrownPulse_low_pair_risk()
    {
        Assert.Equal("low", StatusVfxIdentityScoring.PairRisk("pact_mark", "command"));
    }

    [Fact]
    public void Deterministic_by_inputs()
    {
        foreach (VfxAuraStyle style in Enum.GetValues<VfxAuraStyle>())
        {
            var a = VfxAuraMath.Particle(style, 3, Count, 0.7f, Span, Life);
            var b = VfxAuraMath.Particle(style, 3, Count, 0.7f, Span, Life);
            Assert.Equal(a, b);
        }
    }
}
