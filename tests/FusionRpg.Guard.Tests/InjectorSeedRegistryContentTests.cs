using System.Text.RegularExpressions;
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

    /// <summary>
    /// One <c>&lt;Content Include&gt;</c> element, with its root captured rather than assumed.
    /// </summary>
    /// <remarks>
    /// The elements this test cares about carry no <c>Exclude</c>, so the attribute run is matched
    /// explicitly; the creatures entry does carry one and is simply not one of the registries.
    /// </remarks>
    static readonly Regex ContentInclude = new(
        @"<Content\s+Include=""(?<root>[^""]*)""(?<attrs>[^>]*)>\s*<Link>(?<link>[^<]*)</Link>",
        RegexOptions.Compiled);

    /// <summary>
    /// The workspace-root properties <c>gk-fusion/Directory.Build.props</c> actually declares.
    /// </summary>
    /// <remarks>
    /// Read from the file rather than listed here, so this cannot drift into a second copy of the
    /// table. The point is that a <c>$(Gk…Root)</c> used in a csproj and not declared in the props
    /// expands to the empty string, which turns a Content glob into one that silently matches nothing
    /// — a host that builds green and ships no registry, which is the failure this whole test exists
    /// to prevent. The old assertion could not catch that; this one can.
    /// </remarks>
    static readonly HashSet<string> DeclaredRoots = ReadDeclaredRoots();

    static HashSet<string> ReadDeclaredRoots()
    {
        var props = Path.Combine(KeepverseRoots.Fusion(), "Directory.Build.props");
        Assert.True(File.Exists(props), $"gk-fusion/Directory.Build.props not found at {props}");
        var text = File.ReadAllText(props);
        return Regex.Matches(text, @"<(?<name>Gk\w*Root)\s+Condition=")
            .Select(m => m.Groups["name"].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Asserts one registry is Content-Included from a real, declared root and linked plugin-relative.</summary>
    /// <param name="text">The host csproj's text.</param>
    /// <param name="needle">The registry's path below the root, e.g. <c>data\seed\dungeon\_registry</c>.</param>
    /// <param name="hostName">For the failure message only.</param>
    /// <remarks>
    /// <para>What changed, and why the old form could not be kept. The assertion used to read
    /// <c>&lt;Content Include="..\..\data\seed\dungeon\_registry</c> — the PRE-SPLIT spelling, in which
    /// the registry sat two directories up inside one repository. Post-split the registries live in
    /// gk-data's pack, so every host now writes
    /// <c>&lt;Content Include="$(GkDataRoot)packs\fusion\data\seed\dungeon\_registry\*.json"</c>.
    /// That is a legitimate root change, not a defect: the path is now owned by a different repository,
    /// and <c>gk-fusion/Directory.Build.props:11</c> declares <c>GkDataRoot</c> for exactly this. All
    /// three theories failed here once the owner was corrected, on this assertion and no other.</para>
    ///
    /// <para>So the pinned contract is the one the comment above already states — the needle must sit in
    /// a real Content Include, not a comment — plus the two things the split could plausibly have
    /// broken: the registry tail and its <c>\*.json</c> glob, and the plugin-relative <c>&lt;Link&gt;</c>
    /// destination that <c>RpgHost</c> actually reads. The root is now checked for being DECLARED
    /// rather than for a fixed spelling, which is strictly more than the old form checked.</para>
    /// </remarks>
    static void AssertRegistryContentInclude(string text, string needle, string hostName)
    {
        var expectedIncludeTail = needle + @"\*.json";
        var expectedLink = needle + @"\%(Filename)%(Extension)";

        var candidates = ContentInclude.Matches(text)
            .Cast<Match>()
            .Where(m => m.Groups["root"].Value.EndsWith(expectedIncludeTail, StringComparison.Ordinal))
            .ToList();

        Assert.True(
            candidates.Count > 0,
            $"{hostName}: no <Content Include=…{expectedIncludeTail}> element with a <Link>. "
            + "The registries must be Content-Included so a fresh install finds them beside the plugin; "
            + "a mention inside a comment does not ship.");

        foreach (var m in candidates)
        {
            var root = m.Groups["root"].Value[..^expectedIncludeTail.Length];
            var link = m.Groups["link"].Value;

            // The destination RpgHost reads is plugin-relative and is NOT a repository path. If this
            // ever gains a `..\`, the registry is being copied to the wrong place beside the plugin.
            Assert.Equal(expectedLink, link);

            // The root must be one MSBuild can actually expand. Self-relative, a real relative path,
            // a declared $(Gk…Root), or an absolute path - and nothing else.
            var property = Regex.Match(root, @"^\$\((?<name>[^)]+)\)");
            if (property.Success)
            {
                var name = property.Groups["name"].Value;
                Assert.True(
                    DeclaredRoots.Contains(name),
                    $"{hostName}: $({name}) is not declared in gk-fusion/Directory.Build.props, so it "
                    + "expands to the empty string and this Content glob silently matches nothing. "
                    + $"Declared: {string.Join(", ", DeclaredRoots.OrderBy(x => x))}");
            }
            else
            {
                var selfRelative = root.Length == 0 || root.StartsWith(@".\", StringComparison.Ordinal);
                var rooted = Path.IsPathRooted(root);
                Assert.True(
                    selfRelative || rooted,
                    $"{hostName}: Content root {root} is neither self-relative, rooted, nor a declared "
                    + "$(Gk…Root) property, so it cannot resolve.");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public void HostCsproj_contentIncludesBothRegistriesRpgHostReadsFromPluginDir(string relativePath)
    {
        // The three host projects are gk-FUSION's. These reads went through FindRepoRoot(), which
        // returns KeepverseRoots.Core(), and every theory failed with
        // `host project not found: D:\Works\source\Keepverse\gk-core\src\FusionRpg.Injector.BepInEx\…`.
        // The file was already half-migrated: the RpgHost.cs read below uses KeepverseRoots.Fusion(),
        // which is why one file held a working path beside a broken one.
        var path = Path.Combine(KeepverseRoots.Fusion(), relativePath);
        Assert.True(File.Exists(path), $"host project not found: {path} - gk-fusion carries the host projects");
        var text = File.ReadAllText(path);
        var hostName = relativePath.Replace(Path.DirectorySeparatorChar, '/');

        AssertRegistryContentInclude(text, DungeonNeedle, hostName);
        AssertRegistryContentInclude(text, CommandersNeedle, hostName);
    }

    [Fact]
    public void RpgHost_stillReadsTheCommandersRegistryFromThePluginDir()
    {
        // If RpgHost.cs ever stops reading this path, the csproj Content items above become dead
        // weight and this guard would be asserting a fact nobody depends on any more.
        var path = Path.Combine(KeepverseRoots.Fusion(), "src", "FusionRpg.Injector", "Host", "RpgHost.cs");
        var text = File.ReadAllText(path);
        Assert.Contains("\"data\", \"seed\", \"commanders\", \"_registry\", \"default-commanders.v1.json\"", text, StringComparison.Ordinal);
        Assert.Contains("\"data\", \"seed\", \"dungeon\", \"_registry\"", text, StringComparison.Ordinal);
    }
}
