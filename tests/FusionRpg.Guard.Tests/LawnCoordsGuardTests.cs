using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// BoardAction world XY must go through LawnCoords.CellCenter (Mouse box),
/// not a living zombie/plant transform.
/// </summary>
public class LawnCoordsGuardTests
{
    [Fact]
    public void CellPos_delegates_to_LawnCoords_CellCenter()
    {
        var text = ReadInjector("DebugActions.cs");
        Assert.Contains("LawnCoords.CellCenter", text, StringComparison.Ordinal);
        Assert.DoesNotContain("theZombieRow != row", text, StringComparison.Ordinal);
        Assert.DoesNotContain("GetBoxYFromRow", text, StringComparison.Ordinal);
        Assert.DoesNotContain("col + 0.5f", text, StringComparison.Ordinal);
    }

    [Fact]
    public void LawnCoords_uses_Mouse_box_for_cell_center()
    {
        var text = ReadInjector(Path.Combine("Lawn", "LawnCoords.cs"));
        Assert.Contains("GetBoxXFromColumn", text, StringComparison.Ordinal);
        Assert.Contains("GetBoxYFromRow", text, StringComparison.Ordinal);
        Assert.Contains("BodyWorld", text, StringComparison.Ordinal);
        Assert.Contains("theZombieRow", text, StringComparison.Ordinal);
        Assert.DoesNotContain("FindObjectsOfType<Zombie>", text, StringComparison.Ordinal);
        Assert.DoesNotContain("public static Vector2 WorldToGui", text, StringComparison.Ordinal);
        Assert.Contains("TryWorldToGui", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Overlay_draw_uses_UnitFrame_and_TryWorldToGui()
    {
        // vfx-ssot.md §9.1: VfxDirector owns floater draw; UnitFrame anchor + Repaint gate.
        var text = ReadInjector(Path.Combine("Fx", "VfxDirector.cs"));
        Assert.Contains("UnitFrameResolver.Resolve", text, StringComparison.Ordinal);
        Assert.DoesNotContain("LawnCoords.BodyWorld", text, StringComparison.Ordinal);
        Assert.Contains("LawnCoords.TryWorldToGui", text, StringComparison.Ordinal);
        Assert.Contains("LawnCoords.CellCenter", text, StringComparison.Ordinal);
        Assert.Contains("GUI.Label", text, StringComparison.Ordinal);
        Assert.Contains("EventType.Repaint", text, StringComparison.Ordinal);
        Assert.Contains("BurstPool.Spawn", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Follow.position", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ActorHudPool_uses_screen_space_silhouette_anchor()
    {
        var text = ReadInjector(Path.Combine("Hud", "ActorHudPool.cs"));
        Assert.Contains("ActorScreenAnchorResolver.TryResolve", text, StringComparison.Ordinal);
        Assert.DoesNotContain("UnitFrameResolver.Resolve", text, StringComparison.Ordinal);
        Assert.DoesNotContain("LawnCoords.BodyWorld", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Fx_only_UnitFrameResolver_reads_BodyWorld_or_bounds()
    {
        var fxDir = Path.Combine(KeepverseRoots.Fusion(), "src", "FusionRpg.Injector", "Fx");
        var failures = new List<string>();
        foreach (var file in Directory.GetFiles(fxDir, "*.cs", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(file);
            if (string.Equals(name, "UnitFrameResolver.cs", StringComparison.OrdinalIgnoreCase))
                continue;

            var text = File.ReadAllText(file);
            if (text.Contains("BodyWorld", StringComparison.Ordinal))
                failures.Add(name + ": BodyWorld");
            if (text.Contains("GetComponentInChildren<Renderer>", StringComparison.Ordinal))
                failures.Add(name + ": GetComponentInChildren<Renderer>");
            // `\.bounds\b` matched `sprite?.bounds.size` and reported it as a world-frame read. A
            // Sprite's own bounds is its size in its OWN space — it is not the plant's frame, so reading
            // it cannot be the "read the frame yourself instead of asking UnitFrameResolver" defect this
            // rule exists to catch. Measured: EarthPhasePool.cs DOES resolve through UnitFrameResolver
            // (three call sites) and its only `.bounds` is `sprite?.bounds.size`, so it was a compliant
            // file being failed for a compliant line.
            //
            // The pattern now names the WORLD-SPACE forms specifically, and a self-check below proves it
            // still matches them — narrowing a pattern is only safe when the narrowed pattern is shown to
            // keep its teeth, and "the test passes" is not that proof.
            if (WorldBoundsUse.IsMatch(text))
                failures.Add(name + ": " + WorldBoundsUse.Match(text).Value);
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void Vfx_per_cue_path_never_scans_the_scene()
    {
        // vfx-ssot.md ban: no FindObjectsOfType anywhere in VFX code — anchors resolve via
        // InjectorEntityRegistry, and the texture steal is gone (soft disc always).
        foreach (var file in new[] { "AnchorResolver.cs", "VfxDirector.cs", "BurstPool.cs", "FxResources.cs", "ImpactStampPool.cs" })
        {
            var text = ReadInjector(Path.Combine("Fx", file));
            Assert.DoesNotContain("FindObjectsOfType", text, StringComparison.Ordinal);
        }

        var anchor = ReadInjector(Path.Combine("Fx", "AnchorResolver.cs"));
        Assert.Contains("InjectorEntityRegistry", anchor, StringComparison.Ordinal);
    }

    [Fact]
    public void Impact_stamp_is_a_bounded_shared_material_pool()
    {
        var text = ReadInjector(Path.Combine("Fx", "ImpactStampPool.cs"));
        Assert.Contains("sharedMaterial", text, StringComparison.Ordinal);
        Assert.Contains("steal the oldest", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("StopAll", text, StringComparison.Ordinal);
        Assert.DoesNotContain(".material", text, StringComparison.Ordinal);
        Assert.DoesNotContain("FindObjectsOfType", text, StringComparison.Ordinal);
        Assert.DoesNotContain("new Texture2D", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Overlay_world_fx_uses_found_shader_and_cell_center()
    {
        var probe = ReadInjector(Path.Combine("Fx", "OverlayShaderProbe.cs"));
        Assert.Contains("Shader.Find", probe, StringComparison.Ordinal);
        Assert.Contains("Particles/Additive", probe, StringComparison.Ordinal);
        Assert.Contains("Sprites/Default", probe, StringComparison.Ordinal);
        Assert.DoesNotContain("new Material(\"", probe, StringComparison.Ordinal);

        // vfx-ssot.md §10: FxResources owns the found-shader material; no runtime ShaderLab.
        var resources = ReadInjector(Path.Combine("Fx", "FxResources.cs"));
        Assert.Contains("new Material(shader)", resources, StringComparison.Ordinal);
        Assert.DoesNotContain("Texture2D.whiteTexture", resources, StringComparison.Ordinal);

        // vfx-ssot.md §8.4: pooled bursts, presentation only — no vanilla spawns, no HP writes.
        var world = ReadInjector(Path.Combine("Fx", "BurstPool.cs"));
        Assert.Contains("ParticleSystem", world, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateCherryExplode", world, StringComparison.Ordinal);
        Assert.DoesNotContain(".TakeDamage", world, StringComparison.Ordinal);
        Assert.DoesNotContain("EntityStatWriter", world, StringComparison.Ordinal);
        Assert.DoesNotContain("BoardAction", world, StringComparison.Ordinal);
    }

    [Fact]
    public void CheatState_spawn_cell_setters_clamp()
    {
        var text = ReadInjector("CheatState.cs");
        Assert.Contains("LawnCoords.ClampCol", text, StringComparison.Ordinal);
        Assert.Contains("LawnCoords.ClampRow", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Injector_has_no_hardcoded_row5_col9_clamp_outside_LawnCoords()
    {
        var failures = new List<string>();
        foreach (var file in EnumerateInjectorCs())
        {
            if (file.EndsWith("LawnCoords.cs", StringComparison.OrdinalIgnoreCase)) continue;
            var text = File.ReadAllText(file);
            if (System.Text.RegularExpressions.Regex.IsMatch(text, @"Clamp\([^;]*0,\s*5\)"))
                failures.Add(Rel(file) + ": Clamp(..., 0, 5)");
            if (System.Text.RegularExpressions.Regex.IsMatch(text, @"Clamp\([^;]*0,\s*9\)"))
                failures.Add(Rel(file) + ": Clamp(..., 0, 9)");
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void Pet_and_bucket_use_CellCenter_not_col_row_as_world()
    {
        var failures = new List<string>();
        foreach (var file in EnumerateInjectorCs())
        {
            var text = File.ReadAllText(file);
            if (text.Contains("new Vector2(CheatState.SpawnCol, CheatState.SpawnRow)", StringComparison.Ordinal))
                failures.Add(Rel(file));
        }

        Assert.True(failures.Count == 0, "leftover Vector2(SpawnCol, SpawnRow) in:\n" + string.Join("\n", failures));
        var cheats = ReadInjector("CheatActions.cs");
        Assert.Contains("LawnCoords.CellCenter(CheatState.SpawnCol, CheatState.SpawnRow)", cheats, StringComparison.Ordinal);
    }

    static IEnumerable<string> EnumerateInjectorCs()
    {
        var root = Path.Combine(KeepverseRoots.Fusion(), "src", "FusionRpg.Injector");
        return Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(p => p.IndexOf($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) < 0
                        && p.IndexOf($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) < 0);
    }

    static string ReadInjector(string relative)
    {
        var path = Path.Combine(KeepverseRoots.Fusion(), "src", "FusionRpg.Injector", relative);
        Assert.True(File.Exists(path), "missing " + path);
        return File.ReadAllText(path);
    }

    static string Rel(string full)
    {
        var root = FindRepoRoot();
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? full[root.Length..].TrimStart('\\', '/')
            : full;
    }

    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }

    /// <summary>
    /// A world-space bounds read: a Renderer's own <c>bounds</c>, which is the frame the rule is about.
    /// </summary>
    /// <remarks>
    /// Deliberately does NOT match <c>sprite.bounds</c>. A Sprite's bounds is its size in its own space;
    /// a Renderer's is a world-space AABB. The guard once matched both with <c>\.bounds\b</c> and failed a
    /// compliant file over <c>sprite?.bounds.size</c>. Both alternatives below are the world-space read.
    /// </remarks>
    static readonly System.Text.RegularExpressions.Regex WorldBoundsUse = new(
        @"\b[Rr]enderer\s*[?!]?\s*\.\s*bounds\b|(?:\.\s*)?GetComponentInChildren\s*<\s*Renderer\s*>\s*\(\s*\)\s*[?!]?\s*\.\s*bounds\b",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <remarks>
    /// This falsifier EARNED ITS PLACE on its first run: the pattern it checks was written with
    /// <c>Renderer\s*\??</c>, which allows the null-conditional <c>?</c> but not the null-forgiving
    /// <c>!</c> — so it failed to catch <c>slot.Renderer!.bounds</c>, a real world-space read in the very
    /// shape C# uses when the renderer was already null-checked. A guard test with no falsifier would have
    /// shipped that.
    /// </remarks>
    [Fact]
    public void The_world_bounds_pattern_still_matches_what_it_is_meant_to_forbid()
    {
        // The falsifier for the narrowing above. A pattern made narrower without this would pass by
        // ceasing to match anything, which is indistinguishable from a correct rule.
        foreach (var forbidden in new[]
                 {
                     "var b = slot.Renderer.bounds;",
                     "var b = slot.Renderer!.bounds;",
                     "var b = GetComponentInChildren<Renderer>()!.bounds;",
                     "var b = go.GetComponentInChildren<Renderer>().bounds;",
                 })
        {
            Assert.True(WorldBoundsUse.IsMatch(forbidden), $"the pattern no longer catches: {forbidden}");
        }

        // …and the forms it must NOT catch, so the narrowing is not a blind one.
        foreach (var allowed in new[]
                 {
                     "var size = sprite?.bounds.size ?? Vector3.zero;",
                     "var size = sprite.bounds.size;",
                     "var frame = UnitFrameResolver.Resolve(plant);",
                 })
        {
            Assert.False(WorldBoundsUse.IsMatch(allowed), $"the pattern now flags a legal line: {allowed}");
        }
    }

}
