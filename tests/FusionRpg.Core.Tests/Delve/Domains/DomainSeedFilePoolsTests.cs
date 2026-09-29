using FusionRpg.Core.Delve.Domains;
using FusionRpg.Core.Tests.Dungeon;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Domains;

/// <summary>D4.16's own real bridging gap (party-dungeon-todo.md, 2026-09-07): `LoadQuestPools`/
/// `LoadLootBindings`/`LoadProvenanceJson`, the three `DomainSeedFile` additions its own SQL-import
/// writer needs.</summary>
public class DomainSeedFilePoolsTests
{
    [Fact]
    public void LoadQuestPools_null_directory_throws()
    {
        Assert.Throws<ArgumentNullException>(() => DomainSeedFile.LoadQuestPools(null!));
    }

    [Fact]
    public void LoadQuestPools_a_missing_directory_returns_empty_never_throws()
    {
        Assert.Empty(DomainSeedFile.LoadQuestPools(Path.Combine(DungeonTestFiles.RepoRoot(), "does-not-exist")));
    }

    [Fact]
    public void LoadQuestPools_reads_all_six_real_domains_own_real_quest_pool()
    {
        var pools = DomainSeedFile.LoadQuestPools(DungeonTestFiles.DomainsDir());
        Assert.Equal(6, pools.Count);
        Assert.Contains("quest.kill-boss-domain-001", pools["domain.fire-001"]);
        Assert.True(pools["domain.fire-001"].Count >= 2);
    }

    [Fact]
    public void LoadLootBindings_null_directory_throws()
    {
        Assert.Throws<ArgumentNullException>(() => DomainSeedFile.LoadLootBindings(null!));
    }

    [Fact]
    public void LoadLootBindings_a_missing_directory_returns_empty_never_throws()
    {
        Assert.Empty(DomainSeedFile.LoadLootBindings(Path.Combine(DungeonTestFiles.RepoRoot(), "does-not-exist")));
    }

    [Fact]
    public void LoadLootBindings_reads_all_four_bound_kinds_for_a_real_domain()
    {
        var bindings = DomainSeedFile.LoadLootBindings(DungeonTestFiles.DomainsDir());
        Assert.Equal(6, bindings.Count);
        var fire = bindings["domain.fire-001"];
        Assert.Equal("drop.dungeon.fire.boss", fire["boss"]);
        Assert.Equal("drop.dungeon.fire.cache", fire["cache"]);
        Assert.Equal("drop.dungeon.fire.elite", fire["elite"]);
        Assert.Equal("drop.dungeon.fire.fight", fire["fight"]);
    }

    [Fact]
    public void LoadProvenanceJson_null_path_throws()
    {
        Assert.Throws<ArgumentNullException>(() => DomainSeedFile.LoadProvenanceJson(null!));
    }

    /// <summary>Real, measured 2026-09-07: none of the six real shipped domains carry a `_provenance`
    /// block (predates the D1.11 tracking convention) — the well-formed empty placeholder is returned,
    /// never a thrown exception and never a fabricated value.</summary>
    [Fact]
    public void LoadProvenanceJson_a_real_domain_with_no_provenance_block_returns_the_well_formed_empty_placeholder()
    {
        var path = Path.Combine(DungeonTestFiles.DomainsDir(), "domain.fire-001.json");
        var json = DomainSeedFile.LoadProvenanceJson(path);
        Assert.Equal(DomainSeedFile.EmptyProvenanceJson, json);
    }

    [Fact]
    public void EmptyProvenanceJson_is_well_formed_and_carries_every_field_spec_3_names()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(DomainSeedFile.EmptyProvenanceJson);
        var root = doc.RootElement;
        foreach (var field in new[] { "planHash", "briefHash", "promptVersions", "registryVersions", "motifSubsetHash", "attempts", "confidence", "minorityValues" })
            Assert.True(root.TryGetProperty(field, out _), $"missing field '{field}'");
    }
}
