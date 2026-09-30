using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Guard.Tests;

/// <summary>live-qa 2026-09-20 (B27/BP4 probe): <c>RpgHost.Initialize</c> reads two authored
/// registries straight off <c>_pluginDir</c> -- <c>gk-data/packs/fusion/data/seed/dungeon/_registry</c>
/// (<c>DungeonRegistryLoader.LoadAll</c>, party-dungeon D1.4) and
/// <c>gk-data/packs/fusion/data/seed/commanders/_registry/default-commanders.v1.json</c>
/// (<c>DataCommanderDirectory.Parse</c>, commander-identity SE4.3) -- but no injector host csproj
/// ever Content-Included them, and the deploy tool has no copy step for either. Every fresh
/// game install crashed on <c>OnInitializeMelon</c> with a <c>DirectoryNotFoundException</c> on the
/// commanders file before this test's needles existed, which meant the injector could never say
/// hello and every live probe silently failed at the very first step. Dungeon's copy happened to
/// already sit in one machine's <c>Mods\data\seed\dungeon\_registry</c> from some earlier, untracked
/// placement -- proving nothing about a fresh install or a different machine. This is a structural
/// text-scan (this test assembly is deliberately reference-free, same rationale as
/// <see cref="AptitudeHostInjectionTests"/>), not a build: it cannot catch a typo'd path, only a
/// missing Content Include entirely.</summary>
public class InjectorSeedRegistryContentTests
{
    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }

    const string DungeonNeedle = @"data\seed\dungeon\_registry";
    const string CommandersNeedle = @"data\seed\commanders\_registry";

    static readonly string[] HostCsprojRelativePaths =
    {
        Path.Combine("src", "FusionRpg.Injector.MelonLoader.39", "FusionRpg.Injector.MelonLoader.39.csproj"),
        Path.Combine("src", "FusionRpg.Injector.MelonLoader", "FusionRpg.Injector.MelonLoader.csproj"),
        Path.Combine("src", "FusionRpg.Injector.BepInEx", "FusionRpg.Injector.BepInEx.csproj"),
    };

    public static IEnumerable<object[]> Hosts() =>
        HostCsprojRelativePaths.Select(p => new object[] { p });

    [Theory]
    [MemberData(nameof(Hosts))]
    public void HostCsproj_contentIncludesBothRegistriesRpgHostReadsFromPluginDir(string relativePath)
    {
        var path = Path.Combine(FindRepoRoot(), relativePath);
        Assert.True(File.Exists(path), $"host project not found: {path}");
        var text = File.ReadAllText(path);

        Assert.Contains(DungeonNeedle, text, StringComparison.Ordinal);
        Assert.Contains(CommandersNeedle, text, StringComparison.Ordinal);
        // Both needles must appear inside a real <Content Include=...> element, not merely a comment,
        // so a future edit that deletes the Content item but leaves the comment behind still fails.
        Assert.Contains($"<Content Include=\"..\\..\\{DungeonNeedle}", text, StringComparison.Ordinal);
        Assert.Contains($"<Content Include=\"..\\..\\{CommandersNeedle}", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RpgHost_stillReadsTheCommandersRegistryFromThePluginDir()
    {
        // If RpgHost.cs ever stops reading this path, the csproj Content items above become dead
        // weight and this guard would be asserting a fact nobody depends on any more.
        var path = Path.Combine(FindRepoRoot(), "src", "FusionRpg.Injector", "Host", "RpgHost.cs");
        var text = File.ReadAllText(path);
        Assert.Contains("\"data\", \"seed\", \"commanders\", \"_registry\", \"default-commanders.v1.json\"", text, StringComparison.Ordinal);
        Assert.Contains("\"data\", \"seed\", \"dungeon\", \"_registry\"", text, StringComparison.Ordinal);
    }
}
