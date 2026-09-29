using System.Text.RegularExpressions;
using Xunit;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// Map Decision 17: <b>switch the WebView; never fight the PVZ engine.</b> No rift-gate code may call a
/// <c>UIMgr</c> navigation method (or any engine navigation action) to change what the player sees.
/// The injector may **read** menu presence and **attach** a UI affordance to an existing screen; it may
/// not drive the engine's menu state.
///
/// This is a rule a reviewer would otherwise have to hold in their head every time they touch the
/// menu. It goes red the moment someone reaches for the obvious-but-forbidden shortcut — the exact
/// shortcut that a documented live hazard (<c>DebugActions.cs:1495-1496</c>) and a mid-run breaker
/// make unsafe. A comment cannot enforce that; this can.
///
/// Scope: the rift-gate sources only. The pre-existing observe-only patches on <c>UIMgr</c> live in
/// <c>GameCaptureHooks.cs</c> and are deliberately NOT in scope — they observe, and the rule is about
/// *calling*, not about patching what already exists.
/// </summary>
public class RiftGateNoNavigationGuardTests
{
    /// <summary>The engine navigation methods rift-gate must never call.</summary>
    static readonly string[] ForbiddenNavCalls =
    {
        "EnterMainMenu", "BackToMenu", "EnterPauseMenu", "BackToGame",
    };

