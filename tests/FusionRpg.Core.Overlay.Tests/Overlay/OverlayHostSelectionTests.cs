using FusionRpg.Core.Overlay;
using Xunit;

namespace FusionRpg.Core.Tests.Overlay;

/// <summary>
/// Which process owns the web view. Env beats host config, mirroring how FUSIONRPG_SERVER_URL
/// already overrides the injector's ServerUrl setting.
/// </summary>
public class OverlayHostSelectionTests
{
    [Fact]
    public void Nothing_set_means_the_launcher_hosts_it()
    {
        Assert.Equal(OverlayHostMode.Launcher, OverlayHostSelection.Resolve(null, null));
    }

    [Theory]
    [InlineData("injector")]
    [InlineData("INJECTOR")]
    [InlineData("  Injector  ")]
    public void Env_can_select_the_injector(string env)
    {
        Assert.Equal(OverlayHostMode.Injector, OverlayHostSelection.Resolve(env, null));
    }

    [Theory]
    [InlineData("launcher")]
    [InlineData("Launcher")]
    public void Config_can_select_the_launcher_explicitly(string config)
    {
        Assert.Equal(OverlayHostMode.Launcher, OverlayHostSelection.Resolve(null, config));
    }

    [Fact]
    public void Config_can_select_the_injector()
    {
        Assert.Equal(OverlayHostMode.Injector, OverlayHostSelection.Resolve(null, "injector"));
    }

    [Fact]
    public void Env_wins_over_config_in_both_directions()
    {
        Assert.Equal(OverlayHostMode.Launcher, OverlayHostSelection.Resolve("launcher", "injector"));
        Assert.Equal(OverlayHostMode.Injector, OverlayHostSelection.Resolve("injector", "launcher"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("wat")]
    [InlineData("both")]
    public void An_unusable_env_value_falls_through_to_config_rather_than_overriding_it(string env)
    {
        // A typo in an env var should not silently discard a deliberate config choice.
        Assert.Equal(OverlayHostMode.Injector, OverlayHostSelection.Resolve(env, "injector"));
    }

    [Theory]
    [InlineData("wat")]
    [InlineData("")]
    public void An_unusable_value_everywhere_lands_on_the_launcher(string junk)
    {
        Assert.Equal(OverlayHostMode.Launcher, OverlayHostSelection.Resolve(junk, junk));
    }

    [Fact]
    public void The_env_var_name_is_the_documented_one()
    {
        Assert.Equal("FUSIONRPG_OVERLAY_HOST", OverlayHostSelection.EnvVar);
    }

    [Fact]
    public void Launcher_is_the_default_mode()
    {
        // Default must stay launcher until the in-game host is proven live.
        Assert.Equal(OverlayHostMode.Launcher, default(OverlayHostMode));
    }
}
