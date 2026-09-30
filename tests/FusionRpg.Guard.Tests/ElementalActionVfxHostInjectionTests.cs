using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Guard.Tests;

public class ElementalActionVfxHostInjectionTests
{
    static readonly (string Path, string LogicalName)[] RuntimeVariants =
    {
        ("earth\\v2\\impact-128x128.png", "Earth.V2.impact-128x128.png"),
        ("earth\\v2\\impact-256x256.png", "Earth.V2.impact-256x256.png"),
        ("earth\\v2\\impact-512x512.png", "Earth.V2.impact-512x512.png"),
        ("earth\\charge-256x256.png", "Earth.charge-256x256.png"),
        ("earth\\travel-256x64.png", "Earth.travel-256x64.png")
    };

    [Fact]
    public void Injector_host_loads_the_v4_stamp_tuning()
    {
        var path = Path.Combine(KeepverseRoots.Fusion(), "src", "FusionRpg.Injector", "Host", "RpgHost.cs");
        var text = File.ReadAllText(path);

        Assert.Contains("VfxTuningHub.Configure(", text, StringComparison.Ordinal);
        Assert.Contains("vfx.v7.json", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Earth_phase_leases_receive_the_existing_concrete_element_color_plan()
    {
        var root = KeepverseRoots.Fusion();
        var director = File.ReadAllText(Path.Combine(root, "src", "FusionRpg.Injector", "Fx", "VfxDirector.cs"));
        var pool = File.ReadAllText(Path.Combine(root, "src", "FusionRpg.Injector", "Fx", "EarthPhasePool.cs"));

        Assert.Contains("SpawnCharge(source, plan.Rgb", director, StringComparison.Ordinal);
        Assert.Contains("SpawnTravel(source, follow, plan.Rgb", director, StringComparison.Ordinal);
        Assert.Contains("new Color32(rgb.R, rgb.G, rgb.B", pool, StringComparison.Ordinal);
        Assert.DoesNotContain("new Color(1f, 1f, 1f, Mathf.Lerp", pool, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_injector_host_embeds_the_same_Earth_runtime_resources_without_the_source_master()
    {
        foreach (var project in HostProjects())
        {
            var text = File.ReadAllText(Path.Combine(FindRepoRoot(), project));
            foreach (var variant in RuntimeVariants)
            {
                var include = "Assets\\elemental-action-vfx\\" + variant.Path;
                var logicalName = "FusionRpg.Injector.Assets.ElementalActionVfx." + variant.LogicalName;
                Assert.Contains(include, text, StringComparison.Ordinal);
                Assert.Contains(logicalName, text, StringComparison.Ordinal);
            }

            Assert.DoesNotContain("elemental-action-vfx\\earth\\v2\\source", text, StringComparison.Ordinal);
        }
    }

    static IEnumerable<string> HostProjects() => new[]
    {
        Path.Combine("src", "FusionRpg.Injector.BepInEx", "FusionRpg.Injector.BepInEx.csproj"),
        Path.Combine("src", "FusionRpg.Injector.MelonLoader", "FusionRpg.Injector.MelonLoader.csproj"),
        Path.Combine("src", "FusionRpg.Injector.MelonLoader.39", "FusionRpg.Injector.MelonLoader.39.csproj"),
        Path.Combine("src", "FusionRpg.Injector.MelonLoader.40", "FusionRpg.Injector.MelonLoader.40.csproj")
    };

    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
