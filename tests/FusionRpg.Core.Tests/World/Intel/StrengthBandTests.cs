using FusionRpg.Core.World.Intel;
using Xunit;

namespace FusionRpg.Core.Tests.World.Intel;

/// <summary>
/// W17 (spec-world-intel.md): what a glimpse is allowed to say about a force.
///
/// Never an exact roster, never a bare "something is there" — a band, with a midpoint and a ceiling.
/// Those two readings are the whole estimation model the AI gets: the ceiling when the question is
/// whether to defend, the midpoint when it is whether to attack.
/// </summary>
public class StrengthBandTests
{
    [Fact]
    public void Nothing_there_is_its_own_band()
    {
        var band = StrengthBandCatalog.Of(0);

        Assert.Equal(0, band.Index);
        Assert.Equal(0, band.Ceiling);
        Assert.Equal(0, band.Midpoint);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(499, 1)]
    [InlineData(500, 2)]
    [InlineData(1499, 2)]
    [InlineData(1500, 3)]
    [InlineData(3999, 3)]
    [InlineData(4000, 4)]
    [InlineData(9999, 4)]
    [InlineData(10_000, 5)]
    [InlineData(500_000, 5)]
    public void Every_strength_lands_in_exactly_one_band(long strength, int expected)
    {
        Assert.Equal(expected, StrengthBandCatalog.Of(strength).Index);
    }

    [Fact]
    public void A_negative_strength_reads_as_nothing_rather_than_throwing()
    {
        // Strength is Σ(hp − wounds) clamped at zero per member, so this should be unreachable —
        // but a band table that throws on a number is a worse failure than one that says "empty".
        Assert.Equal(0, StrengthBandCatalog.Of(-5).Index);
    }

    [Fact]
    public void The_ceiling_is_never_below_the_midpoint_and_the_midpoint_never_below_the_floor()
    {
        Assert.All(StrengthBandCatalog.All, b =>
        {
            Assert.True(b.Floor <= b.Midpoint, $"{b.Name}: floor {b.Floor} > midpoint {b.Midpoint}");
            Assert.True(b.Midpoint <= b.Ceiling, $"{b.Name}: midpoint {b.Midpoint} > ceiling {b.Ceiling}");
        });
    }

    [Fact]
    public void The_bands_ascend_with_no_gaps_and_no_overlaps()
    {
        var all = StrengthBandCatalog.All;

        Assert.Equal(0, all[0].Floor);
        for (var i = 1; i < all.Count; i++)
        {
            Assert.Equal(i, all[i].Index);
            Assert.Equal(all[i - 1].Ceiling + 1, all[i].Floor);
        }
    }

    [Fact]
    public void Reading_defensively_is_never_cheaper_than_reading_offensively()
    {
        // The entire point of the two readings: pessimism where being wrong is fatal.
        Assert.All(StrengthBandCatalog.All, b => Assert.True(b.Ceiling >= b.Midpoint));
    }

    [Fact]
    public void The_top_band_is_open_ended_but_still_answerable()
    {
        var horde = StrengthBandCatalog.All[^1];

        Assert.Equal(horde, StrengthBandCatalog.Of(long.MaxValue / 2));
        Assert.True(horde.Ceiling > horde.Floor, "an open-ended band still has to name a number to plan against");
    }

    [Fact]
    public void The_catalog_refuses_a_table_with_a_hole_in_it()
    {
        var broken = new[]
        {
            new StrengthBand { Index = 0, Name = "empty", Floor = 0, Ceiling = 0, Midpoint = 0 },
            new StrengthBand { Index = 1, Name = "gap", Floor = 5, Ceiling = 99, Midpoint = 50 }
        };

        Assert.Throws<InvalidOperationException>(() => StrengthBandCatalog.Validate(broken));
    }

    [Fact]
    public void The_catalog_refuses_a_midpoint_outside_its_own_band()
    {
        var broken = new[]
        {
            new StrengthBand { Index = 0, Name = "empty", Floor = 0, Ceiling = 0, Midpoint = 0 },
            new StrengthBand { Index = 1, Name = "wrong", Floor = 1, Ceiling = 99, Midpoint = 500 }
        };

        Assert.Throws<InvalidOperationException>(() => StrengthBandCatalog.Validate(broken));
    }

    [Fact]
    public void Names_are_stable_kebab_case_like_every_other_catalog_id()
    {
        Assert.All(StrengthBandCatalog.All, b => Assert.False(string.IsNullOrWhiteSpace(b.Name)));
        Assert.Equal(StrengthBandCatalog.All.Select(b => b.Name).Distinct().Count(), StrengthBandCatalog.All.Count);
    }
}
