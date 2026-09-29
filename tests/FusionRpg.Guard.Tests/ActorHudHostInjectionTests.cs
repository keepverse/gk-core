using System.Text.Json;
using Xunit;

namespace FusionRpg.Guard.Tests;

/// <summary>Injector host loads the current actor-hud tuning revision into ActorHudTuningHub at startup.</summary>
public class ActorHudHostInjectionTests
{
    const string WiringNeedle = "ActorHudTuningHub.Configure(";
    const string LoaderNeedle = "ActorHudTuningLoader.Parse(";
    const string FileNeedle = "actor-hud.v7.json";

    [Fact]
    public void InjectorHost_wiresActorHudTuningHub()
    {
        var text = ReadInjector(Path.Combine("Host", "RpgHost.cs"));
        Assert.Contains(WiringNeedle, text, StringComparison.Ordinal);
        Assert.Contains(LoaderNeedle, text, StringComparison.Ordinal);
        Assert.Contains(FileNeedle, text, StringComparison.Ordinal);
    }

    [Fact]
    public void ActorHudTuningHub_throws_when_unconfigured()
    {
        var text = ReadCore(Path.Combine("Hud", "ActorHudTuning.cs"));
        Assert.Contains("ActorHudTuningHub.Configure(...) has not run", text, StringComparison.Ordinal);
        Assert.Contains("there is no built-in default", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ActorHudElementArt_covers_every_non_presentation_catalog_glyph()
    {
        var catalogPath = Path.Combine(FindRepoRoot(), "data", "tuning", "element-catalog.v2.json");
        using var catalog = JsonDocument.Parse(File.ReadAllText(catalogPath));
        var assetRoot = Path.Combine(FindRepoRoot(), "src", "FusionRpg.Injector", "Assets", "actor-hud-elements");

        foreach (var entry in catalog.RootElement.GetProperty("entries").EnumerateArray())
        {
            if (entry.GetProperty("presentationOnly").GetBoolean()) continue;
            var glyph = entry.GetProperty("hudGlyph").GetString();
            Assert.False(string.IsNullOrWhiteSpace(glyph));
            Assert.True(File.Exists(Path.Combine(assetRoot, glyph + ".png")), "missing art for glyph " + glyph);
        }
    }

    [Fact]
    public void Every_injector_host_copies_the_actor_hud_element_art()
    {
        var root = FindRepoRoot();
        foreach (var project in new[]
                 {
                     Path.Combine("src", "FusionRpg.Injector.BepInEx", "FusionRpg.Injector.BepInEx.csproj"),
                     Path.Combine("src", "FusionRpg.Injector.MelonLoader", "FusionRpg.Injector.MelonLoader.csproj"),
                     Path.Combine("src", "FusionRpg.Injector.MelonLoader.39", "FusionRpg.Injector.MelonLoader.39.csproj"),
                 })
        {
            var text = File.ReadAllText(Path.Combine(root, project));
            Assert.Contains("Assets\\actor-hud-elements\\**\\*.png", text, StringComparison.Ordinal);
            Assert.Contains("assets\\actor-hud-elements\\%(RecursiveDir)%(Filename)%(Extension)", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Element_row_uses_catalog_glyph_assets_not_a_hard_coded_visual_switch()
    {
        var text = ReadInjector(Path.Combine("Hud", "ActorHudRowElements.cs"));
        var art = ReadInjector(Path.Combine("Hud", "ActorHudElementArt.cs"));
        Assert.Contains("ActorHudElementArt.GetSprite(entry.HudGlyph)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("switch (entry.HudGlyph)", text, StringComparison.Ordinal);
        Assert.Contains("Sprite.Create", art, StringComparison.Ordinal);
        Assert.Contains("ImageConversion.LoadImage", art, StringComparison.Ordinal);
    }

    static string ReadInjector(string relative)
    {
        var path = Path.Combine(FindRepoRoot(), "src", "FusionRpg.Injector", relative);
        Assert.True(File.Exists(path), "missing " + path);
        return File.ReadAllText(path);
    }

    static string ReadCore(string relative)
    {
        var path = Path.Combine(FindRepoRoot(), "src", "FusionRpg.Core", relative);
        Assert.True(File.Exists(path), "missing " + path);
        return File.ReadAllText(path);
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Injector")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("repo root");
    }
}
