using FusionRpg.Core.Overlay;
using Xunit;

namespace FusionRpg.Core.Tests.Overlay;

/// <summary>
/// The tombstone's placement (Decision 17 removed the hand-derived hit box; uGUI owns hit-testing, so
/// what remains is placement). A feel number loaded from data/tuning/overlay.v{n}.json — never a
/// default in code, so the hub must be configured first (tunables-ssot.md T5).
/// </summary>
public class RiftMenuPlacementTests
{
    static RiftMenuPlacementTests()
    {
        OverlayTuningHub.Configure(DefaultTuning());
    }

    [Theory]
    [InlineData(1280f, 720f)]
    [InlineData(1920f, 1080f)]
    [InlineData(3840f, 2160f)]
    public void Resolved_rect_is_positive_and_on_screen(float w, float h)
    {
        var r = RiftMenuPlacement.Resolve(w, h);

        Assert.True(r.Width > 0f, "width must be positive");
        Assert.True(r.Height > 0f, "height must be positive");
        Assert.True(r.Width >= 44f && r.Height >= 44f, "the min-device-px floor must hold");
        Assert.True(r.X >= -0.001f && r.Y >= -0.001f, "rect must not start off-screen");
        Assert.True(r.Right <= w + 0.001f, "rect must not overrun the right edge");
        Assert.True(r.Bottom <= h + 0.001f, "rect must not overrun the bottom edge");
    }

    /// <summary>
    /// A degenerate screen (Unity reports 0x0 before the first layout pass) must still yield a
    /// well-formed rect rather than a negative one. There is no screen to be "on", so only
    /// well-formedness is asserted — not on-screennness.
    /// </summary>
    [Fact]
    public void A_degenerate_screen_yields_the_floor_at_the_origin()
    {
        var r = RiftMenuPlacement.Resolve(0f, 0f);
        Assert.Equal(44f, r.Width);
        Assert.Equal(44f, r.Height);
        Assert.Equal(0f, r.X);
        Assert.Equal(0f, r.Y);
    }

    [Fact]
    public void A_tiny_window_still_yields_a_hittable_rect()
    {
        // The floor is the whole point: 0.11 of a 100px window would be 11px, a sliver, so the floor
        // lifts both dimensions to 44 — which still fits, so the screen clamp does not engage.
        // Centre (50, 62) minus half the 44-box gives (28, 40).
        var r = RiftMenuPlacement.Resolve(100f, 100f);
        Assert.Equal(44f, r.Width);
        Assert.Equal(44f, r.Height);
        Assert.Equal(28f, r.X);
        Assert.Equal(40f, r.Y);
    }

    [Fact]
    public void The_floor_holds_when_the_screen_is_larger_than_it()
    {
        // 200x200: 0.11*200 = 22 and 0.16*200 = 32, both below the 44 floor, and 44 fits the screen.
        var r = RiftMenuPlacement.Resolve(200f, 200f);
        Assert.Equal(44f, r.Width);
        Assert.Equal(44f, r.Height);
    }

    [Fact]
    public void Placement_is_centred_on_the_configured_anchor()
    {
        var r = RiftMenuPlacement.Resolve(1920f, 1080f);
        // 0.11 * 1920 = 211.2 wide, 0.16 * 1080 = 172.8 tall, centred at (0.5, 0.62).
        Assert.Equal(1920f * 0.5f, r.X + r.Width / 2f, 2);
        Assert.Equal(1080f * 0.62f, r.Y + r.Height / 2f, 2);
    }

    [Fact]
    public void It_is_a_pure_function_of_the_arguments()
    {
        // Decision 17 removed the wobble-vs-hitbox problem, but purity still matters: the rect must
        // not depend on any hidden animation state. Same inputs, same rect.
        Assert.Equal(RiftMenuPlacement.Resolve(1920f, 1080f), RiftMenuPlacement.Resolve(1920f, 1080f));
    }

    // ---- Decision 18: the normalized anchor rect a uGUI node is placed with ----

    [Fact]
    public void Normalized_rect_is_inside_the_parent()
    {
        var n = RiftMenuPlacement.ResolveNormalized();
        Assert.InRange(n.MinX, 0f, 1f);
        Assert.InRange(n.MinY, 0f, 1f);
        Assert.InRange(n.MaxX, 0f, 1f);
        Assert.InRange(n.MaxY, 0f, 1f);
        Assert.True(n.Width > 0f && n.Height > 0f);
        Assert.True(n.MaxX >= n.MinX && n.MaxY >= n.MinY);
    }

