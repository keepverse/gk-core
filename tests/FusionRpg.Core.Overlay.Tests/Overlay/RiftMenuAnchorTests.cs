using FusionRpg.Core.Overlay;
using Xunit;

namespace FusionRpg.Core.Tests.Overlay;

/// <summary>
/// The menu-screen **presence** signal (map Decision 17). No Unity, no Harmony: the state machine and
/// its type-guard semantics are exercised directly.
///
/// The load-bearing property is the type guard: the injector patches <c>BaseMenu.OnHide/OnExit</c>,
/// which every menu inherits, so a submenu hiding must NOT clear a main menu that is still up.
/// </summary>
public class RiftMenuAnchorTests
{
    [Fact]
    public void Starts_empty()
    {
        Assert.Equal(RiftMenuSurface.None, new RiftMenuAnchor().Current);
    }

    [Fact]
    public void Screen_start_sets_the_surface()
    {
        var a = new RiftMenuAnchor();
        a.Set(RiftMenuSurface.MainMenu);
        Assert.Equal(RiftMenuSurface.MainMenu, a.Current);
        Assert.True(a.IsReviewed);
    }

    [Fact]
    public void Screen_hide_clears_the_surface()
    {
        var a = new RiftMenuAnchor();
        a.Set(RiftMenuSurface.MainMenu);
        a.Clear(RiftMenuSurface.MainMenu);
        Assert.Equal(RiftMenuSurface.None, a.Current);
        Assert.False(a.IsReviewed);
    }

    /// <summary>The type guard: a DIFFERENT screen going away must not clear ours.</summary>
    [Fact]
    public void Clearing_a_different_surface_leaves_the_present_one_alone()
    {
        var a = new RiftMenuAnchor();
        a.Set(RiftMenuSurface.MainMenu);
        a.Clear(RiftMenuSurface.None);
        Assert.Equal(RiftMenuSurface.MainMenu, a.Current);
    }

    [Fact]
    public void Clearing_an_already_cleared_surface_is_a_noop()
    {
        var a = new RiftMenuAnchor();
        a.Clear(RiftMenuSurface.MainMenu);
        Assert.Equal(RiftMenuSurface.None, a.Current);
    }

    [Fact]
    public void Set_after_clear_re_presents()
    {
        var a = new RiftMenuAnchor();
        a.Set(RiftMenuSurface.MainMenu);
        a.Clear(RiftMenuSurface.MainMenu);
        a.Set(RiftMenuSurface.MainMenu);
        Assert.Equal(RiftMenuSurface.MainMenu, a.Current);
    }
}

/// <summary>
/// The reviewed-surface policy. <see cref="RiftMenuSurface"/> is a **closed vocabulary** the code owns
/// and a human reviews (not a derived population), so pinning its literal membership here is correct —
/// and this test says why, so a later reader does not mistake it for a forbidden population count.
/// </summary>
public class RiftMenuSurfacePolicyTests
{
    [Fact]
    public void The_surface_enum_is_a_closed_vocabulary_pinned_with_its_reason()
    {
        // Pinned LITERAL on purpose: this enum is a closed set the code owns and a human changes.
        // It is not a corpus/roster population that grows when content ships, so it may not be
        // asserted as derived. Widening it is an owner review (spec-menu-anchor.md, Open Question 1).
        var members = Enum.GetNames<RiftMenuSurface>();
        Assert.Equal(new[] { "None", "MainMenu" }, members);
    }

    [Fact]
    public void MainMenu_is_reviewed_and_None_is_not()
    {
        Assert.True(RiftMenuAnchorPolicy.IsReviewed(RiftMenuSurface.MainMenu));
        Assert.False(RiftMenuAnchorPolicy.IsReviewed(RiftMenuSurface.None));
    }

    /// <summary>The pause menu is excluded by name, with its reason recorded — not merely absent.</summary>
    [Fact]
    public void The_pause_menu_is_excluded_by_name_with_a_reason()
    {
        var excluded = RiftMenuAnchorPolicy.ExcludedByDesign;
        Assert.Contains(excluded, e => e.Contains("PauseMenu", StringComparison.Ordinal));
        Assert.Contains(excluded, e => e.Contains("live board", StringComparison.Ordinal));
    }
}
