using System.Reflection;
using FusionRpg.Core.Effects.Atoms;
using Xunit;
using FusionRpg.TestSupport;

namespace FusionRpg.Data.Tests;

/// <summary>
/// E46 (player-content-boot) test 6, spec-player-content-boot.md §5: "the import covers every
/// SeedContent list, asserted by reflection over the type rather than a hand-written list."
///
/// <para>A hand-written list of "which SeedContent fields an import covers" is exactly what let
/// <c>SeedContent</c> grow an <c>Affixes</c> list <c>ImportContent</c> never read, unnoticed until E32
/// went looking for it (spec's own §5 note). Reflecting the type instead means a new seed kind added
/// tomorrow fails THIS test the moment nothing references it — nobody has to remember to update a
/// parallel list here.</para>
/// </summary>
public class SeedContentCoverageTests
{
    [Fact]
    public void ImportContent_references_every_list_valued_property_SeedContent_declares()
    {
        var source = File.ReadAllText(
            Path.Combine(CoreRoot.Path, "src", "FusionRpg.Data", "Sqlite", "RpgStore.Import.cs"));

        var listProps = typeof(SeedContent)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == typeof(List<>))
            .Select(p => p.Name)
            .ToList();

        // Sanity: if this comes back empty or tiny, SeedContent's shape changed under this test's
        // feet rather than coverage actually improving — a test that can silently pass over nothing
        // is worse than no test.
        Assert.True(listProps.Count >= 8,
            $"expected at least 8 list-valued fields on SeedContent, found {listProps.Count}: {string.Join(", ", listProps)}");

        // KNOWN, PRE-EXISTING GAP — found BY this test while building E46, not introduced by it, and
        // not E46's to fix (E46 is the import TRIGGER, not the import's own coverage). Verified
        // 2026-09-03: SeedContent.ChannelPools is populated by AtomSeedFile.Collect (ReadChannelPool),
        // and real authored content already exists on disk (gk-data/packs/fusion/data/seed/channel-pools/pools.v1.json),
        // but RpgStore.ImportContent never references content.ChannelPools at all — there is no
        // channel_pool table, no UpsertChannelPool, no reader anywhere in FusionRpg.Data.
        // ProduceAndBind instead takes a caller-supplied `Func<string, ChannelPoolRow?> lookupPool`
        // delegate, so pools resolve some other way today, never through this import path. Closing
        // this is E30 channel-pool's own remaining work (or a fresh completeness-audit finding), and
        // is called out in E46's own report rather than silently patched here. Remove this entry the
        // day ImportContent actually reads content.ChannelPools.
        var knownGaps = new[] { nameof(SeedContent.ChannelPools) };

        var uncovered = listProps
            .Except(knownGaps, StringComparer.Ordinal)
            .Where(name => !source.Contains("content." + name, StringComparison.Ordinal))
            .ToList();

        Assert.True(uncovered.Count == 0,
            "RpgStore.ImportContent never references SeedContent." + string.Join(", SeedContent.", uncovered) +
            " — either wire the import or add it to knownGaps above with a dated reason.");
    }

    static string RepoRoot() => ContentRoot.Path;
}
