using FusionRpg.Core.Delve.Domains;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Domains;

/// <summary>
/// `DomainSeedFile.LoadAll`'s own `firstClearRef` reading (party-dungeon-todo.md D3.15, 2026-09-07) —
/// the one field this dedicated test targets. Every other field `LoadAll` reads is already exercised
/// indirectly, at real-content scale, by `DomainGraphPreflightBridgeTests`/`DomainEncounterCoverageTests`
/// and friends; duplicating that here would test the same lines twice for no new information.
/// </summary>
public class DomainSeedFileTests
{
    string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fusionrpg-domain-seed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    static void WriteDomain(string dir, string fileName, string? firstClearRefJsonFragment)
    {
        var firstClearRefLine = firstClearRefJsonFragment is null ? "" : $"\"firstClearRef\": {firstClearRefJsonFragment},";
        File.WriteAllText(Path.Combine(dir, fileName), $$"""
            {
              "domainId": "domain.test-001", "name": "Test", "flavor": "f", "theme": "theme.overgrown",
              "climate": "fire", "dangerBand": "shallow", "entry": "many",
              "layoutTemplateId": "layout.standard", "bossSpeciesRef": "species.warden",
              {{firstClearRefLine}}
              "entranceHint": "Lair"
            }
            """);
    }

    [Fact]
    public void LoadAll_absent_firstClearRef_resolves_to_null()
    {
        var dir = NewTempDir();
        try
        {
            WriteDomain(dir, "d.json", firstClearRefJsonFragment: null);
            var row = Assert.Single(DomainSeedFile.LoadAll(dir));
            Assert.Null(row.FirstClearRef);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void LoadAll_none_sentinel_firstClearRef_resolves_to_null()
    {
        var dir = NewTempDir();
        try
        {
            WriteDomain(dir, "d.json", firstClearRefJsonFragment: "\"none\"");
            var row = Assert.Single(DomainSeedFile.LoadAll(dir));
            Assert.Null(row.FirstClearRef);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void LoadAll_a_real_firstClearRef_resolves_verbatim()
    {
        var dir = NewTempDir();
        try
        {
            WriteDomain(dir, "d.json", firstClearRefJsonFragment: "\"item.test-unique\"");
            var row = Assert.Single(DomainSeedFile.LoadAll(dir));
            Assert.Equal("item.test-unique", row.FirstClearRef);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
