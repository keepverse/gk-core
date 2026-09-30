using FusionRpg.Core.Hud;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.Hud;

public sealed class ActorHudTuningLoaderTests
{
    [Fact]
    public void Parse_empty_json_rejects()
    {
        var ex = Assert.Throws<ActorHudTuningRejection>(() => ActorHudTuningLoader.Parse(""));
        Assert.Contains("empty document", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_invalid_json_rejects()
    {
        var ex = Assert.Throws<ActorHudTuningRejection>(() => ActorHudTuningLoader.Parse("{ not json"));
        Assert.Contains("not valid JSON", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_missing_badgeMax_rejects()
    {
        var json = """
            {
              "schemaVersion": 1,
              "version": 2,
              "statusStripMax": 3,
              "hpSliverEnabled": false,
              "anchorKind": "body",
              "worldYOffset": -0.35,
              "barWorldWidth": 0.95,
              "barWorldHeight": 0.12,
              "rowOffsetIdentity": 0.30,
              "rowOffsetResources": 0.0,
              "rowOffsetStatuses": 0.16,
              "maxStackPips": 3,
              "magnitudeMidThreshold": 10.0,
              "magnitudeHighThreshold": 30.0
            }
            """;

        var ex = Assert.Throws<ActorHudTuningRejection>(() => ActorHudTuningLoader.Parse(json));
        Assert.Contains("badgeMax", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_shipped_actor_hud_v1_json()
    {
        var path = Path.Combine(FindRepoRoot(), "data", "tuning", "actor-hud.v1.json");
        Assert.True(File.Exists(path), "missing " + path);

        var tuning = ActorHudTuningLoader.Parse(File.ReadAllText(path));

        Assert.Equal(1, tuning.SchemaVersion);
        Assert.Equal(2, tuning.Version);
        Assert.Equal(3, tuning.StatusStripMax);
        Assert.False(tuning.HpSliverEnabled);
        Assert.Equal(99, tuning.BadgeMax);
        Assert.Equal("body", tuning.AnchorKind);
        Assert.Equal(0.08, tuning.WorldYOffset);
        Assert.Equal(0.95, tuning.BarWorldWidth);
        Assert.Equal(0.12, tuning.BarWorldHeight);
        Assert.Equal(0.30, tuning.RowOffsetIdentity);
        Assert.Equal(0.0, tuning.RowOffsetResources);
        Assert.Equal(0.16, tuning.RowOffsetStatuses);
        Assert.Equal(3, tuning.MaxStackPips);
        Assert.Null(tuning.EliteTierThreshold);
        Assert.Equal(10.0, tuning.MagnitudeMidThreshold);
        Assert.Equal(30.0, tuning.MagnitudeHighThreshold);
    }

    [Fact]
    public void Parse_shipped_actor_hud_v4_json_includes_compact_larger_screen_silhouette_layout()
    {
        var path = Path.Combine(FindRepoRoot(), "data", "tuning", "actor-hud.v4.json");
        Assert.True(File.Exists(path), "missing " + path);

        var tuning = ActorHudTuningLoader.Parse(File.ReadAllText(path));

        Assert.Equal(4, tuning.Version);
        Assert.Equal(0d, tuning.ScreenGapPixels);
        Assert.Equal(1.2d, tuning.ScreenWidthFactor);
        Assert.Equal(48d, tuning.ScreenMinWidthPixels);
        Assert.Equal(144d, tuning.ScreenMaxWidthPixels);
        Assert.Equal(4.5d, tuning.ScreenRowGapPixels);
        Assert.Equal(10.5d, tuning.ScreenResourceHeightPixels);
    }

    [Fact]
    public void Parse_shipped_actor_hud_v5_json_includes_element_row_geometry()
    {
        var path = Path.Combine(FindRepoRoot(), "data", "tuning", "actor-hud.v5.json");
        Assert.True(File.Exists(path), "missing " + path);

        var tuning = ActorHudTuningLoader.Parse(File.ReadAllText(path));

        Assert.Equal(5, tuning.Version);
        Assert.Equal(16d, tuning.ScreenElementIconPixels);
        Assert.Equal(3d, tuning.ScreenElementGapPixels);
        Assert.Equal(3d, tuning.ScreenElementRowGapPixels);
    }

    [Fact]
    public void Parse_shipped_actor_hud_v6_json_includes_identity_element_geometry()
    {
        var path = Path.Combine(FindRepoRoot(), "data", "tuning", "actor-hud.v6.json");
        Assert.True(File.Exists(path), "missing " + path);

        var tuning = ActorHudTuningLoader.Parse(File.ReadAllText(path));

        Assert.Equal(6, tuning.Version);
        Assert.Equal(24d, tuning.ScreenIdentityElementPrimaryPixels);
        Assert.Equal(20d, tuning.ScreenIdentityElementSecondaryPixels);
        Assert.Equal(3d, tuning.ScreenIdentityElementGapPixels);
    }

    [Fact]
    public void Parse_shipped_actor_hud_v7_json_increases_identity_element_legibility()
    {
        var path = Path.Combine(FindRepoRoot(), "data", "tuning", "actor-hud.v7.json");
        Assert.True(File.Exists(path), "missing " + path);

        var tuning = ActorHudTuningLoader.Parse(File.ReadAllText(path));

        Assert.Equal(7, tuning.Version);
        Assert.Equal(36d, tuning.ScreenIdentityElementPrimaryPixels);
        Assert.Equal(30d, tuning.ScreenIdentityElementSecondaryPixels);
        Assert.Equal(4.5d, tuning.ScreenIdentityElementGapPixels);
    }

    [Fact]
    public void Parse_rejects_non_body_anchorKind()
    {
        var json = """
            {
              "schemaVersion": 1,
              "version": 2,
              "statusStripMax": 3,
              "hpSliverEnabled": false,
              "badgeMax": 99,
              "anchorKind": "crown",
              "worldYOffset": -0.35,
              "barWorldWidth": 0.95,
              "barWorldHeight": 0.12,
              "rowOffsetIdentity": 0.30,
              "rowOffsetResources": 0.0,
              "rowOffsetStatuses": 0.16,
              "maxStackPips": 3,
              "magnitudeMidThreshold": 10.0,
              "magnitudeHighThreshold": 30.0
            }
            """;

        var ex = Assert.Throws<ActorHudTuningRejection>(() => ActorHudTuningLoader.Parse(json));
        Assert.Contains("anchorKind", ex.Message, StringComparison.Ordinal);
        Assert.Contains("body", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_missing_magnitudeMidThreshold_rejects()
    {
        var json = """
            {
              "schemaVersion": 1,
              "version": 2,
              "statusStripMax": 3,
              "hpSliverEnabled": false,
              "badgeMax": 99,
              "anchorKind": "body",
              "worldYOffset": -0.35,
              "barWorldWidth": 0.95,
              "barWorldHeight": 0.12,
              "rowOffsetIdentity": 0.30,
              "rowOffsetResources": 0.0,
              "rowOffsetStatuses": 0.16,
              "maxStackPips": 3,
              "magnitudeHighThreshold": 30.0
            }
            """;

        var ex = Assert.Throws<ActorHudTuningRejection>(() => ActorHudTuningLoader.Parse(json));
        Assert.Contains("magnitudeMidThreshold", ex.Message, StringComparison.Ordinal);
    }

    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