    /// <summary>
    /// Rift-gate sources, discovered rather than listed so a new file is covered the day it is added.
    /// The program's own files are recognisable by name; scanning the whole injector would flag the
    /// sanctioned read-only debug surface (DebugActions drives navigation on purpose — it is the
    /// debug API, not rift-gate).
    /// </summary>
    static IEnumerable<string> RiftGateSources()
    {
        var root = FindRepoRoot();
        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal)) continue;
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal)) continue;

            var name = Path.GetFileName(file);
            if (name.StartsWith("Rift", StringComparison.Ordinal) ||
                name is "MenuPresenceHook.cs" ||
                name.Contains("RiftMenu", StringComparison.Ordinal))
                yield return file;
        }
    }

    [Fact]
    public void No_rift_gate_source_calls_a_UI_navigation_method()
    {
        var files = RiftGateSources().ToList();

        // Anchor the guard on positive evidence it scanned something: a guard that quietly finds no
        // files passes forever and protects nothing.
        Assert.True(files.Count >= 2,
            $"expected at least the presence hook + a rift source, found {files.Count}");

        var offenders = new List<string>();
        foreach (var file in files)
        {
            foreach (var raw in File.ReadAllLines(file))
            {
                var line = raw.Trim();
                if (line.StartsWith("//", StringComparison.Ordinal)) continue;
                if (line.StartsWith("///", StringComparison.Ordinal)) continue;

                foreach (var nav in ForbiddenNavCalls)
                {
                    // A CALL: `UIMgr.EnterMainMenu(`, `.BackToMenu(` — not a mere mention in a
                    // comment or a doc string (those are stripped above).
                    if (Regex.IsMatch(line, $@"\.{nav}\s*\("))
                        offenders.Add($"{Path.GetFileName(file)}: {line}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Decision 17 forbids calling a UIMgr navigation method from rift-gate code " +
            "(read presence; never drive the engine's menu state): " + string.Join(" | ", offenders));
    }

    /// <summary>
    /// The forbidden methods must still exist somewhere in the injector — otherwise this guard would
    /// pass merely because the names were renamed. Anchored on the debug surface that legitimately
    /// drives navigation (the Game-Injector-Debug scope), so the grep target is proven live.
    /// </summary>
    [Fact]
    public void The_forbidden_method_names_are_still_real_call_sites_elsewhere()
    {
        var root = FindRepoRoot();
        var debugActions = Path.Combine(root, "src", "FusionRpg.Injector", "DebugActions.cs");
        Assert.True(File.Exists(debugActions), "missing " + debugActions);

        var text = File.ReadAllText(debugActions);
        Assert.Contains("EnterMainMenu", text, StringComparison.Ordinal);
        Assert.Contains("BackToMenu", text, StringComparison.Ordinal);
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}

/// <summary>
/// Map Decision 18: the menu becomes **one rendering system — uGUI**. The menu art must not still be
/// painted in IMGUI beside the uGUI node, and the in-match "RPG" button must **stay** IMGUI (it is
/// match HUD chrome, not part of a menu hierarchy). Both halves are asserted so the split is a
/// recorded decision rather than a drift either way.
/// </summary>
public class RiftGateMenuRenderingGuardTests
{
    [Fact]
    public void The_retired_IMGUI_menu_painter_is_gone_and_nothing_draws_it()
    {
        var root = FindRepoRoot();
        var retired = Path.Combine(root, "src", "FusionRpg.Injector", "Hud", "RiftMenuOverlay.cs");
        Assert.False(File.Exists(retired),
            "the IMGUI menu painter must be retired (Decision 18: one rendering system for the menu)");

        foreach (var host in new[]
                 {
                     @"src\FusionRpg.Injector.BepInEx\Plugin.cs",
                     @"src\FusionRpg.Injector.MelonLoader\MelonFusionRpgMod.cs",
                 })
        {
            var text = File.ReadAllText(Path.Combine(root, host));
            Assert.False(text.Contains("RiftMenuOverlay.Draw", StringComparison.Ordinal),
                $"{host} still calls the retired IMGUI menu painter");
        }
    }

    [Fact]
    public void The_menu_art_is_a_uGUI_sprite_and_Sprite_Create_is_the_new_call()
    {
        var art = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "FusionRpg.Injector", "Hud", "RiftMenuArt.cs"));

        Assert.Contains("Sprite.Create", art, StringComparison.Ordinal);
        Assert.Contains("ImageConversion.LoadImage", art, StringComparison.Ordinal);

        var tombstone = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "FusionRpg.Injector", "Hud", "RiftMenuTombstone.cs"));
        Assert.Contains("UnityEngine.UI.Image", tombstone, StringComparison.Ordinal);
        Assert.Contains("UnityEngine.UI.Button", tombstone, StringComparison.Ordinal);
        // The affordance must reuse the existing click path, never a second toggle path.
        Assert.Contains("OverlaySwitch.RequestToggle", tombstone, StringComparison.Ordinal);
        // Decision 17: attach, never drive.
        Assert.False(Regex.IsMatch(tombstone, @"\.(EnterMainMenu|BackToMenu|EnterPauseMenu|BackToGame)\s*\("),
            "the tombstone must never call a UIMgr navigation method");
    }

    [Fact]
    public void The_in_match_RPG_button_stays_IMGUI_by_decision()
    {
        // Decision 18's deliberate split: match chrome is not a menu hierarchy.
        var gui = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "FusionRpg.Injector", "Hud", "OverlaySwitchGui.cs"));

        Assert.Contains("GUI.Button", gui, StringComparison.Ordinal);
        Assert.False(gui.Contains("UnityEngine.UI.Button", StringComparison.Ordinal),
            "the in-match button must stay IMGUI (Decision 18's deliberate split)");
    }

    /// <summary>
    /// Decision 15: the in-match button is restyled but keeps exactly ONE action. The restyle is
    /// presentation only — a second action (or a second request call) is the defect this catches.
    /// </summary>
    [Fact]
    public void The_restyled_in_match_button_still_has_exactly_one_action()
    {
        var gui = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "FusionRpg.Injector", "Hud", "OverlaySwitchGui.cs"));

        // Exactly one interactive control...
        var buttons = Regex.Matches(gui, @"GUI\.Button\s*\(").Count;
        Assert.Equal(1, buttons);

        // ...and exactly one call into the shared request path.
        var requests = Regex.Matches(gui, @"OverlaySwitch\.RequestToggle\s*\(").Count;
        Assert.Equal(1, requests);

        // The gate that decides visibility is still the switch's, not a local re-implementation.
        Assert.Contains("OverlaySwitch.ButtonVisible", gui, StringComparison.Ordinal);

        // And it must NOT reach for a cheats/toggle surface of its own.
        Assert.DoesNotContain("CheatCommandRunner", gui, StringComparison.Ordinal);
        Assert.DoesNotContain("SendChat", gui, StringComparison.Ordinal);
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
