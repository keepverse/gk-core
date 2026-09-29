using FusionRpg.Core.Delve.Events;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Events;

/// <summary>D3.6 (spec-event-deck.md §5's dispatch table row for `ui.present`/`op:banner`):
/// `DelveUiPresentSink` implements the existing, shipped `IUiPresentSink`.</summary>
public class DelveUiPresentSinkTests
{
    [Fact]
    public void Constructor_rejects_a_non_positive_default_duration()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DelveUiPresentSink(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DelveUiPresentSink(-1));
    }

    [Fact]
    public void ShowBanner_null_or_blank_bannerId_throws()
    {
        var sink = new DelveUiPresentSink(2600);
        Assert.Throws<ArgumentException>(() => sink.ShowBanner(null!, 1000));
        Assert.Throws<ArgumentException>(() => sink.ShowBanner("", 1000));
        Assert.Throws<ArgumentException>(() => sink.ShowBanner("   ", 1000));
    }

    [Fact]
    public void An_authored_duration_is_kept_verbatim()
    {
        var sink = new DelveUiPresentSink(2600);
        sink.ShowBanner("curio.horror", 5000);
        Assert.Equal(new DelveBanner("curio.horror", 5000), Assert.Single(sink.Banners));
    }

    [Fact]
    public void A_null_duration_falls_back_to_the_callers_own_default()
    {
        var sink = new DelveUiPresentSink(2600);
        sink.ShowBanner("curio.blessing", null);
        Assert.Equal(new DelveBanner("curio.blessing", 2600), Assert.Single(sink.Banners));
    }

    [Fact]
    public void A_different_sink_instance_can_carry_a_different_default()
    {
        var short_ = new DelveUiPresentSink(1000);
        var long_ = new DelveUiPresentSink(9000);
        short_.ShowBanner("a", null);
        long_.ShowBanner("a", null);
        Assert.Equal(1000, short_.Banners[0].DurationMs);
        Assert.Equal(9000, long_.Banners[0].DurationMs);
    }

    [Fact]
    public void Banners_accumulate_in_call_order()
    {
        var sink = new DelveUiPresentSink(2600);
        sink.ShowBanner("first", 100);
        sink.ShowBanner("second", null);
        sink.ShowBanner("third", 300);

        Assert.Equal(3, sink.Banners.Count);
        Assert.Equal("first", sink.Banners[0].BannerId);
        Assert.Equal("second", sink.Banners[1].BannerId);
        Assert.Equal(2600, sink.Banners[1].DurationMs);
        Assert.Equal("third", sink.Banners[2].BannerId);
    }

    [Fact]
    public void SetMeter_is_a_no_op_and_never_touches_Banners()
    {
        var sink = new DelveUiPresentSink(2600);
        sink.SetMeter("actor:1", "hp", 0.5);
        Assert.Empty(sink.Banners);
    }

    [Fact]
    public void Implements_the_real_shipped_IUiPresentSink_interface()
    {
        FusionRpg.Core.Effects.IUiPresentSink sink = new DelveUiPresentSink(2600);
        sink.ShowBanner("via-interface", null);
        Assert.IsType<DelveUiPresentSink>(sink);
    }
}
