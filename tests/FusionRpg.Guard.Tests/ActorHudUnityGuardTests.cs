using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Guard.Tests;

public sealed class ActorHudUnityGuardTests
{
    static readonly string[] RenderFiles =
    {
        Path.Combine("Hud", "ActorHudPool.cs"),
        Path.Combine("Hud", "ActorHudRowIdentity.cs"),
        Path.Combine("Hud", "ActorHudRowElements.cs"),
        Path.Combine("Hud", "ActorHudRowResources.cs"),
        Path.Combine("Hud", "ActorHudRowStatuses.cs"),
    };

    static readonly string[] BannedRuntimeNeedles =
    {
        "ShieldRuntime",
        "StatusRuntime",
        "EffectRuntime.Bag",
    };

    [Fact]
    public void ActorHudPool_uses_screen_space_silhouette_anchor_not_a_world_offset()
    {
        var text = ReadInjector(Path.Combine("Hud", "ActorHudPool.cs"));
        Assert.Contains("ActorScreenAnchorResolver.TryResolve", text, StringComparison.Ordinal);
        Assert.Contains("anchor.CenterX", text, StringComparison.Ordinal);
        Assert.Contains("anchor.TopY", text, StringComparison.Ordinal);
        Assert.Contains("ScreenSpaceOverlay", text, StringComparison.Ordinal);
        Assert.Contains("Only rows that actually draw advance the cursor", text, StringComparison.Ordinal);
        Assert.Contains("ActorHudRowStatuses.HasContent", text, StringComparison.Ordinal);
        Assert.DoesNotContain("WorldYOffset", text, StringComparison.Ordinal);
        Assert.DoesNotContain("UnitFrameResolver.Resolve", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ActorVisualResolver_unions_sprite_bounds_and_selects_a_rendering_camera()
    {
        var text = ReadInjector(Path.Combine("Hud", "ActorVisualResolver.cs"));
        Assert.Contains("GetComponentsInChildren<SpriteRenderer>", text, StringComparison.Ordinal);
        Assert.Contains("TryProjectBounds", text, StringComparison.Ordinal);
        Assert.Contains("Camera.allCameras", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Camera.main", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ActorHudScreenAnchorResolver_uses_the_pure_screen_anchor_math()
    {
        var text = ReadInjector(Path.Combine("Hud", "ActorScreenAnchorResolver.cs"));
        Assert.Contains("ActorVisualResolver.TryResolveScreenRect", text, StringComparison.Ordinal);
        Assert.Contains("ActorHudScreenAnchorMath.TryResolve", text, StringComparison.Ordinal);
    }

    [Fact]
    public void VfxDirector_wakes_actor_hud_on_live_match_phase()
    {
        var text = ReadInjector(Path.Combine("Fx", "VfxDirector.cs"));
        Assert.Contains("MatchHost.Runtime.Phase", text, StringComparison.Ordinal);
        Assert.Contains("MatchPhase.InMatch", text, StringComparison.Ordinal);
        Assert.Contains("ActorHudDirector.TickSync", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ActorHud_render_files_no_BodyWorld_or_bounds()
    {
        foreach (var relative in RenderFiles)
        {
            var text = ReadInjector(relative);
            Assert.DoesNotContain("LawnCoords.BodyWorld", text, StringComparison.Ordinal);
            Assert.DoesNotContain(".bounds", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ActorHud_render_path_no_direct_runtime_reads()
    {
        foreach (var relative in RenderFiles)
        {
            var text = ReadInjector(relative);
            foreach (var needle in BannedRuntimeNeedles)
                Assert.DoesNotContain(needle, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void VfxDirector_ticks_actor_hud_director()
    {
        var text = ReadInjector(Path.Combine("Fx", "VfxDirector.cs"));
        Assert.Contains("ActorHudDirector.TickSync", text, StringComparison.Ordinal);
        Assert.Contains("ActorHudDirector.StopAll", text, StringComparison.Ordinal);
    }

    [Fact]
    public void VfxDirector_does_not_call_ShieldBarPool_TickSync()
    {
        var text = ReadInjector(Path.Combine("Fx", "VfxDirector.cs"));
        Assert.DoesNotContain("ShieldBarPool.TickSync", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ShieldBarPool.StopAll", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ActorHudRowResources_honors_ShieldBarEnabled_toggle()
    {
        var text = ReadInjector(Path.Combine("Hud", "ActorHudRowResources.cs"));
        Assert.Contains("OverlaySettings.ShieldBarEnabled", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ActorHudPool_uses_ActorHudVisibility_ShouldShow()
    {
        var text = ReadInjector(Path.Combine("Hud", "ActorHudPool.cs"));
        Assert.Contains("OverlaySettings.ShieldBarEnabled", text, StringComparison.Ordinal);
        Assert.Contains("ActorHudVisibility.ShouldShow", text, StringComparison.Ordinal);
        Assert.DoesNotContain("static bool ShouldShow(ActorHudSnapshot", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ActorHudRowIdentity_uses_display_tokens_and_hides_blank_tier()
    {
        var text = ReadInjector(Path.Combine("Hud", "ActorHudRowIdentity.cs"));
        Assert.Contains("ActorHudDisplayTokens.TierLetter", text, StringComparison.Ordinal);
        Assert.Contains("public static bool HasContent", text, StringComparison.Ordinal);
        Assert.Contains("PlaceLabel", text, StringComparison.Ordinal);
        Assert.Contains("hasLetter", text, StringComparison.Ordinal);
        Assert.Contains("if (!HasContent(identity) && !hasElements)", text, StringComparison.Ordinal);
        Assert.Contains("SetActive(slot.TierFrame, false)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ActorHudIdentity_line_measures_catalog_element_glyphs_with_the_identity_slots()
    {
        var identity = ReadInjector(Path.Combine("Hud", "ActorHudRowIdentity.cs"));
        var pool = ReadInjector(Path.Combine("Hud", "ActorHudPool.cs"));
        Assert.Contains("ActorHudRowElements.TryResolve", identity, StringComparison.Ordinal);
        Assert.Contains("ScreenIdentityElementPrimaryPixels", pool, StringComparison.Ordinal);
        Assert.Contains("ScreenIdentityElementSecondaryPixels", pool, StringComparison.Ordinal);
        Assert.DoesNotContain("ActorHudRowElements.Sync(", pool, StringComparison.Ordinal);
    }

    [Fact]
    public void ActorHudRowStatuses_resolves_catalog_tokens_not_id_slice_or_hash()
    {
        var text = ReadInjector(Path.Combine("Hud", "ActorHudRowStatuses.cs"));
        Assert.Contains("ActorHudDisplayTokens.ResolveStatus", text, StringComparison.Ordinal);
        Assert.Contains("ParseCatalogColor", text, StringComparison.Ordinal);
        Assert.Contains("ColorUtility.TryParseHtmlString", text, StringComparison.Ordinal);
        Assert.DoesNotContain("StatusInitials", text, StringComparison.Ordinal);
        Assert.DoesNotContain("StatusRgb", text, StringComparison.Ordinal);
        Assert.DoesNotContain("GetHashCode(id)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ActorHudRowResources_uses_StackPips_and_maxStackPips()
    {
        var text = ReadInjector(Path.Combine("Hud", "ActorHudRowResources.cs"));
        Assert.Contains("StackPips", text, StringComparison.Ordinal);
        Assert.Contains("maxStackPips", text, StringComparison.Ordinal);
        Assert.Contains("var gap", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ShieldBarPool_file_removed_from_injector()
    {
        var path = Path.Combine(FindRepoRoot(), "src", "FusionRpg.Injector", "Fx", "ShieldBarPool.cs");
        Assert.False(File.Exists(path), "ShieldBarPool.cs must be deleted after shield-slot-migration");
    }

    [Fact]
    public void Injector_has_no_ShieldBarPool_references()
    {
        var root = Path.Combine(FindRepoRoot(), "src", "FusionRpg.Injector");
        var hits = new List<string>();
        foreach (var file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            if (text.Contains("ShieldBarPool", StringComparison.Ordinal))
                hits.Add(Path.GetRelativePath(root, file));
        }

        Assert.Empty(hits);
    }

    [Fact]
    public void ShieldBarOverlay_capture_delegates_to_ActorHudDirector()
    {
        var text = ReadInjector(Path.Combine("Hud", "ShieldBarOverlay.cs"));
        Assert.Contains("ActorHudDirector.CaptureStatus", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ShieldBarPool", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ActorHudDirector_exposes_CaptureStatus_for_debug_bar_status()
    {
        var text = ReadInjector(Path.Combine("Hud", "ActorHudDirector.cs"));
        Assert.Contains("public static Dictionary<string, object> CaptureStatus()", text, StringComparison.Ordinal);
        Assert.Contains("ActorHudPool.ShieldBarsDrawn", text, StringComparison.Ordinal);
        Assert.Contains("[\"hudSlots\"]", text, StringComparison.Ordinal);
        Assert.Contains("[\"shieldBars\"]", text, StringComparison.Ordinal);
        Assert.Contains("WorldBars", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ActorHudPool_TickSync_releases_slots_when_presentation_cannot_run()
    {
        var text = ReadInjector(Path.Combine("Hud", "ActorHudPool.cs"));
        Assert.Contains("_lastEarly = \"world-hud-off\"", text, StringComparison.Ordinal);
        Assert.Contains("_lastEarly = \"no-tuning\"", text, StringComparison.Ordinal);
        Assert.Contains("_lastEarly = \"no-canvas\"", text, StringComparison.Ordinal);
        Assert.True(Count(text, "StopAll();") >= 3, "every presentation-wide early exit must release live slots");
    }

    /// <summary>
    /// The unseen-slot sweep is skipped while a per-frame sync budget is active, and that is
    /// deliberate: under a round-robin budget "unseen" usually means "not this frame's slice", so
    /// sweeping would make bars flicker at the budget's cadence. Release then comes from the entity's
    /// own death/despawn path. Pin BOTH halves — the condition, and the <c>ReleaseOwner</c> path it
    /// leans on — so the budget cannot quietly become a ghost-HUD source.
    /// </summary>
    [Fact]
    public void A_budgeted_walk_skips_the_sweep_and_leans_on_ReleaseOwner()
    {
        var text = ReadInjector(Path.Combine("Hud", "ActorHudPool.cs"));
        Assert.Contains("if (SyncBudgetPerFrame <= 0)", text, StringComparison.Ordinal);
        Assert.Contains("public static void ReleaseOwner(string? ptrHex)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ForgetEntity_clears_actor_hud_cache_and_pool()
    {
        var text = ReadInjector("GameHooks.cs");
        Assert.Contains("ActorHudCache.Remove", text, StringComparison.Ordinal);
        Assert.Contains("ActorHudPool.ReleaseOwner", text, StringComparison.Ordinal);
    }

    static string ReadInjector(string relative)
    {
        var path = Path.Combine(FindRepoRoot(), "src", "FusionRpg.Injector", relative);
        Assert.True(File.Exists(path), "missing " + path);
        return File.ReadAllText(path);
    }

    static int Count(string text, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }
        return count;
    }

    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
