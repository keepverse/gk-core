using FusionRpg.Core.Hud;
using Xunit;

namespace FusionRpg.Core.Tests.Hud;

public sealed class ActorHudLayoutTests
{
    static ActorHudStatusToken Tok(string id, bool cc = false, MagnitudeBand band = MagnitudeBand.Low) =>
        new(id, cc, band);

    [Fact]
    public void Prioritize_cc_first()
    {
        var input = new[]
        {
            Tok("expose"),
            Tok("freeze", cc: true),
            Tok("command"),
        };

        var (visible, overflow) = ActorHudLayout.Prioritize(input, maxVisible: 3);

        Assert.Equal(0, overflow);
        Assert.Equal(3, visible.Count);
        Assert.Equal("freeze", visible[0].Id);
        Assert.True(visible[0].Cc);
    }

    [Fact]
    public void Prioritize_overflow_count()
    {
        var input = new[]
        {
            Tok("a"),
            Tok("b"),
            Tok("c"),
            Tok("d"),
            Tok("e"),
        };

        var (visible, overflow) = ActorHudLayout.Prioritize(input, maxVisible: 3);

        Assert.Equal(2, overflow);
        Assert.Equal(3, visible.Count);
    }

    [Fact]
    public void Prioritize_stable_order()
    {
        var input = new[]
        {
            Tok("spark"),
            Tok("expose"),
            Tok("command"),
        };

        var first = ActorHudLayout.Prioritize(input, maxVisible: 3);
        var second = ActorHudLayout.Prioritize(input, maxVisible: 3);

        Assert.Equal(first.Visible.Select(v => v.Id), second.Visible.Select(v => v.Id));
    }

    [Fact]
    public void FromTheta_monotonic()
    {
        var low = PowerBandDisplay.FromTheta(5);
        var mid = PowerBandDisplay.FromTheta(20);
        var high = PowerBandDisplay.FromTheta(50);

        Assert.True(mid >= low);
        Assert.True(high >= mid);
    }

    [Fact]
    public void FromTheta_zero_and_cap()
    {
        Assert.Equal(1, PowerBandDisplay.FromTheta(0));
        Assert.Equal(1, PowerBandDisplay.FromTheta(-10));
        Assert.Equal(99, PowerBandDisplay.FromTheta(200));
        Assert.Equal(42, PowerBandDisplay.FromTheta(42));
    }

    [Fact]
    public void Prioritize_cc_first_when_cap_truncates()
    {
        var input = new[]
        {
            Tok("expose"),
            Tok("freeze", cc: true),
            Tok("command"),
        };

        var (visible, overflow) = ActorHudLayout.Prioritize(input, maxVisible: 2);

        Assert.Equal(1, overflow);
        Assert.Equal(2, visible.Count);
        Assert.Equal("freeze", visible[0].Id);
        Assert.True(visible[0].Cc);
    }

    [Fact]
    public void Prioritize_empty_input()
    {
        var (visible, overflow) = ActorHudLayout.Prioritize(Array.Empty<ActorHudStatusToken>(), maxVisible: 3);

        Assert.Empty(visible);
        Assert.Equal(0, overflow);
    }

    [Fact]
    public void Prioritize_maxVisible_zero()
    {
        var input = new[] { Tok("expose") };

        var (visible, overflow) = ActorHudLayout.Prioritize(input, maxVisible: 0);

        Assert.Empty(visible);
        Assert.Equal(1, overflow);
    }

    [Fact]
    public void Prioritize_negative_max_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ActorHudLayout.Prioritize(new[] { Tok("expose") }, maxVisible: -1));
    }

    [Fact]
    public void FromTheta_respects_configured_badgeMax()
    {
        var prior = ActorHudTuningHub.Tuning;
        try
        {
            ActorHudTuningHub.Configure(prior with { BadgeMax = 50 });
            Assert.Equal(50, PowerBandDisplay.FromTheta(200));
            Assert.Equal(50, PowerBandDisplay.FromTheta(50));
        }
        finally
        {
            ActorHudTuningHub.Configure(prior);
        }
    }

    [Fact]
    public void FromTheta_badgeMax_below_one_throws()
    {
        var prior = ActorHudTuningHub.Tuning;
        try
        {
            ActorHudTuningHub.Configure(prior with { BadgeMax = 0 });
            Assert.Throws<InvalidOperationException>(() => PowerBandDisplay.FromTheta(5));
        }
        finally
        {
            ActorHudTuningHub.Configure(prior);
        }
    }

    [Fact]
    public void Configure_null_throws()
    {
        Assert.Throws<ArgumentNullException>(() => ActorHudTuningHub.Configure(null!));
    }

    [Fact]
    public void Tuning_loader_parses_v1_defaults()
    {
        var json = """
            {
              "schemaVersion": 1,
              "version": 2,
              "statusStripMax": 3,
              "hpSliverEnabled": false,
              "badgeMax": 99,
              "anchorKind": "body",
              "worldYOffset": 0.08,
              "barWorldWidth": 0.95,
              "barWorldHeight": 0.12,
              "rowOffsetIdentity": 0.30,
              "rowOffsetResources": 0.0,
              "rowOffsetStatuses": 0.16,
              "maxStackPips": 3,
              "eliteTierThreshold": null,
              "magnitudeMidThreshold": 10.0,
              "magnitudeHighThreshold": 30.0
            }
            """;

        var tuning = ActorHudTuningLoader.Parse(json);

        Assert.Equal(3, tuning.StatusStripMax);
        Assert.False(tuning.HpSliverEnabled);
        Assert.Equal(99, tuning.BadgeMax);
        Assert.Equal("body", tuning.AnchorKind);
        Assert.Equal(0.08, tuning.WorldYOffset);
    }
}
