using FusionRpg.Core.ActorSurface;
using FusionRpg.Core.Hud;
using FusionRpg.Core.Status;
using Xunit;

namespace FusionRpg.Core.Tests.Hud;

public sealed class ActorHudDisplayTokensTests
{
    public ActorHudDisplayTokensTests()
    {
        StatusSurfaceCatalogHub.Clear();
    }

    [Theory]
    [InlineData(ActorHudTier.Elite, "E")]
    [InlineData(ActorHudTier.Boss, "B")]
    [InlineData(ActorHudTier.Unique, "U")]
    [InlineData(ActorHudTier.Normal, "")]
    public void TierLetter_enum(ActorHudTier tier, string expect) =>
        Assert.Equal(expect, ActorHudDisplayTokens.TierLetter(tier));

    [Theory]
    [InlineData("elite", "E")]
    [InlineData("boss", "B")]
    [InlineData("unique", "U")]
    [InlineData("normal", "")]
    public void TierLetter_string(string tier, string expect) =>
        Assert.Equal(expect, ActorHudDisplayTokens.TierLetter(tier));

    [Fact]
    public void ResolveStatus_unconfigured_returns_designed_placeholder()
    {
        var resolved = ActorHudDisplayTokens.ResolveStatus("expose");
        Assert.Equal("·", resolved.HudToken);
        Assert.Equal("#a89880", resolved.Color);
        Assert.Equal("Unknown status", resolved.DisplayName);
        // H3 guard: must never equal id-slice initials ("expose" → "EX")
        Assert.NotEqual("EX", resolved.HudToken);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveStatus_blank_id_is_placeholder(string? id)
    {
        ConfigureMiniCatalog();
        var resolved = ActorHudDisplayTokens.ResolveStatus(id);
        Assert.Equal(ActorHudDisplayTokens.UnknownStatus, resolved);
    }

    [Fact]
    public void ResolveStatus_catalog_hit_uses_authored_fields()
    {
        ConfigureMiniCatalog();
        var resolved = ActorHudDisplayTokens.ResolveStatus("expose");
        Assert.Equal("E", resolved.HudToken);
        Assert.Equal("#c45cff", resolved.Color);
        Assert.Equal("Expose", resolved.DisplayName);
        Assert.NotEqual("EX", resolved.HudToken);
    }

    [Fact]
    public void ResolveStatus_unknown_id_is_placeholder_not_id_slice()
    {
        ConfigureMiniCatalog();
        var resolved = ActorHudDisplayTokens.ResolveStatus("not_a_real_status");
        Assert.Equal("·", resolved.HudToken);
        Assert.Equal("Unknown status", resolved.DisplayName);
        Assert.NotEqual("NA", resolved.HudToken);
        Assert.NotEqual("NO", resolved.HudToken);
    }

    [Fact]
    public void TryGet_is_ordinal_ignore_case()
    {
        ConfigureMiniCatalog();
        Assert.True(StatusSurfaceCatalogHub.TryGet("EXPOSE", out var entry));
        Assert.Equal("expose", entry.Id);
        Assert.Equal("E", entry.HudToken);
        Assert.False(StatusSurfaceCatalogHub.TryGet("missing", out _));
    }

    [Fact]
    public void ResolveStatus_expose_never_equals_id_slice()
    {
        // Unconfigured path
        StatusSurfaceCatalogHub.Clear();
        Assert.NotEqual("EX", ActorHudDisplayTokens.ResolveStatus("expose").HudToken);

        // Catalog path — authored token is single "E", not "EX"
        ConfigureMiniCatalog();
        Assert.NotEqual("EX", ActorHudDisplayTokens.ResolveStatus("expose").HudToken);
        Assert.Equal("E", ActorHudDisplayTokens.ResolveStatus("expose").HudToken);
    }

    static void ConfigureMiniCatalog()
    {
        StatusSurfaceCatalogHub.Configure(new StatusSurfaceCatalog(
            SchemaVersion: 1,
            Version: 1,
            Entries: new[]
            {
                new StatusSurfaceEntry(
                    Id: "expose",
                    Kind: StatusKind.Debuff,
                    Categories: new[] { "dot" },
                    Stacking: StatusStacking.Refresh,
                    PayloadKinds: Array.Empty<StatusPayloadKind>(),
                    DisplayName: "Expose",
                    Reading: "Makes the next hits land cleaner.",
                    HudToken: "E",
                    Color: "#c45cff"),
            }));
    }
}