    [Fact]
    public void Normalized_rect_matches_the_tuning_fractions()
    {
        var n = RiftMenuPlacement.ResolveNormalized();
        Assert.Equal(0.11f, n.Width, 4);
        Assert.Equal(0.16f, n.Height, 4);
        // Centred on (0.5, 0.62).
        Assert.Equal(0.5f, (n.MinX + n.MaxX) / 2f, 4);
        Assert.Equal(0.62f, (n.MinY + n.MaxY) / 2f, 4);
    }

    [Fact]
    public void A_box_wider_than_the_parent_is_clamped_inside_it()
    {
        // A pathological tuning (fraction 1.0) must still yield anchors inside 0..1, never outside.
        OverlayTuningHub.Configure(new OverlayTuning(
            SchemaVersion: 1, Version: 1,
            Pause: new OverlayPauseTuning(0f, 10f),
            SwitchLayout: new OverlaySwitchLayoutTuning(72f, 28f, 16f, 1080f, 1f, 3f),
            SwitchState: new OverlaySwitchStateTuning(300, 30000, 3000),
            SettingsGui: new OverlaySettingsGuiTuning(280f, 196f),
            RiftMenu: new RiftMenuTuning(1.5f, 1.5f, 1.0f, 1.0f, 44f)));

        var n = RiftMenuPlacement.ResolveNormalized();
        Assert.Equal(0f, n.MinX, 4);
        Assert.Equal(0f, n.MinY, 4);
        Assert.Equal(1f, n.MaxX, 4);
        Assert.Equal(1f, n.MaxY, 4);

        // Restore the shared fixture for the remaining tests in the class.
        OverlayTuningHub.Configure(DefaultTuning());
    }

    static OverlayTuning DefaultTuning() => new(
        SchemaVersion: 1, Version: 1,
        Pause: new OverlayPauseTuning(0f, 10f),
        SwitchLayout: new OverlaySwitchLayoutTuning(72f, 28f, 16f, 1080f, 1f, 3f),
        SwitchState: new OverlaySwitchStateTuning(300, 30000, 3000),
        SettingsGui: new OverlaySettingsGuiTuning(280f, 196f),
        RiftMenu: new RiftMenuTuning(0.5f, 0.62f, 0.11f, 0.16f, 44f));
}

/// <summary>The parser half of T3: a missing group is a named rejection, never a built-in default.</summary>
public class OverlayTuningRiftMenuParseTests
{
    const string WithRiftMenu = """
        {
          "schemaVersion": 1, "version": 1,
          "pause": { "pausedTimeScale": 0.0, "maxResumeScale": 10.0 },
          "switchLayout": { "baseButtonW": 72, "baseButtonH": 28, "baseMargin": 16,
                            "referenceHeight": 1080, "minScale": 1, "maxScale": 3 },
          "switchState": { "debounceMs": 300, "probeIntervalMs": 30000, "sendTimeoutMs": 3000 },
          "settingsGui": { "panelW": 280, "panelH": 196 },
          "riftMenu": { "anchorCenterX": 0.5, "anchorCenterY": 0.62,
                        "widthFraction": 0.11, "heightFraction": 0.16, "minDevicePx": 44 }
        }
        """;

    [Fact]
    public void Parses_the_riftMenu_group()
    {
        var t = OverlayTuningLoader.Parse(WithRiftMenu);
        Assert.Equal(0.5f, t.RiftMenu.AnchorCenterX);
        Assert.Equal(0.62f, t.RiftMenu.AnchorCenterY);
        Assert.Equal(0.11f, t.RiftMenu.WidthFraction);
        Assert.Equal(0.16f, t.RiftMenu.HeightFraction);
        Assert.Equal(44f, t.RiftMenu.MinDevicePx);
    }

    [Fact]
    public void A_missing_riftMenu_group_is_rejected_by_name()
    {
        var json = WithRiftMenu.Replace(
            """
              "riftMenu": { "anchorCenterX": 0.5, "anchorCenterY": 0.62,
                            "widthFraction": 0.11, "heightFraction": 0.16, "minDevicePx": 44 }
            """.ReplaceLineEndings("\n"),
            "\"riftMenu\": null",
            StringComparison.Ordinal);

        var ex = Assert.Throws<OverlayTuningRejection>(() => OverlayTuningLoader.Parse(json));
        Assert.Contains("riftMenu", ex.Message, StringComparison.Ordinal);
    }
}
