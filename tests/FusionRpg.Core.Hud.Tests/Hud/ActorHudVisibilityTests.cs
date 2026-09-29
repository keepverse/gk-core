using FusionRpg.Core.Hud;
using Xunit;

namespace FusionRpg.Core.Tests.Hud;

public sealed class ActorHudVisibilityTests
{
    static ActorHudSnapshot Snap(
        ActorHudTier tier = ActorHudTier.Normal,
        string role = "vanilla",
        int? levelBand = null,
        IReadOnlyList<string>? flags = null,
        IReadOnlyList<ActorHudStatusToken>? statuses = null,
        int overflow = 0,
        ActorHudShield? shield = null) =>
        new(
            new ActorHudIdentity(tier, role, levelBand, flags ?? Array.Empty<string>()),
            shield is null ? null : new ActorHudResources(shield, null, null),
            statuses ?? Array.Empty<ActorHudStatusToken>(),
            new ActorHudOverflow(overflow));

    [Fact]
    public void Normal_with_level_band_only_shows()
    {
        Assert.True(ActorHudVisibility.ShouldShow(Snap(levelBand: 3), shieldBarEnabled: false));
        Assert.True(ActorHudVisibility.ShouldShow(Snap(levelBand: 3), shieldBarEnabled: true));
    }

    [Fact]
    public void Normal_vanilla_no_band_no_status_no_shield_hides()
    {
        Assert.False(ActorHudVisibility.ShouldShow(Snap(), shieldBarEnabled: true));
        Assert.False(ActorHudVisibility.ShouldShow(Snap(), shieldBarEnabled: false));
    }

    [Fact]
    public void Shield_only_hides_when_F9_disabled()
    {
        var shield = new ActorHudShield(50, 100, Array.Empty<ActorHudShieldStack>());
        Assert.False(ActorHudVisibility.ShouldShow(Snap(shield: shield), shieldBarEnabled: false));
        Assert.True(ActorHudVisibility.ShouldShow(Snap(shield: shield), shieldBarEnabled: true));
    }

    [Fact]
    public void Unique_or_statuses_always_show()
    {
        Assert.True(ActorHudVisibility.ShouldShow(
            Snap(tier: ActorHudTier.Unique, role: "specimen"),
            shieldBarEnabled: false));
        Assert.True(ActorHudVisibility.ShouldShow(
            Snap(statuses: new[] { new ActorHudStatusToken("expose", false, MagnitudeBand.Low) }),
            shieldBarEnabled: false));
        Assert.True(ActorHudVisibility.ShouldShow(
            Snap(overflow: 2),
            shieldBarEnabled: false));
    }

    [Fact]
    public void Non_vanilla_role_shows_without_band()
    {
        Assert.True(ActorHudVisibility.ShouldShow(Snap(role: "commander"), shieldBarEnabled: false));
    }
}
